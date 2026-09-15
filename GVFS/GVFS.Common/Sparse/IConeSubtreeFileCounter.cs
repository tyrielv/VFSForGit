using System;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Counts the files in a directory's subtree, with a hard cap on how much work is done.
    /// </summary>
    /// <remarks>
    /// The cone granularity heuristic never needs an exact subtree size -- it only needs to
    /// know whether a subtree is at or below a threshold. Capping lets the implementation stop
    /// walking as soon as the answer cannot change, so the cost is O(cap) per candidate
    /// directory rather than O(subtree). At the default threshold that is roughly 251 node
    /// visits no matter how large the real subtree is.
    /// <para>
    /// <see cref="ConeBuilder"/> takes this as an abstraction so the heuristic stays pure and
    /// unit testable. The mount supplies an implementation backed by the in-memory projection.
    /// </para>
    /// </remarks>
    public interface IConeSubtreeFileCounter
    {
        /// <summary>
        /// Count the files in <paramref name="directory"/>'s subtree, including nested
        /// subdirectories, stopping once the count would exceed <paramref name="cap"/>.
        /// </summary>
        /// <param name="directory">
        /// Repo-relative directory path with forward slashes and no leading or trailing
        /// separator, as produced by <see cref="ConeBuilder"/>.
        /// </param>
        /// <param name="cap">Maximum number of files to count before giving up. Never negative.</param>
        /// <param name="count">The exact file count when the method returns true; otherwise unspecified.</param>
        /// <returns>
        /// True when the subtree holds <paramref name="cap"/> files or fewer and
        /// <paramref name="count"/> is exact. False when the subtree exceeds the cap, or when
        /// the directory cannot be resolved -- callers must treat false as "do not collapse",
        /// so an unknown directory never widens the cone.
        /// </returns>
        bool TryCountSubtreeFiles(string directory, int cap, out int count);
    }
}
