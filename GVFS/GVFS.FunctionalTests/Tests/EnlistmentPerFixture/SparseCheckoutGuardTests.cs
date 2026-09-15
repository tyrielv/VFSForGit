using GVFS.FunctionalTests.Tools;
using GVFS.Tests.Should;
using NUnit.Framework;
using System;
using System.IO;

namespace GVFS.FunctionalTests.Tests.EnlistmentPerFixture
{
    /// <summary>
    /// Proves the pre-command hook guard for git sparse-checkout. When
    /// gvfs.auto-sparse-index is enabled, GVFS owns .git/info/sparse-checkout, so the
    /// hook blocks the mutating git sparse-checkout subcommands (set, add, reapply,
    /// init, disable) and redirects the user to the gvfs sparse-index verb. Read-only
    /// subcommands (list, check-rules) are not blocked. With the flag off the guard is
    /// silent, so git sparse-checkout behaves exactly as it does for a plain repo.
    ///
    /// The proof is deterministic without depending on git's working-tree side effects:
    /// when the guard blocks, the command never runs, so .git/info/sparse-checkout is
    /// unchanged; when the guard is silent, git's own "set" rewrites the file to cone
    /// format naming the requested directory.
    /// </summary>
    [TestFixture]
    public class SparseCheckoutGuardTests : TestsWithEnlistmentPerFixture
    {
        private const string AutoSparseIndexConfig = "gvfs.auto-sparse-index";
        private const string RedirectVerb = "gvfs sparse-index";
        private const string BlockedMarker = "is blocked while";
        private const string ConeDir = "GVFS";

        [TestCase]
        public void SparseCheckoutSetIsBlockedWithFeatureOnAndAllowedWithFeatureOff()
        {
            string repoRoot = this.Enlistment.RepoRoot;
            string sparseCheckoutPath = Path.Combine(repoRoot, ".git", "info", "sparse-checkout");

            try
            {
                // ── Feature ON: the mutating subcommands are blocked ────────────────
                this.SetAutoSparseIndex(repoRoot, "true");

                string coneBeforeBlockedSet = ReadIfExists(sparseCheckoutPath);

                ProcessResult blockedSet = GitProcess.InvokeProcess(repoRoot, "sparse-checkout set " + ConeDir);
                blockedSet.ExitCode.ShouldNotEqual(0, "git sparse-checkout set must be blocked while the feature is on.");

                // Git redirects a hook's stdout to its own stderr, so the block message
                // lands in Errors; check both streams to be robust.
                string blockedSetText = Combined(blockedSet);
                blockedSetText.ShouldContain(BlockedMarker, RedirectVerb);

                // The command never ran, so GVFS's cone file is untouched.
                ReadIfExists(sparseCheckoutPath).ShouldEqual(
                    coneBeforeBlockedSet,
                    "A blocked 'set' must not modify .git/info/sparse-checkout.");

                // 'disable' is blocked and names the working recovery verb explicitly.
                ProcessResult blockedDisable = GitProcess.InvokeProcess(repoRoot, "sparse-checkout disable");
                blockedDisable.ExitCode.ShouldNotEqual(0, "git sparse-checkout disable must be blocked while the feature is on.");
                Combined(blockedDisable).ShouldContain("gvfs sparse-index --disable");

                // A read-only subcommand is NOT blocked by the guard.
                ProcessResult listResult = GitProcess.InvokeProcess(repoRoot, "sparse-checkout list");
                Combined(listResult).ShouldNotContain(false, BlockedMarker);

                // ── Feature OFF: the guard is silent and git's own set runs ─────────
                this.SetAutoSparseIndex(repoRoot, "false");

                ProcessResult allowedSet = GitProcess.InvokeProcess(repoRoot, "sparse-checkout set " + ConeDir);
                Combined(allowedSet).ShouldNotContain(false, BlockedMarker);
                allowedSet.ExitCode.ShouldEqual(
                    0,
                    $"git sparse-checkout set must succeed while the feature is off. Output: {allowedSet.Output} Errors: {allowedSet.Errors}");

                // git's set actually ran: the cone file changed and now names the directory.
                File.Exists(sparseCheckoutPath).ShouldBeTrue("git sparse-checkout set should have written the sparse-checkout file.");
                string coneAfterAllowedSet = File.ReadAllText(sparseCheckoutPath);
                coneAfterAllowedSet.ShouldContain(ConeDir);
                coneAfterAllowedSet.ShouldNotEqual(
                    coneBeforeBlockedSet ?? string.Empty,
                    "With the guard off, git's own 'set' must rewrite the sparse-checkout file.");
            }
            finally
            {
                GitProcess.InvokeProcess(repoRoot, "config --unset " + AutoSparseIndexConfig);
            }
        }

        private static string ReadIfExists(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static string Combined(ProcessResult result)
        {
            return (result.Output ?? string.Empty) + Environment.NewLine + (result.Errors ?? string.Empty);
        }

        private void SetAutoSparseIndex(string repoRoot, string value)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, $"config {AutoSparseIndexConfig} {value}");
            result.ExitCode.ShouldEqual(0, $"Failed to set {AutoSparseIndexConfig}={value}: {result.Errors}");
        }
    }
}
