using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.NamedPipes;
using GVFS.Common.Sparse;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GVFS.Hooks
{
    /// <summary>
    /// Partial class for automatic sparse-index cone management.
    /// When gvfs.auto-sparse-index is enabled, the pre-command hook asks the
    /// mount to widen the sparse-index cone to cover the paths a Git command
    /// names, and the post-command hook asks it to narrow the cone back. This
    /// pre-empts the one measured sparse-index expansion trigger: naming an
    /// out-of-cone path.
    /// </summary>
    /// <remarks>
    /// The mount-side handler is owned by the cone-management work. This hook
    /// side sends the requests and never blocks Git on failure: a missed widen
    /// only makes Git transiently expand the index and re-collapse it (the
    /// on-disk index stays sparse), so every failure path proceeds silently.
    /// </remarks>
    public partial class Program
    {
        // Bounded so a fast widen never adds noticeable latency to Git: Task.Wait returns
        // as soon as the mount replies, so these budgets are only spent when the mount is
        // genuinely slow. On timeout the widen is abandoned and Git proceeds (fail-open):
        // Git transiently expands the index and re-collapses it, so the on-disk index stays
        // sparse and the result is correct -- but the optimization did not apply for that
        // command. The mount decouples its O(repo) projection reparse from the reply, so
        // this wait covers only the O(cone) on-disk index write; the widen budget can
        // therefore be generous without penalizing fast widens. The budgets are shared with
        // the mount, which warns when its apply time exceeds the budget so a breach is
        // visible in the mount log rather than silent.
        private const int ConeWidenTimeoutMs = GVFSConstants.ConeManagement.WidenHookWaitBudgetMs;
        private const int ConeNarrowTimeoutMs = GVFSConstants.ConeManagement.NarrowHookWaitBudgetMs;
        private const int ConeConnectTimeoutMs = GVFSConstants.ConeManagement.ConnectTimeoutMs;

        private static bool ConfigurationAllowsAutoSparseIndex()
        {
            return LibGit2Repo.GetConfigBoolOrDefault(
                NullTracer.Instance,
                normalizedCurrentDirectory,
                GVFSConstants.GitConfig.AutoSparseIndex,
                GVFSConstants.GitConfig.AutoSparseIndexDefault);
        }

        /// <summary>
        /// If the command names any working-tree paths, asks the mount to widen
        /// the sparse-index cone to cover them before Git runs. Non-blocking on
        /// failure: any error, timeout, or unmounted state proceeds silently.
        /// </summary>
        private static void TrySendConeWiden(string[] args)
        {
            ParsedGitCommand parsed = GitPathspecParser.ParseHookArgs(args);
            if (!parsed.NamesPaths)
            {
                return;
            }

            // Read the flag only after the parse proves the command names paths,
            // so the config read is skipped for the common commands that never
            // widen the cone (status, fetch, log, commit without pathspecs, ...).
            if (!ConfigurationAllowsAutoSparseIndex())
            {
                return;
            }

            // Skip the request entirely when it cannot help. This avoids the named-pipe
            // round-trip on commands where widening would either do nothing (the paths are
            // already in the cone) or make things worse (stash). The saved round-trip is
            // small when the mount is idle, but at large-repo scale a mount busy with a
            // prior widen's background reparse can delay even a no-op reply up to the wait
            // budget, so skipping locally decouples fast commands from a busy mount.
            if (TryDetermineWidenUnnecessary(parsed, out string skipReason))
            {
                TraceConeDiagnostic($"widen skipped: {skipReason}");
                return;
            }

            NamedPipeMessages.ConeManagement.WidenParameters parameters =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    GetGitCommandSessionId(),
                    normalizedCurrentDirectory,
                    parsed.Pathspecs,
                    parsed.PathspecFromFile,
                    parsed.PathspecFileNul);

            bool applied = SendConeRequestBounded(
                NamedPipeMessages.ConeManagement.WidenRequest,
                parameters.ToBody(),
                ConeWidenTimeoutMs);

            if (!applied)
            {
                // The cone was not confirmed applied before Git ran. This is not a skip:
                // Git will transiently expand and re-collapse the index. The mount records
                // the matching over-budget warning in its log; this trace makes the hook
                // side visible too, and distinct from a deliberate skip above.
                TraceConeDiagnostic("widen not confirmed within budget");
            }
        }

        /// <summary>
        /// If the command named any working-tree paths, asks the mount to narrow
        /// the sparse-index cone back. Keyed by the Git session id so the mount
        /// undoes the matching widen. Non-blocking on failure.
        /// </summary>
        private static void TrySendConeNarrow(string[] args)
        {
            ParsedGitCommand parsed = GitPathspecParser.ParseHookArgs(args);
            if (!parsed.NamesPaths)
            {
                return;
            }

            if (!ConfigurationAllowsAutoSparseIndex())
            {
                return;
            }

            // Stash never widens (see TryDetermineWidenUnnecessary), so it registered no
            // transient session for this command; there is nothing to narrow. The check is
            // command-based, so both hooks reach it identically. The in-cone exclusion is
            // deliberately NOT mirrored here: the cone may have changed between the pre- and
            // post-command hooks, so a narrow decision cannot be re-derived from the current
            // file without risking a leaked transient session. A narrow after a widen that
            // was skipped for in-cone paths is already a cheap session-keyed no-op.
            if (IsStashCommand(parsed))
            {
                TraceConeDiagnostic("narrow skipped: stash never widens");
                return;
            }

            NamedPipeMessages.ConeManagement.NarrowParameters parameters =
                new NamedPipeMessages.ConeManagement.NarrowParameters(GetGitCommandSessionId());

            SendConeRequestBounded(
                NamedPipeMessages.ConeManagement.NarrowRequest,
                parameters.ToBody(),
                ConeNarrowTimeoutMs);
        }

        /// <summary>
        /// Decide, without any IPC, whether the widen request can be skipped because it
        /// cannot help. Two independent exclusions:
        /// <list type="number">
        /// <item>stash expands the index unconditionally regardless of the cone, and
        /// widening it is measured net-negative, so never widen for stash;</item>
        /// <item>a command that names only paths already covered by the on-disk cone would
        /// make the mount rebuild an identical cone and do nothing, so skip it.</item>
        /// </list>
        /// Both are conservative: any uncertainty returns false and defers to the mount,
        /// which then behaves exactly as before.
        /// </summary>
        private static bool TryDetermineWidenUnnecessary(ParsedGitCommand parsed, out string skipReason)
        {
            skipReason = null;

            if (IsStashCommand(parsed))
            {
                // builtin/stash.c calls ensure_full_index unconditionally when a pathspec
                // is present: it never consults the cone, so widening cannot prevent the
                // expansion, and un-hiding the skip-worktree change only gives Git more work.
                skipReason = "stash expands unconditionally; widening cannot help";
                return true;
            }

            if (AllNamedPathsAlreadyInCone(parsed))
            {
                skipReason = "all named paths already in the cone";
                return true;
            }

            return false;
        }

        private static bool IsStashCommand(ParsedGitCommand parsed)
        {
            return string.Equals(parsed.Command, "stash", StringComparison.Ordinal);
        }

        /// <summary>
        /// Return true only when the hook can prove, from the on-disk cone file alone, that
        /// every path the command names is already covered -- so the mount would rewrite an
        /// identical cone file and do nothing. Mirrors the mount's own resolve-and-compare
        /// exactly (same <see cref="ConePathspecResolver"/> and cone semantics), so a skip
        /// is observably identical to sending the widen and getting a no-op reply. Any case
        /// the hook cannot decide locally returns false and defers to the mount.
        /// </summary>
        private static bool AllNamedPathsAlreadyInCone(ParsedGitCommand parsed)
        {
            // The full path set must be knowable from the command line. A pathspec file (the
            // paths live in a file or on stdin) or a -C / --git-dir / --work-tree override
            // changes what the mount resolves in ways this local check does not reproduce, so
            // defer those to the mount. A linked worktree's cone file is not resolved here.
            if (parsed.Failed ||
                !string.IsNullOrEmpty(parsed.PathspecFromFile) ||
                !string.IsNullOrEmpty(parsed.ChangeDirectory) ||
                !string.IsNullOrEmpty(parsed.GitDir) ||
                !string.IsNullOrEmpty(parsed.WorkTree) ||
                runningInWorktree)
            {
                return false;
            }

            try
            {
                string workingDirectoryRoot = Path.Combine(enlistmentRoot, GVFSConstants.WorkingDirectoryRootName);
                string sparseCheckoutPath = Path.Combine(workingDirectoryRoot, GVFSConstants.DotGit.Info.SparseCheckoutPath);

                if (!File.Exists(sparseCheckoutPath))
                {
                    // No cone file means the mount would write one, so this is not a no-op.
                    return false;
                }

                string content = File.ReadAllText(sparseCheckoutPath);
                if (!ConeCoverage.TryParseConeFile(content, out ConePatternSet cone))
                {
                    // Not a cone file this hook can reason about (legacy or hand-edited).
                    return false;
                }

                IReadOnlyList<string> resolvedPaths = ConePathspecResolver.ResolveToGitPaths(
                    normalizedCurrentDirectory,
                    workingDirectoryRoot,
                    parsed.Pathspecs);

                return ConeCoverage.AreAllPathsCovered(cone, resolvedPaths);
            }
            catch (Exception)
            {
                // Any failure to read or parse locally is non-fatal: defer to the mount.
                return false;
            }
        }

        /// <summary>
        /// Sends a cone-management request and waits, bounded, for the mount's
        /// reply. Returns whether the mount confirmed the cone was applied within the
        /// budget. Any failure (no mount, timeout, exception, failure result) returns
        /// false and is swallowed by the caller: cone management is best-effort and must
        /// never block Git. The outcome is captured rather than discarded; when it is
        /// false the cone was not confirmed applied before Git ran, and the mount records
        /// that breach as a warning in its log (it knows the same budget and its own
        /// apply duration).
        /// </summary>
        private static bool SendConeRequestBounded(string header, string body, int timeoutMilliseconds)
        {
            try
            {
                Task<bool> task = Task.Run(() =>
                {
                    using (NamedPipeClient pipeClient = new NamedPipeClient(enlistmentPipename))
                    {
                        if (!pipeClient.Connect(timeoutMilliseconds: ConeConnectTimeoutMs))
                        {
                            return false;
                        }

                        pipeClient.SendRequest(new NamedPipeMessages.Message(header, body));
                        NamedPipeMessages.Message response = pipeClient.ReadResponse();
                        return response.Header == NamedPipeMessages.ConeManagement.SuccessResult;
                    }
                });

                // Hard outer bound. If the mount stalls, abandon the orphaned task -- the
                // hook process exits immediately after this returns. Capture the outcome,
                // matching the cached-hydration-status precedent (Program.cs): the task must
                // complete within the budget, run to completion (not fault or cancel), and
                // report a success reply for the cone to be confirmed applied.
                return task.Wait(timeoutMilliseconds)
                    && task.Status == TaskStatus.RanToCompletion
                    && task.Result;
            }
            catch (Exception)
            {
                // Best-effort: never block or fail Git for cone management.
                return false;
            }
        }

        /// <summary>
        /// Emit a one-line cone-management diagnostic to stderr, but only when the user has
        /// turned on git's stderr trace (GIT_TRACE=1, 2, or true). This makes a deliberate
        /// skip distinguishable from a failed or timed-out widen without adding any IPC or
        /// tracer setup to the fire-and-forget hook, and costs nothing when tracing is off.
        /// A GIT_TRACE value that names a file or fd is left to git; the hook does not write
        /// into git's trace target.
        /// </summary>
        private static void TraceConeDiagnostic(string message)
        {
            if (!GitStderrTraceEnabled())
            {
                return;
            }

            try
            {
                Console.Error.WriteLine($"gvfs cone-management: {message}");
            }
            catch (IOException)
            {
                // A diagnostic must never disrupt Git; drop it if stderr is unavailable.
            }
        }

        private static bool GitStderrTraceEnabled()
        {
            string value = Environment.GetEnvironmentVariable("GIT_TRACE");
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // Match git's stderr trace targets only: "1", "2", or "true". A path or other fd
            // value means git writes elsewhere, so the hook stays silent to avoid polluting
            // an unrelated stream.
            return value.Equals("1", StringComparison.Ordinal)
                || value.Equals("2", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
