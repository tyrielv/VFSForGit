using GVFS.Common.Git;
using System;
using System.ComponentModel;
using System.IO;

namespace GVFS.Common.Tracing
{
    internal static class LogRotationConfig
    {
        public static bool ReadEnabled(GitProcess git, bool systemOnly, out string error)
        {
            error = null;
            try
            {
                if (git == null)
                {
                    string gitPath = GVFSPlatform.Instance.GitInstallation.GetInstalledGitBinPath();
                    if (string.IsNullOrEmpty(gitPath))
                    {
                        error = "Cannot read gvfs.log-rotation because Git is not available. Log rotation remains disabled.";
                        return GVFSConstants.GitConfig.LogRotationDefault;
                    }

                    git = new GitProcess(gitPath, workingDirectoryRoot: null);
                }

                GitProcess.Result result = git.GetBooleanConfig(GVFSConstants.GitConfig.LogRotation, systemOnly);
                if (result.ExitCode == 1 &&
                    string.IsNullOrEmpty(result.Output) &&
                    !result.OutputTruncated &&
                    !result.ErrorsTruncated &&
                    !result.StderrContainsErrors())
                {
                    return GVFSConstants.GitConfig.LogRotationDefault;
                }

                if (result.ExitCodeIsSuccess && !result.OutputTruncated)
                {
                    string value = result.Output?.Trim();
                    if (value == "true" || value == "false")
                    {
                        return value == "true";
                    }
                }

                error = $"Cannot read gvfs.log-rotation as a boolean (Git exit code {result.ExitCode}). Log rotation remains disabled. {result.Errors}";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
            {
                error = "Cannot read gvfs.log-rotation. Log rotation remains disabled. " + ex.ToString();
            }

            return GVFSConstants.GitConfig.LogRotationDefault;
        }
    }
}
