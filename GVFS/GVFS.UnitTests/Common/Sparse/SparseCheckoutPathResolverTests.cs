using System;
using System.IO;
using GVFS.Common.Sparse;
using GVFS.Tests.Should;
using NUnit.Framework;

namespace GVFS.UnitTests.Common.Sparse
{
    [TestFixture]
    public class SparseCheckoutPathResolverTests
    {
        private const string PrimaryGitDir = @"C:\repo\src\.git";
        private const string WorktreeGitDir = @"C:\repo\src\.git\worktrees\linked1";

        [TestCase]
        public void PrimaryEnlistmentUsesDotGitRoot()
        {
            string expected = Path.Combine(PrimaryGitDir, "info", "sparse-checkout");

            SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: false, dotGitRoot: PrimaryGitDir, worktreeGitDir: null)
                .ShouldEqual(expected);
        }

        [TestCase]
        public void PrimaryEnlistmentIgnoresWorktreeGitDir()
        {
            string expected = Path.Combine(PrimaryGitDir, "info", "sparse-checkout");

            // Even if a worktree git dir is supplied, the primary path must come from dotGitRoot.
            SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: false, dotGitRoot: PrimaryGitDir, worktreeGitDir: WorktreeGitDir)
                .ShouldEqual(expected);
        }

        [TestCase]
        public void LinkedWorktreeUsesWorktreeGitDir()
        {
            string expected = Path.Combine(WorktreeGitDir, "info", "sparse-checkout");

            SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: true, dotGitRoot: PrimaryGitDir, worktreeGitDir: WorktreeGitDir)
                .ShouldEqual(expected);
        }

        [TestCase]
        public void LinkedWorktreeDoesNotUseSharedDotGitRoot()
        {
            string sharedPath = Path.Combine(PrimaryGitDir, "info", "sparse-checkout");

            // The linked worktree must not resolve to the shared git dir, even though
            // GVFSEnlistment.DotGitRoot points there for a worktree.
            SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: true, dotGitRoot: PrimaryGitDir, worktreeGitDir: WorktreeGitDir)
                .ShouldNotEqual(sharedPath);
        }

        [TestCase]
        public void PrimaryEnlistmentThrowsWhenDotGitRootMissing()
        {
            Assert.Throws<ArgumentException>(
                () => SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: false, dotGitRoot: null, worktreeGitDir: null));
            Assert.Throws<ArgumentException>(
                () => SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: false, dotGitRoot: string.Empty, worktreeGitDir: null));
        }

        [TestCase]
        public void WorktreeEnlistmentThrowsWhenWorktreeGitDirMissing()
        {
            Assert.Throws<ArgumentException>(
                () => SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: true, dotGitRoot: PrimaryGitDir, worktreeGitDir: null));
            Assert.Throws<ArgumentException>(
                () => SparseCheckoutPathResolver.GetSparseCheckoutFilePath(isWorktree: true, dotGitRoot: PrimaryGitDir, worktreeGitDir: string.Empty));
        }

        [TestCase]
        public void EnlistmentOverloadThrowsOnNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => SparseCheckoutPathResolver.GetSparseCheckoutFilePath(null));
        }
    }
}
