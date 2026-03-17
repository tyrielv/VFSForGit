using GVFS.Common;
using GVFS.UnitTests.Mock.Common;
using GVFS.Virtualization.Projection;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;

namespace GVFS.UnitTests.Common
{
    [TestFixture]
    public class EnlistmentHydrationSummaryTests
    {
        [TestCase]
        public void CountIndexFolders_FlatDirectories()
        {
            int count = CountFoldersInIndex(new[] { "src/file1.cs", "test/file2.cs" });
            Assert.AreEqual(2, count); // "src", "test"
        }

        [TestCase]
        public void CountIndexFolders_NestedDirectories()
        {
            int count = CountFoldersInIndex(new[] { "a/b/c/file1.cs", "a/b/file2.cs", "x/file3.cs" });
            Assert.AreEqual(4, count); // "a", "a/b", "a/b/c", "x"
        }

        [TestCase]
        public void CountIndexFolders_RootFilesOnly()
        {
            int count = CountFoldersInIndex(new[] { "README.md", ".gitignore" });
            Assert.AreEqual(0, count);
        }

        [TestCase]
        public void CountIndexFolders_EmptyIndex()
        {
            int count = CountFoldersInIndex(new string[0]);
            Assert.AreEqual(0, count);
        }

        [TestCase]
        public void CountIndexFolders_DeepNesting()
        {
            int count = CountFoldersInIndex(new[] { "a/b/c/d/e/file.txt" });
            Assert.AreEqual(5, count); // "a", "a/b", "a/b/c", "a/b/c/d", "a/b/c/d/e"
        }

        private static int CountFoldersInIndex(string[] paths)
        {
            byte[] indexBytes = CreateV4Index(paths);
            using (MemoryStream stream = new MemoryStream(indexBytes))
            {
                return GitIndexProjection.CountIndexFolders(new MockTracer(), stream);
            }
        }

        /// <summary>
        /// Create a minimal git index v4 binary matching the format GitIndexGenerator produces.
        /// Uses prefix-compression for paths (v4 format).
        /// </summary>
        private static byte[] CreateV4Index(string[] paths)
        {
            // Stat entry header matching GitIndexGenerator.EntryHeader:
            // 40 bytes with file mode 0x81A4 (regular file, 644) at offset 24-27
            byte[] entryHeader = new byte[40];
            entryHeader[26] = 0x81;
            entryHeader[27] = 0xA4;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                // Header
                bw.Write(new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C' });
                WriteBigEndian32(bw, 4); // version 4
                WriteBigEndian32(bw, (uint)paths.Length);

                string previousPath = string.Empty;
                foreach (string path in paths)
                {
                    // 40-byte stat entry header with valid file mode
                    bw.Write(entryHeader);
                    // 20 bytes SHA-1 (zeros)
                    bw.Write(new byte[20]);
                    // Flags: path length in low 12 bits, skip-worktree in extended
                    byte[] pathBytes = Encoding.UTF8.GetBytes(path);
                    ushort flags = (ushort)(Math.Min(pathBytes.Length, 0xFFF) | 0x4000); // extended bit set
                    WriteBigEndian16(bw, flags);
                    // Extended flags: skip-worktree bit set
                    WriteBigEndian16(bw, 0x4000);

                    // V4 prefix compression: compute common prefix with previous path
                    int commonLen = 0;
                    int maxCommon = Math.Min(previousPath.Length, path.Length);
                    while (commonLen < maxCommon && previousPath[commonLen] == path[commonLen])
                    {
                        commonLen++;
                    }

                    int replaceLen = previousPath.Length - commonLen;
                    string suffix = path.Substring(commonLen);

                    // Write replace length as varint
                    WriteVarint(bw, replaceLen);
                    // Write suffix + null terminator
                    bw.Write(Encoding.UTF8.GetBytes(suffix));
                    bw.Write((byte)0);

                    previousPath = path;
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
            // Git index v4 varint encoding (same as ReadReplaceLength in GitIndexParser)
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
    }
}
