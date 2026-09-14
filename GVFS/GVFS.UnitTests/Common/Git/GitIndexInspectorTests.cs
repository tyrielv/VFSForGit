using GVFS.Common.Git;
using GVFS.Tests.Should;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace GVFS.UnitTests.Common.Git
{
    [TestFixture]
    public class GitIndexInspectorTests
    {
        private const uint RegularFileMode = 0x81A4; // 100644 octal
        private const uint DirectoryMode = 0x4000;   // 040000 octal

        private string testDirectory;

        [SetUp]
        public void SetUp()
        {
            this.testDirectory = Path.Combine(Path.GetTempPath(), "GVFS.UnitTests.GitIndexInspector", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.testDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (this.testDirectory != null && Directory.Exists(this.testDirectory))
            {
                Directory.Delete(this.testDirectory, recursive: true);
            }
        }

        [TestCase]
        public void MissingFileReturnsFalse()
        {
            string missingPath = Path.Combine(this.testDirectory, "does-not-exist");

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(missingPath, out info, out error).ShouldBeFalse();
            info.ShouldBeNull();
            error.ShouldNotBeNull();
        }

        [TestCase]
        public void NonIndexFileReturnsFalse()
        {
            string path = Path.Combine(this.testDirectory, "not-an-index");
            File.WriteAllBytes(path, new byte[] { (byte)'N', (byte)'O', (byte)'P', (byte)'E', 0, 0, 0, 4, 0, 0, 0, 0 });

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(path, out info, out error).ShouldBeFalse();
            info.ShouldBeNull();
        }

        [TestCase]
        public void FullIndexVersion2IsNotSparse()
        {
            string path = this.WriteIndex(version: 2, new List<KeyValuePair<uint, string>>
            {
                new KeyValuePair<uint, string>(RegularFileMode, "a/first.txt"),
                new KeyValuePair<uint, string>(RegularFileMode, "a/second.txt"),
            });

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(path, out info, out error).ShouldBeTrue(error);
            info.Version.ShouldEqual((uint)2);
            info.EntryCount.ShouldEqual((uint)2);
            info.IsSparse.ShouldBeFalse();
            info.SizeInBytes.ShouldEqual(new FileInfo(path).Length);
        }

        [TestCase]
        public void FullIndexVersion4IsNotSparse()
        {
            string path = this.WriteIndex(version: 4, new List<KeyValuePair<uint, string>>
            {
                new KeyValuePair<uint, string>(RegularFileMode, "dirA/file1"),
                new KeyValuePair<uint, string>(RegularFileMode, "dirA/file2"),
                new KeyValuePair<uint, string>(RegularFileMode, "dirB/other"),
            });

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(path, out info, out error).ShouldBeTrue(error);
            info.Version.ShouldEqual((uint)4);
            info.EntryCount.ShouldEqual((uint)3);
            info.IsSparse.ShouldBeFalse();
        }

        [TestCase]
        public void SparseIndexVersion4DetectsDirectoryEntry()
        {
            string path = this.WriteIndex(version: 4, new List<KeyValuePair<uint, string>>
            {
                new KeyValuePair<uint, string>(RegularFileMode, "top.txt"),
                new KeyValuePair<uint, string>(DirectoryMode, "collapsed/"),
            });

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(path, out info, out error).ShouldBeTrue(error);
            info.EntryCount.ShouldEqual((uint)2);
            info.IsSparse.ShouldBeTrue();
        }

        [TestCase]
        public void SparseIndexVersion2DetectsDirectoryEntry()
        {
            string path = this.WriteIndex(version: 2, new List<KeyValuePair<uint, string>>
            {
                new KeyValuePair<uint, string>(RegularFileMode, "top.txt"),
                new KeyValuePair<uint, string>(DirectoryMode, "collapsed/"),
            });

            GitIndexInfo info;
            string error;
            GitIndexInspector.TryReadIndexInfo(path, out info, out error).ShouldBeTrue(error);
            info.IsSparse.ShouldBeTrue();
        }

        private string WriteIndex(uint version, List<KeyValuePair<uint, string>> entries)
        {
            string path = Path.Combine(this.testDirectory, "index");
            File.WriteAllBytes(path, BuildIndex(version, entries));
            return path;
        }

        private static byte[] BuildIndex(uint version, List<KeyValuePair<uint, string>> entries)
        {
            List<byte> bytes = new List<byte>();

            bytes.AddRange(new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C' });
            AppendUInt32BigEndian(bytes, version);
            AppendUInt32BigEndian(bytes, (uint)entries.Count);

            string previousName = string.Empty;
            foreach (KeyValuePair<uint, string> entry in entries)
            {
                uint mode = entry.Key;
                string name = entry.Value;

                byte[] fixedHeader = new byte[62];
                AppendUInt32BigEndianAt(fixedHeader, 24, mode);
                int nameLength = name.Length;
                fixedHeader[60] = (byte)((nameLength >> 8) & 0xFF);
                fixedHeader[61] = (byte)(nameLength & 0xFF);
                bytes.AddRange(fixedHeader);

                byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);

                if (version == 4)
                {
                    int common = CommonPrefixLength(previousName, name);
                    int toRemove = previousName.Length - common;
                    bytes.AddRange(EncodeVarint(toRemove));
                    for (int i = common; i < nameBytes.Length; i++)
                    {
                        bytes.Add(nameBytes[i]);
                    }

                    bytes.Add(0);
                    previousName = name;
                }
                else
                {
                    bytes.AddRange(nameBytes);
                    int entryLength = 62 + nameLength;
                    int numNullBytes = 8 - (entryLength % 8);
                    for (int i = 0; i < numNullBytes; i++)
                    {
                        bytes.Add(0);
                    }
                }
            }

            // A real index ends with a 20-byte trailing checksum. The inspector does not read it,
            // but include filler so the file size is representative.
            bytes.AddRange(new byte[20]);

            return bytes.ToArray();
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int max = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < max && a[i] == b[i])
            {
                i++;
            }

            return i;
        }

        // Matches git's encode_varint (varint.c), the format its index v4 path compression uses.
        private static byte[] EncodeVarint(int value)
        {
            byte[] scratch = new byte[16];
            int pos = scratch.Length - 1;
            scratch[pos] = (byte)(value & 127);
            value >>= 7;
            while (value != 0)
            {
                value -= 1;
                scratch[--pos] = (byte)(128 | (value & 127));
                value >>= 7;
            }

            int length = scratch.Length - pos;
            byte[] result = new byte[length];
            Array.Copy(scratch, pos, result, 0, length);
            return result;
        }

        private static void AppendUInt32BigEndian(List<byte> bytes, uint value)
        {
            bytes.Add((byte)((value >> 24) & 0xFF));
            bytes.Add((byte)((value >> 16) & 0xFF));
            bytes.Add((byte)((value >> 8) & 0xFF));
            bytes.Add((byte)(value & 0xFF));
        }

        private static void AppendUInt32BigEndianAt(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
