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
    /// only inside the child git) -> projection reparse (which takes the projection
    /// read/write lock on the parse thread inside <see cref="FileSystemCallbacks.ForceIndexProjectionUpdate"/>).
    /// The handler deliberately does NOT take the GVFS lock: the pre-command hook already
    /// holds it for the commands that widen (add, rm, restore, stash, reset -- &lt;path&gt;),
    /// so acquiring it here would deadlock the very command that is waiting on the reply.
    /// The collapse git process disables every GVFS hook, so it never re-enters the mount
    /// pipe. See decisions/0019.
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
        }

        /// <summary>
        /// Widen the cone to cover the paths a Git command names, then reply. The hook
        /// blocks on this, so latency is user-visible: it is paid on every command that
        /// names an out-of-cone path.
        /// </summary>
        public bool TryWiden(NamedPipeMessages.ConeManagement.WidenParameters parameters, out string error)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            IReadOnlyList<string> requestedPaths = this.ResolveRequestedPaths(parameters);

            this.coneLock.Wait();
            try
            {
                this.transientState.AddPaths(parameters.SessionId, requestedPaths);
                return this.ApplyCurrentCone(nameof(this.TryWiden), requestedPaths.Count, out error);
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

                return this.ApplyCurrentCone(nameof(this.TryNarrow), 0, out error);
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

        private bool ApplyCurrentCone(string caller, int requestedPathCount, out string error)
        {
            error = null;

            Stopwatch stopwatch = Stopwatch.StartNew();

            List<string> allPaths = new List<string>(this.fileSystemCallbacks.GetAllModifiedPaths());
            allPaths.AddRange(this.transientState.GetAllTransientPaths());

            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(allPaths);
            string newContent = ConeFileWriter.Serialize(cone);

            if (this.ConeContentUnchanged(newContent))
            {
                // The cone already covers these paths, so there is nothing to write and no
                // projection rebuild to trigger. Current behaviour is byte-for-byte unchanged.
                this.TraceApplied(caller, requestedPathCount, changed: false, stopwatch);
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

            GitProcess.Result collapseResult = this.git.ForceCollapseSparseIndex();
            if (collapseResult.ExitCodeIsFailure)
            {
                error = collapseResult.Errors;
                this.RollBack(caller, backupPath, collapseResult);
                return false;
            }

            // The collapse rewrote the index with hooks disabled, so the mount received no
            // PostIndexChanged notification. Reparse the projection so the GVFS view matches
            // the new on-disk cone before replying. The modified-paths set is unchanged.
            this.fileSystemCallbacks.ForceIndexProjectionUpdate(invalidateProjection: true, invalidateModifiedPaths: false);

            this.TraceApplied(caller, requestedPathCount, changed: true, stopwatch);
            return true;
        }

        private bool ConeContentUnchanged(string newContent)
        {
            if (!this.fileSystem.FileExists(this.sparseCheckoutPath))
            {
                return false;
            }

            try
            {
                return string.Equals(this.fileSystem.ReadAllText(this.sparseCheckoutPath), newContent, StringComparison.Ordinal);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // If the current content cannot be read, fall through to a write; a
                // redundant write is safe.
                return false;
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

        private void TraceApplied(string caller, int requestedPathCount, bool changed, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            EventMetadata metadata = this.CreateEventMetadata();
            metadata.Add("RequestedPathCount", requestedPathCount);
            metadata.Add("TransientSessionCount", this.transientState.SessionCount);
            metadata.Add("ConeChanged", changed);
            metadata.Add("DurationMs", stopwatch.Elapsed.TotalMilliseconds);
            metadata.Add(TracingConstants.MessageKey.InfoMessage, $"{caller}: Applied cone");
            this.tracer.RelatedEvent(EventLevel.Informational, $"{caller}_Applied", metadata);
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
