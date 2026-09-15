using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static GVFS.Virtualization.Projection.GitIndexProjection;

namespace GVFS.UnitTests.Virtualization.Git
{
    /// <summary>
    /// Tests for expanding a sparse-directory entry's collapsed subtree into the projection.
    /// The expander reads tree objects through a fake <see cref="IProjectionTreeReader"/>, so a
    /// synthetic tree (with regular files, executables, symlinks, gitlinks, and nested folders)
    /// can be built without a real libgit2 repository. The tests confirm the projection shape,
    /// the mode recording, the gvfs sparse-cone inclusion, the download-then-retry path, and the
    /// fail-fast on a missing, corrupt, or non-tree object.
    /// </summary>
    [TestFixture]
    public class SparseDirectoryExpanderTests
    {
        private const int DefaultIndexEntryCount = 1000;

        // Git tree entry modes, in the on-disk 16-bit form.
        private const ushort RegularFileMode = 0x81A4;   // 100644
        private const ushort ExecutableFileMode = 0x81ED; // 100755
        private const ushort SymLinkMode = 0xA000;        // 120000
        private const ushort GitLinkMode = 0xE000;        // 160000
        private const ushort DirectoryMode = 0x4000;      // 040000

        [OneTimeSetUp]
        public void Setup()
        {
            LazyUTF8String.InitializePools(new MockTracer(), DefaultIndexEntryCount);
            SortedFolderEntries.InitializePools(new MockTracer(), DefaultIndexEntryCount);
        }

        [SetUp]
        public void TestSetup()
        {
            LazyUTF8String.ResetPool(new MockTracer(), DefaultIndexEntryCount);
            SortedFolderEntries.ResetPool(new MockTracer(), DefaultIndexEntryCount);
        }

        [TestCase]
        public void Expand_FlatTree_CreatesFilesAndFolders()
        {
            FakeTreeReader reader = new FakeTreeReader();

            byte[] readmeId = MakeObjectId(0x10);
            string srcSha = reader.AddTree(
                MakeObjectId(0x30),
                FileEntry("main.cs", MakeObjectId(0x40), RegularFileMode));

            string rootSha = reader.AddTree(
                MakeObjectId(0x20),
                FileEntry("readme.md", readmeId, RegularFileMode),
                FolderEntry("src", srcSha));

            FolderData root = CreateRootFolder("GVFS");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded);

            root.ChildEntries.Count.ShouldEqual(2);

            FileData readme = GetFile(root, "readme.md");
            readme.ConvertShaToString().ShouldEqual(HexUpper(readmeId));

            FolderData src = GetFolder(root, "src");
            src.ChildEntries.Count.ShouldEqual(1);
            GetFile(src, "main.cs").ShouldNotBeNull();
        }

        [TestCase]
        public void Expand_NestedTree_CreatesGrandchildren()
        {
            FakeTreeReader reader = new FakeTreeReader();

            string grandChildSha = reader.AddTree(
                MakeObjectId(0x50),
                FileEntry("leaf.txt", MakeObjectId(0x51), RegularFileMode));

            string childSha = reader.AddTree(
                MakeObjectId(0x60),
                FolderEntry("grandchild", grandChildSha));

            string rootSha = reader.AddTree(
                MakeObjectId(0x70),
                FolderEntry("child", childSha));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded);

            FolderData child = GetFolder(root, "child");
            FolderData grandchild = GetFolder(child, "grandchild");
            GetFile(grandchild, "leaf.txt").ShouldNotBeNull();
        }

        [TestCase]
        public void Expand_RecordsNonDefaultModes_WhenPlatformSupportsFileMode()
        {
            FakeTreeReader reader = new FakeTreeReader();

            string rootSha = reader.AddTree(
                MakeObjectId(0x80),
                FileEntry("a.txt", MakeObjectId(0x81), RegularFileMode),
                FileEntry("run.sh", MakeObjectId(0x82), ExecutableFileMode),
                FileEntry("link", MakeObjectId(0x83), SymLinkMode),
                FileEntry("sub", MakeObjectId(0x84), GitLinkMode));

            FolderData root = CreateRootFolder("GVFS");
            Dictionary<string, FileTypeAndMode> modes = new Dictionary<string, FileTypeAndMode>(StringComparer.OrdinalIgnoreCase);
            SparseDirectoryExpander expander = new SparseDirectoryExpander(
                new MockTracer(),
                reader,
                platformSupportsFileMode: true,
                rootSparseFolder: new SparseFolderData(),
                nonDefaultFileTypesAndModes: modes);

            expander.Expand(root, rootGitPath: "GVFS", rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded);

            // A regular 644 file is the default, so it is not recorded.
            modes.ContainsKey("GVFS/a.txt").ShouldBeFalse();

            modes.ContainsKey("GVFS/run.sh").ShouldBeTrue();
            modes["GVFS/run.sh"].Type.ShouldEqual(FileType.Regular);
            modes["GVFS/run.sh"].Mode.ShouldEqual(FileMode755);

            modes.ContainsKey("GVFS/link").ShouldBeTrue();
            modes["GVFS/link"].Type.ShouldEqual(FileType.SymLink);

            modes.ContainsKey("GVFS/sub").ShouldBeTrue();
            modes["GVFS/sub"].Type.ShouldEqual(FileType.GitLink);
        }

        [TestCase]
        public void Expand_DoesNotRecordModes_WhenPlatformDoesNotSupportFileMode()
        {
            FakeTreeReader reader = new FakeTreeReader();

            string rootSha = reader.AddTree(
                MakeObjectId(0x90),
                FileEntry("run.sh", MakeObjectId(0x91), ExecutableFileMode),
                FileEntry("link", MakeObjectId(0x92), SymLinkMode));

            FolderData root = CreateRootFolder("GVFS");
            Dictionary<string, FileTypeAndMode> modes = new Dictionary<string, FileTypeAndMode>(StringComparer.OrdinalIgnoreCase);
            SparseDirectoryExpander expander = new SparseDirectoryExpander(
                new MockTracer(),
                reader,
                platformSupportsFileMode: false,
                rootSparseFolder: new SparseFolderData(),
                nonDefaultFileTypesAndModes: modes);

            expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded);

            modes.Count.ShouldEqual(0);
        }

        [TestCase]
        public void Expand_AppliesSparseConeInclusion()
        {
            FakeTreeReader reader = new FakeTreeReader();

            string includedSha = reader.AddTree(
                MakeObjectId(0xA0),
                FileEntry("in.txt", MakeObjectId(0xA1), RegularFileMode));
            string excludedSha = reader.AddTree(
                MakeObjectId(0xB0),
                FileEntry("out.txt", MakeObjectId(0xB1), RegularFileMode));

            string rootSha = reader.AddTree(
                MakeObjectId(0xC0),
                FolderEntry("inc", includedSha),
                FolderEntry("exc", excludedSha));

            // A gvfs sparse cone that recursively includes only "inc".
            SparseFolderData rootSparse = new SparseFolderData();
            rootSparse.Children.Add("inc", new SparseFolderData { IsRecursive = true });

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: rootSparse);

            // The collapsed folder is inside the cone: included, non-recursive, matched at the root.
            FrameInclusion rootInclusion = new FrameInclusion(included: true, recursive: false, node: rootSparse);
            expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: rootInclusion);

            GetFolder(root, "inc").IsIncluded.ShouldBeTrue();
            GetFolder(root, "exc").IsIncluded.ShouldBeFalse();
        }

        [TestCase]
        public void Expand_DownloadsMissingTree_ThenRetries()
        {
            FakeTreeReader reader = new FakeTreeReader();

            // The child tree is downloadable, not local. The root tree is local.
            string childSha = reader.AddDownloadableTree(
                MakeObjectId(0xD0),
                FileEntry("leaf.txt", MakeObjectId(0xD1), RegularFileMode));

            string rootSha = reader.AddTree(
                MakeObjectId(0xE0),
                FolderEntry("child", childSha));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded);

            reader.DownloadedShas.ShouldContain(sha => sha.Equals(childSha, StringComparison.OrdinalIgnoreCase));
            FolderData child = GetFolder(root, "child");
            GetFile(child, "leaf.txt").ShouldNotBeNull();
        }

        [TestCase]
        public void Expand_FailsFast_WhenTreePermanentlyMissing()
        {
            FakeTreeReader reader = new FakeTreeReader();

            // "child" references a tree SHA that is neither local nor downloadable.
            byte[] missingId = MakeObjectId(0xF0);
            string missingSha = HexLower(missingId);

            string rootSha = reader.AddTree(
                MakeObjectId(0xF5),
                FolderEntry("child", missingSha));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded));

            error.Message.ShouldContain(missingSha, "MissingTree");
            reader.DownloadedShas.ShouldContain(sha => sha.Equals(missingSha, StringComparison.OrdinalIgnoreCase));
        }

        [TestCase]
        public void Expand_FailsFast_WhenTreeCorrupt_WithoutDownloading()
        {
            FakeTreeReader reader = new FakeTreeReader();

            byte[] corruptId = MakeObjectId(0x11);
            string corruptSha = HexLower(corruptId);
            reader.CorruptTrees.Add(corruptSha);

            string rootSha = reader.AddTree(
                MakeObjectId(0x12),
                FolderEntry("child", corruptSha));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded));

            error.Message.ShouldContain(corruptSha, "CorruptTree");

            // A corrupt tree is a hard error; no download is attempted.
            reader.DownloadedShas.Count.ShouldEqual(0);
        }

        [TestCase]
        public void Expand_FailsFast_WhenObjectIsNotATree()
        {
            FakeTreeReader reader = new FakeTreeReader();

            byte[] blobId = MakeObjectId(0x13);
            string blobSha = HexLower(blobId);
            reader.NotTrees.Add(blobSha);

            string rootSha = reader.AddTree(
                MakeObjectId(0x14),
                FolderEntry("child", blobSha));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded));

            error.Message.ShouldContain(blobSha, "NotTree");
            reader.DownloadedShas.Count.ShouldEqual(0);
        }

        [TestCase]
        public void Expand_FailsFast_WhenTreeEntryHasEmptyName()
        {
            FakeTreeReader reader = new FakeTreeReader();

            string rootSha = reader.AddTree(
                MakeObjectId(0x15),
                FileEntry(string.Empty, MakeObjectId(0x16), RegularFileMode));

            FolderData root = CreateRootFolder("collapsed");
            SparseDirectoryExpander expander = CreateExpander(reader, platformSupportsFileMode: false, rootSparse: new SparseFolderData());

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => expander.Expand(root, rootGitPath: null, rootTreeSha: rootSha, rootInclusion: FrameInclusion.AllIncluded));

            error.Message.ShouldContain("empty name");
        }

        private static SparseDirectoryExpander CreateExpander(FakeTreeReader reader, bool platformSupportsFileMode, SparseFolderData rootSparse)
        {
            return new SparseDirectoryExpander(
                new MockTracer(),
                reader,
                platformSupportsFileMode,
                rootSparse,
                new Dictionary<string, FileTypeAndMode>(StringComparer.OrdinalIgnoreCase));
        }

        private static FolderData CreateRootFolder(string name)
        {
            FolderData folder = new FolderData();
            folder.ResetData(ConstructLazyUTF8String(name), isIncluded: true);
            return folder;
        }

        private static FileData GetFile(FolderData folder, string name)
        {
            folder.ChildEntries.TryGetValue(ConstructLazyUTF8String(name), out FolderEntryData entry).ShouldBeTrue($"Expected a child named {name}");
            entry.ShouldNotBeNull();
            entry.IsFolder.ShouldBeFalse($"{name} should be a file");
            return (FileData)entry;
        }

        private static FolderData GetFolder(FolderData folder, string name)
        {
            folder.ChildEntries.TryGetValue(ConstructLazyUTF8String(name), out FolderEntryData entry).ShouldBeTrue($"Expected a child named {name}");
            entry.ShouldNotBeNull();
            entry.IsFolder.ShouldBeTrue($"{name} should be a folder");
            return (FolderData)entry;
        }

        private static unsafe LazyUTF8String ConstructLazyUTF8String(string name)
        {
            byte[] buffer = Encoding.ASCII.GetBytes(name);
            fixed (byte* bufferPtr = buffer)
            {
                return LazyUTF8String.FromByteArray(bufferPtr, name.Length);
            }
        }

        private static byte[] MakeObjectId(int seed)
        {
            byte[] id = new byte[20];
            for (int i = 0; i < id.Length; i++)
            {
                id[i] = (byte)(seed + i);
            }

            return id;
        }

        private static string HexLower(byte[] id)
        {
            return SHA1Util.HexStringFromBytes(id);
        }

        private static string HexUpper(byte[] id)
        {
            return SHA1Util.HexStringFromBytes(id).ToUpperInvariant();
        }

        private static FakeTreeEntry FileEntry(string name, byte[] objectId, ushort mode)
        {
            return new FakeTreeEntry(name, objectId, mode, isTree: false);
        }

        private static FakeTreeEntry FolderEntry(string name, string childTreeSha)
        {
            return new FakeTreeEntry(name, SHA1Util.BytesFromHexString(childTreeSha), DirectoryMode, isTree: true);
        }

        private class FakeTreeEntry
        {
            public FakeTreeEntry(string name, byte[] objectId, ushort mode, bool isTree)
            {
                this.Name = name;
                this.ObjectId = objectId;
                this.Mode = mode;
                this.IsTree = isTree;
            }

            public string Name { get; }
            public byte[] ObjectId { get; }
            public ushort Mode { get; }
            public bool IsTree { get; }
        }

        private class FakeTreeReader : IProjectionTreeReader
        {
            public Dictionary<string, List<FakeTreeEntry>> LocalTrees { get; } = new Dictionary<string, List<FakeTreeEntry>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<FakeTreeEntry>> DownloadableTrees { get; } = new Dictionary<string, List<FakeTreeEntry>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CorruptTrees { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> NotTrees { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> DownloadedShas { get; } = new List<string>();

            public string AddTree(byte[] treeId, params FakeTreeEntry[] entries)
            {
                string sha = SHA1Util.HexStringFromBytes(treeId);
                this.LocalTrees[sha] = new List<FakeTreeEntry>(entries);
                return sha;
            }

            public string AddDownloadableTree(byte[] treeId, params FakeTreeEntry[] entries)
            {
                string sha = SHA1Util.HexStringFromBytes(treeId);
                this.DownloadableTrees[sha] = new List<FakeTreeEntry>(entries);
                return sha;
            }

            public TreeEnumerationResult TryReadTree(string treeSha, TreeEntryVisitor visitor)
            {
                if (this.CorruptTrees.Contains(treeSha))
                {
                    return TreeEnumerationResult.CorruptTree;
                }

                if (this.NotTrees.Contains(treeSha))
                {
                    return TreeEnumerationResult.NotTree;
                }

                if (this.LocalTrees.TryGetValue(treeSha, out List<FakeTreeEntry> entries))
                {
                    foreach (FakeTreeEntry entry in entries)
                    {
                        byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
                        visitor(nameBytes, entry.ObjectId, entry.Mode, entry.IsTree);
                    }

                    return TreeEnumerationResult.Success;
                }

                return TreeEnumerationResult.MissingTree;
            }

            public bool TryDownloadTree(string treeSha)
            {
                this.DownloadedShas.Add(treeSha);
                if (this.DownloadableTrees.TryGetValue(treeSha, out List<FakeTreeEntry> entries))
                {
                    this.LocalTrees[treeSha] = entries;
                    this.DownloadableTrees.Remove(treeSha);
                    return true;
                }

                return false;
            }
        }
    }
}
