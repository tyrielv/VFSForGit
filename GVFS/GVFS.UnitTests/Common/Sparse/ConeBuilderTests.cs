using System;
using System.Collections.Generic;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class ConeBuilderTests
    {
        // The worked examples use this tree:
        //   /
        //     root.txt
        //     .gitattributes
        //     A/
        //       a.txt
        //       .gitattributes
        //       B/
        //         b.txt
        //         C/
        //           c.txt
        //       D/
        //         d.txt
        private const string Header = "/*\n!/*/\n";

        [TestCase]
        public void EmptyModifiedSetProducesHeaderOnly()
        {
            SerializeCone().ShouldEqual(Header);
        }

        [TestCase]
        public void NullModifiedPathsThrows()
        {
            Assert.Throws<ArgumentNullException>(() => ConeBuilder.BuildFromModifiedPaths(null));
        }

        [TestCase]
        public void Example1_OneRootFileProducesHeaderOnly()
        {
            SerializeCone("root.txt").ShouldEqual(Header);
        }

        [TestCase]
        public void RootGitAttributesProducesNoExtraPattern()
        {
            SerializeCone(".gitattributes").ShouldEqual(Header);
        }

        [TestCase]
        public void Example2_OneDeepFileProducesParentOnlyChain()
        {
            SerializeCone("A/B/b.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n");
        }

        [TestCase]
        public void DeeplyNestedFileProducesFullParentOnlyChain()
        {
            SerializeCone("A/B/C/c.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n" +
                "/A/B/C/\n!/A/B/C/*/\n");
        }

        [TestCase]
        public void Example3_TwoFilesInAdjacentDirectories()
        {
            SerializeCone("A/B/b.txt", "A/D/d.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n" +
                "/A/D/\n!/A/D/*/\n");
        }

        [TestCase]
        public void Example4_OneDeepFileAndOneDeeperFile()
        {
            SerializeCone("A/B/b.txt", "A/B/C/c.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n" +
                "/A/B/C/\n!/A/B/C/*/\n");
        }

        [TestCase]
        public void Example5_ModifiedFolderMapsToRecursive()
        {
            SerializeCone("A/B/").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n");
        }

        [TestCase]
        public void Example6_FolderPlusSiblingFile()
        {
            SerializeCone("A/B/", "A/D/d.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/D/\n!/A/D/*/\n" +
                "/A/B/\n");
        }

        [TestCase]
        public void TwoFilesInSameDirectoryProduceOneParentPair()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt", "A/B/b2.txt" });

            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A", "A/B");
        }

        [TestCase]
        public void SingleModifiedFileNeverProducesRecursivePattern()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt" });

            // This is the whole point of the feature: an edited file must not pull its
            // subtree into the index, so it must not create a recursive pattern.
            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A", "A/B");
        }

        [TestCase]
        public void FileAndItsOwnParentFolderCollapseToRecursive()
        {
            // A/B/ (folder) is recursive; the file A/B/b.txt below it needs no parent-only
            // pattern, because the recursive pattern already covers it.
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt", "A/B/" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A/B");
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A");

            SerializeCone("A/B/b.txt", "A/B/").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n");
        }

        [TestCase]
        public void ModifiedFolderPlusSiblingFileKeepsParentAndSibling()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/", "A/D/d.txt" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A/B");
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A", "A/D");
        }

        [TestCase]
        public void RecursiveAncestorRemovesChildRecursiveEntries()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/", "A/B/" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A");
            cone.ParentOnlyDirectories.ShouldBeEmpty();

            SerializeCone("A/", "A/B/").ShouldEqual(Header + "/A/\n");
        }

        [TestCase]
        public void ParentOnlyDirectoriesBelowRecursiveVanish()
        {
            // The file's parent-only chain (A, A/B) is entirely inside the recursive A/,
            // so nothing parent-only survives.
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/x.txt", "A/" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A");
            cone.ParentOnlyDirectories.ShouldBeEmpty();

            SerializeCone("A/B/x.txt", "A/").ShouldEqual(Header + "/A/\n");
        }

        [TestCase]
        public void ParentOnlyAncestorOfRecursiveIsKept()
        {
            // A is an ancestor of the recursive A/B, not a descendant, so it stays
            // parent-only: a recursive directory does not include its ancestors' direct files.
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A/B");
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A");
        }

        [TestCase]
        public void SiblingRecursiveFoldersAreBothKept()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/", "A/D/" });

            cone.RecursiveDirectories.ShouldMatchInOrder("A/B", "A/D");
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A");
        }

        [TestCase]
        public void NullAndEmptyEntriesAreSkipped()
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { null, string.Empty, "/", "A/B/b.txt" });

            cone.RecursiveDirectories.ShouldBeEmpty();
            cone.ParentOnlyDirectories.ShouldMatchInOrder("A", "A/B");
        }

        [TestCase]
        public void PathsAreNormalizedBeforeMapping()
        {
            // Backslashes, leading slash, and duplicate separators must all normalize to the
            // same cone as the canonical git-form input.
            string canonical = SerializeCone("A/B/b.txt");

            SerializeCone(@"A\B\b.txt").ShouldEqual(canonical);
            SerializeCone("/A/B/b.txt").ShouldEqual(canonical);
            SerializeCone("A//B/b.txt").ShouldEqual(canonical);
        }

        [TestCase]
        public void FolderEntryWithBackslashIsRecognizedAsFolder()
        {
            SerializeCone(@"A\B\").ShouldEqual(SerializeCone("A/B/"));
        }

        [TestCase]
        public void OrdinalSortOrdersUppercaseBeforeLowercase()
        {
            // Ordinal sort places all uppercase letters before lowercase, matching git's
            // strcmp-based writer.
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "a/one.txt", "B/two.txt" });

            cone.ParentOnlyDirectories.ShouldMatchInOrder("B", "a");
        }

        [TestCase]
        public void GlobSpecialCharactersInPathAreEscaped()
        {
            // A '[' is glob-special and must be escaped; ']' is not.
            SerializeCone("A/B[1]/x.txt").ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B\\[1]/\n!/A/B\\[1]/*/\n");
        }

        [TestCase]
        public void StarAndQuestionInPathAreEscaped()
        {
            SerializeCone("weird*dir/f.txt", "q?dir/f.txt").ShouldEqual(
                Header +
                "/q\\?dir/\n!/q\\?dir/*/\n" +
                "/weird\\*dir/\n!/weird\\*dir/*/\n");
        }

        private static string SerializeCone(params string[] modifiedPaths)
        {
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(modifiedPaths);
            return ConeFileWriter.Serialize(cone);
        }
    }
}
