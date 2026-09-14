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
        // Bounded so the hook never adds noticeable latency to Git. On timeout
        // the widen is abandoned and Git proceeds; the result is only a
        // transient in-memory index expansion, which is benign.
        private const int ConeWidenTimeoutMs = 250;
        private const int ConeNarrowTimeoutMs = 100;
        private const int ConeConnectTimeoutMs = 50;

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
        /// reply. Any failure (no mount, timeout, exception, failure result) is
        /// swallowed: cone management is best-effort and must never block Git.
        /// </summary>
        private static void SendConeRequestBounded(string header, string body, int timeoutMilliseconds)
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

                // Hard outer bound. If the mount stalls, abandon the orphaned
                // task — the hook process exits immediately after this returns.
                task.Wait(timeoutMilliseconds);
            }
            catch (Exception)
            {
                // Best-effort: never block or fail Git for cone management.
            }
        }
    }
}
