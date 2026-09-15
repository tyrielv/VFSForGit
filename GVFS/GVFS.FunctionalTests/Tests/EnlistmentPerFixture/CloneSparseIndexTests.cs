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
    /// End-to-end proof that 'gvfs clone --sparse-index' constructs a sparse index during the
    /// clone, so the enlistment is already sparse before its first mount - with no post-hoc
    /// collapse. See decisions/0015.
    ///
    /// The feature has a hard prerequisite: the configured git must advertise
    /// 'feature: vfs-sparse-index' in 'git version --build-options' (decisions/0018). Without it,
    /// git re-expands the index during checkout and would silently write a FULL index, so clone
    /// fails fast. These tests therefore split by the configured git's capability:
    ///
    ///  - <see cref="CloneWithSparseIndexIsAlreadySparseBeforeFirstMountAndProjectsCompleteTree"/>
    ///    asserts the sparse construction and complete projection. It runs only when the git is
    ///    capable, and <see cref="Assert.Ignore(string)"/>s otherwise (e.g. on stock git today).
    ///  - <see cref="CloneWithSparseIndexFailsFastWhenGitLacksCapability"/> asserts the fail-fast
    ///    behavior. It runs only when the git is NOT capable, and Ignores otherwise. This is the
    ///    test that runs today on stock git.
    ///  - <see cref="CloneWithoutSparseIndexProducesFullIndexAndMountsReady"/> is the control and
    ///    always runs.
    ///
    /// Each sparse/control test provisions a fresh enlistment with 'gvfs clone --no-mount' (so the
    /// on-disk index can be read before the first mount, and the first mount can be timed), then
    /// mounts and asserts the mount projects the complete HEAD tree from the collapsed index.
    ///
    /// The DIRC header is read directly and 'git ls-files --sparse' is used with the virtual file
    /// system disabled - never plain 'git ls-files', which forces the sparse index to expand.
    /// </summary>
    [TestFixture]
    public class CloneSparseIndexTests
    {
        private const string AutoSparseIndexConfig = "gvfs.auto-sparse-index";

        // The exact line 'git version --build-options' emits on a capable git build.
        private const string VfsSparseIndexCapabilityLine = "feature: vfs-sparse-index";

        private const string CapabilityRequiredSkipReason =
            "The configured git does not advertise 'feature: vfs-sparse-index'; clone-time sparse " +
            "construction cannot be validated. Install a git build with VFS for Git sparse-index support.";

        private const string CapabilityPresentSkipReason =
            "The configured git advertises 'feature: vfs-sparse-index', so 'gvfs clone --sparse-index' " +
            "does not fail fast.";

        // Read git objects/trees and the raw index directly instead of through the projection.
        private const string DisableVfs = "-c core.virtualfilesystem= -c core.hookspath= -c core.quotepath=false";

        // A deep file under a top-level directory that the minimal cone collapses. This is the
        // exact file that read as "does not exist" from a truncated projection of a collapsed
        // index in earlier sessions.
        private static readonly string ProgramCsRelativePath = Path.Combine("GVFS", "FastFetch", "Program.cs");
        private const string ProgramCsTreePath = "HEAD:GVFS/FastFetch/Program.cs";
        private const string ProgramCsGitPath = "GVFS/FastFetch/Program.cs";

        // The mount historically crashed within a few seconds of coming up. Confirm the mount is
        // still serving well past that window.
        private const int PostMountStabilityWindowMs = 4000;

        // The full ForTests index is hundreds of entries; a collapsed index is a handful. Any
        // value at or below this bound proves the on-disk index is sparse.
        private const int CollapsedIndexUpperBound = 100;

        private FileSystemRunner fileSystem = new SystemIORunner();

        [TestCase]
        public void CloneWithSparseIndexIsAlreadySparseBeforeFirstMountAndProjectsCompleteTree()
        {
            if (!ConfiguredGitSupportsVfsSparseIndex())
            {
                Assert.Ignore(CapabilityRequiredSkipReason);
            }

            GVFSFunctionalTestEnlistment enlistment = GVFSFunctionalTestEnlistment.CloneNoMount(GVFSTestConfig.PathToGVFS, sparseIndex: true);
            try
            {
                string repoRoot = enlistment.RepoRoot;
                string indexPath = Path.Combine(repoRoot, ".git", "index");
                string programCsVirtualPath = enlistment.GetVirtualPathTo(ProgramCsRelativePath);

                // 1. The clone succeeded (CloneNoMount throws on a non-zero exit) and did not mount.
                enlistment.IsMounted().ShouldBeFalse("gvfs clone --no-mount must not leave the enlistment mounted.");

                // 2. BEFORE the first mount, the on-disk index is genuinely sparse. Read the DIRC
                //    header count directly - never plain 'git ls-files', which forces expansion.
                int clonedIndexCount = ReadIndexEntryCount(indexPath);
                clonedIndexCount.ShouldBeAtMost(
                    CollapsedIndexUpperBound,
                    $"gvfs clone --sparse-index did not construct a sparse index: {clonedIndexCount} entries before first mount.");

                // The sparse-directory entries must be present, and the deep file must be inside a
                // collapsed one, so there is something for the first mount to expand.
                string sparseListing = InvokeGit(repoRoot, $"{DisableVfs} ls-files --sparse");
                AssertProgramCsIsInsideCollapsedEntry(sparseListing);

                // 3. The clone-time config the collapse depends on is set.
                AssertLocalConfig(repoRoot, "core.sparseCheckout", "true");
                AssertLocalConfig(repoRoot, "core.sparseCheckoutCone", "true");
                AssertLocalConfig(repoRoot, "index.sparse", "true");
                AssertLocalConfig(repoRoot, "sparse.expectFilesOutsideOfPatterns", "true");
                AssertLocalConfig(repoRoot, AutoSparseIndexConfig, "true");

                // 4. Time the first mount. Because the enlistment is already sparse, this first
                //    mount exercises the cold (uncached) sparse-directory projection build.
                Stopwatch mountTimer = Stopwatch.StartNew();
                enlistment.MountGVFS();
                mountTimer.Stop();
                Stopwatch sinceMount = Stopwatch.StartNew();
                enlistment.IsMounted().ShouldBeTrue("The first mount of a clone-time sparse enlistment did not reach Ready.");

                TestContext.WriteLine(
                    $"[w15-clone-time] SPARSE first mount (uncached projection build) took {mountTimer.ElapsedMilliseconds} ms " +
                    $"(index {clonedIndexCount} entries before mount).");

                // 5. The on-disk index must still be sparse right after mount; the projection is
                //    built in memory and does not re-expand the index file.
                ReadIndexEntryCount(indexPath).ShouldBeAtMost(
                    CollapsedIndexUpperBound,
                    "The first mount re-expanded the on-disk index instead of projecting from the collapsed one.");

                // 6. PROOF OF COMPLETENESS - the entire projected working tree must equal HEAD. The
                //    expected set comes from the object store (ls-tree), never a pre-mount walk.
                HashSet<string> expectedFiles = ReadHeadFileSet(repoRoot);
                expectedFiles.Count.ShouldBeAtLeast(100, "HEAD should contain many files; a short list means the tree read failed.");
                expectedFiles.ShouldContain(path => path.Equals(ProgramCsGitPath, StringComparison.Ordinal));

                HashSet<string> actualFiles = EnumerateProjectedFiles(repoRoot);
                List<string> missing = expectedFiles.Except(actualFiles).OrderBy(path => path, StringComparer.Ordinal).ToList();
                List<string> extra = actualFiles.Except(expectedFiles).OrderBy(path => path, StringComparer.Ordinal).ToList();

                TestContext.WriteLine($"[w15-clone-time] Projected {actualFiles.Count} files; HEAD has {expectedFiles.Count}.");

                actualFiles.SetEquals(expectedFiles).ShouldBeTrue(
                    "The first mount of a clone-time sparse index did not project the complete HEAD tree.\n" +
                    $"  expected {expectedFiles.Count}, actual {actualFiles.Count}\n" +
                    $"  missing ({missing.Count}): {string.Join(", ", missing.Take(50))}\n" +
                    $"  extra ({extra.Count}): {string.Join(", ", extra.Take(50))}");

                // 7. PROOF OF CONTENT - a file inside a collapsed directory reads with the correct
                //    bytes, not just an enumerable name.
                programCsVirtualPath.ShouldBeAFile(this.fileSystem);
                string projectedContent = File.ReadAllText(programCsVirtualPath);
                projectedContent.Length.ShouldBeAtLeast(1, "Program.cs projected as an empty file.");

                ProcessResult expectedContentResult = GitProcess.InvokeProcess(repoRoot, $"show {ProgramCsTreePath}");
                expectedContentResult.ExitCode.ShouldEqual(0, $"git show could not read the blob for comparison: {expectedContentResult.Errors}");
                NormalizeNewlines(projectedContent).ShouldEqual(
                    NormalizeNewlines(expectedContentResult.Output),
                    "Projected Program.cs content does not match the committed blob.");

                // 8. Reading files must not have re-expanded the on-disk index.
                ReadIndexEntryCount(indexPath).ShouldBeAtMost(
                    CollapsedIndexUpperBound,
                    "Enumerating and reading re-expanded the on-disk index; the projection did not come from the collapsed index.");

                // 9. The mount stays Ready past the window in which it used to crash.
                if (sinceMount.ElapsedMilliseconds < PostMountStabilityWindowMs)
                {
                    Thread.Sleep((int)(PostMountStabilityWindowMs - sinceMount.ElapsedMilliseconds));
                }

                enlistment.IsMounted().ShouldBeTrue("Mount did not stay Ready past the crash window.");
                File.ReadAllText(programCsVirtualPath).Length.ShouldBeAtLeast(1, "Program.cs became unreadable after the crash window.");
            }
            finally
            {
                enlistment.UnmountAndDeleteAll();
            }
        }

        [TestCase]
        public void CloneWithSparseIndexFailsFastWhenGitLacksCapability()
        {
            if (ConfiguredGitSupportsVfsSparseIndex())
            {
                Assert.Ignore(CapabilityPresentSkipReason);
            }

            // The clone must fail (non-zero exit) rather than silently producing a full index. The
            // harness asserts the non-zero exit; here we confirm the failure was the capability gate
            // and that no working enlistment was produced.
            string cloneOutput;
            GVFSFunctionalTestEnlistment enlistment =
                GVFSFunctionalTestEnlistment.CloneSparseIndexExpectingCapabilityFailure(GVFSTestConfig.PathToGVFS, out cloneOutput);
            try
            {
                cloneOutput.ShouldContain(VfsSparseIndexCapabilityLine);

                enlistment.IsMounted().ShouldBeFalse("A clone that failed fast must not leave a mounted enlistment.");

                // The gate fails before checkout, so no index is written - certainly not a full one
                // masquerading as success.
                string indexPath = Path.Combine(enlistment.RepoRoot, ".git", "index");
                File.Exists(indexPath).ShouldBeFalse(
                    "Clone failed fast on the missing git capability, so it must not have written an index.");
            }
            finally
            {
                enlistment.DeleteEnlistment();
            }
        }

        [TestCase]
        public void CloneWithoutSparseIndexProducesFullIndexAndMountsReady()
        {
            GVFSFunctionalTestEnlistment enlistment = GVFSFunctionalTestEnlistment.CloneNoMount(GVFSTestConfig.PathToGVFS, sparseIndex: false);
            try
            {
                string repoRoot = enlistment.RepoRoot;
                string indexPath = Path.Combine(repoRoot, ".git", "index");
                string programCsVirtualPath = enlistment.GetVirtualPathTo(ProgramCsRelativePath);

                enlistment.IsMounted().ShouldBeFalse("gvfs clone --no-mount must not leave the enlistment mounted.");

                // Control: a plain clone is unaffected by the clone-time construction - the index
                // is full, not sparse.
                int clonedIndexCount = ReadIndexEntryCount(indexPath);
                clonedIndexCount.ShouldBeAtLeast(
                    CollapsedIndexUpperBound + 1,
                    $"A plain gvfs clone should produce a full index, but found only {clonedIndexCount} entries.");

                // The sparse-index config keys must not be set by a plain clone.
                AssertLocalConfigUnset(repoRoot, AutoSparseIndexConfig);

                Stopwatch mountTimer = Stopwatch.StartNew();
                enlistment.MountGVFS();
                mountTimer.Stop();
                Stopwatch sinceMount = Stopwatch.StartNew();
                enlistment.IsMounted().ShouldBeTrue("The first mount of a plain clone did not reach Ready.");

                TestContext.WriteLine(
                    $"[w15-clone-time] FULL first mount took {mountTimer.ElapsedMilliseconds} ms " +
                    $"(index {clonedIndexCount} entries before mount).");

                // The deep file still reads and byte-matches its committed blob.
                programCsVirtualPath.ShouldBeAFile(this.fileSystem);
                string projectedContent = File.ReadAllText(programCsVirtualPath);
                ProcessResult expectedContentResult = GitProcess.InvokeProcess(repoRoot, $"show {ProgramCsTreePath}");
                expectedContentResult.ExitCode.ShouldEqual(0, $"git show could not read the blob for comparison: {expectedContentResult.Errors}");
                NormalizeNewlines(projectedContent).ShouldEqual(
                    NormalizeNewlines(expectedContentResult.Output),
                    "Projected Program.cs content does not match the committed blob in the control clone.");

                if (sinceMount.ElapsedMilliseconds < PostMountStabilityWindowMs)
                {
                    Thread.Sleep((int)(PostMountStabilityWindowMs - sinceMount.ElapsedMilliseconds));
                }

                enlistment.IsMounted().ShouldBeTrue("Plain-clone mount did not stay Ready past the crash window.");
            }
            finally
            {
                enlistment.UnmountAndDeleteAll();
            }
        }

        private static bool ConfiguredGitSupportsVfsSparseIndex()
        {
            ProcessResult result = GitProcess.InvokeProcess(Environment.CurrentDirectory, "version --build-options");
            if (result.ExitCode != 0 || string.IsNullOrEmpty(result.Output))
            {
                return false;
            }

            foreach (string line in result.Output.Split('\n'))
            {
                if (line.Trim().Equals(VfsSparseIndexCapabilityLine, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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

        /// <summary>
        /// Recursively enumerates every file in the projected working tree (excluding the real
        /// .git directory), returning repo-root-relative forward-slash paths. Directory
        /// enumeration only - it creates placeholders but does not hydrate file content.
        /// </summary>
        private static HashSet<string> EnumerateProjectedFiles(string repoRoot)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.Ordinal);
            Stack<string> directories = new Stack<string>();
            directories.Push(repoRoot);
            int prefixLength = repoRoot.Length + 1;

            while (directories.Count > 0)
            {
                string directory = directories.Pop();

                foreach (string subDirectory in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(subDirectory);
                    if (name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    directories.Push(subDirectory);
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string relative = file.Substring(prefixLength).Replace(Path.DirectorySeparatorChar, '/');
                    files.Add(relative);
                }
            }

            return files;
        }

        private static void AssertProgramCsIsInsideCollapsedEntry(string sparseListing)
        {
            List<string> entries = sparseListing
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            bool hasAncestorSparseDir = entries.Any(
                entry => entry.EndsWith("/", StringComparison.Ordinal) && ProgramCsGitPath.StartsWith(entry, StringComparison.Ordinal));
            bool hasIndividualEntry = entries.Any(
                entry => entry.Equals(ProgramCsGitPath, StringComparison.Ordinal));

            hasAncestorSparseDir.ShouldBeTrue(
                $"{ProgramCsGitPath} is not inside a collapsed sparse-directory entry; there is nothing to expand.");
            hasIndividualEntry.ShouldBeFalse(
                $"{ProgramCsGitPath} is present as an individual index entry; the directory did not collapse.");
        }

        private static HashSet<string> ReadHeadFileSet(string repoRoot)
        {
            // ls-tree reads the object store, not the index, so it materializes no placeholder.
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, "-c core.quotepath=false ls-tree -r --name-only HEAD");
            result.ExitCode.ShouldEqual(0, $"git ls-tree failed: {result.Errors}");
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in result.Output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    paths.Add(trimmed);
                }
            }

            return paths;
        }

        private static string InvokeGit(string repoRoot, string command)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, command);
            result.ExitCode.ShouldEqual(0, $"git {command} failed: {result.Errors}");
            return result.Output;
        }

        private static void AssertLocalConfig(string repoRoot, string key, string expectedValue)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, $"{DisableVfs} config --local --get {key}");
            result.ExitCode.ShouldEqual(0, $"git config --local --get {key} failed (key not set): {result.Errors}");
            result.Output.Trim().ShouldEqual(expectedValue, $"Config {key} should be {expectedValue}.");
        }

        private static void AssertLocalConfigUnset(string repoRoot, string key)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, $"{DisableVfs} config --local --get {key}");
            result.ExitCode.ShouldNotEqual(0, $"Config {key} should not be set by a plain clone, but git config --get returned '{result.Output.Trim()}'.");
        }
    }
}
