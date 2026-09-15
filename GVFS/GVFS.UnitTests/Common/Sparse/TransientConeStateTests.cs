using System.Collections.Generic;
using System.Linq;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class TransientConeStateTests
    {
        [TestCase]
        public void AddedPathsAppearInUnion()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/", "B/c.txt" });

            Union(state).ShouldMatchInOrder("A/", "B/c.txt");
            state.SessionCount.ShouldEqual(1);
        }

        [TestCase]
        public void PathsFromMultipleSessionsAreUnioned()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/" });
            state.AddPaths("s2", new[] { "B/" });

            Union(state).ShouldMatchInOrder("A/", "B/");
            state.SessionCount.ShouldEqual(2);
        }

        [TestCase]
        public void RepeatedAddInSameSessionAccumulates()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/" });
            state.AddPaths("s1", new[] { "B/" });

            Union(state).ShouldMatchInOrder("A/", "B/");
            state.SessionCount.ShouldEqual(1);
        }

        [TestCase]
        public void DuplicatePathsAreCollapsed()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/", "A/" });
            state.AddPaths("s2", new[] { "A/" });

            Union(state).ShouldMatchInOrder("A/");
        }

        [TestCase]
        public void RemoveSessionDropsOnlyThatSession()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/" });
            state.AddPaths("s2", new[] { "B/" });

            state.RemoveSession("s1").ShouldBeTrue();

            Union(state).ShouldMatchInOrder("B/");
            state.SessionCount.ShouldEqual(1);
        }

        [TestCase]
        public void RemoveUnknownSessionReturnsFalse()
        {
            TransientConeState state = new TransientConeState();
            state.RemoveSession("missing").ShouldBeFalse();
        }

        [TestCase]
        public void NullAndEmptyGitPathsAreIgnored()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths("s1", new[] { "A/", null, string.Empty });

            Union(state).ShouldMatchInOrder("A/");
        }

        [TestCase]
        public void NullSessionIdSharesTheEmptyKey()
        {
            TransientConeState state = new TransientConeState();
            state.AddPaths(null, new[] { "A/" });
            state.AddPaths(string.Empty, new[] { "B/" });

            state.SessionCount.ShouldEqual(1);
            Union(state).ShouldMatchInOrder("A/", "B/");

            state.RemoveSession(null).ShouldBeTrue();
            state.SessionCount.ShouldEqual(0);
        }

        private static List<string> Union(TransientConeState state)
        {
            List<string> paths = state.GetAllTransientPaths().ToList();
            paths.Sort(System.StringComparer.Ordinal);
            return paths;
        }
    }
}
