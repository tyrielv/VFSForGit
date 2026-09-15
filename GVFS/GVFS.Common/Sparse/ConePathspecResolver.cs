using System;
using System.Collections.Generic;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Resolves the literal pathspecs a Git command names into repo-root-relative,
    /// git-form paths that <see cref="ConeBuilder"/> understands. This is the mount
    /// side of the widen request: the hook sends pathspecs as typed, relative to the
    /// command's working directory, and the mount turns them into cone entries.
    /// </summary>
    /// <remarks>
    /// This type performs no I/O and never touches the working tree, so it is safe to
    /// call from inside the mount (a working-tree stat would re-enter ProjFS) and can be
    /// unit tested exhaustively. A pathspec's cone shape is taken from its trailing
    /// slash only: a trailing slash means a recursive folder, and anything else is
    /// treated as a file (a parent-only entry). This deliberately under-widens an
    /// ambiguous <c>dir/subdir</c> rather than over-widening it, because an over-wide
    /// recursive entry is a size regression while an under-wide entry only makes Git
    /// transiently expand and re-collapse the index, which is benign.
    /// <para>
    /// A pathspec the resolver cannot map to a path inside the worktree is skipped, not
    /// failed: magic pathspecs (a leading <c>:</c>), rooted paths outside the worktree,
    /// and paths that escape the worktree root with <c>..</c> all yield no cone entry.
    /// The result is at worst an under-widen, which Git handles transiently.
    /// </para>
    /// </remarks>
    public static class ConePathspecResolver
    {
        /// <summary>
        /// Resolve pathspecs to distinct repo-root-relative git-form paths. A file entry
        /// has no trailing slash; a folder entry keeps its trailing slash so
        /// <see cref="ConeBuilder"/> emits a recursive pattern for it.
        /// </summary>
        /// <param name="currentDirectory">
        /// The command's working directory (absolute). Relative pathspecs resolve against
        /// it. It is expected to be inside <paramref name="workingDirectoryRoot"/>.
        /// </param>
        /// <param name="workingDirectoryRoot">The enlistment working-tree root (absolute).</param>
        /// <param name="pathspecs">The literal pathspecs, as typed on the command line.</param>
        public static IReadOnlyList<string> ResolveToGitPaths(
            string currentDirectory,
            string workingDirectoryRoot,
            IEnumerable<string> pathspecs)
        {
            ArgumentNullException.ThrowIfNull(workingDirectoryRoot);

            List<string> resolved = new List<string>();
            if (pathspecs == null)
            {
                return resolved;
            }

            string rootAbsolute = TrimTrailingSeparators(ToForwardSlashes(workingDirectoryRoot));
            string currentAbsolute = TrimTrailingSeparators(ToForwardSlashes(currentDirectory ?? string.Empty));

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawPathspec in pathspecs)
            {
                if (TryResolveOne(rawPathspec, rootAbsolute, currentAbsolute, out string gitPath) &&
                    seen.Add(gitPath))
                {
                    resolved.Add(gitPath);
                }
            }

            return resolved;
        }

        private static bool TryResolveOne(string rawPathspec, string rootAbsolute, string currentAbsolute, out string gitPath)
        {
            gitPath = null;

            if (string.IsNullOrEmpty(rawPathspec))
            {
                return false;
            }

            // A leading ':' introduces a magic pathspec (:/, :(glob), :!, :^, ...). These
            // are rare and their cone shape cannot be resolved without interpreting Git's
            // pathspec grammar, so skip them and let Git expand transiently.
            if (rawPathspec[0] == ':')
            {
                return false;
            }

            string normalized = ToForwardSlashes(rawPathspec);
            bool isFolder = normalized.EndsWith(GVFSConstants.GitPathSeparatorString, StringComparison.Ordinal);

            string absolute;
            if (IsAbsolute(normalized))
            {
                absolute = normalized;
            }
            else
            {
                absolute = currentAbsolute.Length == 0
                    ? normalized
                    : currentAbsolute + GVFSConstants.GitPathSeparatorString + normalized;
            }

            if (!TryCanonicalize(absolute, out string canonical))
            {
                return false;
            }

            if (!TryMakeRepoRelative(canonical, rootAbsolute, out string relative))
            {
                return false;
            }

            if (relative.Length == 0)
            {
                // The pathspec is the worktree root itself; root files are already covered
                // by the cone header, so there is no directory pattern to add.
                return false;
            }

            gitPath = isFolder ? relative + GVFSConstants.GitPathSeparatorString : relative;
            return true;
        }

        private static bool TryCanonicalize(string path, out string canonical)
        {
            canonical = null;

            string[] parts = path.Split(GVFSConstants.GitPathSeparator);
            List<string> segments = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (segments.Count == 0)
                    {
                        // The path escapes above its own root. Preserve a leading drive
                        // segment (for example "C:") so a rooted path stays rooted; a
                        // relative path that pops past its start cannot be mapped.
                        return false;
                    }

                    if (IsDriveSegment(segments[segments.Count - 1]))
                    {
                        return false;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(part);
            }

            canonical = string.Join(GVFSConstants.GitPathSeparatorString, segments);
            return true;
        }

        private static bool TryMakeRepoRelative(string canonicalAbsolute, string rootAbsolute, out string relative)
        {
            relative = null;

            string root = rootAbsolute;
            if (root.Length == 0)
            {
                relative = canonicalAbsolute;
                return true;
            }

            // Windows paths are case-insensitive, so compare the prefix without case but
            // keep the caller's casing in the returned suffix to match on-disk entries.
            if (canonicalAbsolute.Length == root.Length)
            {
                if (canonicalAbsolute.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    relative = string.Empty;
                    return true;
                }

                return false;
            }

            if (canonicalAbsolute.Length > root.Length &&
                canonicalAbsolute[root.Length] == GVFSConstants.GitPathSeparator &&
                canonicalAbsolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                relative = canonicalAbsolute.Substring(root.Length + 1);
                return true;
            }

            return false;
        }

        private static bool IsAbsolute(string forwardSlashedPath)
        {
            // A drive-rooted path (C:/...) or a leading separator (a UNC or POSIX-rooted
            // path). A bare "C:relative" (drive-relative, no slash) is treated as relative.
            if (forwardSlashedPath.StartsWith(GVFSConstants.GitPathSeparatorString, StringComparison.Ordinal))
            {
                return true;
            }

            return forwardSlashedPath.Length >= 3 &&
                IsDriveSegment(forwardSlashedPath.Substring(0, 2)) &&
                forwardSlashedPath[2] == GVFSConstants.GitPathSeparator;
        }

        private static bool IsDriveSegment(string segment)
        {
            return segment.Length == 2 &&
                char.IsLetter(segment[0]) &&
                segment[1] == ':';
        }

        private static string ToForwardSlashes(string path)
        {
            return path.Replace('\\', GVFSConstants.GitPathSeparator);
        }

        private static string TrimTrailingSeparators(string path)
        {
            return path.TrimEnd(GVFSConstants.GitPathSeparator);
        }
    }
}
