using System;
using System.IO;

namespace GVFS.Common.Git
{
    /// <summary>
    /// Reads just enough of a git index file (.git/index) to report the numbers that make the
    /// sparse-index feature worth having: the format version, the entry count, the on-disk size,
    /// and whether the index is currently collapsed (sparse).
    /// </summary>
    /// <remarks>
    /// "Sparse" here means the index contains at least one sparse-directory entry - a cache entry
    /// whose mode is a directory (git mode 040000). A full index has none. The scan advances entry
    /// by entry and stops at the first directory entry, so a collapsed index (a handful of entries)
    /// is classified almost instantly. A full index has no directory entries, so the scan reads the
    /// whole file; that is acceptable because status is a user-invoked diagnostic, not a hot path.
    /// The index format is documented at
    /// https://github.com/git/git/blob/master/Documentation/gitformat-index.txt.
    /// </remarks>
    public static class GitIndexInspector
    {
        private const int BaseEntryLength = 62;
        private const int FixedEntryHeaderLength = 62;
        private const int ModeOffsetInEntry = 24;
        private const ushort ExtendedBit = 0x4000;
        private const ushort PathLengthMask = 0x0FFF;

        // Git object mode nibble for a directory (tree). 040000 octal == 0x4000; the high nibble
        // 0x4 identifies a directory entry, which is how a sparse-directory entry is encoded.
        private const uint ModeTypeMask = 0xF000;
        private const uint ModeTypeDirectory = 0x4000;

        private static readonly byte[] MagicSignature = new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C' };

        /// <summary>
        /// Reads index metadata from the file at <paramref name="indexPath"/>.
        /// </summary>
        /// <returns>
        /// True if the header was read and the sparse scan completed. False (with
        /// <paramref name="error"/> set) if the file is missing, is not a git index, or is
        /// truncated. On success <paramref name="info"/> is fully populated; on failure it is null.
        /// </returns>
        public static bool TryReadIndexInfo(string indexPath, out GitIndexInfo info, out string error)
        {
            info = null;
            error = null;

            if (string.IsNullOrEmpty(indexPath))
            {
                error = "Index path was not provided.";
                return false;
            }

            if (!File.Exists(indexPath))
            {
                error = "Index file does not exist: " + indexPath;
                return false;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(indexPath);
                long sizeInBytes = fileInfo.Length;

                using (FileStream fileStream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BufferedStream stream = new BufferedStream(fileStream, 1 << 16))
                {
                    byte[] headerBuffer = new byte[12];
                    if (!TryReadExactly(stream, headerBuffer, 0, headerBuffer.Length))
                    {
                        error = "Index file is too small to contain a header.";
                        return false;
                    }

                    if (headerBuffer[0] != MagicSignature[0] ||
                        headerBuffer[1] != MagicSignature[1] ||
                        headerBuffer[2] != MagicSignature[2] ||
                        headerBuffer[3] != MagicSignature[3])
                    {
                        error = "Index file has an incorrect magic signature.";
                        return false;
                    }

                    uint version = ReadUInt32BigEndian(headerBuffer, 4);
                    uint entryCount = ReadUInt32BigEndian(headerBuffer, 8);

                    if (version < 2 || version > 4)
                    {
                        error = "Unsupported index version: " + version;
                        return false;
                    }

                    bool isSparse;
                    if (!TryScanForDirectoryEntry(stream, version, entryCount, out isSparse, out error))
                    {
                        return false;
                    }

                    info = new GitIndexInfo(version, entryCount, sizeInBytes, isSparse);
                    return true;
                }
            }
            catch (IOException e)
            {
                error = "Failed to read index file: " + e.Message;
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                error = "Failed to read index file: " + e.Message;
                return false;
            }
        }

        private static bool TryScanForDirectoryEntry(Stream stream, uint version, uint entryCount, out bool isSparse, out string error)
        {
            isSparse = false;
            error = null;

            byte[] fixedBuffer = new byte[FixedEntryHeaderLength];
            int previousPathLength = 0;

            for (uint i = 0; i < entryCount; i++)
            {
                if (!TryReadExactly(stream, fixedBuffer, 0, FixedEntryHeaderLength))
                {
                    error = "Index file is truncated (entry " + i + " of " + entryCount + ").";
                    return false;
                }

                uint mode = ReadUInt32BigEndian(fixedBuffer, ModeOffsetInEntry);
                if ((mode & ModeTypeMask) == ModeTypeDirectory)
                {
                    isSparse = true;
                    return true;
                }

                ushort flags = (ushort)((fixedBuffer[60] << 8) | fixedBuffer[61]);
                bool isExtended = (flags & ExtendedBit) == ExtendedBit;
                int pathLength = flags & PathLengthMask;
                int entryLength = BaseEntryLength + pathLength;

                if (isExtended && version > 2)
                {
                    if (!TrySkip(stream, 2))
                    {
                        error = "Index file is truncated reading extended flags.";
                        return false;
                    }

                    entryLength += 2;
                }

                if (version == 4)
                {
                    int replaceLength;
                    if (!TryReadVariableWidthOffset(stream, out replaceLength))
                    {
                        error = "Index file is truncated reading a compressed path.";
                        return false;
                    }

                    int retainedPrefix = previousPathLength - replaceLength;
                    int suffixPlusNull = (pathLength - retainedPrefix) + 1;
                    if (retainedPrefix < 0 || suffixPlusNull < 1)
                    {
                        error = "Index file has a malformed compressed path entry.";
                        return false;
                    }

                    if (!TrySkip(stream, suffixPlusNull))
                    {
                        error = "Index file is truncated reading a compressed path.";
                        return false;
                    }

                    previousPathLength = pathLength;
                }
                else
                {
                    int numNullBytes = 8 - (entryLength % 8);
                    if (!TrySkip(stream, pathLength + numNullBytes))
                    {
                        error = "Index file is truncated reading a padded path.";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }

        private static bool TrySkip(Stream stream, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (stream.ReadByte() < 0)
                {
                    return false;
                }
            }

            return true;
        }

        // Reads the variable-width offset that prefixes a v4 (prefix-compressed) path. See
        // https://github.com/git/git/blob/master/Documentation/gitformat-index.txt.
        private static bool TryReadVariableWidthOffset(Stream stream, out int offset)
        {
            offset = 0;
            int headerByte = stream.ReadByte();
            if (headerByte < 0)
            {
                return false;
            }

            offset = headerByte & 0x7F;
            while ((headerByte & 0x80) != 0)
            {
                headerByte = stream.ReadByte();
                if (headerByte < 0)
                {
                    return false;
                }

                offset += 1;
                offset = (offset << 7) + (headerByte & 0x7F);
            }

            return true;
        }

        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }
    }
}
