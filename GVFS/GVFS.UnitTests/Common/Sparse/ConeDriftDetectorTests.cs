using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class ConeDriftDetectorTests
    {
        private const string ConeContent = "/*\n!/*/\n/src/\n";
        private const string LegacyContent = "/.gitattributes";

        // ── HasDrifted ──────────────────────────────────────────────────

        [TestCase]
        public void HasDrifted_FalseWhenNoBaseline()
        {
            // GVFS cannot judge drift before it has written the file once.
            ConeDriftDetector.HasDrifted(lastWrittenContent: null, currentContent: ConeContent)
                .ShouldBeFalse();
        }

        [TestCase]
        public void HasDrifted_FalseWhenIdentical()
        {
            ConeDriftDetector.HasDrifted(ConeContent, ConeContent).ShouldBeFalse();
        }

        [TestCase]
        public void HasDrifted_TrueWhenContentChanged()
        {
            ConeDriftDetector.HasDrifted(ConeContent, ConeContent + "/docs/\n").ShouldBeTrue();
        }

        [TestCase]
        public void HasDrifted_TrueWhenFileEmptiedAfterBaseline()
        {
            ConeDriftDetector.HasDrifted(ConeContent, currentContent: null).ShouldBeTrue();
            ConeDriftDetector.HasDrifted(ConeContent, currentContent: string.Empty).ShouldBeTrue();
        }

        [TestCase]
        public void HasDrifted_IsOrdinalNotWhitespaceInsensitive()
        {
            // A trailing-whitespace edit is still drift; the check is a byte compare.
            ConeDriftDetector.HasDrifted(ConeContent, ConeContent + " ").ShouldBeTrue();
        }

        // ── IsNonConeFormat ─────────────────────────────────────────────

        [TestCase]
        public void IsNonConeFormat_FalseForEmptyOrNull()
        {
            ConeDriftDetector.IsNonConeFormat(null).ShouldBeFalse();
            ConeDriftDetector.IsNonConeFormat(string.Empty).ShouldBeFalse();
        }

        [TestCase]
        public void IsNonConeFormat_FalseForLegacyContent()
        {
            // GVFS's own pre-sparse-index content is not a user hand-edit.
            ConeDriftDetector.IsNonConeFormat(LegacyContent).ShouldBeFalse();
        }

        [TestCase]
        public void IsNonConeFormat_FalseForConeContent()
        {
            ConeDriftDetector.IsNonConeFormat(ConeContent).ShouldBeFalse();
            ConeDriftDetector.IsNonConeFormat("/src/\n/docs/\n").ShouldBeFalse();
            ConeDriftDetector.IsNonConeFormat("!/build/\n/src/\n").ShouldBeFalse();
        }

        [TestCase]
        public void IsNonConeFormat_TrueForNonAnchoredPattern()
        {
            // These silently disable the sparse index via is_sparse_index_allowed().
            ConeDriftDetector.IsNonConeFormat("*.cs\n").ShouldBeTrue();
            ConeDriftDetector.IsNonConeFormat("src/\n").ShouldBeTrue();
            ConeDriftDetector.IsNonConeFormat("/src/\nDocumentation/**\n").ShouldBeTrue();
        }

        [TestCase]
        public void IsNonConeFormat_IgnoresBlankAndCommentLines()
        {
            ConeDriftDetector.IsNonConeFormat("# comment\n\n/src/\n").ShouldBeFalse();
            ConeDriftDetector.IsNonConeFormat("# only comments\n").ShouldBeFalse();
        }

        // ── LooksLikeConeFormat ─────────────────────────────────────────

        [TestCase]
        public void LooksLikeConeFormat_FalseForEmpty()
        {
            ConeDriftDetector.LooksLikeConeFormat(null).ShouldBeFalse();
            ConeDriftDetector.LooksLikeConeFormat(string.Empty).ShouldBeFalse();
        }

        [TestCase]
        public void LooksLikeConeFormat_FalseWhenNoSignificantLine()
        {
            // Only comments/blanks — no pattern to judge as cone-format.
            ConeDriftDetector.LooksLikeConeFormat("# a\n\n").ShouldBeFalse();
        }

        [TestCase]
        public void LooksLikeConeFormat_TrueForRootAnchoredLines()
        {
            ConeDriftDetector.LooksLikeConeFormat("/src/\n!/src/bin/\n").ShouldBeTrue();
        }
    }
}
