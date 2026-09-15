using GVFS.Common;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using GVFS.UnitTests.Mock.FileSystem;
using GVFS.Virtualization.Projection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static GVFS.Virtualization.Projection.GitIndexProjection;

namespace GVFS.UnitTests.Virtualization.Git
{
    /// <summary>
    /// Tests for parsing a sparse index (git index.sparse). A sparse index stores a
    /// collapsed subtree as a single directory entry with mode 040000 and a trailing '/'.
    /// GVFS cannot yet expand such an entry, so it must detect it during parse and fail fast
    /// with an actionable message, before the mount reports the repository ready.
    /// </summary>
    [TestFixture]
    public class SparseIndexParsingTests
    {
        // Git index mode values, in the on-disk 16-bit form.
        private const ushort RegularFileMode = 0x81A4;   // 100644
        private const ushort SparseDirectoryMode = 0x4000; // 040000

        // The extended flag bit and the skip-worktree bit, as stored in the index.
        private const ushort ExtendedFlagBit = 0x4000;
        private const ushort SkipWorktreeFlagBit = 0x4000;

        [SetUp]
        public void SetUp()
        {
            // Every test starts on the mode-parsing (macOS-like) path. Individual tests toggle
            // this to exercise the Windows path, where the parser skips the mode field.
            SetSupportsFileMode(true);
        }

        [TearDown]
        public void TearDown()
        {
            // Restore the shared platform singleton so other fixtures are unaffected.
            SetSupportsFileMode(true);
        }

        [TestCase]
        public void FileTypeAndMode_DirectoryMode_IsDirectoryWithZeroMode()
        {
            FileTypeAndMode typeAndMode = new FileTypeAndMode(SparseDirectoryMode);
            typeAndMode.Type.ShouldEqual(FileType.Directory);
            typeAndMode.Mode.ShouldEqual((ushort)0);
        }

        [TestCase]
        public void FileTypeAndMode_DirectoryModeWithStrayPermissionBits_KeepsTypeExposesMode()
        {
            // The type nibble still identifies a directory even when the low permission bits are
            // set. The parser uses the exposed non-zero mode to reject a malformed entry.
            FileTypeAndMode typeAndMode = new FileTypeAndMode((ushort)(SparseDirectoryMode | 0x1FF));
            typeAndMode.Type.ShouldEqual(FileType.Directory);
            typeAndMode.Mode.ShouldEqual((ushort)0x1FF);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Parse_SparseDirectoryEntry_IsAccepted(bool supportsFileMode)
        {
            // Increment 1: the parser recognises a 040000 sparse-directory entry on both
            // platforms. On the mode-parsing path it must not throw "Invalid file type"; on the
            // Windows path the mode field is skipped entirely. CountIndexFolders does not fail
            // fast, so it proves the entry parses.
            SetSupportsFileMode(supportsFileMode);

            byte[] index = BuildV4Index(
                new SparseEntry("GVFS/", SparseDirectoryMode),
                new SparseEntry("Scripts/", SparseDirectoryMode));

            using (MemoryStream stream = new MemoryStream(index))
            {
                int folders = GitIndexProjection.CountIndexFolders(new MockTracer(), stream);
                folders.ShouldEqual(2);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Parse_SparseDirectoryEntry_FailsFastDuringValidate(bool supportsFileMode)
        {
            // Increment 2: the projection fails fast at parse time. ValidateIndex runs the same
            // per-entry validation as projection building, so it throws the actionable error
            // instead of proceeding to enumeration and crashing later.
            SetSupportsFileMode(supportsFileMode);

            byte[] index = BuildV4Index(
                new SparseEntry("GVFS/", SparseDirectoryMode),
                new SparseEntry("README.md", RegularFileMode));

            using (MemoryStream stream = new MemoryStream(index))
            {
                InvalidDataException error = Assert.Throws<InvalidDataException>(
                    () => GitIndexParser.ValidateIndex(new MockTracer(), stream));

                error.Message.ShouldContain("sparse index", "GVFS/", "gvfs sparse-index --disable");
            }
        }

        [TestCase]
        public void Parse_DirectoryEntryWithNonZeroMode_FailsWithModeError()
        {
            // On the mode-parsing path a directory entry must carry mode zero. A directory whose
            // permission bits are set is rejected as a malformed mode, before the trailing-slash
            // check runs.
            SetSupportsFileMode(true);

            byte[] index = BuildV4Index(
                new SparseEntry("GVFS/", (ushort)(SparseDirectoryMode | 0x1FF)));

            using (MemoryStream stream = new MemoryStream(index))
            {
                InvalidDataException error = Assert.Throws<InvalidDataException>(
                    () => GitIndexParser.ValidateIndex(new MockTracer(), stream));

                error.Message.ShouldContain("Invalid file mode", "sparse directory");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Parse_V4PrefixCompression_TrailingSlashEntryDoesNotCorruptNextPath(bool supportsFileMode)
        {
            // A sparse-directory path keeps its trailing '/', so its length includes the slash.
            // The next entry's v4 prefix compression is computed against that full length. Verify
            // the following path decompresses to exactly the right bytes, not shifted by one.
            SetSupportsFileMode(supportsFileMode);

            byte[] index = BuildV4Index(
                new SparseEntry("alpha/", SparseDirectoryMode),
                new SparseEntry("alpha2/file.txt", RegularFileMode));

            using (MemoryStream stream = new MemoryStream(index))
            {
                List<string> paths = GitIndexParser.GetIndexPaths(new MockTracer(), stream);
                paths.Count.ShouldEqual(2);
                paths[0].ShouldEqual("alpha/");
                paths[1].ShouldEqual("alpha2/file.txt");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Parse_FullIndexWithoutSparseEntries_IsUnaffected(bool supportsFileMode)
        {
            // A normal full index must still parse and validate cleanly on both platforms.
            SetSupportsFileMode(supportsFileMode);

            SparseEntry[] entries = new[]
            {
                new SparseEntry("a/b/c.txt", RegularFileMode),
                new SparseEntry("a/d.txt", RegularFileMode),
                new SparseEntry("z.txt", RegularFileMode),
            };

            byte[] index = BuildV4Index(entries);

            using (MemoryStream validateStream = new MemoryStream(index))
            {
                Assert.DoesNotThrow(() => GitIndexParser.ValidateIndex(new MockTracer(), validateStream));
            }

            using (MemoryStream pathStream = new MemoryStream(index))
            {
                List<string> paths = GitIndexParser.GetIndexPaths(new MockTracer(), pathStream);
                paths.Count.ShouldEqual(3);
                paths[0].ShouldEqual("a/b/c.txt");
                paths[1].ShouldEqual("a/d.txt");
                paths[2].ShouldEqual("z.txt");
            }
        }

        private static void SetSupportsFileMode(bool value)
        {
            ((MockPlatformFileSystem)GVFSPlatform.Instance.FileSystem).SupportsFileMode = value;
        }

        /// <summary>
        /// Build a minimal git index v4 binary. Mirrors the format GitIndexGenerator produces,
        /// but lets each entry carry an arbitrary mode and an arbitrary (possibly trailing-slash)
        /// path, so a sparse-directory entry can be emitted. Entries must be supplied in git sort
        /// order because the format uses prefix compression against the previous path.
        /// </summary>
        private static byte[] BuildV4Index(params SparseEntry[] entries)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C' });
                WriteBigEndian32(bw, 4);
                WriteBigEndian32(bw, (uint)entries.Length);

                string previousPath = string.Empty;
                foreach (SparseEntry entry in entries)
                {
                    // 40-byte stat header. The 16-bit type/mode lives at offset 26.
                    byte[] entryHeader = new byte[40];
                    entryHeader[26] = (byte)((entry.IndexMode >> 8) & 0xFF);
                    entryHeader[27] = (byte)(entry.IndexMode & 0xFF);
                    bw.Write(entryHeader);

                    // 20-byte object id (zeros).
                    bw.Write(new byte[20]);

                    byte[] pathBytes = Encoding.UTF8.GetBytes(entry.Path);
                    ushort flags = (ushort)(Math.Min(pathBytes.Length, 0xFFF) | ExtendedFlagBit | ((ushort)entry.MergeState << 12));
                    WriteBigEndian16(bw, flags);

                    ushort extendedFlags = entry.SkipWorktree ? SkipWorktreeFlagBit : (ushort)0;
                    WriteBigEndian16(bw, extendedFlags);

                    int commonLen = 0;
                    int maxCommon = Math.Min(previousPath.Length, entry.Path.Length);
                    while (commonLen < maxCommon && previousPath[commonLen] == entry.Path[commonLen])
                    {
                        commonLen++;
                    }

                    int replaceLen = previousPath.Length - commonLen;
                    string suffix = entry.Path.Substring(commonLen);

                    WriteVarint(bw, replaceLen);
                    bw.Write(Encoding.UTF8.GetBytes(suffix));
                    bw.Write((byte)0);

                    previousPath = entry.Path;
                }

                return ms.ToArray();
            }
        }

        private static void WriteBigEndian32(BinaryWriter bw, uint value)
        {
            bw.Write((byte)((value >> 24) & 0xFF));
            bw.Write((byte)((value >> 16) & 0xFF));
            bw.Write((byte)((value >> 8) & 0xFF));
            bw.Write((byte)(value & 0xFF));
        }

        private static void WriteBigEndian16(BinaryWriter bw, ushort value)
        {
            bw.Write((byte)((value >> 8) & 0xFF));
            bw.Write((byte)(value & 0xFF));
        }

        private static void WriteVarint(BinaryWriter bw, int value)
        {
            // Git index v4 varint encoding, matching ReadReplaceLength in GitIndexParser.
            if (value < 0x80)
            {
                bw.Write((byte)value);
                return;
            }

            byte[] bytes = new byte[5];
            int pos = 4;
            bytes[pos] = (byte)(value & 0x7F);
            value = (value >> 7) - 1;
            while (value >= 0)
            {
                pos--;
                bytes[pos] = (byte)(0x80 | (value & 0x7F));
                value = (value >> 7) - 1;
            }

            bw.Write(bytes, pos, 5 - pos);
        }

        private class SparseEntry
        {
            public SparseEntry(string path, ushort indexMode)
            {
                this.Path = path;
                this.IndexMode = indexMode;
                this.SkipWorktree = true;
                this.MergeState = GitIndexParser.MergeStage.NoConflicts;
            }

            public string Path { get; }
            public ushort IndexMode { get; }
            public bool SkipWorktree { get; set; }
            public GitIndexParser.MergeStage MergeState { get; set; }
        }
    }
}
