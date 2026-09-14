using GVFS.FunctionalTests.FileSystemRunners;
using GVFS.FunctionalTests.Should;
using GVFS.FunctionalTests.Tools;
using GVFS.Tests.Should;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GVFS.FunctionalTests.Tests.EnlistmentPerFixture
{
    /// <summary>
    /// Proves that GVFS projects a collapsed sparse index correctly when
    /// gvfs.auto-sparse-index is enabled.
    ///
    /// A sparse index stores an out-of-cone directory as a single sparse-directory
    /// tree entry instead of one entry per file. Without expansion support, GVFS
    /// projects only the entries it can read from that collapsed index, so files
    /// deep inside a collapsed directory silently disappear: the folder lists a few
    /// files and a deep file reads as "does not exist" even though it is present in
    /// the sparse index and in the HEAD tree.
    ///
    /// This fixture collapses the GVFS top-level directory to a sparse-directory
    /// entry on the live mount, remounts with the flag on, and asserts that every
    /// file in GVFS\FastFetch enumerates and that GVFS\FastFetch\Program.cs reads
    /// with the correct content, while the on-disk index stays collapsed.
    /// </summary>
    [TestFixture]
    public class SparseIndexProjectionTests : TestsWithEnlistmentPerFixture
    {
        private const string AutoSparseIndexConfig = "gvfs.auto-sparse-index";

        // Read git objects/trees directly instead of through the GVFS projection.
        private const string DisableVfs = "-c core.virtualfilesystem= -c core.hookspath= -c core.quotepath=false";

        // The collapsed directory and the deep file under test.
        private static readonly string FastFetchRelativeDir = Path.Combine("GVFS", "FastFetch");
        private static readonly string ProgramCsRelativePath = Path.Combine("GVFS", "FastFetch", "Program.cs");
        private const string FastFetchTreePath = "HEAD:GVFS/FastFetch";
        private const string ProgramCsTreePath = "HEAD:GVFS/FastFetch/Program.cs";
        private const string ProgramCsIndexPath = "GVFS/FastFetch/Program.cs";

        // The mount previously crashed within a few seconds of coming up. Confirm the
        // mount is still serving well past that window.
        private const int PostMountStabilityWindowMs = 4000;

        // Full ForTests index is ~468 entries; a collapsed index is a handful. Any
        // value at or below this bound proves the on-disk index is still collapsed.
        private const int CollapsedIndexUpperBound = 100;

        private FileSystemRunner fileSystem = new SystemIORunner();

        [TestCase]
        public void CollapsedSparseIndexProjectsEveryFileInCollapsedDirectory()
        {
            string repoRoot = this.Enlistment.RepoRoot;
            string indexPath = Path.Combine(repoRoot, ".git", "index");
            string sparseCheckoutPath = Path.Combine(repoRoot, ".git", "info", "sparse-checkout");
            string projectionCachePath = Path.Combine(this.Enlistment.DotGVFSRoot, "GVFS_projection");
            string fastFetchVirtualPath = this.Enlistment.GetVirtualPathTo(FastFetchRelativeDir);
            string programCsVirtualPath = this.Enlistment.GetVirtualPathTo(ProgramCsRelativePath);

            // The fixture just cloned and mounted this enlistment and nothing has
            // enumerated GVFS\FastFetch, so it is still virtual. That matters: the first
            // enumeration after the collapsed remount must go through the expander, not a
            // stale ProjFS folder placeholder left from an earlier full projection.

            // 1. Read the full (uncollapsed) entry count for comparison.
            int fullIndexCount = ReadIndexEntryCount(indexPath);
            fullIndexCount.ShouldBeAtLeast(
                CollapsedIndexUpperBound + 1,
                $"Expected a full index before collapse, but found only {fullIndexCount} entries.");

            // 2. Capture the expected FastFetch children from the HEAD tree. This reads
            //    trees, not the index, so it does not force expansion.
            HashSet<string> expectedFastFetchNames = this.ReadTreeChildNames(repoRoot, FastFetchTreePath);
            expectedFastFetchNames.Count.ShouldBeAtLeast(
                7,
                "FastFetch should contain many files; a truncated set means the tree read failed.");
            expectedFastFetchNames.ShouldContain(name => name.Equals("Program.cs", StringComparison.Ordinal));

            // 3. Enable the feature flag and the sparse-index prerequisites.
            this.InvokeGit(repoRoot, $"config {AutoSparseIndexConfig} true");
            this.InvokeGit(repoRoot, "config core.sparseCheckout true");
            this.InvokeGit(repoRoot, "config core.sparseCheckoutCone true");
            this.InvokeGit(repoRoot, "config index.sparse true");

            // A minimal cone: keep top-level files, collapse every top-level directory
            // to a single sparse-directory entry.
            File.WriteAllText(sparseCheckoutPath, "/*\n!/*/\n");

            // 4. Collapse the index on the live mount. update-index --force-write-index
            //    rewrites the on-disk index in sparse form without touching the working
            //    tree; the active virtual filesystem keeps skip-worktree set so the
            //    directories actually collapse. The mount re-parses on lock release and
            //    stays Ready at this repo scale.
            ProcessResult collapseResult = GitProcess.InvokeProcess(
                repoRoot,
                "-c core.sparseCheckout=true -c core.sparseCheckoutCone=true -c index.sparse=true update-index --force-write-index");
            collapseResult.ExitCode.ShouldEqual(0, $"update-index --force-write-index failed: {collapseResult.Errors}");

            // 5. Unmount so the index is static, then prove it really collapsed - without
            //    git ls-files (which would force expansion).
            this.Enlistment.UnmountGVFS();

            int collapsedIndexCount = ReadIndexEntryCount(indexPath);
            collapsedIndexCount.ShouldBeAtMost(
                CollapsedIndexUpperBound,
                $"Index did not collapse: {fullIndexCount} -> {collapsedIndexCount} entries.");

            string sparseListing = this.InvokeGit(repoRoot, $"{DisableVfs} ls-files --sparse");
            this.AssertProgramCsIsInsideCollapsedEntry(sparseListing);

            // 6. Drop the cached projection so the mount reparses the collapsed index
            //    instead of replaying the full projection built at clone time.
            if (File.Exists(projectionCachePath))
            {
                File.Delete(projectionCachePath);
            }

            // 7. Mount with the flag on. Time the mount: the projection build expands
            //    the collapsed directory synchronously here.
            Stopwatch mountTimer = Stopwatch.StartNew();
            this.Enlistment.MountGVFS();
            mountTimer.Stop();
            Stopwatch sinceMount = Stopwatch.StartNew();
            this.Enlistment.IsMounted().ShouldBeTrue("GVFS should mount a collapsed sparse index when the flag is on.");

            TestContext.WriteLine(
                $"[sparse-index] Mount + uncached projection expansion took {mountTimer.ElapsedMilliseconds} ms " +
                $"(index {fullIndexCount} -> {collapsedIndexCount} entries; cache replay / increment 4 deferred).");

            // 8. The on-disk index must still be collapsed right after mount. The
            //    projection is built in memory; the index file is not re-expanded.
            ReadIndexEntryCount(indexPath).ShouldBeAtMost(
                CollapsedIndexUpperBound,
                "Mount re-expanded the on-disk index instead of projecting from the collapsed one.");

            // 9. THE PROOF - enumerate the collapsed directory. Every HEAD-tree child
            //    must appear. A truncated set reproduces the silent file loss.
            fastFetchVirtualPath.ShouldBeADirectory(this.fileSystem);
            HashSet<string> actualFastFetchNames = new HashSet<string>(
                new DirectoryInfo(fastFetchVirtualPath).GetFileSystemInfos().Select(info => info.Name),
                StringComparer.Ordinal);

            actualFastFetchNames.SetEquals(expectedFastFetchNames).ShouldBeTrue(
                "FastFetch projection does not match the HEAD tree.\n" +
                $"  expected ({expectedFastFetchNames.Count}): {string.Join(", ", expectedFastFetchNames.OrderBy(n => n))}\n" +
                $"  actual   ({actualFastFetchNames.Count}): {string.Join(", ", actualFastFetchNames.OrderBy(n => n))}\n" +
                $"  missing: {string.Join(", ", expectedFastFetchNames.Except(actualFastFetchNames).OrderBy(n => n))}");

            // 10. THE PROOF - the deep file reads with the correct content.
            programCsVirtualPath.ShouldBeAFile(this.fileSystem);
            string projectedContent = File.ReadAllText(programCsVirtualPath);
            projectedContent.Length.ShouldBeAtLeast(1, "Program.cs projected as an empty file.");

            ProcessResult expectedContentResult = GitProcess.InvokeProcess(repoRoot, $"show {ProgramCsTreePath}");
            if (expectedContentResult.ExitCode == 0)
            {
                NormalizeNewlines(projectedContent).ShouldEqual(
                    NormalizeNewlines(expectedContentResult.Output),
                    "Projected Program.cs content does not match the committed blob.");
            }
            else
            {
                TestContext.WriteLine($"[sparse-index] git show could not read the blob for comparison: {expectedContentResult.Errors}");
            }

            // 11. Reading the file must not have re-expanded the on-disk index.
            ReadIndexEntryCount(indexPath).ShouldBeAtMost(
                CollapsedIndexUpperBound,
                "Reading a file re-expanded the on-disk index; the read did not come from the collapsed projection.");

            // 12. Confirm the mount stays healthy past the window in which it used to
            //     crash, and the file is still readable.
            if (sinceMount.ElapsedMilliseconds < PostMountStabilityWindowMs)
            {
                Thread.Sleep((int)(PostMountStabilityWindowMs - sinceMount.ElapsedMilliseconds));
            }

            this.Enlistment.IsMounted().ShouldBeTrue("Mount did not stay Ready past the crash window.");
            File.ReadAllText(programCsVirtualPath).Length.ShouldBeAtLeast(1, "Program.cs became unreadable after the crash window.");
        }

        private static int ReadIndexEntryCount(string indexPath)
        {
            byte[] header = new byte[12];
            using (FileStream stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int total = 0;
                while (total < header.Length)
                {
                    int read = stream.Read(header, total, header.Length - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }

            bool hasMagic = header[0] == (byte)'D' && header[1] == (byte)'I' && header[2] == (byte)'R' && header[3] == (byte)'C';
            hasMagic.ShouldBeTrue("Index file does not start with the DIRC magic.");

            return (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
        }

        private static string NormalizeNewlines(string value)
        {
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private void AssertProgramCsIsInsideCollapsedEntry(string sparseListing)
        {
            List<string> entries = sparseListing
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            bool hasAncestorSparseDir = entries.Any(
                entry => entry.EndsWith("/", StringComparison.Ordinal) && ProgramCsIndexPath.StartsWith(entry, StringComparison.Ordinal));
            bool hasIndividualEntry = entries.Any(
                entry => entry.Equals(ProgramCsIndexPath, StringComparison.Ordinal));

            hasAncestorSparseDir.ShouldBeTrue(
                $"{ProgramCsIndexPath} is not inside a collapsed sparse-directory entry; there is nothing to expand.");
            hasIndividualEntry.ShouldBeFalse(
                $"{ProgramCsIndexPath} is present as an individual index entry; the directory did not collapse.");
        }

        private HashSet<string> ReadTreeChildNames(string repoRoot, string treePath)
        {
            string output = this.InvokeGit(repoRoot, $"{DisableVfs} ls-tree --name-only {treePath}");
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    names.Add(trimmed);
                }
            }

            return names;
        }

        private string InvokeGit(string repoRoot, string command)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, command);
            result.ExitCode.ShouldEqual(0, $"git {command} failed: {result.Errors}");
            return result.Output;
        }
    }
}
