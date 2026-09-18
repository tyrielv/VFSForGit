using GVFS.Common;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common
{
    [TestFixture]
    public class SparseCheckoutCommandGuardTests
    {
        // ── Mutating subcommands are blocked when the feature is ON ─────

        [TestCase("set")]
        [TestCase("add")]
        [TestCase("reapply")]
        [TestCase("init")]
        [TestCase("disable")]
        public void MutatingSubcommand_Blocked_WhenFeatureOn(string subcommand)
        {
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", subcommand),
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeTrue();
            message.ShouldNotBeNull();
            message.ShouldContain("gvfs sparse-index");
        }

        [TestCase]
        public void DisableSubcommand_NamesDisableRecoveryVerb()
        {
            SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", "disable"),
                autoSparseIndexEnabled: true,
                out string message);

            message.ShouldContain("gvfs sparse-index --disable");
        }

        [TestCase]
        public void ReapplySubcommand_ExplainsLiveMountExpansion()
        {
            SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", "reapply"),
                autoSparseIndexEnabled: true,
                out string message);

            message.ToLowerInvariant().ShouldContain("mount");
            message.ToLowerInvariant().ShouldContain("expands");
        }

        // ── Read-only and unknown subcommands are always allowed ────────

        [TestCase("list")]
        [TestCase("check-rules")]
        [TestCase("clean")]
        public void ReadOnlyOrCleanSubcommand_Allowed_WhenFeatureOn(string subcommand)
        {
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", subcommand),
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        [TestCase]
        public void UnknownSubcommand_Allowed_WhenFeatureOn()
        {
            // If unsure whether a subcommand is safe, allow it (decisions/0020).
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", "future-verb"),
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        [TestCase]
        public void NoSubcommand_Allowed_WhenFeatureOn()
        {
            // "git sparse-checkout" alone prints usage; nothing to block.
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                new[] { "pre-command", "sparse-checkout" },
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        // ── Abbreviations are NOT blocked (git does not abbreviate) ─────

        [TestCase("se")]
        [TestCase("dis")]
        [TestCase("reapp")]
        public void AbbreviatedSubcommand_NotBlocked(string abbreviation)
        {
            // Git's OPT_SUBCOMMAND dispatch rejects abbreviations ("unknown subcommand"),
            // so the guard treats them as unknown and allows them through to git.
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", abbreviation),
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        // ── Feature OFF: nothing is ever blocked ────────────────────────

        [TestCase("set")]
        [TestCase("add")]
        [TestCase("reapply")]
        [TestCase("init")]
        [TestCase("disable")]
        [TestCase("list")]
        [TestCase("check-rules")]
        [TestCase("clean")]
        public void AnySubcommand_Allowed_WhenFeatureOff(string subcommand)
        {
            // With gvfs.auto-sparse-index off, git sparse-checkout must behave exactly as
            // today for plain sparse-checkout / gvfs sparse users.
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                Hook("sparse-checkout", subcommand),
                autoSparseIndexEnabled: false,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        // ── A different git command is never touched ────────────────────

        [TestCase]
        public void NonSparseCheckoutCommand_NotBlocked()
        {
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                new[] { "pre-command", "add", "set" },
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        // ── Global options before the subcommand still resolve ──────────

        [TestCase]
        public void GlobalOptionsBeforeSubcommand_StillBlocked()
        {
            // git -C <dir> sparse-checkout set
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                new[] { "pre-command", "-C", "sub/dir", "sparse-checkout", "set", "a/" },
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeTrue();
            message.ShouldNotBeNull();
        }

        // ── A token after "--" is not a subcommand ──────────────────────

        [TestCase]
        public void SubcommandAfterDashDash_NotBlocked()
        {
            bool blocked = SparseCheckoutCommandGuard.TryGetBlockMessage(
                new[] { "pre-command", "sparse-checkout", "--", "set" },
                autoSparseIndexEnabled: true,
                out string message);

            blocked.ShouldBeFalse();
            message.ShouldBeNull();
        }

        private static string[] Hook(string command, string subcommand)
        {
            return new[] { "pre-command", command, subcommand };
        }
    }
}
