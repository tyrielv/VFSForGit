using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;

namespace GVFS.Virtualization.Projection
{
    public partial class GitIndexProjection
    {
        /// <summary>
        /// Reads and, when needed, downloads the tree objects that a sparse-directory entry
        /// collapses. Kept behind <see cref="GitIndexProjection.sparseIndexExpansionEnabled"/> so
        /// the default behavior is unchanged.
        /// </summary>
        internal interface IProjectionTreeReader
        {
            /// <summary>
            /// Read a locally-present tree, calling <paramref name="visitor"/> once per entry.
            /// Does no network work.
            /// </summary>
            TreeEnumerationResult TryReadTree(string treeSha, TreeEntryVisitor visitor);

            /// <summary>
            /// Download the given tree object into the local object store.
            /// Returns true when the object is present afterward.
            /// </summary>
            bool TryDownloadTree(string treeSha);
        }

        /// <summary>
        /// The tree reader used at run time. Reads through the in-process libgit2 repo and
        /// downloads missing trees through <see cref="GVFSGitObjects"/>.
        /// </summary>
        internal class LibGit2ProjectionTreeReader : IProjectionTreeReader
        {
            private readonly GitRepo repository;
            private readonly GVFSGitObjects gitObjects;

            public LibGit2ProjectionTreeReader(GitRepo repository, GVFSGitObjects gitObjects)
            {
                this.repository = repository;
                this.gitObjects = gitObjects;
            }

            public TreeEnumerationResult TryReadTree(string treeSha, TreeEntryVisitor visitor)
            {
                return this.repository.TryEnumerateTree(treeSha, visitor);
            }

            public bool TryDownloadTree(string treeSha)
            {
                return this.gitObjects.TryDownloadAndSaveObject(treeSha, GVFSGitObjects.RequestSource.ProjectionBuild)
                    == Common.Git.GitObjects.DownloadAndSaveObjectResult.Success;
            }
        }

        /// <summary>
        /// Expands a sparse-directory entry's collapsed subtree into the projection, producing the
        /// same <see cref="FolderData"/> and <see cref="FileData"/> shape the projection already
        /// serves for a full index. The walk is iterative (an explicit stack), reads tree objects
        /// through <see cref="IProjectionTreeReader"/>, and downloads a missing tree once before
        /// retrying. A permanent miss, a corrupt tree, or a non-tree object fails the projection
        /// build so GVFS never serves a partial working directory.
        /// </summary>
        /// <remarks>
        /// The expander is single-use per projection build thread and is not thread safe. It
        /// reuses one visitor delegate and one 20-byte SHA buffer to avoid per-entry allocation,
        /// matching the allocation discipline of the index parser.
        /// </remarks>
        internal class SparseDirectoryExpander
        {
            private const int RawShaLength = 20;

            private readonly ITracer tracer;
            private readonly IProjectionTreeReader treeReader;
            private readonly bool platformSupportsFileMode;
            private readonly SparseFolderData rootSparseFolder;
            private readonly Dictionary<string, FileTypeAndMode> nonDefaultFileTypesAndModes;
            private readonly TreeEntryVisitor visitor;
            private readonly byte[] shaBuffer = new byte[RawShaLength];

            private Stack<TreeFrame> stack;

            // The frame currently being read. Set before each tree read so the visitor can
            // attach children to the right folder without a per-folder closure allocation.
            private FolderData currentFolder;
            private string currentGitPath;
            private FrameInclusion currentInclusion;

            public SparseDirectoryExpander(
                ITracer tracer,
                IProjectionTreeReader treeReader,
                bool platformSupportsFileMode,
                SparseFolderData rootSparseFolder,
                Dictionary<string, FileTypeAndMode> nonDefaultFileTypesAndModes)
            {
                this.tracer = tracer;
                this.treeReader = treeReader;
                this.platformSupportsFileMode = platformSupportsFileMode;
                this.rootSparseFolder = rootSparseFolder;
                this.nonDefaultFileTypesAndModes = nonDefaultFileTypesAndModes;
                this.visitor = this.VisitEntry;
            }

            /// <summary>
            /// Expand the collapsed subtree rooted at <paramref name="rootFolder"/>.
            /// </summary>
            /// <param name="rootFolder">The FolderData for the collapsed folder (already created in the tree).</param>
            /// <param name="rootGitPath">
            /// The collapsed folder's git-relative path, or null on platforms that do not record
            /// file modes. Used only to key <see cref="nonDefaultFileTypesAndModes"/>.
            /// </param>
            /// <param name="rootTreeSha">The 40-character hex SHA of the collapsed folder's tree.</param>
            /// <param name="rootInclusion">The sparse-inclusion frame for the collapsed folder.</param>
            public void Expand(FolderData rootFolder, string rootGitPath, string rootTreeSha, FrameInclusion rootInclusion)
            {
                if (this.stack == null)
                {
                    this.stack = new Stack<TreeFrame>();
                }
                else
                {
                    this.stack.Clear();
                }

                this.stack.Push(new TreeFrame(rootFolder, rootTreeSha, rootGitPath, rootInclusion));

                while (this.stack.Count > 0)
                {
                    TreeFrame frame = this.stack.Pop();
                    this.currentFolder = frame.Folder;
                    this.currentGitPath = frame.GitPath;
                    this.currentInclusion = frame.Inclusion;

                    TreeEnumerationResult result = this.treeReader.TryReadTree(frame.TreeSha, this.visitor);
                    if (result == TreeEnumerationResult.MissingTree)
                    {
                        // The tree is not local. Download it once (under the projection write lock)
                        // and retry. The visitor was not called for a missing tree, so no partial
                        // state was added before the retry.
                        if (this.treeReader.TryDownloadTree(frame.TreeSha))
                        {
                            result = this.treeReader.TryReadTree(frame.TreeSha, this.visitor);
                        }
                    }

                    if (result != TreeEnumerationResult.Success)
                    {
                        // Fail fast. Never serve a partial projection.
                        EventMetadata metadata = CreateEventMetadata();
                        metadata.Add("treeSha", frame.TreeSha);
                        metadata.Add("result", result.ToString());
                        metadata.Add(TracingConstants.MessageKey.ErrorMessage, "Failed to expand sparse-directory tree");
                        this.tracer.RelatedError(metadata, $"{nameof(SparseDirectoryExpander)}: failed to expand tree {frame.TreeSha} ({result})");

                        throw new InvalidDataException(
                            $"Failed to expand sparse-directory tree '{frame.TreeSha}': {result}. " +
                            "The sparse index references a tree that is missing, corrupt, or not a tree. " +
                            "Cannot build a complete projection.");
                    }
                }
            }

            private unsafe void VisitEntry(ReadOnlySpan<byte> nameUtf8, ReadOnlySpan<byte> objectId, ushort gitFileMode, bool isTree)
            {
                if (nameUtf8.Length == 0)
                {
                    throw new InvalidDataException("Encountered a tree entry with an empty name while expanding a sparse directory");
                }

                if (objectId.Length != RawShaLength)
                {
                    throw new InvalidDataException($"Encountered a tree entry with a {objectId.Length}-byte object id (expected {RawShaLength}) while expanding a sparse directory");
                }

                LazyUTF8String name;
                fixed (byte* namePtr = nameUtf8)
                {
                    name = LazyUTF8String.FromByteArray(namePtr, nameUtf8.Length);
                }

                if (isTree)
                {
                    FrameInclusion childInclusion = this.ComputeChildInclusion(this.currentInclusion, name);
                    FolderData childFolder = this.currentFolder.AddChildFolder(name, childInclusion.Included);

                    objectId.CopyTo(this.shaBuffer);
                    string childTreeSha = SHA1Util.HexStringFromBytes(this.shaBuffer);

                    string childGitPath = this.currentGitPath == null
                        ? null
                        : this.currentGitPath + GVFSConstants.GitPathSeparatorString + name.GetString();

                    this.stack.Push(new TreeFrame(childFolder, childTreeSha, childGitPath, childInclusion));
                }
                else
                {
                    objectId.CopyTo(this.shaBuffer);
                    this.currentFolder.AddChildFile(name, this.shaBuffer);

                    if (this.platformSupportsFileMode)
                    {
                        FileTypeAndMode typeAndMode = new FileTypeAndMode(gitFileMode);
                        if (typeAndMode.Type != FileType.Regular || typeAndMode.Mode != FileMode644)
                        {
                            string gitPath = this.currentGitPath + GVFSConstants.GitPathSeparatorString + name.GetString();
                            this.nonDefaultFileTypesAndModes[gitPath] = typeAndMode;
                        }
                    }
                }
            }

            /// <summary>
            /// Compute the sparse-inclusion frame for a child folder. Mirrors the incremental
            /// inclusion walk in <see cref="SortedFolderEntries.GetOrAddFolder"/>, but carries the
            /// matched sparse node on the parent frame so each step is O(1) instead of walking from
            /// the sparse root. When no gvfs sparse cone is set (the common case), every folder is
            /// included and no name string is allocated.
            /// </summary>
            private FrameInclusion ComputeChildInclusion(FrameInclusion parent, LazyUTF8String childName)
            {
                if (this.rootSparseFolder.Children.Count == 0)
                {
                    return FrameInclusion.AllIncluded;
                }

                if (!parent.Included)
                {
                    return FrameInclusion.Excluded;
                }

                if (parent.Recursive)
                {
                    return FrameInclusion.AllIncluded;
                }

                SparseFolderData parentNode = parent.Node;
                if (parentNode != null && parentNode.Children.TryGetValue(childName.GetString(), out SparseFolderData childNode))
                {
                    return new FrameInclusion(included: true, recursive: childNode.IsRecursive, node: childNode);
                }

                return FrameInclusion.Excluded;
            }

            private struct TreeFrame
            {
                public TreeFrame(FolderData folder, string treeSha, string gitPath, FrameInclusion inclusion)
                {
                    this.Folder = folder;
                    this.TreeSha = treeSha;
                    this.GitPath = gitPath;
                    this.Inclusion = inclusion;
                }

                public FolderData Folder { get; }
                public string TreeSha { get; }
                public string GitPath { get; }
                public FrameInclusion Inclusion { get; }
            }
        }

        /// <summary>
        /// The gvfs sparse-cone inclusion state carried down the tree walk while expanding a
        /// sparse-directory entry. It lets each child's inclusion be computed from its parent in
        /// O(1), mirroring <see cref="SortedFolderEntries.GetOrAddFolder"/>.
        /// </summary>
        internal struct FrameInclusion
        {
            /// <summary>All folders are included: no gvfs sparse cone is set, or a recursive cone entry covers this subtree.</summary>
            public static readonly FrameInclusion AllIncluded = new FrameInclusion(included: true, recursive: true, node: null);

            /// <summary>This folder is outside the gvfs sparse cone.</summary>
            public static readonly FrameInclusion Excluded = new FrameInclusion(included: false, recursive: false, node: null);

            public FrameInclusion(bool included, bool recursive, SparseFolderData node)
            {
                this.Included = included;
                this.Recursive = recursive;
                this.Node = node;
            }

            /// <summary>Whether this folder is inside the gvfs sparse cone (IsIncluded on its FolderData).</summary>
            public bool Included { get; }

            /// <summary>Whether every descendant of this folder is unconditionally included.</summary>
            public bool Recursive { get; }

            /// <summary>The matched sparse-cone node for this folder, used to evaluate its children. Null when included recursively or excluded.</summary>
            public SparseFolderData Node { get; }
        }
    }
}
