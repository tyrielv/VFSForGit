using System;
using System.Collections.Generic;

namespace GVFS.Common
{
    /// <summary>
    /// Decides whether the GVFS pre-command hook must block a <c>git sparse-checkout</c>
    /// invocation and redirect the user to the <c>gvfs sparse-index</c> verb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>gvfs.auto-sparse-index</c> is enabled, GVFS owns
    /// <c>.git/info/sparse-checkout</c> and recomputes the cone automatically. The
    /// mutating <c>git sparse-checkout</c> subcommands fight that ownership: their file
    /// is silently overwritten on the next cone recompute (<c>set</c>, <c>add</c>,
    /// <c>init</c>); <c>reapply</c> expands the sparse index back to full on a live mount,
    /// undoing the feature (109.9 s at os.2020 scale, decisions/0015); <c>disable</c>
    /// turns off git's sparse config without clearing GVFS's, so GVFS re-enables it; and
    /// any of them can write a non-cone pattern that silently disables the sparse index.
    /// Every one of them also fails outright when the mount is down. So this guard blocks
    /// them and names the working recovery verb. The empirical matrix (subcommand x flag x
    /// mount state) is in decisions/0020.
    /// </para>
    /// <para>
    /// The read-only subcommands (<c>list</c>, <c>check-rules</c>) are useful for
    /// diagnostics and never change state, so they are allowed. Any subcommand this guard
    /// does not recognize as mutating is allowed, because blocking a command a user
    /// legitimately needs is worse than not blocking (see decisions/0020).
    /// </para>
    /// <para>
    /// The subcommand is read from <see cref="GitPathspecParser"/> rather than by matching
    /// argv positions, so a leading run of Git global options (<c>git -C &lt;dir&gt;
    /// sparse-checkout set</c>) resolves to the same subcommand.
    /// </para>
    /// <para>
    /// With the flag off the guard blocks nothing, so <c>git sparse-checkout</c> behaves
    /// exactly as it does today for repositories that have not opted in.
    /// </para>
    /// </remarks>
    public static class SparseCheckoutCommandGuard
    {
        /// <summary>The Git command this guard governs.</summary>
        public const string SparseCheckoutCommand = "sparse-checkout";

        // The sparse-checkout subcommands that mutate the sparse-checkout state GVFS owns.
        // Match is exact and case-sensitive: Git's OPT_SUBCOMMAND dispatch does not accept
        // abbreviations ("git sparse-checkout dis" errors "unknown subcommand"), so this
        // guard does not either.
        private static readonly HashSet<string> MutatingSubcommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "set",
            "add",
            "reapply",
            "init",
            "disable",
        };

        /// <summary>
        /// Returns whether the pre-command hook must block this command. When it returns
        /// true, <paramref name="blockMessage"/> is the message to show the user before
        /// aborting.
        /// </summary>
        /// <param name="hookArgs">The full hook argument array (hookArgs[0] is the hook type).</param>
        /// <param name="autoSparseIndexEnabled">
        /// The value of <c>gvfs.auto-sparse-index</c>. When false, nothing is ever blocked.
        /// </param>
        /// <param name="blockMessage">The redirect message when blocked; otherwise null.</param>
        public static bool TryGetBlockMessage(string[] hookArgs, bool autoSparseIndexEnabled, out string blockMessage)
        {
            blockMessage = null;

            ParsedGitCommand parsed = GitPathspecParser.ParseHookArgs(hookArgs);
            if (!string.Equals(parsed.Command, SparseCheckoutCommand, StringComparison.Ordinal))
            {
                return false;
            }

            string subcommand = parsed.Subcommand;
            if (subcommand == null || !MutatingSubcommands.Contains(subcommand))
            {
                // No subcommand (git prints usage), a read-only subcommand (list,
                // check-rules), or one this guard does not recognize as mutating. Allow it.
                return false;
            }

            if (!autoSparseIndexEnabled)
            {
                // The feature is off, so GVFS does not own the sparse-checkout file and
                // git sparse-checkout must behave exactly as it does today.
                return false;
            }

            blockMessage = BuildBlockMessage(subcommand);
            return true;
        }

        private static string BuildBlockMessage(string subcommand)
        {
            // The recovery verb for turning the feature off. Named explicitly for
            // "disable" because "git sparse-checkout disable" does not clear GVFS's
            // sparse-index config (GVFS re-enables it) and fails outright when unmounted.
            // See decisions/0007 and the empirical matrix in decisions/0020.
            const string DisableRecovery =
                "To turn off the sparse index, run 'gvfs sparse-index --disable' from the enlistment.";

            string header =
                "'git " + SparseCheckoutCommand + " " + subcommand + "' is blocked while '"
                + GVFSConstants.GitConfig.AutoSparseIndex + "' is enabled.";

            string reason;
            switch (subcommand)
            {
                case "disable":
                    reason =
                        "GVFS manages the sparse index for this repo. "
                        + "'git sparse-checkout disable' does not clear GVFS's sparse-index config, so GVFS re-enables it, "
                        + "and it fails outright (exit 128) when the mount is down.";
                    break;

                case "reapply":
                    reason =
                        "GVFS applies the cone automatically. On a live mount "
                        + "'git sparse-checkout reapply' expands the sparse index back to full and undoes the collapse "
                        + "(109.9 s at os.2020 scale); when the mount is down it fails.";
                    break;

                default:
                    reason =
                        "GVFS owns .git/info/sparse-checkout and recomputes the cone automatically, "
                        + "so a manual '" + subcommand + "' is overwritten on the next update and can silently disable the sparse index.";
                    break;
            }

            return string.Join(
                Environment.NewLine,
                header,
                reason,
                DisableRecovery,
                "Run 'gvfs sparse-index --status' to inspect the sparse index.");
        }
    }
}
