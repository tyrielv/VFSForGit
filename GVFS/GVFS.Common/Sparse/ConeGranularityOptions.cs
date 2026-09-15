using System;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Tuning constants for the cone granularity heuristic, which collapses a parent-only
    /// ancestor chain into a single recursive include when doing so costs no meaningful
    /// index entries.
    /// </summary>
    /// <remarks>
    /// The constants are calibrated to a large repository whose directory subtree sizes were
    /// measured directly: 88% of directories hold 25 files or fewer in their whole subtree and
    /// 98.4% hold 250 or fewer, while every top-level directory holds tens to hundreds of
    /// thousands. The mechanism is repository independent; a very differently shaped repository
    /// would want its own values, which is why they are options rather than constants.
    /// <para>
    /// Both gates are entry neutral by construction: they fire only where a recursive include
    /// costs about the same index entries the parent-only chain already carries. The payoff is
    /// a smaller pattern count and less cone churn, not a smaller index.
    /// </para>
    /// </remarks>
    public sealed class ConeGranularityOptions
    {
        /// <summary>Default maximum subtree size for the size gate.</summary>
        public const int DefaultMaxSubtreeFiles = 250;

        /// <summary>Default minimum modified files under a directory before the size gate fires.</summary>
        public const int DefaultMinModifiedFiles = 8;

        /// <summary>Default modified-to-subtree ratio at which the density gate fires.</summary>
        public const double DefaultMinDensity = 0.5;

        public ConeGranularityOptions(
            int maxSubtreeFiles = DefaultMaxSubtreeFiles,
            int minModifiedFiles = DefaultMinModifiedFiles,
            double minDensity = DefaultMinDensity)
        {
            if (maxSubtreeFiles < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSubtreeFiles));
            }

            if (minModifiedFiles < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(minModifiedFiles));
            }

            if (minDensity <= 0.0 || minDensity > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(minDensity));
            }

            this.MaxSubtreeFiles = maxSubtreeFiles;
            this.MinModifiedFiles = minModifiedFiles;
            this.MinDensity = minDensity;
        }

        /// <summary>
        /// The measured default. 98.4% of directories fall under the size gate, and an
        /// independent per-directory crossover shows recursing is entry neutral at or below
        /// this size and catastrophic above roughly 5,000.
        /// </summary>
        public static ConeGranularityOptions Default { get; } = new ConeGranularityOptions();

        /// <summary>Size gate: collapse only when the subtree holds at most this many files.</summary>
        public int MaxSubtreeFiles { get; }

        /// <summary>
        /// Size gate: require at least this many modified files under the directory. An isolated
        /// deep edit is already close to the entry floor and has no pattern problem to solve.
        /// </summary>
        public int MinModifiedFiles { get; }

        /// <summary>
        /// Density gate: collapse regardless of size when this fraction of the subtree is
        /// modified, because the parent-only chain already names most of it.
        /// </summary>
        public double MinDensity { get; }

        /// <summary>
        /// The largest subtree that can still satisfy the density gate for a given number of
        /// modified files. Used as the walk cap so the counter stops as soon as the density
        /// gate cannot be met, keeping the density check O(cap) like the size check.
        /// </summary>
        public int DensityCapFor(int modifiedFileCount)
        {
            if (modifiedFileCount <= 0)
            {
                return 0;
            }

            double cap = modifiedFileCount / this.MinDensity;
            return cap >= int.MaxValue ? int.MaxValue : (int)cap;
        }
    }
}
