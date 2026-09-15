using System;
using System.Collections.Generic;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// The computed cone-mode sparse-checkout pattern set for a worktree. It is the
    /// output of <see cref="ConeBuilder"/> and the input to the cone file writer.
    /// </summary>
    /// <remarks>
    /// Directory paths are stored in git form: forward slashes, no leading slash, and
    /// no trailing slash (for example <c>A</c> or <c>A/B</c>). Both lists are sorted
    /// ordinally, matching git's write_cone_to_file, which sorts patterns with strcmp.
    /// A parent-only directory includes files directly in that directory but not its
    /// subtree. A recursive directory includes the whole subtree.
    /// </remarks>
    public class ConePatternSet
    {
        public ConePatternSet(IReadOnlyList<string> parentOnlyDirectories, IReadOnlyList<string> recursiveDirectories)
        {
            ArgumentNullException.ThrowIfNull(parentOnlyDirectories);
            ArgumentNullException.ThrowIfNull(recursiveDirectories);

            this.ParentOnlyDirectories = parentOnlyDirectories;
            this.RecursiveDirectories = recursiveDirectories;
        }

        /// <summary>
        /// Directories whose direct files are in the cone, without their subtrees.
        /// Each emits a positive directory pattern plus a negative child-directory pattern.
        /// </summary>
        public IReadOnlyList<string> ParentOnlyDirectories { get; }

        /// <summary>
        /// Directories whose entire subtree is in the cone. Each emits a single positive
        /// directory pattern.
        /// </summary>
        public IReadOnlyList<string> RecursiveDirectories { get; }
    }
}
