using System;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.FileSystem;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class ConeFileWriterTests
    {
        private const string RepoRoot = @"mock:\repo";
        private const string GitDir = @"mock:\repo\.git";
        private const string InfoDir = @"mock:\repo\.git\info";
        private const string SparseCheckoutPath = @"mock:\repo\.git\info\sparse-checkout";
        private const string Header = "/*\n!/*/\n";

        [TestCase]
        public void ConstructorThrowsOnNullFileSystem()
        {
            Assert.Throws<ArgumentNullException>(() => new ConeFileWriter(null));
        }

        [TestCase]
        public void SerializeThrowsOnNullCone()
        {
            Assert.Throws<ArgumentNullException>(() => ConeFileWriter.Serialize(null));
        }

        [TestCase]
        public void SerializeEmptyConeProducesHeaderOnly()
        {
            ConePatternSet cone = new ConePatternSet(new string[0], new string[0]);
            ConeFileWriter.Serialize(cone).ShouldEqual(Header);
        }

        [TestCase]
        public void SerializeEmitsParentPairsThenRecursiveLines()
        {
            ConePatternSet cone = new ConePatternSet(
                new[] { "A", "A/B" },
                new[] { "A/D" });

            ConeFileWriter.Serialize(cone).ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n" +
                "/A/D/\n");
        }

        [TestCase("simple", "simple")]
        [TestCase("A/B", "A/B")]
        [TestCase("star*", "star\\*")]
        [TestCase("q?", "q\\?")]
        [TestCase("br[x", "br\\[x")]
        [TestCase("back\\slash", "back\\\\slash")]
        [TestCase("close]bracket", "close]bracket")]
        [TestCase("all*?[\\", "all\\*\\?\\[\\\\")]
        public void EscapePatternEscapesGlobSpecialCharacters(string input, string expected)
        {
            ConeFileWriter.EscapePattern(input).ShouldEqual(expected);
        }

        [TestCase]
        public void EscapePatternThrowsOnNull()
        {
            Assert.Throws<ArgumentNullException>(() => ConeFileWriter.EscapePattern(null));
        }

        [TestCase]
        public void IsLegacyContentRecognizesGitAttributesLine()
        {
            ConeFileWriter.IsLegacyContent("/.gitattributes").ShouldBeTrue();
            ConeFileWriter.IsLegacyContent("/.gitattributes\r\n").ShouldBeTrue();
            ConeFileWriter.IsLegacyContent("/.gitattributes\n").ShouldBeTrue();
        }

        [TestCase]
        public void IsLegacyContentRejectsConeAndNull()
        {
            ConeFileWriter.IsLegacyContent(null).ShouldBeFalse();
            ConeFileWriter.IsLegacyContent(Header).ShouldBeFalse();
            ConeFileWriter.IsLegacyContent("/A/\n").ShouldBeFalse();
            ConeFileWriter.IsLegacyContent(string.Empty).ShouldBeFalse();
        }

        [TestCase]
        public void TryWriteCreatesFileWhenNoneExists()
        {
            MockFileSystem fileSystem = CreateFileSystem();
            ConeFileWriter writer = new ConeFileWriter(fileSystem);
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt" });

            bool result = writer.TryWrite(SparseCheckoutPath, cone, out string backupPath, out Exception handledException);

            result.ShouldBeTrue();
            handledException.ShouldBeNull();
            backupPath.ShouldBeNull();
            fileSystem.ReadAllText(SparseCheckoutPath).ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n");
        }

        [TestCase]
        public void TryWriteBacksUpLegacyContentAndReplacesIt()
        {
            MockFileSystem fileSystem = CreateFileSystem();
            fileSystem.WriteAllText(SparseCheckoutPath, ConeFileWriter.LegacyContent + "\r\n");

            ConeFileWriter writer = new ConeFileWriter(fileSystem);
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt" });

            bool result = writer.TryWrite(SparseCheckoutPath, cone, out string backupPath, out Exception handledException);

            result.ShouldBeTrue();
            handledException.ShouldBeNull();
            backupPath.ShouldEqual(SparseCheckoutPath + ConeFileWriter.BackupExtension);

            string backupContent = fileSystem.ReadAllText(backupPath);
            ConeFileWriter.IsLegacyContent(backupContent).ShouldBeTrue();

            fileSystem.ReadAllText(SparseCheckoutPath).ShouldEqual(
                Header +
                "/A/\n!/A/*/\n" +
                "/A/B/\n!/A/B/*/\n");
        }

        [TestCase]
        public void TryRestoreBackupRestoresPreviousContent()
        {
            MockFileSystem fileSystem = CreateFileSystem();
            fileSystem.WriteAllText(SparseCheckoutPath, ConeFileWriter.LegacyContent + "\r\n");

            ConeFileWriter writer = new ConeFileWriter(fileSystem);
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(new[] { "A/B/b.txt" });

            writer.TryWrite(SparseCheckoutPath, cone, out string backupPath, out Exception writeException).ShouldBeTrue();
            writeException.ShouldBeNull();

            bool restored = writer.TryRestoreBackup(SparseCheckoutPath, backupPath, out Exception restoreException);

            restored.ShouldBeTrue();
            restoreException.ShouldBeNull();
            ConeFileWriter.IsLegacyContent(fileSystem.ReadAllText(SparseCheckoutPath)).ShouldBeTrue();
        }

        [TestCase]
        public void TryWriteThrowsOnNullArguments()
        {
            ConeFileWriter writer = new ConeFileWriter(CreateFileSystem());
            ConePatternSet cone = new ConePatternSet(new string[0], new string[0]);

            Assert.Throws<ArgumentNullException>(
                () => writer.TryWrite(null, cone, out _, out _));
            Assert.Throws<ArgumentNullException>(
                () => writer.TryWrite(SparseCheckoutPath, null, out _, out _));
        }

        private static MockFileSystem CreateFileSystem()
        {
            MockDirectory info = new MockDirectory(InfoDir, folders: null, files: null);
            MockDirectory git = new MockDirectory(GitDir, new[] { info }, files: null);
            MockDirectory root = new MockDirectory(RepoRoot, new[] { git }, files: null);
            return new MockFileSystem(root);
        }
    }
}
