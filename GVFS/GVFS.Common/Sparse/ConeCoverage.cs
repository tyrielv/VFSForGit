using System;
using System.Collections.Generic;
using System.Text;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Reads a git cone-mode <c>info/sparse-checkout</c> file back into its directory
    /// sets and answers whether adding a resolved pathspec would leave the cone
    /// unchanged. This is the local, IPC-free half of the widen decision.
    /// </summary>
    /// <remarks>
    /// The mount widens by rebuilding the cone from the modified-and-transient path set
    /// and rewriting the file only when the serialized content changes (see
    /// <c>AutoSparseIndexConeManager.ConeContentUnchanged</c>). Because
    /// <see cref="ConeBuilder"/> is deterministic and the current file is exactly
    /// <c>Serialize(ConeBuilder(existing))</c>, adding a path changes that content only
    /// when the path is not already covered. So a hook that parses the current file and
    /// finds every named path covered can prove the mount would do nothing, and skip the
    /// request entirely -- the observable result is identical to sending the widen and
    /// getting a no-op reply.
    /// <para>
    /// The parse is deliberately strict: any line that is not a cone header line or a
    /// well-formed cone directory pattern makes <see cref="TryParseConeFile"/> fail, so a
    /// legacy or hand-edited file falls through to the mount instead of risking a wrong
    /// skip. All comparisons are ordinal, matching <see cref="ConeBuilder"/> and git's
    /// write_cone_to_file. A path that differs only by case therefore falls through to the
    /// mount rather than being wrongly treated as covered.
    /// </para>
    /// </remarks>
    public static class ConeCoverage
    {
        private const string HeaderInclude = "/*";
        private const string HeaderExclude = "!/*/";
        private const string NegativeSuffix = "/*/";

        /// <summary>
        /// Parse git cone-mode sparse-checkout content into a <see cref="ConePatternSet"/>.
        /// Returns false when the content is not a well-formed GVFS cone file, so the
        /// caller defers to the mount rather than risk a wrong coverage answer.
        /// </summary>
        public static bool TryParseConeFile(string content, out ConePatternSet cone)
        {
            cone = null;
            if (content == null)
            {
                return false;
            }

            HashSet<string> positives = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> parentOnly = new HashSet<string>(StringComparer.Ordinal);
            bool sawHeaderInclude = false;
            bool sawHeaderExclude = false;

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Equals(HeaderInclude, StringComparison.Ordinal))
                {
                    sawHeaderInclude = true;
                    continue;
                }

                if (line.Equals(HeaderExclude, StringComparison.Ordinal))
                {
                    sawHeaderExclude = true;
                    continue;
                }

                if (line[0] == '!')
                {
                    if (!TryParseNegative(line, out string parentDirectory))
                    {
                        return false;
                    }

                    parentOnly.Add(parentDirectory);
                }
                else
                {
                    if (!TryParsePositive(line, out string directory))
                    {
                        return false;
                    }

                    positives.Add(directory);
                }
            }

            // A GVFS cone file always begins with the "/*" + "!/*/" header. Absent it,
            // the file is not a cone this hook wrote, so defer to the mount.
            if (!sawHeaderInclude || !sawHeaderExclude)
            {
                return false;
            }

            // Every parent-only marker pairs with a positive directory line; git and
            // ConeFileWriter always emit them together. A dangling marker means the file
            // is malformed, so fail rather than risk a wrong skip.
            foreach (string parentDirectory in parentOnly)
            {
                if (!positives.Contains(parentDirectory))
                {
                    return false;
                }
            }

            List<string> recursiveDirectories = new List<string>();
            foreach (string directory in positives)
            {
                if (!parentOnly.Contains(directory))
                {
                    recursiveDirectories.Add(directory);
                }
            }

            cone = new ConePatternSet(new List<string>(parentOnly), recursiveDirectories);
            return true;
        }

        /// <summary>
        /// Return true when adding every resolved git-form path to the cone would leave it
        /// unchanged -- that is, every path is already covered. An empty list is covered
        /// vacuously, matching the mount, which rebuilds an identical cone from no new
        /// paths and rewrites nothing.
        /// </summary>
        public static bool AreAllPathsCovered(ConePatternSet cone, IReadOnlyList<string> resolvedGitPaths)
        {
            ArgumentNullException.ThrowIfNull(cone);
            ArgumentNullException.ThrowIfNull(resolvedGitPaths);

            HashSet<string> parentOnly = new HashSet<string>(cone.ParentOnlyDirectories, StringComparer.Ordinal);
            IReadOnlyList<string> recursive = cone.RecursiveDirectories;

            foreach (string path in resolvedGitPaths)
            {
                if (!IsPathCovered(path, parentOnly, recursive))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPathCovered(string resolvedGitPath, HashSet<string> parentOnly, IReadOnlyList<string> recursive)
        {
            bool isFolder = resolvedGitPath.EndsWith(GVFSConstants.GitPathSeparatorString, StringComparison.Ordinal);

            if (isFolder)
            {
                // A recursive folder pathspec pulls in its whole subtree, so it is covered
                // only when the subtree is already recursively in the cone. Parent-only
                // coverage of the folder is not enough.
                string folder = resolvedGitPath.Substring(0, resolvedGitPath.Length - 1);
                return IsRecursivelyCovered(folder, recursive);
            }

            // A file is covered when its parent directory's direct files are already in the
            // cone: the repo root (empty parent, covered by the "/*" header), a parent-only
            // directory, or any directory inside a recursive subtree.
            string parent = GetParent(resolvedGitPath);
            if (parent.Length == 0)
            {
                return true;
            }

            if (parentOnly.Contains(parent))
            {
                return true;
            }

            return IsRecursivelyCovered(parent, recursive);
        }

        private static bool IsRecursivelyCovered(string directory, IReadOnlyList<string> recursive)
        {
            foreach (string recursiveDirectory in recursive)
            {
                if (directory.Equals(recursiveDirectory, StringComparison.Ordinal) ||
                    IsDescendantOf(directory, recursiveDirectory))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDescendantOf(string path, string potentialAncestor)
        {
            return path.Length > potentialAncestor.Length
                && path.StartsWith(potentialAncestor, StringComparison.Ordinal)
                && path[potentialAncestor.Length] == GVFSConstants.GitPathSeparator;
        }

        private static string GetParent(string path)
        {
            int lastSeparator = path.LastIndexOf(GVFSConstants.GitPathSeparator);
            return lastSeparator < 0 ? string.Empty : path.Substring(0, lastSeparator);
        }

        private static bool TryParsePositive(string line, out string directory)
        {
            directory = null;

            // A positive directory pattern is "/<escaped-dir>/".
            if (line.Length < 3 ||
                line[0] != GVFSConstants.GitPathSeparator ||
                line[line.Length - 1] != GVFSConstants.GitPathSeparator)
            {
                return false;
            }

            string escaped = line.Substring(1, line.Length - 2);
            return TryUnescapeDirectory(escaped, out directory);
        }

        private static bool TryParseNegative(string line, out string directory)
        {
            directory = null;

            // A parent-only marker is "!/<escaped-dir>/*/".
            if (line.Length < 2 + NegativeSuffix.Length + 1 ||
                line[1] != GVFSConstants.GitPathSeparator ||
                !line.EndsWith(NegativeSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            string escaped = line.Substring(2, line.Length - 2 - NegativeSuffix.Length);
            return TryUnescapeDirectory(escaped, out directory);
        }

        private static bool TryUnescapeDirectory(string escaped, out string directory)
        {
            directory = null;
            if (escaped.Length == 0)
            {
                return false;
            }

            if (escaped.IndexOf('\\') < 0)
            {
                directory = escaped;
                return escaped.Length != 0;
            }

            StringBuilder builder = new StringBuilder(escaped.Length);
            for (int i = 0; i < escaped.Length; i++)
            {
                char character = escaped[i];
                if (character == '\\' && i + 1 < escaped.Length && IsGlobSpecial(escaped[i + 1]))
                {
                    i++;
                    builder.Append(escaped[i]);
                }
                else
                {
                    builder.Append(character);
                }
            }

            directory = builder.ToString();
            return directory.Length != 0;
        }

        private static bool IsGlobSpecial(char character)
        {
            return character == '*' || character == '?' || character == '[' || character == '\\';
        }
    }
}
