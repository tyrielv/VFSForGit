using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.NamedPipes;
using GVFS.Common.Sparse;
using GVFS.Common.Tracing;
using GVFS.Virtualization;
using GVFS.Virtualization.Projection;

namespace GVFS.Mount
{
    /// <summary>
    /// Mount-side handler for automatic sparse-index cone management. When
    /// <c>gvfs.auto-sparse-index</c> is enabled, the pre-command hook asks the mount to
    /// widen the cone to cover the paths a Git command names, before Git runs, and the
    /// post-command hook asks it to narrow the cone back afterward. Widening pre-empts
    /// the one measured sparse-index expansion trigger: naming an out-of-cone path.
    /// </summary>
    /// <remarks>
    /// The applied cone is always the union of the modified-path-derived entries plus the
    /// live transient entries across every session (see <see cref="TransientConeState"/>),
    /// recomputed with <see cref="ConeBuilder"/> and written with <see cref="ConeFileWriter"/>,
    /// then made effective in place with <see cref="GitProcess.ForceCollapseSparseIndex"/>.
    /// <para>
    /// Concurrency: all cone operations run under a single process-internal lock, in the
    /// order coneLock -> atomic sparse-checkout file write -> git's own index.lock (held
    /// only inside the child git) -> a projection-reparse trigger. The reparse itself is
    /// NOT waited for on the reply path: <see cref="FileSystemCallbacks.RequestIndexProjectionUpdate"/>
    /// invalidates the projection and the background index-parsing thread rebuilds it
    /// asynchronously (the same machinery every other index change uses). Git only needs
    /// the on-disk index widened -- which the collapse does before the reply -- to avoid a
    /// full expansion; the projected set is the full HEAD tree regardless of the cone, so a
    /// widen never changes what ProjFS projects and the reparse only reconciles GVFS-internal
    /// state. Keeping the O(repo) reparse off the reply path is what keeps the hook's bounded
    /// wait covering only the O(cone) index write. The handler never holds
    /// projectionReadWriteLock itself and never opens the index for read, so it does not
    /// fight the index-parsing thread. The handler deliberately does NOT take the GVFS lock:
    /// the pre-command hook already holds it for the commands that widen (add, rm, restore,
    /// stash, reset -- &lt;path&gt;), so acquiring it here would deadlock the very command that
    /// is waiting on the reply. The collapse git process disables every GVFS hook, so it
    /// never re-enters the mount pipe. See decisions/0019 and decisions/0021.
    /// </para>
    /// </remarks>
    public class AutoSparseIndexConeManager
    {
        private readonly ITracer tracer;
        private readonly PhysicalFileSystem fileSystem;
        private readonly FileSystemCallbacks fileSystemCallbacks;
        private readonly GVFSEnlistment enlistment;
        private readonly GitProcess git;
        private readonly ConeFileWriter coneFileWriter;
        private readonly TransientConeState transientState;
        private readonly SemaphoreSlim coneLock;
        private readonly string sparseCheckoutPath;
        private readonly string workingDirectoryRoot;

        // The cone content GVFS last wrote this session, used to detect a hand-edit to the
        // file GVFS owns. Null until GVFS writes the file once (see decisions/0020).
        private string lastWrittenConeContent;

        // Non-null only when gvfs.sparse-index-cone-granularity is on. Supplies capped subtree
        // file counts so ConeBuilder can collapse entry-neutral parent-only chains. Read once
        // at construction: the flag is not meant to change within a mount session, and reading
        // it per recompute would put a git config process on the hook's reply path.
        private readonly IConeSubtreeFileCounter subtreeFileCounter;

        public AutoSparseIndexConeManager(GVFSContext context, FileSystemCallbacks fileSystemCallbacks)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(fileSystemCallbacks);

            this.tracer = context.Tracer;
            this.fileSystem = context.FileSystem;
            this.enlistment = context.Enlistment;
            this.fileSystemCallbacks = fileSystemCallbacks;
            this.git = new GitProcess(context.Enlistment);
            this.coneFileWriter = new ConeFileWriter(context.FileSystem);
            this.transientState = new TransientConeState();
            this.coneLock = new SemaphoreSlim(1, 1);
            this.sparseCheckoutPath = SparseCheckoutPathResolver.GetSparseCheckoutFilePath(context.Enlistment);
            this.workingDirectoryRoot = context.Enlistment.WorkingDirectoryRoot;

            if (LibGit2Repo.GetConfigBoolOrDefault(
                    context.Tracer,
                    context.Enlistment.WorkingDirectoryBackingRoot,
                    GVFSConstants.GitConfig.SparseIndexConeGranularity,
                    GVFSConstants.GitConfig.SparseIndexConeGranularityDefault))
            {
                this.subtreeFileCounter = new ProjectionSubtreeFileCounter(fileSystemCallbacks.GitIndexProjection);
            }
        }

        /// <summary>
        /// Widen the cone to cover the paths a Git command names, then reply. The hook
        /// blocks on this, so latency is user-visible: it is paid on every command that
        /// names an out-of-cone path. The reply is sent as soon as the on-disk index is
        /// widened; the projection reparse is triggered but not waited for, so the wait
        /// covers only the O(cone) index write, not the O(repo) reparse.
        /// </summary>
        public bool TryWiden(NamedPipeMessages.ConeManagement.WidenParameters parameters, out string error)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            IReadOnlyList<string> requestedPaths = this.ResolveRequestedPaths(parameters);

            this.coneLock.Wait();
            try
            {
                this.transientState.AddPaths(parameters.SessionId, requestedPaths);
                return this.ApplyCurrentCone(nameof(this.TryWiden), requestedPaths.Count, GVFSConstants.ConeManagement.WidenHookWaitBudgetMs, blocksGitCommand: true, out error);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
                this.TraceUnhandled(nameof(this.TryWiden), e);
                return false;
            }
            finally
            {
                this.coneLock.Release();
            }
        }

        /// <summary>
        /// Narrow the cone back by dropping this session's transient additions, then
        /// recompute from the modified paths plus any other sessions' live widens.
        /// </summary>
        public bool TryNarrow(NamedPipeMessages.ConeManagement.NarrowParameters parameters, out string error)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            this.coneLock.Wait();
            try
            {
                if (!this.transientState.RemoveSession(parameters.SessionId))
                {
                    // No matching widen for this session, so the cone already reflects the
                    // absence of its transient entries. Nothing to rewrite.
                    error = null;
                    return true;
                }

                return this.ApplyCurrentCone(nameof(this.TryNarrow), 0, GVFSConstants.ConeManagement.NarrowHookWaitBudgetMs, blocksGitCommand: false, out error);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
                this.TraceUnhandled(nameof(this.TryNarrow), e);
                return false;
            }
            finally
            {
                this.coneLock.Release();
            }
        }

        private IReadOnlyList<string> ResolveRequestedPaths(NamedPipeMessages.ConeManagement.WidenParameters parameters)
        {
            List<string> pathspecs = new List<string>(parameters.Pathspecs);

            if (!string.IsNullOrEmpty(parameters.PathspecFromFile))
            {
                this.AddPathspecsFromFile(parameters, pathspecs);
            }

            return ConePathspecResolver.ResolveToGitPaths(
                parameters.CurrentDirectory,
                this.workingDirectoryRoot,
                pathspecs);
        }

        private void AddPathspecsFromFile(NamedPipeMessages.ConeManagement.WidenParameters parameters, List<string> pathspecs)
        {
            try
            {
                string filePath = parameters.PathspecFromFile;
                if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(parameters.CurrentDirectory))
                {
                    filePath = Path.Combine(parameters.CurrentDirectory, filePath);
                }

                if (!this.fileSystem.FileExists(filePath))
                {
                    return;
                }

                string content = this.fileSystem.ReadAllText(filePath);
                char separator = parameters.PathspecFileNul ? '\0' : '\n';
                foreach (string line in content.Split(separator))
                {
                    string trimmed = parameters.PathspecFileNul ? line : line.TrimEnd('\r');
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        pathspecs.Add(trimmed);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                // A missed pathspec file only under-widens the cone, which Git handles by
                // transiently expanding the index. Proceed with the inline pathspecs.
                EventMetadata metadata = this.CreateEventMetadata();
                metadata.Add("PathspecFromFile", parameters.PathspecFromFile);
                metadata.Add("Exception", e.ToString());
                this.tracer.RelatedWarning(metadata, $"{nameof(this.AddPathspecsFromFile)}: Failed to read pathspec file; widening with inline pathspecs only");
            }
        }

        private bool ApplyCurrentCone(string caller, int requestedPathCount, int hookWaitBudgetMs, bool blocksGitCommand, out string error)
        {
            error = null;

            Stopwatch stopwatch = Stopwatch.StartNew();

            List<string> allPaths = new List<string>(this.fileSystemCallbacks.GetAllModifiedPaths());
            allPaths.AddRange(this.transientState.GetAllTransientPaths());

            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(
                allPaths,
                this.subtreeFileCounter,
                ConeGranularityOptions.Default);
            string newContent = ConeFileWriter.Serialize(cone);
            double coneBuildMs = stopwatch.Elapsed.TotalMilliseconds;

            // Read the on-disk cone once: used both to detect a hand-edit (drift) and to
            // skip a redundant write. The file is small (a few KB even at os.2020 scale),
            // so a read plus ordinal compare is cheap and never parses the file with git.
            string currentContent = this.TryReadCurrentConeFile();
            this.WarnIfConeFileDrifted(caller, currentContent);

            if (string.Equals(currentContent, newContent, StringComparison.Ordinal))
            {
                // The cone already covers these paths, so there is nothing to write and no
                // projection rebuild to trigger. Current behaviour is byte-for-byte unchanged.
                this.lastWrittenConeContent = newContent;
                this.TraceApplied(caller, requestedPathCount, changed: false, hookWaitBudgetMs, blocksGitCommand, stopwatch, coneBuildMs, writeMs: 0, collapseMs: 0);
                return true;
            }

            if (!this.coneFileWriter.TryWrite(this.sparseCheckoutPath, cone, out string backupPath, out Exception writeException))
            {
                error = writeException?.Message ?? "Failed to write sparse-checkout file";
                EventMetadata metadata = this.CreateEventMetadata();
                metadata.Add("Exception", writeException?.ToString());
                this.tracer.RelatedError(metadata, $"{caller}: Failed to write cone sparse-checkout file");
                return false;
            }

            double writeMs = stopwatch.Elapsed.TotalMilliseconds - coneBuildMs;

            GitProcess.Result collapseResult = this.git.ForceCollapseSparseIndex();
            if (collapseResult.ExitCodeIsFailure)
            {
                error = collapseResult.Errors;
                this.RollBack(caller, backupPath, collapseResult);
                return false;
            }

            double collapseMs = stopwatch.Elapsed.TotalMilliseconds - coneBuildMs - writeMs;

            // GVFS now owns the on-disk content, so record it as the drift baseline for the
            // next recompute.
            this.lastWrittenConeContent = newContent;

            // The collapse rewrote the on-disk index with hooks disabled, so the mount got no
            // PostIndexChanged notification. Trigger a projection reparse to reconcile the
            // GVFS view with the new on-disk cone -- but do NOT wait for it. Git only needs
            // the on-disk index widened (done above) to avoid a full expansion; the reparse
            // is GVFS-internal and the projected set is the full HEAD tree regardless of the
            // cone, so a widen never changes what ProjFS projects. Waiting for the reparse
            // would put its O(repo) cost (seconds at os.2020 scale) on the hook's reply path
            // and blow the wait budget; the background index-parsing thread rebuilds the
            // projection asynchronously, exactly as it does for any other index change. The
            // modified-paths set is unchanged.
            this.fileSystemCallbacks.RequestIndexProjectionUpdate(invalidateProjection: true, invalidateModifiedPaths: false);

            this.TraceApplied(caller, requestedPathCount, changed: true, hookWaitBudgetMs, blocksGitCommand, stopwatch, coneBuildMs, writeMs, collapseMs);
            return true;
        }

        /// <summary>
        /// Reads the on-disk cone file, returning null when it is absent or unreadable. A
        /// null result makes the caller fall through to a write, which is safe.
        /// </summary>
        private string TryReadCurrentConeFile()
        {
            if (!this.fileSystem.FileExists(this.sparseCheckoutPath))
            {
                return null;
            }

            try
            {
                return this.fileSystem.ReadAllText(this.sparseCheckoutPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Warns when the on-disk cone file was edited outside GVFS. GVFS owns the file and
        /// overwrites the edit (warn-and-overwrite, decisions/0020). A non-cone hand-edit
        /// gets its own warning, because it silently disables the sparse index via git's
        /// <c>is_sparse_index_allowed()</c>. The warning goes to the mount trace; the
        /// mount handler has no interactive user channel.
        /// </summary>
        private void WarnIfConeFileDrifted(string caller, string currentContent)
        {
            if (ConeDriftDetector.IsNonConeFormat(currentContent))
            {
                EventMetadata metadata = this.CreateEventMetadata();
                metadata.Add("CurrentLength", currentContent?.Length ?? 0);
                metadata.Add(
                    TracingConstants.MessageKey.WarningMessage,
                    $"{caller}: .git/info/sparse-checkout was hand-edited to a non-cone pattern, which silently disables the sparse index (git's is_sparse_index_allowed requires cone patterns). GVFS owns this file and is restoring a cone-format file.");
                this.tracer.RelatedWarning(metadata, $"{caller}_ConeFileNonConeFormat");
                return;
            }

            if (ConeDriftDetector.HasDrifted(this.lastWrittenConeContent, currentContent))
            {
                EventMetadata metadata = this.CreateEventMetadata();
                metadata.Add("CurrentLength", currentContent?.Length ?? 0);
                metadata.Add(
                    TracingConstants.MessageKey.WarningMessage,
                    $"{caller}: .git/info/sparse-checkout was edited outside GVFS. GVFS owns this file and is overwriting the change.");
                this.tracer.RelatedWarning(metadata, $"{caller}_ConeFileDrift");
            }
        }

        private void RollBack(string caller, string backupPath, GitProcess.Result collapseResult)
        {
            EventMetadata metadata = this.CreateEventMetadata();
            metadata.Add("ExitCode", collapseResult.ExitCode);
            metadata.Add("Errors", collapseResult.Errors);

            bool restored = false;
            Exception restoreException = null;
            if (backupPath != null)
            {
                restored = this.coneFileWriter.TryRestoreBackup(this.sparseCheckoutPath, backupPath, out restoreException);
            }

            if (restored)
            {
                metadata.Add(TracingConstants.MessageKey.WarningMessage, "Cone collapse failed; restored previous sparse-checkout file");
            }
            else
            {
                metadata.Add("RestoreException", restoreException?.ToString());
                metadata.Add(TracingConstants.MessageKey.ErrorMessage, "Cone collapse failed and the previous sparse-checkout file could not be restored");
            }

            this.tracer.RelatedError(metadata, $"{caller}: ForceCollapseSparseIndex failed");
        }

        private void TraceApplied(
            string caller,
            int requestedPathCount,
            bool changed,
            int hookWaitBudgetMs,
            bool blocksGitCommand,
            Stopwatch stopwatch,
            double coneBuildMs,
            double writeMs,
            double collapseMs)
        {
            stopwatch.Stop();
            double durationMs = stopwatch.Elapsed.TotalMilliseconds;

            EventMetadata metadata = this.CreateEventMetadata();
            metadata.Add("RequestedPathCount", requestedPathCount);
            metadata.Add("TransientSessionCount", this.transientState.SessionCount);
            metadata.Add("ConeChanged", changed);

            // DurationMs is the hook-blocking portion only (cone build + sparse-checkout
            // write + in-place index collapse + reparse trigger). It excludes the projection
            // reparse, which now runs asynchronously on the background parse thread.
            metadata.Add("DurationMs", durationMs);
            metadata.Add("ConeBuildMs", coneBuildMs);
            metadata.Add("WriteMs", writeMs);
            metadata.Add("CollapseMs", collapseMs);
            metadata.Add("HookWaitBudgetMs", hookWaitBudgetMs);
            metadata.Add(TracingConstants.MessageKey.InfoMessage, $"{caller}: Applied cone");
            this.tracer.RelatedEvent(EventLevel.Informational, $"{caller}_Applied", metadata);

            if (blocksGitCommand && changed && durationMs > hookWaitBudgetMs)
            {
                // The pre-command widen outran the hook's wait budget, so the hook abandoned
                // the wait and let Git run before the cone was confirmed applied. This is
                // correctness-safe (Git transiently expands and re-collapses the index), but
                // the "block Git until the cone is applied" guarantee did not hold this time.
                // Surface it as a warning so the soft-guarantee breach is visible in the mount
                // log rather than silent. Only the pre-command widen blocks Git, so only it
                // warns; the post-command narrow exceeding its budget is expected and benign
                // (Git already ran; a late narrow only defers shrinking the cone).
                EventMetadata warning = this.CreateEventMetadata();
                warning.Add("DurationMs", durationMs);
                warning.Add("CollapseMs", collapseMs);
                warning.Add("HookWaitBudgetMs", hookWaitBudgetMs);
                warning.Add(
                    TracingConstants.MessageKey.WarningMessage,
                    $"{caller}: Cone apply took {durationMs:F1} ms, over the hook's {hookWaitBudgetMs} ms wait budget; Git may have proceeded before the cone was applied and transiently expanded the index.");
                this.tracer.RelatedEvent(EventLevel.Warning, $"{caller}_ExceededHookBudget", warning);
            }
        }

        private EventMetadata CreateEventMetadata()
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", nameof(AutoSparseIndexConeManager));
            return metadata;
        }

        private void TraceUnhandled(string caller, Exception exception)
        {
            EventMetadata metadata = this.CreateEventMetadata();
            metadata.Add("Exception", exception.ToString());
            this.tracer.RelatedError(metadata, $"{caller}: Unhandled exception applying cone");
        }
    }
}
