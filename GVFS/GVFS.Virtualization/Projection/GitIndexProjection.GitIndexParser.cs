using GVFS.Common;
using GVFS.Common.Database;
using GVFS.Common.Tracing;
using GVFS.Virtualization.Background;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GVFS.Virtualization.Projection
{
    public partial class GitIndexProjection
    {
        internal partial class GitIndexParser
        {
            public const int PageSize = 512 * 1024;

            private const ushort ExtendedBit = 0x4000;
            private const ushort SkipWorktreeBit = 0x4000;

            private Stream indexStream;
            private byte[] page;
            private int nextByteIndex;

            private GitIndexProjection projection;

            /// <summary>
            /// Set only while parsing a clone-time seed index, which carries no skip-worktree bits
            /// (see <see cref="EntryIsProjected"/>).
            /// </summary>
            private bool assumeSkipWorktree;

            /// <summary>
            /// UTF-8 paths that are materialized in the working tree, used only in seed mode. These
            /// stand in for the paths the virtual-filesystem hook would report as present.
            /// </summary>
            private List<byte[]> materializedPathBytes = new List<byte[]>();

            /// <summary>
            /// Entries seen and entries projected during the current <see cref="RebuildProjection"/>.
            /// Used only to detect a degenerate result; see the check at the end of that method.
            /// </summary>
            private long entriesSeenInBuild;
            private long entriesProjectedInBuild;

            /// <summary>
            /// A single GitIndexEntry instance used for parsing all entries in the index when building the projection
            /// </summary>
            private GitIndexEntry resuableProjectionBuildingIndexEntry = new GitIndexEntry(buildingNewProjection: true);

            /// <summary>
            /// A single GitIndexEntry instance used by the background task thread for parsing all entries in the index
            /// </summary>
            private GitIndexEntry resuableBackgroundTaskThreadIndexEntry = new GitIndexEntry(buildingNewProjection: false);

            public GitIndexParser(GitIndexProjection projection)
            {
                this.projection = projection;
                this.page = new byte[PageSize];
            }

            public enum MergeStage : byte
            {
                NoConflicts = 0,
                CommonAncestor = 1,
                Yours = 2,
                Theirs = 3
            }

            public static void ValidateIndex(ITracer tracer, Stream indexStream)
            {
                GitIndexParser indexParser = new GitIndexParser(null);
                FileSystemTaskResult result = indexParser.ParseIndex(tracer, indexStream, indexParser.resuableProjectionBuildingIndexEntry, ValidateIndexEntry);

                if (result != FileSystemTaskResult.Success)
                {
                    // ValidateIndex should always result in FileSystemTaskResult.Success (or a thrown exception)
                    throw new InvalidOperationException($"{nameof(ValidateIndex)} failed: {result.ToString()}");
                }
            }

            /// <summary>
            /// Count unique directories in the index by scanning entry paths for separators.
            /// Uses the existing index parser to read entries, avoiding a custom index parser.
            /// </summary>
            public static int CountIndexFolders(ITracer tracer, Stream indexStream)
            {
                HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GitIndexParser indexParser = new GitIndexParser(null);

                FileSystemTaskResult result = indexParser.ParseIndex(
                    tracer,
                    indexStream,
                    indexParser.resuableProjectionBuildingIndexEntry,
                    entry =>
                    {
                        // Match the same filter as AddIndexEntryToProjection so the
                        // fallback folder count agrees with the mounted projection.
                        if (!((entry.MergeState != MergeStage.CommonAncestor && entry.SkipWorktree) || entry.MergeState == MergeStage.Yours))
                        {
                            return FileSystemTaskResult.Success;
                        }

                        // Extract unique parent directories from the raw path buffer
                        string path = Encoding.UTF8.GetString(entry.PathBuffer, 0, entry.PathLength);
                        int lastSlash = path.LastIndexOf('/');
                        while (lastSlash > 0)
                        {
                            string dir = path.Substring(0, lastSlash);
                            if (!dirs.Add(dir))
                            {
                                break;
                            }

                            lastSlash = dir.LastIndexOf('/');
                        }

                        return FileSystemTaskResult.Success;
                    });

                if (result != FileSystemTaskResult.Success)
                {
                    throw new InvalidOperationException($"{nameof(CountIndexFolders)} failed: {result}");
                }

                return dirs.Count;
            }

            /// <summary>
            /// Parse an index and return the full path of every entry, in index order. This is a
            /// test and diagnostic helper: it exercises the same v4 prefix-decompression path as
            /// projection building, but without a <see cref="GitIndexProjection"/>. It reads the
            /// raw path buffer, so a sparse-directory entry's trailing '/' is preserved. Use it to
            /// verify that a trailing-slash entry does not corrupt the next entry's decompressed
            /// path.
            /// </summary>
            internal static List<string> GetIndexPaths(ITracer tracer, Stream indexStream)
            {
                List<string> paths = new List<string>();
                GitIndexParser indexParser = new GitIndexParser(null);

                FileSystemTaskResult result = indexParser.ParseIndex(
                    tracer,
                    indexStream,
                    indexParser.resuableProjectionBuildingIndexEntry,
                    entry =>
                    {
                        paths.Add(Encoding.UTF8.GetString(entry.PathBuffer, 0, entry.PathLength));
                        return FileSystemTaskResult.Success;
                    });

                if (result != FileSystemTaskResult.Success)
                {
                    throw new InvalidOperationException($"{nameof(GetIndexPaths)} failed: {result}");
                }

                return paths;
            }

            public void RebuildProjection(ITracer tracer, Stream indexStream)
            {
                this.RebuildProjection(tracer, indexStream, seedMode: false, materializedPaths: null);
            }

            /// <summary>
            /// Rebuild the projection from an index stream.
            /// </summary>
            /// <param name="seedMode">
            /// True when the stream is a clone-time seed index. A seed carries no skip-worktree
            /// bits, so every entry is treated as projected except the supplied materialized
            /// paths. See <see cref="EntryIsProjected"/> for why that matches what git computes.
            /// </param>
            /// <param name="materializedPaths">
            /// Git-relative paths present in the working tree, used only in seed mode.
            /// </param>
            public void RebuildProjection(ITracer tracer, Stream indexStream, bool seedMode, IEnumerable<string> materializedPaths)
            {
                if (this.projection == null)
                {
                    throw new InvalidOperationException($"{nameof(this.projection)} cannot be null when calling {nameof(this.RebuildProjection)}");
                }

                this.assumeSkipWorktree = seedMode;
                this.materializedPathBytes.Clear();
                if (seedMode && materializedPaths != null)
                {
                    foreach (string materializedPath in materializedPaths)
                    {
                        if (!string.IsNullOrEmpty(materializedPath))
                        {
                            this.materializedPathBytes.Add(
                                Encoding.UTF8.GetBytes(materializedPath.Replace('\\', GVFSConstants.GitPathSeparator)));
                        }
                    }
                }

                try
                {
                    this.projection.ClearProjectionCaches();
                    this.entriesSeenInBuild = 0;
                    this.entriesProjectedInBuild = 0;
                    FileSystemTaskResult result = this.ParseIndex(
                        tracer,
                        indexStream,
                        this.resuableProjectionBuildingIndexEntry,
                        this.AddIndexEntryToProjection);

                    if (result != FileSystemTaskResult.Success)
                    {
                        // RebuildProjection should always result in FileSystemTaskResult.Success (or a thrown exception)
                        throw new InvalidOperationException($"{nameof(this.RebuildProjection)}: {nameof(GitIndexParser.ParseIndex)} failed to {nameof(this.AddIndexEntryToProjection)}");
                    }

                    // An index with entries that projects none of them is always wrong, and it is
                    // wrong in the worst way: the mount reports ready and serves an empty working
                    // tree. It happens when the stored skip-worktree bits do not mean what this
                    // parser assumes. Under a virtual filesystem those bits are not stored state --
                    // git recomputes them on every index read -- so an index that is perfectly
                    // valid to git can carry none at all. Fail here instead of projecting nothing.
                    if (this.entriesSeenInBuild > 0 && this.entriesProjectedInBuild == 0)
                    {
                        throw new InvalidDataException(
                            $"Refusing to build an empty projection from an index with {this.entriesSeenInBuild} entries. " +
                            "No entry was marked skip-worktree, which usually means the index was written by a tool that " +
                            "does not record those bits. To recover, remount; if that fails, run 'gvfs repair'.");
                    }
                }
                finally
                {
                    // Seed mode applies to a single parse only; a later rebuild reads a real index.
                    this.assumeSkipWorktree = false;
                    this.materializedPathBytes.Clear();
                }
            }

            public FileSystemTaskResult AddMissingModifiedFilesAndRemoveThemFromPlaceholderList(
                ITracer tracer,
                Stream indexStream)
            {
                if (this.projection == null)
                {
                    throw new InvalidOperationException($"{nameof(this.projection)} cannot be null when calling {nameof(this.AddMissingModifiedFilesAndRemoveThemFromPlaceholderList)}");
                }

                HashSet<string> filePlaceholders = this.projection.placeholderDatabase.GetAllFilePaths();

                tracer.RelatedEvent(
                    EventLevel.Informational,
                    $"{nameof(this.AddMissingModifiedFilesAndRemoveThemFromPlaceholderList)}_FilePlaceholderCount",
                    new EventMetadata
                    {
                        { "FilePlaceholderCount", filePlaceholders.Count }
                    });

                FileSystemTaskResult result = this.ParseIndex(
                    tracer,
                    indexStream,
                    this.resuableBackgroundTaskThreadIndexEntry,
                    (data) => this.AddEntryToModifiedPathsAndRemoveFromPlaceholdersIfNeeded(data, filePlaceholders));

                if (result != FileSystemTaskResult.Success)
                {
                    return result;
                }

                // Any paths that were not found in the index need to be added to ModifiedPaths
                // and removed from the placeholder list
                foreach (string path in filePlaceholders)
                {
                    result = this.projection.AddModifiedPath(path);
                    if (result != FileSystemTaskResult.Success)
                    {
                        return result;
                    }

                    this.projection.RemoveFromPlaceholderList(path);
                }

                return FileSystemTaskResult.Success;
            }

            private static FileSystemTaskResult ValidateIndexEntry(GitIndexEntry data)
            {
                if (data.PathLength <= 0 || data.PathBuffer[0] == 0)
                {
                    throw new InvalidDataException("Zero-length path found in index");
                }

                FailIfSparseDirectoryEntry(data);

                return FileSystemTaskResult.Success;
            }

            /// <summary>
            /// Fail fast when the index contains a sparse-directory entry (git index.sparse
            /// format). Such an entry has mode 040000, the skip-worktree bit, and a trailing '/'.
            /// GVFS builds its projection from the index and cannot yet expand a sparse
            /// directory's tree. If it accepted the entry it would create a folder with an empty
            /// child name and crash the mount later during ProjFS enumeration, after the mount
            /// reported the repository ready. Worse, that projection would silently omit every
            /// collapsed subtree. Shutting down here, at parse time, avoids serving an incomplete
            /// working directory. Detection uses the trailing '/', not the mode field, because
            /// Windows skips mode parsing (SupportsFileMode is false).
            /// </summary>
            private static void FailIfSparseDirectoryEntry(GitIndexEntry data)
            {
                if (data.PathEndsInSlash)
                {
                    string path = Encoding.UTF8.GetString(data.PathBuffer, 0, data.PathLength);
                    throw new InvalidDataException(
                        $"Unsupported sparse index. Entry '{path}' is a sparse-directory entry (git index.sparse). " +
                        "This version of VFS for Git cannot project a sparse index. " +
                        "To recover, run 'gvfs sparse-index --disable' from the enlistment. It unmounts if needed, " +
                        "expands the index to full format, and clears the sparse-index config. Then run 'gvfs mount'.");
                }
            }

            private FileSystemTaskResult AddIndexEntryToProjection(GitIndexEntry data)
            {
                this.entriesSeenInBuild++;

                // Never want to project the common ancestor even if the skip worktree bit is on
                if ((data.MergeState != MergeStage.CommonAncestor && this.EntryIsProjected(data)) || data.MergeState == MergeStage.Yours)
                {
                    this.entriesProjectedInBuild++;

                    if (data.IsSparseDirectory)
                    {
                        // A sparse-directory entry (git index.sparse) collapses a folder to its
                        // tree OID. When expansion is disabled, fail fast before building a
                        // projection that omits the collapsed subtree and crashes ProjFS
                        // enumeration. When enabled, expand the tree into the projection.
                        if (!this.projection.SparseIndexExpansionEnabled)
                        {
                            FailIfSparseDirectoryEntry(data);
                        }

                        ValidateSparseDirectoryEntry(data);
                        this.projection.ExpandSparseDirectory(data);
                    }
                    else
                    {
                        data.BuildingProjection_ParsePath();
                        this.projection.AddItemFromIndexEntry(data);
                    }
                }
                else
                {
                    data.ClearLastParent();
                }

                return FileSystemTaskResult.Success;
            }

            /// <summary>
            /// Decide whether an index entry belongs in the projection.
            /// </summary>
            /// <remarks>
            /// Normally this is the entry's skip-worktree bit: GVFS projects the entries git has
            /// marked as absent from the working tree.
            /// <para>
            /// A clone-time seed index is different. It is produced by <c>git read-tree</c>, which
            /// writes no skip-worktree bits at all, so reading the bit would project nothing. That
            /// is not a defect in the seed: under a virtual filesystem the bit is not stored
            /// state. <c>apply_virtualfilesystem()</c> recomputes it on every index read by
            /// setting CE_SKIP_WORKTREE on every entry and then clearing it only for the paths the
            /// virtual-filesystem hook reports as present. Seed mode performs exactly that
            /// computation, using the modified-paths database as the set of present paths, so the
            /// projection it produces matches the one built from an index git wrote.
            /// </para>
            /// </remarks>
            private bool EntryIsProjected(GitIndexEntry data)
            {
                if (!this.assumeSkipWorktree)
                {
                    return data.SkipWorktree;
                }

                // A sparse-directory entry covers out-of-cone content, which is never
                // materialized, so it is always projected.
                if (data.IsSparseDirectory)
                {
                    return true;
                }

                // Compare against the materialized paths on the raw buffer. Decoding a string per
                // entry would allocate once for every entry in a multi-million entry index; the
                // materialized set is small (one path on a fresh clone), so a length check plus a
                // byte compare is far cheaper and allocates nothing.
                for (int i = 0; i < this.materializedPathBytes.Count; i++)
                {
                    byte[] candidate = this.materializedPathBytes[i];
                    if (candidate.Length != data.PathLength)
                    {
                        continue;
                    }

                    bool same = true;
                    for (int b = 0; b < candidate.Length; b++)
                    {
                        if (candidate[b] != data.PathBuffer[b])
                        {
                            same = false;
                            break;
                        }
                    }

                    if (same)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Validate the invariants of a sparse-directory entry before expanding it. On a
            /// platform that parses the mode field, the entry's type must be Directory (040000);
            /// on Windows the mode is not parsed, so the trailing '/' is the only signal and no
            /// further check applies here.
            /// </summary>
            private static void ValidateSparseDirectoryEntry(GitIndexEntry data)
            {
                if (GVFSPlatform.Instance.FileSystem.SupportsFileMode && data.TypeAndMode.Type != FileType.Directory)
                {
                    string path = Encoding.UTF8.GetString(data.PathBuffer, 0, data.PathLength);
                    throw new InvalidDataException(
                        $"Sparse-directory entry '{path}' has unexpected type {data.TypeAndMode.Type} (expected {FileType.Directory}).");
                }
            }

            /// <summary>
            /// Adjusts the modifed paths and placeholders list for an index entry.
            /// </summary>
            /// <param name="gitIndexEntry">Index entry</param>
            /// <param name="filePlaceholders">
            /// Dictionary of file placeholders.  AddEntryToModifiedPathsAndRemoveFromPlaceholdersIfNeeded will
            /// remove enties from filePlaceholders as they are found in the index.  After
            /// AddEntryToModifiedPathsAndRemoveFromPlaceholdersIfNeeded is called for all entries in the index
            /// filePlaceholders will contain only those placeholders that are not in the index.
            /// </param>
            private FileSystemTaskResult AddEntryToModifiedPathsAndRemoveFromPlaceholdersIfNeeded(
                GitIndexEntry gitIndexEntry,
                HashSet<string> filePlaceholders)
            {
                gitIndexEntry.BackgroundTask_ParsePath();
                string placeholderRelativePath = gitIndexEntry.BackgroundTask_GetPlatformRelativePath();

                FileSystemTaskResult result = FileSystemTaskResult.Success;

                if (!gitIndexEntry.SkipWorktree)
                {
                    // A git command (e.g. 'git reset --mixed') may have cleared a file's skip worktree bit without
                    // triggering an update to the projection. If git cleared the skip-worktree bit then git will
                    // be responsible for updating the file and we need to:
                    //    - Ensure this file is in GVFS's modified files database
                    //    - Remove this path from the placeholders list (if present)
                    result = this.projection.AddModifiedPath(placeholderRelativePath);

                    if (result == FileSystemTaskResult.Success)
                    {
                        if (filePlaceholders.Remove(placeholderRelativePath))
                        {
                            this.projection.RemoveFromPlaceholderList(placeholderRelativePath);
                        }
                    }
                }
                else
                {
                    filePlaceholders.Remove(placeholderRelativePath);
                }

                return result;
            }

            /// <summary>
            /// Takes an action on a GitIndexEntry using the index in indexStream
            /// </summary>
            /// <param name="indexStream">Stream for reading a git index file</param>
            /// <param name="entryAction">Action to take on each GitIndexEntry from the index</param>
            /// <returns>
            /// FileSystemTaskResult indicating success or failure of the specified action
            /// </returns>
            /// <remarks>
            /// Only the AddToModifiedFiles method because it updates the modified paths file can result
            /// in TryIndexAction returning a FileSystemTaskResult other than Success.  All other actions result in success (or an exception in the
            /// case of a corrupt index)
            /// </remarks>
            private FileSystemTaskResult ParseIndex(
                ITracer tracer,
                Stream indexStream,
                GitIndexEntry resuableParsedIndexEntry,
                Func<GitIndexEntry, FileSystemTaskResult> entryAction)
            {
                this.indexStream = indexStream;
                this.indexStream.Position = 0;
                this.ReadNextPage();

                if (this.page[0] != 'D' ||
                    this.page[1] != 'I' ||
                    this.page[2] != 'R' ||
                    this.page[3] != 'C')
                {
                    throw new InvalidDataException("Incorrect magic signature for index: " + string.Join(string.Empty, this.page.Take(4).Select(c => (char)c)));
                }

                this.Skip(4);
                uint indexVersion = this.ReadFromIndexHeader();
                if (indexVersion != 4)
                {
                    throw new InvalidDataException("Unsupported index version: " + indexVersion);
                }

                uint entryCount = this.ReadFromIndexHeader();

                // Don't want to flood the logs on large indexes so only log every 500ms
                const int LoggingTicksThreshold = 500;
                int nextLogTicks = Environment.TickCount + LoggingTicksThreshold;

                SortedFolderEntries.InitializePools(tracer, entryCount);
                LazyUTF8String.InitializePools(tracer, entryCount);

                resuableParsedIndexEntry.ClearLastParent();
                int previousPathLength = 0;

                bool parseMode = GVFSPlatform.Instance.FileSystem.SupportsFileMode;
                FileSystemTaskResult result = FileSystemTaskResult.Success;
                for (int i = 0; i < entryCount; i++)
                {
                    if (parseMode)
                    {
                        this.Skip(26);

                        // 4-bit object type
                        //     valid values in binary are 1000(regular file), 1010(symbolic link) and 1110(gitlink)
                        // 3-bit unused
                        // 9-bit unix permission. Only 0755 and 0644 are valid for regular files. (Legacy repos can also contain 664)
                        //     Symbolic links and gitlinks have value 0 in this field.
                        ushort indexFormatTypeAndMode = this.ReadUInt16();

                        FileTypeAndMode typeAndMode = new FileTypeAndMode(indexFormatTypeAndMode);

                        switch (typeAndMode.Type)
                        {
                            case FileType.Regular:
                                if (typeAndMode.Mode != FileMode755 &&
                                    typeAndMode.Mode != FileMode644 &&
                                    typeAndMode.Mode != FileMode664)
                                {
                                    throw new InvalidDataException($"Invalid file mode {typeAndMode.GetModeAsOctalString()} found for regular file in index");
                                }

                                break;

                            case FileType.SymLink:
                            case FileType.GitLink:
                                if (typeAndMode.Mode != 0)
                                {
                                    throw new InvalidDataException($"Invalid file mode {typeAndMode.GetModeAsOctalString()} found for link file({typeAndMode.Type:X}) in index");
                                }

                                break;

                            case FileType.Directory:
                                // Sparse-directory entry (git index.sparse). Mode bits must be
                                // zero; the entry carries a tree OID rather than file permissions.
                                if (typeAndMode.Mode != 0)
                                {
                                    throw new InvalidDataException($"Invalid file mode {typeAndMode.GetModeAsOctalString()} found for sparse directory in index");
                                }

                                break;

                            default:
                                throw new InvalidDataException($"Invalid file type {typeAndMode.Type:X} found in index");
                        }

                        resuableParsedIndexEntry.TypeAndMode = typeAndMode;

                        this.Skip(12);
                    }
                    else
                    {
                        this.Skip(40);
                    }

                    this.ReadSha(resuableParsedIndexEntry);

                    ushort flags = this.ReadUInt16();
                    if (flags == 0)
                    {
                        throw new InvalidDataException("Invalid flags found in index");
                    }

                    resuableParsedIndexEntry.MergeState = (MergeStage)((flags >> 12) & 3);
                    bool isExtended = (flags & ExtendedBit) == ExtendedBit;
                    resuableParsedIndexEntry.PathLength = (ushort)(flags & 0xFFF);

                    resuableParsedIndexEntry.SkipWorktree = false;
                    if (isExtended)
                    {
                        ushort extendedFlags = this.ReadUInt16();
                        resuableParsedIndexEntry.SkipWorktree = (extendedFlags & SkipWorktreeBit) == SkipWorktreeBit;
                    }

                    int replaceLength = this.ReadReplaceLength();
                    resuableParsedIndexEntry.ReplaceIndex = previousPathLength - replaceLength;
                    int bytesToRead = resuableParsedIndexEntry.PathLength - resuableParsedIndexEntry.ReplaceIndex + 1;
                    this.ReadPath(resuableParsedIndexEntry, resuableParsedIndexEntry.ReplaceIndex, bytesToRead);
                    previousPathLength = resuableParsedIndexEntry.PathLength;

                    result = entryAction.Invoke(resuableParsedIndexEntry);
                    if (result != FileSystemTaskResult.Success)
                    {
                        return result;
                    }

                    int curTicks = Environment.TickCount;
                    if (curTicks - nextLogTicks > 0)
                    {
                        tracer.RelatedInfo($"{i}/{entryCount} index entries parsed.");
                        nextLogTicks = curTicks + LoggingTicksThreshold;
                    }
                }

                tracer.RelatedInfo($"Finished parsing {entryCount} index entries.");
                return result;
            }

            private void ReadNextPage()
            {
                // Last page may be smaller than PageSize; partial fill is safe because
                // the parser stops after entryCount entries and never reads stale bytes.
#pragma warning disable CA2022 // Avoid inexact read
                this.indexStream.Read(this.page, 0, PageSize);
#pragma warning restore CA2022
                this.nextByteIndex = 0;
            }

            private int ReadReplaceLength()
            {
                int headerByte = this.ReadByte();
                int offset = headerByte & 0x7f;

                // Terminate the loop when the high bit is no longer set.
                for (int i = 0; (headerByte & 0x80) != 0; i++)
                {
                    headerByte = this.ReadByte();
                    if (headerByte < 0)
                    {
                        throw new EndOfStreamException("Unexpected end of stream while reading git index.");
                    }

                    offset += 1;
                    offset = (offset << 7) + (headerByte & 0x7f);
                }

                return offset;
            }

            private void ReadSha(GitIndexEntry indexEntryData)
            {
                if (this.nextByteIndex + 20 <= PageSize)
                {
                    Buffer.BlockCopy(this.page, this.nextByteIndex, indexEntryData.Sha, 0, 20);
                    this.Skip(20);
                }
                else
                {
                    int availableBytes = PageSize - this.nextByteIndex;
                    int remainingBytes = 20 - availableBytes;

                    if (availableBytes > 0)
                    {
                        Buffer.BlockCopy(this.page, this.nextByteIndex, indexEntryData.Sha, 0, availableBytes);
                    }

                    this.ReadNextPage();
                    Buffer.BlockCopy(this.page, this.nextByteIndex, indexEntryData.Sha, availableBytes, remainingBytes);
                    this.Skip(remainingBytes);
                }
            }

            private void ReadPath(GitIndexEntry indexEntryData, int replaceIndex, int byteCount)
            {
                if (this.nextByteIndex + byteCount <= PageSize)
                {
                    Buffer.BlockCopy(this.page, this.nextByteIndex, indexEntryData.PathBuffer, replaceIndex, byteCount);
                    this.Skip(byteCount);
                }
                else
                {
                    int availableBytes = PageSize - this.nextByteIndex;
                    int remainingBytes = byteCount - availableBytes;

                    if (availableBytes != 0)
                    {
                        this.ReadPath(indexEntryData, replaceIndex, availableBytes);
                    }

                    this.ReadNextPage();
                    this.ReadPath(indexEntryData, replaceIndex + availableBytes, remainingBytes);
                }
            }

            private uint ReadFromIndexHeader()
            {
                // This code should only get called for parsing the header, so we don't need to worry about wrapping around a page
                uint result = (uint)
                    (this.page[this.nextByteIndex] << 24 |
                    this.page[this.nextByteIndex + 1] << 16 |
                    this.page[this.nextByteIndex + 2] << 8 |
                    this.page[this.nextByteIndex + 3]);
                this.Skip(4);
                return result;
            }

            private ushort ReadUInt16()
            {
                if (this.nextByteIndex + 2 <= PageSize)
                {
                    ushort result = (ushort)
                        (this.page[this.nextByteIndex] << 8 |
                        this.page[this.nextByteIndex + 1]);
                    this.Skip(2);

                    return result;
                }
                else
                {
                    return (ushort)(this.ReadByte() << 8 | this.ReadByte());
                }
            }

            private byte ReadByte()
            {
                if (this.nextByteIndex < PageSize)
                {
                    byte result = this.page[this.nextByteIndex];

                    this.nextByteIndex++;

                    return result;
                }
                else
                {
                    this.ReadNextPage();
                    return this.ReadByte();
                }
            }

            private void Skip(int byteCount)
            {
                if (this.nextByteIndex + byteCount <= PageSize)
                {
                    this.nextByteIndex += byteCount;
                }
                else
                {
                    int availableBytes = PageSize - this.nextByteIndex;
                    int remainingBytes = byteCount - availableBytes;

                    this.ReadNextPage();
                    this.Skip(remainingBytes);
                }
            }
        }
    }
}
