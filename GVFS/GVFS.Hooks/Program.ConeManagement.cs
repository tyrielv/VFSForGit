using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.NamedPipes;
using GVFS.Common.Tracing;
using System;
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

            NamedPipeMessages.ConeManagement.WidenParameters parameters =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    GetGitCommandSessionId(),
                    normalizedCurrentDirectory,
                    parsed.Pathspecs,
                    parsed.PathspecFromFile,
                    parsed.PathspecFileNul);

            SendConeRequestBounded(
                NamedPipeMessages.ConeManagement.WidenRequest,
                parameters.ToBody(),
                ConeWidenTimeoutMs);
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

            NamedPipeMessages.ConeManagement.NarrowParameters parameters =
                new NamedPipeMessages.ConeManagement.NarrowParameters(GetGitCommandSessionId());

            SendConeRequestBounded(
                NamedPipeMessages.ConeManagement.NarrowRequest,
                parameters.ToBody(),
                ConeNarrowTimeoutMs);
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
    }
}
