using System;
using System.Collections.Generic;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    /// <summary>
    /// Tests for the cone granularity heuristic: collapse a parent-only ancestor chain into a
    /// single recursive include only where that is entry neutral.
    /// </summary>
    /// <remarks>
    /// The heuristic is validated against scenario shapes drawn from a measured large
    /// repository: an isolated deep edit must not collapse, a wholesale-modified directory
    /// must, and a directory whose subtree is large must never collapse no matter how many
    /// files are modified under it.
    /// </remarks>
    [TestFixture]
    public class ConeGranularityTests
    {
        /// <summary>
        /// Counter over a fixed directory-to-subtree-size map. Records the caps it was asked
        /// for so tests can assert the walk is bounded rather than exhaustive.
        /// </summary>
        private sealed class FakeSubtreeCounter : IConeSubtreeFileCounter
        {
            private readonly Dictionary<string, int> subtreeSizes;

            public FakeSubtreeCounter(Dictionary<string, int> subtreeSizes)
            {
                this.subtreeSizes = subtreeSizes;
            }

            public List<string> Queried { get; } = new List<string>();

            public int MaxCapRequested { get; private set; }

            public bool TryCountSubtreeFiles(string directory, int cap, out int count)
            {
                this.Queried.Add(directory);
                this.MaxCapRequested = Math.Max(this.MaxCapRequested, cap);

                int size;
                if (!this.subtreeSizes.TryGetValue(directory, out size))
                {
                    count = 0;
                    return false;
                }

                if (size > cap)
                {
                    count = 0;
                    return false;
                }

                count = size;
                return true;
            }
        }

        private static ConePatternSet BuildWith(
            IEnumerable<string> modifiedPaths,
            Dictionary<string, int> subtreeSizes,
            ConeGranularityOptions options = null)
        {
            return ConeBuilder.BuildFromModifiedPaths(
                modifiedPaths,
                new FakeSubtreeCounter(subtreeSizes),
                options ?? ConeGranularityOptions.Default);
        }

        [TestCase]
        public void NullCounter_IsByteForByteTheParentOnlyCone()
        {
            string[] paths = new[] { "a/b/c/f1.txt", "a/b/c/f2.txt", "a/b/d/f3.txt" };

            ConePatternSet withoutHeuristic = ConeBuilder.BuildFromModifiedPaths(paths);
            ConePatternSet nullCounter = ConeBuilder.BuildFromModifiedPaths(paths, null, ConeGranularityOptions.Default);

            nullCounter.ParentOnlyDirectories.ShouldMatchInOrder(withoutHeuristic.ParentOnlyDirectories);
            nullCounter.RecursiveDirectories.ShouldMatchInOrder(withoutHeuristic.RecursiveDirectories);
        }

        [TestCase]
        public void IsolatedDeepEdit_DoesNotCollapse()
        {
            // One modified file is below MinModifiedFiles, and one of 40 is below MinDensity.
            // Parent-only is already near the entry floor here, so there is nothing to gain.
            ConePatternSet cone = BuildWith(
                new[] { "a/b/c/only.txt" },
                new Dictionary<string, int> { { "a", 40 }, { "a/b", 40 }, { "a/b/c", 40 } });

            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "a", "a/b", "a/b/c" });
        }

        [TestCase]
        public void SmallSubtreeWithEnoughActivity_CollapsesAtShallowestQualifyingDirectory()
        {
            // "a" itself is small (<= 250) and has 8 modified files under it, so the collapse
            // happens at "a" -- the shallowest qualifying directory -- not at "a/b/c".
            List<string> paths = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                paths.Add("a/b/c/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(
                paths,
                new Dictionary<string, int> { { "a", 200 }, { "a/b", 150 }, { "a/b/c", 100 } });

            cone.RecursiveDirectories.ShouldMatchInOrder(new[] { "a" });

            // "a" collapsed, so its own parent-only entry is gone and the chain below it is
            // covered by the recursive include.
            cone.ParentOnlyDirectories.ShouldBeEmpty();
        }

        [TestCase]
        public void LargeSubtree_NeverCollapses_EvenWithManyModifiedFiles()
        {
            // A top-level directory is tens to hundreds of thousands of entries. Recursing it
            // is never a win, so neither gate may fire: 50 modified files out of 129,059 is
            // both over MaxSubtreeFiles and far under MinDensity.
            List<string> paths = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                paths.Add("big/sub/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(
                paths,
                new Dictionary<string, int> { { "big", 129059 }, { "big/sub", 90000 } });

            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "big", "big/sub" });
        }

        [TestCase]
        public void DenseDirectory_CollapsesRegardlessOfSizeGate()
        {
            // 40 modified of a 60-file subtree is 0.67 density: over MinDensity, so the density
            // gate fires even though this directory would also pass the size gate. The point is
            // that density alone is sufficient.
            List<string> paths = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                paths.Add("d/hot/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(
                paths,
                new Dictionary<string, int> { { "d", 5000 }, { "d/hot", 60 } });

            // "d" is 5,000 files with 40 modified: fails size (too big) and density (0.008).
            // "d/hot" qualifies on density.
            cone.RecursiveDirectories.ShouldMatchInOrder(new[] { "d/hot" });
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "d" });
        }

        [TestCase]
        public void DensityGate_WalkIsCappedByModifiedCount_NotBySubtreeSize()
        {
            // The density cap must be modified/MinDensity, so a 10-file change never walks more
            // than 20 nodes for the density check even if the directory holds millions.
            List<string> paths = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                paths.Add("x/f" + i + ".txt");
            }

            FakeSubtreeCounter counter = new FakeSubtreeCounter(new Dictionary<string, int>());
            ConeBuilder.BuildFromModifiedPaths(paths, counter, ConeGranularityOptions.Default);

            // The largest cap requested is max(MaxSubtreeFiles, modified / MinDensity).
            // With 10 modified files that is max(250, 20) = 250 -- bounded by the constant,
            // never by the real subtree.
            counter.MaxCapRequested.ShouldEqual(ConeGranularityOptions.DefaultMaxSubtreeFiles);
        }

        [TestCase]
        public void UnresolvableDirectory_FailsClosed_AndKeepsParentOnlyChain()
        {
            // An empty size map means the counter always returns false, which is what the
            // projection does while it is still parsing. The cone must stay narrow.
            List<string> paths = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                paths.Add("a/b/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(paths, new Dictionary<string, int>());

            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "a", "a/b" });
        }

        [TestCase]
        public void DoesNotCollapseAnAncestorOfAnExistingRecursiveDirectory()
        {
            // "a/b/" is a modified folder, so it is already recursive. Collapsing its ancestor
            // "a" into a recursive include would swallow unrelated siblings of "a/b" and stop
            // being entry neutral, so it must not happen even though "a" is small and active.
            List<string> paths = new List<string> { "a/b/" };
            for (int i = 0; i < 10; i++)
            {
                paths.Add("a/c/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(
                paths,
                new Dictionary<string, int> { { "a", 100 }, { "a/c", 50 } });

            cone.RecursiveDirectories.ShouldContain(x => x == "a/b");
            cone.RecursiveDirectories.ShouldContain(x => x == "a/c");
            cone.RecursiveDirectories.ShouldNotContain(x => x == "a");
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "a" });
        }

        [TestCase]
        public void CollapsedDirectory_StillKeepsItsAncestorsAsParentOnly()
        {
            // A recursive include does not cover an ancestor's own direct files, so the
            // ancestors of a collapsed directory must remain parent-only.
            List<string> paths = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                paths.Add("top/mid/leaf/f" + i + ".txt");
            }

            ConePatternSet cone = BuildWith(
                paths,
                new Dictionary<string, int> { { "top", 100000 }, { "top/mid", 90000 }, { "top/mid/leaf", 30 } });

            cone.RecursiveDirectories.ShouldMatchInOrder(new[] { "top/mid/leaf" });
            cone.ParentOnlyDirectories.ShouldMatchInOrder(new[] { "top", "top/mid" });
        }

        [TestCase]
        public void ResultIsIndependentOfInputOrder()
        {
            Dictionary<string, int> sizes = new Dictionary<string, int>
            {
                { "a", 100000 }, { "a/b", 30 }, { "a/c", 40 },
            };

            List<string> forward = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                forward.Add("a/b/f" + i + ".txt");
                forward.Add("a/c/g" + i + ".txt");
            }

            List<string> reversed = new List<string>(forward);
            reversed.Reverse();

            ConePatternSet first = BuildWith(forward, sizes);
            ConePatternSet second = BuildWith(reversed, sizes);

            first.RecursiveDirectories.ShouldMatchInOrder(second.RecursiveDirectories);
            first.ParentOnlyDirectories.ShouldMatchInOrder(second.ParentOnlyDirectories);
        }

        [TestCase]
        public void OptionsRejectOutOfRangeValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConeGranularityOptions(maxSubtreeFiles: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConeGranularityOptions(minModifiedFiles: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConeGranularityOptions(minDensity: 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConeGranularityOptions(minDensity: 1.5));
        }

        [TestCase]
        public void DensityCapFor_IsTheLargestSubtreeThatCanStillQualify()
        {
            ConeGranularityOptions options = ConeGranularityOptions.Default;

            options.DensityCapFor(0).ShouldEqual(0);
            options.DensityCapFor(10).ShouldEqual(20);
            options.DensityCapFor(1).ShouldEqual(2);
        }
    }
}
