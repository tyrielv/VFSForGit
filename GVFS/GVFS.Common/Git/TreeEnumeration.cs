using System;

namespace GVFS.Common.Git
{
    /// <summary>
    /// Result of a local tree-enumeration attempt.
    /// The states are distinct so callers can fail fast on corruption and
    /// download-then-retry on a missing tree.
    /// </summary>
    public enum TreeEnumerationResult
    {
        /// <summary>
        /// The tree object was found locally, was a real tree, and every entry
        /// was read and passed to the visitor.
        /// </summary>
        Success,

        /// <summary>
        /// The tree object is not present in the local object store.
        /// The visitor was not called. The caller can download the object and retry.
        /// </summary>
        MissingTree,

        /// <summary>
        /// The object is present but could not be parsed, or an entry could not
        /// be read. The visitor may have been called for earlier entries.
        /// This is never retryable — the caller must fail fast.
        /// </summary>
        CorruptTree,

        /// <summary>
        /// The object is present but is not a tree (for example a blob or a commit).
        /// The visitor was not called. This is never retryable.
        /// </summary>
        NotTree,
    }

    /// <summary>
    /// Called once per entry when enumerating a tree.
    /// All spans are only valid for the duration of the call — copy anything you keep.
    /// </summary>
    /// <param name="nameUtf8">The raw UTF-8 entry name owned by libgit2. Never empty for a valid tree.</param>
    /// <param name="objectId">The 20-byte raw object id of the entry.</param>
    /// <param name="gitFileMode">
    /// The git tree entry file mode (for example 0o100644, 0o120000, 0o160000, 0o040000).
    /// The low 16 bits map directly onto the index file-type-and-mode format.
    /// </param>
    /// <param name="isTree">True when the entry is a subtree (mode 0o040000).</param>
    public delegate void TreeEntryVisitor(ReadOnlySpan<byte> nameUtf8, ReadOnlySpan<byte> objectId, ushort gitFileMode, bool isTree);
}
