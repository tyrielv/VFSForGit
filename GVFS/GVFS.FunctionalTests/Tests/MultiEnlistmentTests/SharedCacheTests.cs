using GVFS.FunctionalTests.FileSystemRunners;
using GVFS.FunctionalTests.Should;
using GVFS.FunctionalTests.Tools;
using GVFS.Tests.Should;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GVFS.FunctionalTests.Tests.MultiEnlistmentTests
{
    [TestFixture]
    [Category(Categories.ExtraCoverage)]
    public class SharedCacheTests : TestsWithMultiEnlistment
    {
        private const string WellKnownFile = "Readme.md";

        // This branch and commit sha should point to the same place.
        private const string WellKnownBranch = "FunctionalTests/20201014_ResetMixedAndCheckoutFile";
        private const string WellKnownCommitSha = "42eb6632beffae26893a3d6e1a9f48d652327c6f";

        private string localCachePath;
        private string localCacheParentPath;

        private FileSystemRunner fileSystem;

        public SharedCacheTests()
        {
            this.fileSystem = new SystemIORunner();
        }

        [SetUp]
        public void SetCacheLocation()
        {
            this.localCacheParentPath = Path.Combine(Properties.Settings.Default.EnlistmentRoot, "..", Guid.NewGuid().ToString("N"));
            this.localCachePath = Path.Combine(this.localCacheParentPath, ".customGVFSCache");
        }

        [TestCase]
        public void SecondCloneDoesNotDownloadAdditionalObjects()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            File.ReadAllText(Path.Combine(enlistment1.RepoRoot, WellKnownFile));

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);

            string[] allObjects = Directory.EnumerateFiles(enlistment1.LocalCacheRoot, "*", SearchOption.AllDirectories).ToArray();

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            File.ReadAllText(Path.Combine(enlistment2.RepoRoot, WellKnownFile));

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);

            enlistment2.LocalCacheRoot.ShouldEqual(enlistment1.LocalCacheRoot, "Sanity: Local cache roots are expected to match.");
            Directory.EnumerateFiles(enlistment2.LocalCacheRoot, "*", SearchOption.AllDirectories)
                .ShouldMatchInOrder(allObjects);
        }

        [TestCase]
        public void RepairFixesCorruptBlobSizesDatabase()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();
            enlistment.UnmountGVFS();

            // Repair on a healthy enlistment should succeed
            enlistment.Repair(confirm: true);

            string blobSizesRoot = GVFSHelpers.GetPersistedBlobSizesRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            string blobSizesDbPath = Path.Combine(blobSizesRoot, "BlobSizes.sql");
            blobSizesDbPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.WriteAllText(blobSizesDbPath, "0000");

            enlistment.TryMountGVFS().ShouldEqual(false, "GVFS shouldn't mount when blob size db is corrupt");
            enlistment.Repair(confirm: true);
            enlistment.MountGVFS();
        }

        [TestCase]
        public void CloneCleansUpStaleMetadataLock()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            string metadataLockPath = Path.Combine(this.localCachePath, "mapping.dat.lock");
            metadataLockPath.ShouldNotExistOnDisk(this.fileSystem);
            this.fileSystem.WriteAllText(metadataLockPath, enlistment1.EnlistmentRoot);
            metadataLockPath.ShouldBeAFile(this.fileSystem);

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            metadataLockPath.ShouldNotExistOnDisk(this.fileSystem);

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");
        }

        [TestCase]
        public void ParallelReadsInASharedCache()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment3 = null;

            Task task1 = Task.Run(() => this.HydrateEntireRepo(enlistment1));
            Task task2 = Task.Run(() => this.HydrateEntireRepo(enlistment2));
            Task task3 = Task.Run(() => enlistment3 = this.CloneAndMountEnlistment());

            task1.Wait();
            task2.Wait();
            task3.Wait();

            task1.Exception.ShouldBeNull();
            task2.Exception.ShouldBeNull();
            task3.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");
            enlistment3.Status().ShouldContain("Mount status: Ready");

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment3);
        }

        [TestCase]
        public void DeleteObjectsCacheAndCacheMappingBeforeMount()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment();

            enlistment1.UnmountGVFS();

            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment1.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);
            RepositoryHelpers.DeleteTestDirectory(objectsRoot);

            string metadataPath = Path.Combine(this.localCachePath, "mapping.dat");
            metadataPath.ShouldBeAFile(this.fileSystem);
            this.fileSystem.DeleteFile(metadataPath);

            enlistment1.MountGVFS();

            Task task1 = Task.Run(() => this.HydrateRootFolder(enlistment1));
            Task task2 = Task.Run(() => this.HydrateRootFolder(enlistment2));
            task1.Wait();
            task2.Wait();
            task1.Exception.ShouldBeNull();
            task2.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
            enlistment2.Status().ShouldContain("Mount status: Ready");

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment2);
        }

        [TestCase]
        public void DeleteCacheDuringHydrations()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();

            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment1.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            Task task1 = Task.Run(() =>
            {
                this.HydrateEntireRepo(enlistment1);
            });

            while (!task1.IsCompleted)
            {
                try
                {
                    // Delete objectsRoot rather than this.localCachePath as the blob sizes database cannot be deleted while GVFS is mounted
                    RepositoryHelpers.DeleteTestDirectory(objectsRoot);
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    // Hydration may have handles into the cache, so failing this delete is expected.
                }
            }

            task1.Exception.ShouldBeNull();

            enlistment1.Status().ShouldContain("Mount status: Ready");
        }

        [TestCase]
        public void DownloadingACommitWithoutTreesDoesntBreakNextClone()
        {
            GVFSFunctionalTestEnlistment enlistment1 = this.CloneAndMountEnlistment();
            GitProcess.Invoke(enlistment1.RepoRoot, "cat-file -s " + WellKnownCommitSha).ShouldEqual("293\n");

            GVFSFunctionalTestEnlistment enlistment2 = this.CloneAndMountEnlistment(WellKnownBranch);
            enlistment2.Status().ShouldContain("Mount status: Ready");
        }

        [TestCase]
        public void MountReusesLocalCacheKeyWhenGitObjectsRootDeleted()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();

            enlistment.UnmountGVFS();

            // Find the current git objects root and ensure it's on disk
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            string mappingFilePath = Path.Combine(enlistment.LocalCacheRoot, "mapping.dat");
            string mappingFileContents = this.fileSystem.ReadAllText(mappingFilePath);
            mappingFileContents.Length.ShouldNotEqual(0, "mapping.dat should not be empty");

            // Delete the git objects root folder, mount should re-create it and the mapping.dat file should not change
            RepositoryHelpers.DeleteTestDirectory(objectsRoot);

            enlistment.MountGVFS();

            GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldEqual(objectsRoot);
            objectsRoot.ShouldBeADirectory(this.fileSystem);
            mappingFilePath.ShouldBeAFile(this.fileSystem).WithContents(mappingFileContents);

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment);
        }

        [TestCase]
        public void MountUsesNewLocalCacheKeyWhenLocalCacheDeleted()
        {
            GVFSFunctionalTestEnlistment enlistment = this.CloneAndMountEnlistment();

            enlistment.UnmountGVFS();

            // Find the current git objects root and ensure it's on disk
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot).ShouldNotBeNull();
            objectsRoot.ShouldBeADirectory(this.fileSystem);

            string mappingFilePath = Path.Combine(enlistment.LocalCacheRoot, "mapping.dat");
            string mappingFileContents = this.fileSystem.ReadAllText(mappingFilePath);
            mappingFileContents.Length.ShouldNotEqual(0, "mapping.dat should not be empty");

            // Delete the local cache folder, mount should re-create it and generate a new mapping file and local cache key
            RepositoryHelpers.DeleteTestDirectory(enlistment.LocalCacheRoot);

            enlistment.MountGVFS();

            // Mount should recreate the local cache root
            enlistment.LocalCacheRoot.ShouldBeADirectory(this.fileSystem);

            // Determine the new local cache key
            string newMappingFileContents = mappingFilePath.ShouldBeAFile(this.fileSystem).WithContents();
            const int GuidStringLength = 32;
            string mappingFileKey = "A {\"Key\":\"https://gvfs.visualstudio.com/ci/_git/fortests\",\"Value\":\"";
            int localKeyIndex = newMappingFileContents.IndexOf(mappingFileKey);
            string newCacheKey = newMappingFileContents.Substring(localKeyIndex + mappingFileKey.Length, GuidStringLength);

            // Validate the new objects root is on disk and uses the new key
            objectsRoot.ShouldNotExistOnDisk(this.fileSystem);
            string newObjectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot);
            newObjectsRoot.ShouldNotEqual(objectsRoot);
            newObjectsRoot.ShouldContain(newCacheKey);
            newObjectsRoot.ShouldBeADirectory(this.fileSystem);

            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment);
        }

        [TestCase]
        public void SecondCloneSucceedsWithMissingTrees()
        {
            string newCachePath = Path.Combine(this.localCacheParentPath, ".customGvfsCache2");
            GVFSFunctionalTestEnlistment enlistment1 = this.CreateNewEnlistment(localCacheRoot: newCachePath, skipPrefetch: true);
            File.ReadAllText(Path.Combine(enlistment1.RepoRoot, WellKnownFile));
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);

            // This Git command loads the commit and root tree for WellKnownCommitSha,
            // but does not download any more reachable objects.
            string command = "cat-file -p origin/" + WellKnownBranch + "^{tree}";
            ProcessResult result = GitHelpers.InvokeGitAgainstGVFSRepo(enlistment1.RepoRoot, command);
            result.ExitCode.ShouldEqual(0, $"git {command} failed with error: " + result.Errors);

            // If we did not properly check the failed checkout at this step, then clone will fail during checkout.
            GVFSFunctionalTestEnlistment enlistment2 = this.CreateNewEnlistment(localCacheRoot: newCachePath, branch: WellKnownBranch, skipPrefetch: true);
            File.ReadAllText(Path.Combine(enlistment2.RepoRoot, WellKnownFile));
        }

        [TestCase]
        public void SecondCloneSucceedsWithMissingTreesAndPrefetchPacks()
        {
            // Scenario: prefetch packs exist in shared cache, the target commit and
            // root tree are present as loose objects, but subtrees are missing.
            // The checkout fallback must re-download the commit pack.
            string newCachePath = Path.Combine(this.localCacheParentPath, ".customGvfsCache3");
            GVFSFunctionalTestEnlistment enlistment1 = this.CreateNewEnlistment(localCacheRoot: newCachePath);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);

            // While mounted, force-download only the commit and root tree for
            // WellKnownBranch. The VFS read-object hook saves these as loose
            // objects in the shared cache.
            string command = "cat-file -p origin/" + WellKnownBranch + "^{tree}";
            ProcessResult result = GitHelpers.InvokeGitAgainstGVFSRepo(enlistment1.RepoRoot, command);
            result.ExitCode.ShouldEqual(0, $"git {command} failed with error: " + result.Errors);

            string packRoot = enlistment1.GetPackRoot(this.fileSystem);
            string objectRoot = enlistment1.GetObjectRoot(this.fileSystem);

            // Get a known object SHA to seed the fake prefetch pack (use default
            // branch tip — it's already in the cache as a loose object).
            string seedSha = GitProcess.Invoke(enlistment1.RepoRoot, "rev-parse HEAD").Trim();

            enlistment1.UnmountGVFS();

            // Create the fake prefetch pack BEFORE deleting real packs, so that
            // pack-objects can find the seed object in the existing packs.
            this.CreateMinimalPrefetchPack(enlistment1.RepoRoot, packRoot, seedSha);

            // Surgery: delete all NON-fake packs + MIDX so subtrees are gone, but
            // leave loose objects (which include WellKnownBranch commit + root tree).
            this.DeletePackFilesExcept(packRoot, prefix: "prefetch-9999999999");
            this.DeleteMultiPackIndex(packRoot);

            // Clone2 on WellKnownBranch: CommitAndRootTreeExists → true (loose),
            // HasUsablePrefetchPacks → true, but subtrees are missing.
            // The checkout fallback must detect "unable to read tree" and re-download.
            GVFSFunctionalTestEnlistment enlistment2 = this.CreateNewEnlistment(localCacheRoot: newCachePath, branch: WellKnownBranch, skipPrefetch: true);
            File.ReadAllText(Path.Combine(enlistment2.RepoRoot, WellKnownFile));
        }

        [TestCase]
        public void SecondCloneWithPrefetchPacksButMissingCommit()
        {
            // Scenario: prefetch packs exist in the shared cache but the target
            // commit is NOT present. This exercises the deferred-download path
            // (skippedCommitDownload) added by the clone optimization.
            string newCachePath = Path.Combine(this.localCacheParentPath, ".customGvfsCache4");
            GVFSFunctionalTestEnlistment enlistment1 = this.CreateNewEnlistment(localCacheRoot: newCachePath);
            this.AlternatesFileShouldHaveGitObjectsRoot(enlistment1);

            string packRoot = enlistment1.GetPackRoot(this.fileSystem);

            // Get a known object SHA to seed the fake prefetch pack.
            string seedSha = GitProcess.Invoke(enlistment1.RepoRoot, "rev-parse HEAD").Trim();

            enlistment1.UnmountGVFS();

            // Create the fake prefetch pack BEFORE deleting real packs, so that
            // pack-objects can find the seed object in the existing packs.
            this.CreateMinimalPrefetchPack(enlistment1.RepoRoot, packRoot, seedSha);

            // Surgery: delete all NON-fake packs and MIDX. The real prefetch packs
            // contain WellKnownBranch's commit; removing them ensures it's absent.
            // Leave loose objects and our fake prefetch pack.
            this.DeletePackFilesExcept(packRoot, prefix: "prefetch-9999999999");
            this.DeleteMultiPackIndex(packRoot);

            // Clone2 on WellKnownBranch: CommitAndRootTreeExists → false,
            // HasUsablePrefetchPacks → true, skippedCommitDownload = true.
            // CreateBranchWithUpstream or TryDownloadRootGitAttributes will fail
            // and trigger the deferred commit download.
            GVFSFunctionalTestEnlistment enlistment2 = this.CreateNewEnlistment(localCacheRoot: newCachePath, branch: WellKnownBranch, skipPrefetch: true);
            enlistment2.Status().ShouldContain("Mount status: Ready");
            File.ReadAllText(Path.Combine(enlistment2.RepoRoot, WellKnownFile));
        }

        // Override OnTearDownEnlistmentsDeleted rathern than using [TearDown] as the enlistments need to be unmounted before
        // localCacheParentPath can be deleted (as the SQLite blob sizes database cannot be deleted while GVFS is mounted)
        protected override void OnTearDownEnlistmentsDeleted()
        {
            RepositoryHelpers.DeleteTestDirectory(this.localCacheParentPath);
        }

        private GVFSFunctionalTestEnlistment CloneAndMountEnlistment(string branch = null)
        {
            return this.CreateNewEnlistment(this.localCachePath, branch);
        }

        private void AlternatesFileShouldHaveGitObjectsRoot(GVFSFunctionalTestEnlistment enlistment)
        {
            string objectsRoot = GVFSHelpers.GetPersistedGitObjectsRoot(enlistment.DotGVFSRoot);
            string alternatesFileContents = Path.Combine(enlistment.RepoRoot, ".git", "objects", "info", "alternates").ShouldBeAFile(this.fileSystem).WithContents();
            alternatesFileContents.ShouldEqual(objectsRoot);
        }

        private void HydrateRootFolder(GVFSFunctionalTestEnlistment enlistment)
        {
            List<string> allFiles = Directory.EnumerateFiles(enlistment.RepoRoot, "*", SearchOption.TopDirectoryOnly).ToList();
            for (int i = 0; i < allFiles.Count; ++i)
            {
                File.ReadAllText(allFiles[i]);
            }
        }

        private void HydrateEntireRepo(GVFSFunctionalTestEnlistment enlistment)
        {
            List<string> allFiles = Directory.EnumerateFiles(enlistment.RepoRoot, "*", SearchOption.AllDirectories).ToList();
            string dotGitRoot = Path.Combine(enlistment.RepoRoot, ".git") + Path.DirectorySeparatorChar;
            for (int i = 0; i < allFiles.Count; ++i)
            {
                if (!allFiles[i].StartsWith(dotGitRoot, FileSystemHelpers.PathComparison))
                {
                    File.ReadAllText(allFiles[i]);
                }
            }
        }

        /// <summary>
        /// Deletes all .pack, .idx, .keep, .rev, .bitmap, .incomplete files from the
        /// given pack directory EXCEPT those whose file name starts with the given prefix.
        /// </summary>
        private void DeletePackFilesExcept(string packRoot, string prefix)
        {
            foreach (string file in Directory.EnumerateFiles(packRoot))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string ext = Path.GetExtension(file);
                if (ext.Equals(".pack", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".idx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".keep", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".rev", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".bitmap", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".incomplete", StringComparison.OrdinalIgnoreCase))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
        }

        /// <summary>
        /// Deletes the multi-pack-index and related files from the pack directory.
        /// </summary>
        private void DeleteMultiPackIndex(string packRoot)
        {
            foreach (string file in Directory.EnumerateFiles(packRoot))
            {
                if (Path.GetFileName(file).StartsWith("multi-pack-index", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }

        /// <summary>
        /// Creates a minimal prefetch-named pack containing a single object.
        /// Uses a temporary bare repo to run git pack-objects without VFS hooks.
        /// </summary>
        private void CreateMinimalPrefetchPack(string repoRoot, string packRoot, string objectSha)
        {
            // Create a temporary bare repo that borrows objects from the enlistment
            // via alternates, so pack-objects can find the seed object.
            string tempBareRepo = Path.Combine(Path.GetTempPath(), "gvfs_fakeprefetch_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
            Directory.CreateDirectory(tempBareRepo);
            GitProcess.Invoke(tempBareRepo, "init --bare .");

                // Point alternates at the enlistment's .git/objects and the shared cache
                string alternatesPath = Path.Combine(tempBareRepo, "objects", "info", "alternates");
                string enlistmentObjects = Path.Combine(repoRoot, ".git", "objects");
                string alternatesContent = Path.Combine(enlistmentObjects, "info", "alternates");
                string sharedCacheRoot = File.Exists(alternatesContent) ? File.ReadAllText(alternatesContent).Trim() : "";

                string alternatesLines = enlistmentObjects;
                if (!string.IsNullOrEmpty(sharedCacheRoot))
                {
                    alternatesLines += "\n" + sharedCacheRoot;
                }

                File.WriteAllText(alternatesPath, alternatesLines);

                // Use pack-objects to create a pack with just the seed object.
                // The prefix includes a fake timestamp so the pack matches the
                // prefetch-<timestamp>-<hash>.pack naming convention.
                string packPrefix = Path.Combine(packRoot, "prefetch-9999999999");

                MemoryStream inputStream = new MemoryStream(
                Encoding.ASCII.GetBytes(objectSha + "\n"));

                ProcessResult packResult = GitProcess.InvokeProcess(
                    tempBareRepo,
                "-c safe.bareRepository=all pack-objects " + packPrefix,
                    inputStream: inputStream);
                packResult.ExitCode.ShouldEqual(0, "git pack-objects failed: " + packResult.Errors);

                // pack-objects outputs the pack hash; verify the files were created.
                string packHash = packResult.Output.Trim();
                string expectedPack = packPrefix + "-" + packHash + ".pack";
                string expectedIdx = packPrefix + "-" + packHash + ".idx";
                expectedPack.ShouldBeAFile(this.fileSystem);
                expectedIdx.ShouldBeAFile(this.fileSystem);
            }
            finally
            {
                RepositoryHelpers.DeleteTestDirectory(tempBareRepo);
            }
        }
    }
}
