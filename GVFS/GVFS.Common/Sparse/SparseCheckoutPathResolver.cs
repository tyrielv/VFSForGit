using System;
using System.IO;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Resolves the per-worktree <c>info/sparse-checkout</c> file path for a GVFS
    /// enlistment. The cone file is worktree-scoped, so a linked worktree must write
    /// its own file, not the shared one.
    /// </summary>
    /// <remarks>
    /// A linked worktree's sparse-checkout file lives under its per-worktree git dir
    /// (<c>.git/worktrees/&lt;name&gt;/info/sparse-checkout</c>), not the shared git dir.
    /// <see cref="GVFSEnlistment.DotGitRoot"/> is overridden to the shared git dir for
    /// linked worktrees, so it must not be used to locate the cone file. The primary
    /// enlistment writes to <c>.git/info/sparse-checkout</c> under its git dir. Git
    /// resolves the same per-worktree location through <c>git_pathdup</c>, which routes
    /// <c>info/sparse-checkout</c> to the worktree git dir.
    /// </remarks>
    public static class SparseCheckoutPathResolver
    {
        /// <summary>
        /// Resolve the sparse-checkout file path for an enlistment, choosing the
        /// per-worktree git dir for linked worktrees and the shared git dir otherwise.
        /// </summary>
        public static string GetSparseCheckoutFilePath(GVFSEnlistment enlistment)
        {
            ArgumentNullException.ThrowIfNull(enlistment);

            return GetSparseCheckoutFilePath(
                enlistment.IsWorktree,
                enlistment.DotGitRoot,
                enlistment.IsWorktree ? enlistment.Worktree.WorktreeGitDir : null);
        }

        /// <summary>
        /// Resolve the sparse-checkout file path from primitive inputs. When
        /// <paramref name="isWorktree"/> is true the file is placed under
        /// <paramref name="worktreeGitDir"/>; otherwise it is placed under
        /// <paramref name="dotGitRoot"/> (the shared <c>.git</c> directory).
        /// </summary>
        public static string GetSparseCheckoutFilePath(bool isWorktree, string dotGitRoot, string worktreeGitDir)
        {
            string gitDir = isWorktree ? worktreeGitDir : dotGitRoot;
            if (string.IsNullOrEmpty(gitDir))
            {
                throw new ArgumentException(
                    isWorktree
                        ? "worktreeGitDir must be provided for a worktree enlistment."
                        : "dotGitRoot must be provided for a primary enlistment.");
            }

            return Path.Combine(gitDir, GVFSConstants.DotGit.Info.Name, GVFSConstants.DotGit.Info.SparseCheckoutName);
        }
    }
}
