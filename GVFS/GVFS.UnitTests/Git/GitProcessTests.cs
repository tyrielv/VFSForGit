﻿using GVFS.Common.Git;
using GVFS.Tests.Should;
using GVFS.UnitTests.Mock.Common;
using NUnit.Framework;

namespace GVFS.UnitTests.Git
{
    [TestFixture]
    public class GitProcessTests
    {
        [TestCase]
        public void TryKillRunningProcess_NeverRan()
        {
            GitProcess process = new GitProcess(new MockGVFSEnlistment());
            process.TryKillRunningProcess().ShouldBeTrue();
        }

        [TestCase]
        public void ResultHasNoErrors()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                string.Empty,
                0);

            result.ExitCodeIsFailure.ShouldBeFalse();
            result.StderrContainsErrors().ShouldBeFalse();
        }

        [TestCase]
        public void ResultHasWarnings()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                "Warning: this is fine.\n",
                0);

            result.ExitCodeIsFailure.ShouldBeFalse();
            result.StderrContainsErrors().ShouldBeFalse();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_SingleLine_AllWarnings()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                "warning: this line should not be considered an error",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeFalse();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_Multiline_AllWarnings()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                @"warning: this line should not be considered an error
WARNING: neither should this.",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeFalse();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_Multiline_EmptyLines()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                @"
warning: this is fine

warning: this is too

",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeFalse();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_Singleline_AllErrors()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                "this is an error",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeTrue();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_Multiline_AllErrors()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                @"error1
error2",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeTrue();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_Multiline_ErrorsAndWarnings()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                @"WARNING: this is fine
this is an error",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeTrue();
        }

        [TestCase]
        public void ResultHasNonWarningErrors_TrailingWhitespace_Warning()
        {
            GitProcess.Result result = new GitProcess.Result(
                string.Empty,
                "Warning: this is fine\n",
                1);

            result.ExitCodeIsFailure.ShouldBeTrue();
            result.StderrContainsErrors().ShouldBeFalse();
        }
    }
}
