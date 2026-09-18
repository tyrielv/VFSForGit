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
            return BuildFromModifiedPaths(modifiedPaths, subtreeFileCounter: null, options: null);
        }

        /// <summary>
        /// Build a cone pattern set, optionally applying the granularity heuristic that
        /// collapses a parent-only ancestor chain into a single recursive include.
        /// </summary>
        /// <param name="modifiedPaths">See the other overload.</param>
        /// <param name="subtreeFileCounter">
        /// Supplies capped subtree file counts. When null the heuristic is skipped entirely and
        /// the result is byte-for-byte the parent-only cone, which is the default behavior.
        /// </param>
        /// <param name="options">Tuning constants; <see cref="ConeGranularityOptions.Default"/> when null.</param>
        public static ConePatternSet BuildFromModifiedPaths(
            IEnumerable<string> modifiedPaths,
            IConeSubtreeFileCounter subtreeFileCounter,
            ConeGranularityOptions options)
        {
            ArgumentNullException.ThrowIfNull(modifiedPaths);

            HashSet<string> recursiveDirectories = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> parentOnlyDirectories = new HashSet<string>(StringComparer.Ordinal);

            // Modified file paths, kept so the heuristic can count modified files under a
            // candidate directory. Only populated when the heuristic will actually run.
            List<string> modifiedFiles = subtreeFileCounter == null ? null : new List<string>();

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
                    modifiedFiles?.Add(cleanPath);

                    string parent = GetParent(cleanPath);
                    if (parent.Length != 0)
                    {
                        parentOnlyDirectories.Add(parent);
                        AddAncestors(parent, parentOnlyDirectories);
                    }
                }
            }

            if (subtreeFileCounter != null)
            {
                CollapseQualifyingChains(
                    parentOnlyDirectories,
                    recursiveDirectories,
                    modifiedFiles,
                    subtreeFileCounter,
                    options ?? ConeGranularityOptions.Default);
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

        /// <summary>
        /// Replace a parent-only sub-chain with one recursive include at the shallowest
        /// directory that qualifies.
        /// </summary>
        /// <remarks>
        /// Candidates are evaluated shallowest first and the walk stops at the first qualifying
        /// directory on each path, because a recursive include subsumes everything below it and
        /// collapsing highest yields the fewest patterns. A directory that is a proper ancestor
        /// of an existing recursive include is never collapsed: that recursive entry came from a
        /// modified folder, and swallowing it into a larger recursive include would pull in
        /// unrelated siblings and stop being entry neutral.
        /// </remarks>
        private static void CollapseQualifyingChains(
            HashSet<string> parentOnlyDirectories,
            HashSet<string> recursiveDirectories,
            List<string> modifiedFiles,
            IConeSubtreeFileCounter subtreeFileCounter,
            ConeGranularityOptions options)
        {
            if (parentOnlyDirectories.Count == 0 || modifiedFiles.Count == 0)
            {
                return;
            }

            List<string> candidates = new List<string>(parentOnlyDirectories);

            // Shallowest first, then ordinal so the result does not depend on hash order.
            candidates.Sort((left, right) =>
            {
                int depthComparison = CountSeparators(left).CompareTo(CountSeparators(right));
                return depthComparison != 0 ? depthComparison : string.CompareOrdinal(left, right);
            });

            List<string> collapsed = new List<string>();

            foreach (string candidate in candidates)
            {
                // Already covered by a recursive include chosen earlier in this pass, or by one
                // that came from a modified folder.
                if (IsCoveredByRecursive(candidate, recursiveDirectories))
                {
                    continue;
                }

                if (IsProperAncestorOfAnyRecursive(candidate, recursiveDirectories))
                {
                    continue;
                }

                int modifiedUnder = CountModifiedFilesUnder(candidate, modifiedFiles);
                if (modifiedUnder == 0)
                {
                    continue;
                }

                if (Qualifies(candidate, modifiedUnder, subtreeFileCounter, options))
                {
                    collapsed.Add(candidate);
                    recursiveDirectories.Add(candidate);
                }
            }

            // The parent-only entries for the collapsed directories are now redundant. Their
            // ancestors stay, because a recursive include does not cover an ancestor's own files.
            foreach (string directory in collapsed)
            {
                parentOnlyDirectories.Remove(directory);
            }
        }

        private static bool Qualifies(
            string directory,
            int modifiedUnder,
            IConeSubtreeFileCounter subtreeFileCounter,
            ConeGranularityOptions options)
        {
            int subtreeFiles;

            // Size gate: a small subtree with enough activity under it.
            if (modifiedUnder >= options.MinModifiedFiles
                && subtreeFileCounter.TryCountSubtreeFiles(directory, options.MaxSubtreeFiles, out subtreeFiles)
                && subtreeFiles > 0)
            {
                return true;
            }

            // Density gate: most of the subtree is already modified, whatever its size. Capping
            // the walk at the largest subtree that could still satisfy the ratio keeps this
            // O(cap) rather than O(subtree), and a subtree over the cap fails the gate anyway.
            int densityCap = options.DensityCapFor(modifiedUnder);
            if (densityCap > 0
                && subtreeFileCounter.TryCountSubtreeFiles(directory, densityCap, out subtreeFiles)
                && subtreeFiles > 0
                && (double)modifiedUnder / subtreeFiles >= options.MinDensity)
            {
                return true;
            }

            return false;
        }

        private static int CountModifiedFilesUnder(string directory, List<string> modifiedFiles)
        {
            int count = 0;
            foreach (string file in modifiedFiles)
            {
                if (IsDescendantOf(file, directory))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsCoveredByRecursive(string path, HashSet<string> recursiveDirectories)
        {
            if (recursiveDirectories.Contains(path))
            {
                return true;
            }

            foreach (string recursive in recursiveDirectories)
            {
                if (IsDescendantOf(path, recursive))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsProperAncestorOfAnyRecursive(string path, HashSet<string> recursiveDirectories)
        {
            foreach (string recursive in recursiveDirectories)
            {
                if (IsDescendantOf(recursive, path))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountSeparators(string path)
        {
            int count = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == GVFSConstants.GitPathSeparator)
                {
                    count++;
                }
            }

            return count;
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
