using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using GVFS.UnitTests.Category;
using Moq;
using NUnit.Framework;
using System;
using System.IO;

namespace GVFS.UnitTests.Tracing
{
    [TestFixture]
    public class LogRotationConfigTests
    {
        [TestCase("true\n", true)]
        [TestCase("false\n", false)]
        public void CanonicalGitBooleansControlRotation(string output, bool expected)
        {
            Mock<GitProcess> git = CreateGit(new GitProcess.Result(output, string.Empty, 0));
            Assert.That(LogRotationConfig.ReadEnabled(git.Object, false, out string error), Is.EqualTo(expected));
            Assert.That(error, Is.Null);
        }

        [TestCase]
        public void MissingConfigurationUsesTheFalseDefaultWithoutAWarning()
        {
            Mock<GitProcess> git = CreateGit(new GitProcess.Result(string.Empty, string.Empty, 1));
            Assert.That(LogRotationConfig.ReadEnabled(git.Object, false, out string error), Is.False);
            Assert.That(GVFSConstants.GitConfig.LogRotationDefault, Is.False);
            Assert.That(error, Is.Null);
        }

        [TestCase("", "fatal: invalid boolean", 128)]
        [TestCase("", "fatal: access denied", 1)]
        [TestCase("", "", 128)]
        [TestCase("not a boolean", "", 0)]
        public void InvalidValuesAndReadFailuresDisableRotationWithAWarning(string output, string errors, int exitCode)
        {
            Mock<GitProcess> git = CreateGit(new GitProcess.Result(output, errors, exitCode));
            Assert.That(LogRotationConfig.ReadEnabled(git.Object, false, out string error), Is.False);
            Assert.That(error, Does.Contain("gvfs.log-rotation"));
            Assert.That(error, Does.Contain("disabled"));
        }

        [TestCase]
        [Category(CategoryConstants.ExceptionExpected)]
        public void LaunchAndAccessExceptionsProduceWarnings()
        {
            foreach (Exception exception in new Exception[]
            {
                new IOException("read failure"),
                new UnauthorizedAccessException("access failure"),
                new System.ComponentModel.Win32Exception("launch failure"),
            })
            {
                Mock<GitProcess> git = CreateGit(new GitProcess.Result(string.Empty, string.Empty, 0));
                git.Setup(process => process.GetBooleanConfig(GVFSConstants.GitConfig.LogRotation, It.IsAny<bool>(), null)).Throws(exception);
                Assert.That(LogRotationConfig.ReadEnabled(git.Object, false, out string error), Is.False);
                Assert.That(error, Does.Contain(exception.ToString()));
            }
        }

        [TestCase]
        public void ServiceConfigurationExplicitlyUsesSystemScope()
        {
            Mock<GitProcess> git = CreateGit(new GitProcess.Result("true\n", string.Empty, 0));
            Assert.That(LogRotationConfig.ReadEnabled(git.Object, true, out string error), Is.True);
            Assert.That(error, Is.Null);
            git.Verify(process => process.GetBooleanConfig(GVFSConstants.GitConfig.LogRotation, true, null), Times.Once);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void TruncatedMissingResultsAreNotSilentlyTreatedAsMissing(bool outputTruncated, bool errorsTruncated)
        {
            Mock<GitProcess> git = CreateGit(new GitProcess.Result(string.Empty, string.Empty, 1, outputTruncated, errorsTruncated));
            Assert.That(LogRotationConfig.ReadEnabled(git.Object, false, out string error), Is.False);
            Assert.That(error, Is.Not.Null);
        }

        [TestCase(true, false, false, true)]
        [TestCase(false, true, false, true)]
        [TestCase(false, false, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(false, true, true, false)]
        public void BooleanReadsUseTheRepositoryOnlyWhenItExistsAndSystemScopeDoesNotApply(
            bool directoryExists,
            bool fileExists,
            bool systemOnly,
            bool expectRepository)
        {
            CapturingGitProcess git = new CapturingGitProcess();
            git.GetBooleanConfig(GVFSConstants.GitConfig.LogRotation, systemOnly, new RepositoryFileSystem(directoryExists, fileExists));
            Assert.That(git.Command, Is.EqualTo("config " + (systemOnly ? "--system " : string.Empty) + "--type=bool --get gvfs.log-rotation"));
            Assert.That(git.DotGitDirectory, Is.EqualTo(expectRepository ? @"mock:\repo\.git" : null));
            if (expectRepository)
            {
                Assert.That(git.UsePreCommandHook, Is.False);
            }
        }

        private static Mock<GitProcess> CreateGit(GitProcess.Result result)
        {
            Mock<GitProcess> git = new Mock<GitProcess>(@"mock:\git.exe", null);
            git.Setup(process => process.GetBooleanConfig(GVFSConstants.GitConfig.LogRotation, It.IsAny<bool>(), null)).Returns(result);
            return git;
        }

        private class RepositoryFileSystem : PhysicalFileSystem
        {
            private readonly bool directoryExists;
            private readonly bool fileExists;

            public RepositoryFileSystem(bool directoryExists, bool fileExists)
            {
                this.directoryExists = directoryExists;
                this.fileExists = fileExists;
            }

            public override bool DirectoryExists(string path)
            {
                Assert.That(path, Is.EqualTo(@"mock:\repo\.git"));
                return this.directoryExists;
            }

            public override bool FileExists(string path)
            {
                Assert.That(path, Is.EqualTo(@"mock:\repo\.git"));
                return this.fileExists;
            }
        }

        private class CapturingGitProcess : GitProcess
        {
            public CapturingGitProcess()
                : base(@"mock:\git.exe", @"mock:\repo")
            {
            }

            public string Command { get; private set; }
            public string DotGitDirectory { get; private set; }
            public bool UsePreCommandHook { get; private set; }

            protected override Result InvokeGitImpl(
                string command,
                string workingDirectory,
                string dotGitDirectory,
                bool useReadObjectHook,
                Action<StreamWriter> writeStdIn,
                Action<string> parseStdOutLine,
                int timeoutMs,
                string gitObjectsDirectory = null,
                bool usePrecommandHook = true,
                bool mayRequireAuth = false,
                bool? hasVisibleConsoleWindow = null)
            {
                this.Command = command;
                this.DotGitDirectory = dotGitDirectory;
                this.UsePreCommandHook = usePrecommandHook;
                return new Result("true\n", string.Empty, 0);
            }
        }
    }
}
