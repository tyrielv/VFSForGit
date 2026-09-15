using System;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Cheap, pure detection of hand-edits to <c>.git/info/sparse-checkout</c>, the cone
    /// file GVFS owns when <c>gvfs.auto-sparse-index</c> is enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hook cannot intercept a text-editor write to the cone file, so the mount detects
    /// it instead: at every cone recompute it compares the on-disk content against what
    /// GVFS last wrote. The comparison is an ordinal string compare of a small file (a few
    /// KB even at os.2020 scale). It never parses the file with git, so it is safe to run
    /// on every recompute (see decisions/0020).
    /// </para>
    /// <para>
    /// The most damaging hand-edit is a non-cone pattern. Git's
    /// <c>is_sparse_index_allowed()</c> requires cone-format patterns, so a non-cone
    /// pattern silently disables the sparse index with no error. <see cref="IsNonConeFormat"/>
    /// flags that case with a cheap, conservative heuristic: cone-mode patterns are
    /// root-anchored, so a line that is not anchored at the repo root cannot be cone
    /// format. The heuristic can miss a root-anchored-but-non-cone pattern, which only
    /// suppresses the extra warning — GVFS overwrites the file with a cone-format file
    /// regardless.
    /// </para>
    /// </remarks>
    public static class ConeDriftDetector
    {
        /// <summary>
        /// Returns true when the on-disk cone file differs from what GVFS last wrote this
        /// session. Returns false when there is no baseline yet
        /// (<paramref name="lastWrittenContent"/> is null), because GVFS cannot judge drift
        /// before it has written the file once.
        /// </summary>
        public static bool HasDrifted(string lastWrittenContent, string currentContent)
        {
            if (lastWrittenContent == null)
            {
                return false;
            }

            return !string.Equals(currentContent ?? string.Empty, lastWrittenContent, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true when the content is a non-empty, non-legacy cone file that names at
        /// least one non-cone pattern. This is the poisonous case: git silently disables the
        /// sparse index for such a file. Empty/absent content, GVFS's own legacy
        /// <c>/.gitattributes</c> content, and a comments-only file (which names no pattern)
        /// are not flagged.
        /// </summary>
        public static bool IsNonConeFormat(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            if (ConeFileWriter.IsLegacyContent(content))
            {
                // GVFS's own pre-sparse-index content, not a user hand-edit.
                return false;
            }

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (!IsRootAnchored(line))
                {
                    // A non-anchored pattern (e.g. "Dir/", "*.cs", "foo") breaks cone mode.
                    return true;
                }
            }

            // No significant lines, or every significant line is root-anchored: not the
            // poison case.
            return false;
        }

        /// <summary>
        /// Heuristic: returns true when the content names at least one pattern and every
        /// significant line is root-anchored (<c>/...</c> or <c>!/...</c>), which is a
        /// necessary property of git's cone-mode patterns. Blank lines and <c>#</c>
        /// comments are ignored. Returns false for empty content, a comments-only file, or
        /// when any significant line is not root-anchored.
        /// </summary>
        public static bool LooksLikeConeFormat(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            bool sawPattern = false;
            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                sawPattern = true;

                if (!IsRootAnchored(line))
                {
                    return false;
                }
            }

            return sawPattern;
        }

        private static bool IsRootAnchored(string line)
        {
            if (line[0] == '/')
            {
                return true;
            }

            return line.Length >= 2 && line[0] == '!' && line[1] == '/';
        }
    }
}
