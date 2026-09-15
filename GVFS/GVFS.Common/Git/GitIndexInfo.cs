namespace GVFS.Common.Git
{
    /// <summary>
    /// Metadata read from a git index file by <see cref="GitIndexInspector"/>.
    /// </summary>
    public class GitIndexInfo
    {
        public GitIndexInfo(uint version, uint entryCount, long sizeInBytes, bool isSparse)
        {
            this.Version = version;
            this.EntryCount = entryCount;
            this.SizeInBytes = sizeInBytes;
            this.IsSparse = isSparse;
        }

        /// <summary>The index format version (2, 3, or 4).</summary>
        public uint Version { get; }

        /// <summary>The number of entries recorded in the index header.</summary>
        public uint EntryCount { get; }

        /// <summary>The size of the index file on disk, in bytes.</summary>
        public long SizeInBytes { get; }

        /// <summary>True when the index contains at least one sparse-directory entry.</summary>
        public bool IsSparse { get; }
    }
}
