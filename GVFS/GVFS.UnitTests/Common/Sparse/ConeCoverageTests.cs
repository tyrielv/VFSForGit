using System;
using System.Collections.Generic;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class ConeCoverageTests
    {
        private const string Header = "/*\n!/*/\n";

        // ── TryParseConeFile ────────────────────────────────────────────

        [TestCase]
        public void NullContentFailsToParse()
        {
            ConeCoverage.TryParseConeFile(null, out ConePatternSet cone).ShouldBeFalse();
            cone.ShouldBeNull();
        }

        [TestCase]
        public void HeaderOnlyParsesToEmptyCone()
        {
            ConeCoverage.TryParseConeFile(Header, out ConePatternSet cone).ShouldBeTrue();
            cone.ParentOnlyDirectories.Count.ShouldEqual(0);
            cone.RecursiveDirectories.Count.ShouldEqual(0);
        }

        [TestCase]
        public void ParsesParentOnlyDirectory()
        {
            ConeCoverage.TryParseConeFile(Header + "/A/\n!/A/*/\n", out ConePatternSet cone).ShouldBeTrue();
            cone.ParentOnlyDirectories.ShouldContain(d => d == "A");
            cone.RecursiveDirectories.Count.ShouldEqual(0);
        }

        [TestCase]
        public void ParsesRecursiveDirectory()
        {
            ConeCoverage.TryParseConeFile(Header + "/A/B/\n", out ConePatternSet cone).ShouldBeTrue();
            cone.RecursiveDirectories.ShouldContain(d => d == "A/B");
            cone.ParentOnlyDirectories.Count.ShouldEqual(0);
        }

        [TestCase]
        public void ParsesMixedParentOnlyAndRecursive()
        {
            ConeCoverage.TryParseConeFile(
                Header + "/A/\n!/A/*/\n" + "/A/B/\n",
                out ConePatternSet cone).ShouldBeTrue();
            cone.ParentOnlyDirectories.ShouldContain(d => d == "A");
            cone.RecursiveDirectories.ShouldContain(d => d == "A/B");
        }

        [TestCase]
        public void ToleratesCarriageReturnsAndBlankLines()
        {
            ConeCoverage.TryParseConeFile(
                "/*\r\n!/*/\r\n\r\n/A/\r\n!/A/*/\r\n",
                out ConePatternSet cone).ShouldBeTrue();
            cone.ParentOnlyDirectories.ShouldContain(d => d == "A");
        }

        [TestCase]
        public void MissingHeaderFailsToParse()
        {
            ConeCoverage.TryParseConeFile("/A/\n!/A/*/\n", out ConePatternSet cone).ShouldBeFalse();
            cone.ShouldBeNull();
        }

        [TestCase]
        public void LegacyContentFailsToParse()
        {
            ConeCoverage.TryParseConeFile(ConeFileWriter.LegacyContent, out ConePatternSet cone).ShouldBeFalse();
            cone.ShouldBeNull();
        }

        [TestCase]
        public void DanglingNegativeMarkerFailsToParse()
        {
            // "!/A/*/" without a matching "/A/" positive line is malformed.
            ConeCoverage.TryParseConeFile(Header + "!/A/*/\n", out ConePatternSet cone).ShouldBeFalse();
            cone.ShouldBeNull();
        }

        [TestCase]
        public void MalformedLineFailsToParse()
        {
            ConeCoverage.TryParseConeFile(Header + "not-a-pattern\n", out ConePatternSet cone).ShouldBeFalse();
            cone.ShouldBeNull();
        }

        [TestCase]
        public void ParsesEscapedGlobDirectoryName()
        {
            // A directory literally named "a[1]" is escaped as "a\[1]" by ConeFileWriter.
            ConeCoverage.TryParseConeFile(Header + "/a\\[1]/\n!/a\\[1]/*/\n", out ConePatternSet cone).ShouldBeTrue();
            cone.ParentOnlyDirectories.ShouldContain(d => d == "a[1]");
        }

        // ── AreAllPathsCovered: files ───────────────────────────────────

        [TestCase]
        public void EmptyPathListIsCovered()
        {
            ConePatternSet cone = Parse(Header);
            ConeCoverage.AreAllPathsCovered(cone, new List<string>()).ShouldBeTrue();
        }

        [TestCase]
        public void RootFileIsCoveredByHeader()
        {
            ConePatternSet cone = Parse(Header);
            ConeCoverage.AreAllPathsCovered(cone, new[] { "root.txt" }).ShouldBeTrue();
        }

        [TestCase]
        public void FileInParentOnlyDirectoryIsCovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/a.txt" }).ShouldBeTrue();
        }

        [TestCase]
        public void FileInSubdirectoryOfParentOnlyDirectoryIsNotCovered()
        {
            // Parent-only A covers A's direct files, not A/B's files.
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/B/b.txt" }).ShouldBeFalse();
        }

        [TestCase]
        public void FileUnderRecursiveDirectoryIsCovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n" + "/A/B/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/B/C/c.txt" }).ShouldBeTrue();
        }

        [TestCase]
        public void FileWithNoCoveredParentIsNotCovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "Z/z.txt" }).ShouldBeFalse();
        }

        [TestCase]
        public void CaseDifferingParentIsNotCovered()
        {
            // Ordinal comparison: a case-variant path falls through to the mount.
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "a/x.txt" }).ShouldBeFalse();
        }

        // ── AreAllPathsCovered: folders ─────────────────────────────────

        [TestCase]
        public void FolderPathspecNeedsRecursiveCoverage()
        {
            // A folder pathspec pulls in a whole subtree, so parent-only coverage is not
            // enough even though the folder name matches.
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/" }).ShouldBeFalse();
        }

        [TestCase]
        public void FolderEqualToRecursiveDirectoryIsCovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n" + "/A/B/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/B/" }).ShouldBeTrue();
        }

        [TestCase]
        public void FolderUnderRecursiveDirectoryIsCovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n" + "/A/B/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/B/C/" }).ShouldBeTrue();
        }

        [TestCase]
        public void AnyUncoveredPathMakesTheWholeSetUncovered()
        {
            ConePatternSet cone = Parse(Header + "/A/\n!/A/*/\n");
            ConeCoverage.AreAllPathsCovered(cone, new[] { "A/a.txt", "Z/z.txt" }).ShouldBeFalse();
        }

        // ── Round-trip against the real mount serialization ─────────────

        [TestCase]
        public void RoundTripCoversAlreadyIncludedPathButNotNewPath()
        {
            // Build the exact cone the mount would write from a modified file, serialize it
            // with the mount's writer, then parse it back. Adding the same file's sibling
            // must be covered (a no-op widen); adding an out-of-cone path must not be.
            ConePatternSet built = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt" });
            string serialized = ConeFileWriter.Serialize(built);

            ConeCoverage.TryParseConeFile(serialized, out ConePatternSet parsed).ShouldBeTrue();

            // A/B is parent-only, so a sibling file in A/B is covered.
            ConeCoverage.AreAllPathsCovered(parsed, new[] { "A/B/other.txt" }).ShouldBeTrue();

            // A file in an untouched directory is not covered.
            ConeCoverage.AreAllPathsCovered(parsed, new[] { "A/D/d.txt" }).ShouldBeFalse();

            // A recursive folder pathspec on the parent-only directory is not covered.
            ConeCoverage.AreAllPathsCovered(parsed, new[] { "A/B/" }).ShouldBeFalse();
        }

        [TestCase]
        public void NullArgumentsThrow()
        {
            ConePatternSet cone = Parse(Header);
            Assert.Throws<ArgumentNullException>(() => ConeCoverage.AreAllPathsCovered(null, new List<string>()));
            Assert.Throws<ArgumentNullException>(() => ConeCoverage.AreAllPathsCovered(cone, null));
        }

        private static ConePatternSet Parse(string content)
        {
            ConeCoverage.TryParseConeFile(content, out ConePatternSet cone).ShouldBeTrue();
            return cone;
        }
    }
}
