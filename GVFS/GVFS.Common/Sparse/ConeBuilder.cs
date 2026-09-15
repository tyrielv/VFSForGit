using System;
using System.Collections.Generic;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Pure mapping from a set of modified paths to a git cone-mode sparse-checkout
    /// pattern set.
    /// </summary>
    /// <remarks>
    /// A modified file contributes parent-only patterns for its parent directory and
    /// every ancestor directory, so a single edited file never pulls its whole subtree
    /// into the index. A modified folder contributes one recursive pattern for the whole
    /// subtree, which matches GVFS's existing recursive folder-entry semantics.
    /// <para>
    /// This type performs no I/O. It exists so the file-to-cone policy can be unit
    /// tested exhaustively. All comparisons and de-duplication are ordinal, matching
    /// git's write_cone_to_file, which sorts and de-duplicates patterns with strcmp
    /// regardless of core.ignoreCase.
    /// </para>
    /// </remarks>
    public static class ConeBuilder
    {
        /// <summary>
        /// Build a cone pattern set from modified-path entries.
        /// </summary>
        /// <param name="modifiedPaths">
        /// Modified-path entries. A file entry has no trailing slash; a folder entry ends
        /// in a slash. Entries may use either path separator and may carry a leading slash
        /// or duplicate separators; the builder normalizes them. Null entries are skipped.
        /// The repo root itself contributes no directory pattern.
        /// </param>
        public static ConePatternSet BuildFromModifiedPaths(IEnumerable<string> modifiedPaths)
        {
            ArgumentNullException.ThrowIfNull(modifiedPaths);

            HashSet<string> recursiveDirectories = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> parentOnlyDirectories = new HashSet<string>(StringComparer.Ordinal);

            foreach (string rawPath in modifiedPaths)
            {
                if (rawPath == null)
                {
                    continue;
                }

                bool isFolder;
                string cleanPath = NormalizePath(rawPath, out isFolder);
                if (cleanPath.Length == 0)
                {
                    // An empty path is the repo root or a bare separator. Root files are
                    // already included by the "/*" header, so there is nothing to add.
                    continue;
                }

                if (isFolder)
                {
                    recursiveDirectories.Add(cleanPath);
                    AddAncestors(cleanPath, parentOnlyDirectories);
                }
                else
                {
                    string parent = GetParent(cleanPath);
                    if (parent.Length != 0)
                    {
                        parentOnlyDirectories.Add(parent);
                        AddAncestors(parent, parentOnlyDirectories);
                    }
                }
            }

            // A recursive directory nested inside another recursive directory is redundant;
            // the ancestor already covers the whole subtree.
            RemoveRedundantRecursiveDirectories(recursiveDirectories);

            // A parent-only directory that a recursive directory already covers is redundant:
            // either it equals a recursive directory, or it is below one. Parent-only
            // ancestors of a recursive directory are kept, because a recursive directory
            // does not include the direct files of its ancestors.
            RemoveParentOnlyDirectoriesCoveredByRecursive(parentOnlyDirectories, recursiveDirectories);

            List<string> sortedParents = new List<string>(parentOnlyDirectories);
            sortedParents.Sort(StringComparer.Ordinal);

            List<string> sortedRecursive = new List<string>(recursiveDirectories);
            sortedRecursive.Sort(StringComparer.Ordinal);

            return new ConePatternSet(sortedParents, sortedRecursive);
        }

        private static string NormalizePath(string rawPath, out bool isFolder)
        {
            string forwardSlashed = rawPath.Replace('\\', GVFSConstants.GitPathSeparator);
            isFolder = forwardSlashed.EndsWith(GVFSConstants.GitPathSeparatorString, StringComparison.Ordinal);

            // Splitting on the separator with RemoveEmptyEntries trims leading and trailing
            // separators and collapses duplicate separators, then rejoins in canonical form.
            string[] parts = forwardSlashed.Split(GVFSConstants.GitPathSeparator, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(GVFSConstants.GitPathSeparatorString, parts);
        }

        private static string GetParent(string cleanPath)
        {
            int lastSeparator = cleanPath.LastIndexOf(GVFSConstants.GitPathSeparator);
            return lastSeparator < 0 ? string.Empty : cleanPath.Substring(0, lastSeparator);
        }

        private static void AddAncestors(string directory, HashSet<string> parentOnlyDirectories)
        {
            // Add every proper ancestor of the directory, but not the directory itself.
            int separator = directory.IndexOf(GVFSConstants.GitPathSeparator);
            while (separator >= 0)
            {
                parentOnlyDirectories.Add(directory.Substring(0, separator));
                separator = directory.IndexOf(GVFSConstants.GitPathSeparator, separator + 1);
            }
        }

        private static void RemoveRedundantRecursiveDirectories(HashSet<string> recursiveDirectories)
        {
            List<string> toRemove = new List<string>();
            foreach (string candidate in recursiveDirectories)
            {
                foreach (string other in recursiveDirectories)
                {
                    if (IsDescendantOf(candidate, other))
                    {
                        toRemove.Add(candidate);
                        break;
                    }
                }
            }

            foreach (string redundant in toRemove)
            {
                recursiveDirectories.Remove(redundant);
            }
        }

        private static void RemoveParentOnlyDirectoriesCoveredByRecursive(
            HashSet<string> parentOnlyDirectories,
            HashSet<string> recursiveDirectories)
        {
            List<string> toRemove = new List<string>();
            foreach (string parent in parentOnlyDirectories)
            {
                if (recursiveDirectories.Contains(parent))
                {
                    toRemove.Add(parent);
                    continue;
                }

                foreach (string recursive in recursiveDirectories)
                {
                    if (IsDescendantOf(parent, recursive))
                    {
                        toRemove.Add(parent);
                        break;
                    }
                }
            }

            foreach (string covered in toRemove)
            {
                parentOnlyDirectories.Remove(covered);
            }
        }

        private static bool IsDescendantOf(string path, string potentialAncestor)
        {
            return path.Length > potentialAncestor.Length
                && path.StartsWith(potentialAncestor, StringComparison.Ordinal)
                && path[potentialAncestor.Length] == GVFSConstants.GitPathSeparator;
        }
    }
}
