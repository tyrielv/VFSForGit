using GVFS.Common.NamedPipes;
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
using GVFSPlatform = GVFS.Common.GVFSPlatform;

namespace GVFS.FunctionalTests.Tests.EnlistmentPerFixture
{
    /// <summary>
    /// Proves the mount-side handler for automatic sparse-index cone widening. When
    /// gvfs.auto-sparse-index is enabled, the pre-command hook asks the mount to widen
    /// the cone to cover the out-of-cone paths a Git command names, before Git runs, so
    /// Git does a partial expand of just those directories instead of fully expanding
    /// the collapsed index.
    ///
    /// This fixture collapses the top-level directories to sparse-directory entries on
    /// the live mount, then exercises the handler two ways:
    ///   * directly over the named pipe (ConeWiden / ConeNarrow), which measures the
    ///     widen round-trip latency the hook pays and proves the cone file is rewritten
    ///     and reverted; and
    ///   * end to end through the pre-command hook, by running a Git command that names
    ///     an out-of-cone path and proving the command succeeds and the on-disk index
    ///     never fully expands.
    /// </summary>
    [TestFixture]
    public class AutoSparseIndexConeWideningTests : TestsWithEnlistmentPerFixture
    {
        private const string AutoSparseIndexConfig = "gvfs.auto-sparse-index";
        private const string DisableVfs = "-c core.virtualfilesystem= -c core.hookspath= -c core.quotepath=false";

        // An out-of-cone file and its directory. The minimal cone below collapses every
        // top-level directory, so GVFS/FastFetch starts inside a sparse-directory entry.
        private const string OutOfConeFileGitPath = "GVFS/FastFetch/Program.cs";
        private const string OutOfConeDirPattern = "/GVFS/FastFetch/";
        private const string ProbeFileGitPath = "GVFS/FastFetch/w18probe.txt";

        // The full ForTests index is hundreds of entries; a collapsed index is a handful.
        private const int CollapsedIndexUpperBound = 100;

        // How many cold widen round-trips to time. Each is reset with a narrow first, so
        // every sample rewrites the cone rather than short-circuiting as a no-op.
        private const int WidenLatencySamples = 5;

        private const int PipeConnectTimeoutMs = 3000;
        private const int PipeRequestTimeoutMs = 30000;

        // W4's sparse-index git build. Stock git (2.55.0.vfs.0.8) fully expands the sparse
        // index when a command names an out-of-cone path; this build does the partial
        // expansion the feature depends on. It is a git source-tree build, so it needs the
        // git-sdk MSYS2 runtime on PATH to load its DLLs.
        private const string SparseIndexGitExe = @"C:\git-sdk-64\usr\src\git-sparse-index\git.exe";
        private const string SparseIndexGitSdkPath = @"C:\git-sdk-64\mingw64\bin;C:\git-sdk-64\usr\bin";

        private FileSystemRunner fileSystem = new SystemIORunner();

        [TestCase]
        public void WidenHandlerCoversOutOfConePathsWithoutFullyExpandingTheIndex()
        {
            string repoRoot = this.Enlistment.RepoRoot;
            string indexPath = Path.Combine(repoRoot, ".git", "index");
            string sparseCheckoutPath = Path.Combine(repoRoot, ".git", "info", "sparse-checkout");
            string pipeName = GVFSPlatform.Instance.GetNamedPipeName(this.Enlistment.EnlistmentRoot);

            // 1. Enable the feature and the sparse-index prerequisites, then collapse the
            //    top-level directories on the live mount.
            this.InvokeGit(repoRoot, $"config {AutoSparseIndexConfig} true");
            this.InvokeGit(repoRoot, "config core.sparseCheckout true");
            this.InvokeGit(repoRoot, "config core.sparseCheckoutCone true");
            this.InvokeGit(repoRoot, "config index.sparse true");

            int fullIndexCount = ReadIndexEntryCount(indexPath);
            fullIndexCount.ShouldBeAtLeast(
                CollapsedIndexUpperBound + 1,
                $"Expected a full index before collapse, but found only {fullIndexCount} entries.");

            File.WriteAllText(sparseCheckoutPath, "/*\n!/*/\n");
            ProcessResult collapseResult = GitProcess.InvokeProcess(
                repoRoot,
                "-c core.sparseCheckout=true -c core.sparseCheckoutCone=true -c index.sparse=true update-index --force-write-index");
            collapseResult.ExitCode.ShouldEqual(0, $"update-index --force-write-index failed: {collapseResult.Errors}");

            // Unmount so the on-disk index is static, then prove it collapsed. Unmounted,
            // git ls-files --sparse reflects the on-disk sparse form; mounted, disabling
            // core.virtualfilesystem makes Git re-run clear_skip_worktree_from_present_files
            // and expand the sparse index in memory for the listing.
            this.Enlistment.UnmountGVFS();

            int collapsedIndexCount = ReadIndexEntryCount(indexPath);
            collapsedIndexCount.ShouldBeAtMost(
                CollapsedIndexUpperBound,
                $"Index did not collapse: {fullIndexCount} -> {collapsedIndexCount} entries.");

            string sparseListing = this.InvokeGit(repoRoot, $"{DisableVfs} ls-files --sparse");
            TestContext.WriteLine($"[cone-widen] collapsed sparse listing (unmounted):\n{sparseListing}");
            this.AssertOutOfConeFileIsCollapsed(sparseListing);

            // Drop the cached projection so the mount reparses the collapsed index instead
            // of replaying the full projection built at clone time.
            string projectionCachePath = Path.Combine(this.Enlistment.DotGVFSRoot, "GVFS_projection");
            if (File.Exists(projectionCachePath))
            {
                File.Delete(projectionCachePath);
            }

            // Remount with the flag on. The mount reads gvfs.auto-sparse-index at startup,
            // so only a mount taken with the flag set creates the cone-management handler.
            this.Enlistment.MountGVFS();
            this.Enlistment.IsMounted().ShouldBeTrue("GVFS should remount with auto-sparse-index enabled.");

            // 2. Measure the widen round-trip latency. The hook blocks on this on every
            //    command that names an out-of-cone path, so it is user-visible. Each
            //    sample is reset with a narrow so it rewrites the cone (a cold widen).
            //
            //    Hold the GVFS lock across the direct-pipe widen/narrow calls, exactly as
            //    the pre-command hook holds it for the Git command whose widen this
            //    simulates. The mount's virtualizer blocks the in-place collapse's
            //    index.lock -> index rename unless a Git command holds the GVFS lock
            //    (WindowsFileSystemVirtualizer.NotifyPreRenameHandler). The end-to-end
            //    step below instead relies on the real hook to hold the lock, so this
            //    test-held lock is released before it runs.
            ManualResetEventSlim lockHolder = GitHelpers.AcquireGVFSLock(this.Enlistment, out _, resetTimeout: Timeout.Infinite);
            try
            {
                List<double> coldSamples = new List<double>();
                for (int i = 0; i < WidenLatencySamples; i++)
                {
                    ConeResult narrowReset = this.SendNarrow(pipeName, "w18-measure");
                    narrowReset.Header.ShouldEqual(NamedPipeMessages.ConeManagement.SuccessResult, $"Reset narrow failed: {narrowReset.Header}");

                    ConeResult widen = this.SendWiden(pipeName, "w18-measure", repoRoot, OutOfConeFileGitPath);
                    widen.Header.ShouldEqual(NamedPipeMessages.ConeManagement.SuccessResult, $"Widen failed: {widen.Header}");
                    coldSamples.Add(widen.ElapsedMilliseconds);
                }

                coldSamples.Sort();
                double coldMedian = coldSamples[coldSamples.Count / 2];

                // A repeat widen for the same, already-covered path short-circuits: the cone is
                // unchanged, so no file is written and no projection rebuild occurs.
                ConeResult warmWiden = this.SendWiden(pipeName, "w18-measure", repoRoot, OutOfConeFileGitPath);
                warmWiden.Header.ShouldEqual(NamedPipeMessages.ConeManagement.SuccessResult, $"Warm widen failed: {warmWiden.Header}");

                TestContext.WriteLine(
                    $"[cone-widen] round-trip latency (ForTests scale): " +
                    $"cold min {coldSamples.First():F1} ms, median {coldMedian:F1} ms, max {coldSamples.Last():F1} ms " +
                    $"over {WidenLatencySamples} samples; warm no-op {warmWiden.ElapsedMilliseconds:F1} ms. " +
                    $"Index {fullIndexCount} -> collapsed {collapsedIndexCount}.");

                // 3. The widen rewrote the cone file to include the out-of-cone directory, and
                //    reconciling the index in place did not fully expand it.
                string widenedCone = File.ReadAllText(sparseCheckoutPath);
                widenedCone.Contains(OutOfConeDirPattern).ShouldBeTrue(
                    $"Cone was not widened to include {OutOfConeDirPattern}. Cone:\n{widenedCone}");
                ReadIndexEntryCount(indexPath).ShouldBeAtMost(
                    CollapsedIndexUpperBound,
                    "Widen reconcile fully expanded the on-disk index instead of keeping it collapsed.");

                // 4. Narrow drops the transient additions and rewrites the cone back.
                ConeResult narrow = this.SendNarrow(pipeName, "w18-measure");
                narrow.Header.ShouldEqual(NamedPipeMessages.ConeManagement.SuccessResult, $"Narrow failed: {narrow.Header}");

                string narrowedCone = File.ReadAllText(sparseCheckoutPath);
                narrowedCone.Contains(OutOfConeDirPattern).ShouldBeFalse(
                    $"Cone still includes {OutOfConeDirPattern} after narrow. Cone:\n{narrowedCone}");
            }
            finally
            {
                lockHolder.Set();
            }

            // 5. End to end through the pre-command hook: a Git command that names an
            //    out-of-cone path is widened by the hook before Git runs, so the command
            //    succeeds and the on-disk index does not fully expand. This needs W4's
            //    sparse-index git build: stock git fully expands the index on add of an
            //    out-of-cone path even when the cone already covers it.
            if (!File.Exists(SparseIndexGitExe))
            {
                Assert.Ignore($"Sparse-index git build not found at {SparseIndexGitExe}; skipping the end-to-end partial-expansion proof. The direct-pipe widen/narrow proof above still ran.");
            }

            string probeFullPath = Path.Combine(repoRoot, "GVFS", "FastFetch", "w18probe.txt");
            File.WriteAllText(probeFullPath, "w18 cone-handler probe\n");

            try
            {
                ProcessResult addResult = this.InvokeSparseIndexGit(repoRoot, $"add {ProbeFileGitPath}");
                addResult.ExitCode.ShouldEqual(0, $"git add of an out-of-cone path failed: {addResult.Errors}");

                // The hook's widen reached the handler, so the cone now covers the path and
                // git add actually staged the new file.
                string hookWidenedCone = File.ReadAllText(sparseCheckoutPath);
                hookWidenedCone.Contains(OutOfConeDirPattern).ShouldBeTrue(
                    $"The pre-command hook did not widen the cone to include {OutOfConeDirPattern}. Cone:\n{hookWidenedCone}");

                // Verify the probe is staged. The mount is up, so run Git normally (VFS on);
                // disabling core.virtualfilesystem is only needed for the on-disk ls-files
                // check in step 1, and re-parsing the widened cone with VFS off is not the
                // path a user exercises. diff --cached reads the index, so any in-core sparse
                // expansion it does is transient and never rewrites the on-disk index.
                string staged = this.InvokeGit(repoRoot, "diff --cached --name-only");
                staged.Split('\n').Select(line => line.Trim()).ShouldContain(path => path.Equals(ProbeFileGitPath, StringComparison.Ordinal));

                // The on-disk index expanded only the widened directory, not the whole tree.
                // A full expand is the entire ForTests index; a partial expand is the handful
                // of top-level entries plus the one widened directory's files.
                int finalIndexCount = ReadIndexEntryCount(indexPath);
                TestContext.WriteLine(
                    $"[cone-widen] end-to-end git add named an out-of-cone path; index {collapsedIndexCount} -> {finalIndexCount} " +
                    $"entries (full would be {fullIndexCount}). Partial expansion, not full.");
                finalIndexCount.ShouldBeAtMost(
                    fullIndexCount / 2,
                    $"Naming an out-of-cone path expanded the index to {finalIndexCount} of {fullIndexCount} entries; " +
                    $"widening did not keep the expansion partial.");
            }
            finally
            {
                this.InvokeSparseIndexGit(repoRoot, "reset");
                if (File.Exists(probeFullPath))
                {
                    File.Delete(probeFullPath);
                }
            }
        }

        private void AssertOutOfConeFileIsCollapsed(string sparseListing)
        {
            List<string> entries = sparseListing
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            bool hasAncestorSparseDir = entries.Any(
                entry => entry.EndsWith("/", StringComparison.Ordinal) && OutOfConeFileGitPath.StartsWith(entry, StringComparison.Ordinal));
            bool hasIndividualEntry = entries.Any(
                entry => entry.Equals(OutOfConeFileGitPath, StringComparison.Ordinal));

            hasAncestorSparseDir.ShouldBeTrue(
                $"{OutOfConeFileGitPath} is not inside a collapsed sparse-directory entry; there is nothing to widen.");
            hasIndividualEntry.ShouldBeFalse(
                $"{OutOfConeFileGitPath} is present as an individual index entry; the directory did not collapse.");
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

        private ConeResult SendWiden(string pipeName, string sessionId, string repoRoot, params string[] pathspecs)
        {
            NamedPipeMessages.ConeManagement.WidenParameters parameters =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    sessionId,
                    repoRoot,
                    pathspecs.ToList(),
                    null,
                    false);

            return this.SendConeRequest(pipeName, NamedPipeMessages.ConeManagement.WidenRequest, parameters.ToBody());
        }

        private ConeResult SendNarrow(string pipeName, string sessionId)
        {
            NamedPipeMessages.ConeManagement.NarrowParameters parameters =
                new NamedPipeMessages.ConeManagement.NarrowParameters(sessionId);

            return this.SendConeRequest(pipeName, NamedPipeMessages.ConeManagement.NarrowRequest, parameters.ToBody());
        }

        private ConeResult SendConeRequest(string pipeName, string header, string body)
        {
            using (NamedPipeClient pipeClient = new NamedPipeClient(pipeName))
            {
                pipeClient.Connect(PipeConnectTimeoutMs).ShouldBeTrue($"Could not connect to the mount pipe {pipeName}.");

                Stopwatch stopwatch = Stopwatch.StartNew();
                pipeClient.SendRequest(new NamedPipeMessages.Message(header, body));
                NamedPipeMessages.Message response = pipeClient.ReadResponse();
                stopwatch.Stop();

                return new ConeResult(response.Header, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        private string InvokeGit(string repoRoot, string command)
        {
            ProcessResult result = GitProcess.InvokeProcess(repoRoot, command);
            result.ExitCode.ShouldEqual(0, $"git {command} failed: {result.Errors}");
            return result.Output;
        }

        private ProcessResult InvokeSparseIndexGit(string repoRoot, string command)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo(SparseIndexGitExe);
            processInfo.Arguments = command;
            processInfo.WorkingDirectory = repoRoot;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = true;
            processInfo.RedirectStandardError = true;
            processInfo.CreateNoWindow = true;

            // The source-tree build loads its MSYS2 runtime DLLs from the git-sdk, so those
            // directories must lead PATH; the pre-command hook still fires from the repo config.
            string existingPath = processInfo.EnvironmentVariables.ContainsKey("PATH") ? processInfo.EnvironmentVariables["PATH"] : string.Empty;
            processInfo.EnvironmentVariables["PATH"] = SparseIndexGitSdkPath + ";" + existingPath;

            using (Process process = Process.Start(processInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                return new ProcessResult(output, errors, process.ExitCode);
            }
        }

        private struct ConeResult
        {
            public ConeResult(string header, double elapsedMilliseconds)
            {
                this.Header = header;
                this.ElapsedMilliseconds = elapsedMilliseconds;
            }

            public string Header { get; }

            public double ElapsedMilliseconds { get; }
        }
    }
}
