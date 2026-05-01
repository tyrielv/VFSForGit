using GVFS.Common;
using GVFS.Common.Tracing;
using System;
using System.Diagnostics;
using System.IO;

namespace GVFS.Service
{
    /// <summary>
    /// Detects and applies staged upgrades from the PendingUpgrade directory.
    ///
    /// When the installer runs with mounts active, it stages new files to
    /// {installDir}\PendingUpgrade\ instead of replacing files in-place.
    /// On service start (before automount), if no GVFS.Mount processes are
    /// running, this class copies staged files directly into the install
    /// directory. With native AOT, each exe is self-contained — the only
    /// locked file is GVFS.Service.exe itself, which the installer already
    /// replaced in-place.
    /// </summary>
    public static class PendingUpgradeHandler
    {
        private const string PendingUpgradeDirectoryName = "PendingUpgrade";
        private const int MaxRetries = 3;

        /// <summary>
        /// Checks for and applies a pending staged upgrade. Returns true if
        /// an upgrade was applied (caller should proceed with normal startup).
        /// </summary>
        public static bool TryApplyPendingUpgrade(ITracer tracer)
        {
            string installDir = Configuration.AssemblyPath;
            string pendingUpgradeDir = Path.Combine(installDir, PendingUpgradeDirectoryName);

            if (!Directory.Exists(pendingUpgradeDir))
            {
                return false;
            }

            tracer.RelatedInfo($"{nameof(PendingUpgradeHandler)}: Pending upgrade detected at {pendingUpgradeDir}");

            // Don't apply if GVFS.Mount processes are still running — their
            // executables are locked and copy would fail. Upgrade will be
            // retried on next service start when no mounts are active.
            Process[] mountProcesses = Array.Empty<Process>();
            try
            {
                mountProcesses = Process.GetProcessesByName("GVFS.Mount");
                if (mountProcesses.Length > 0)
                {
                    tracer.RelatedWarning(
                        $"{nameof(PendingUpgradeHandler)}: {mountProcesses.Length} GVFS.Mount process(es) still running. " +
                        "Deferring upgrade until no mounts are active.");
                    return false;
                }
            }
            finally
            {
                foreach (Process p in mountProcesses)
                {
                    p.Dispose();
                }
            }

            try
            {
                int filesCopied = 0;
                int filesSkipped = 0;
                int filesFailed = 0;

                foreach (string sourceFile in Directory.GetFiles(pendingUpgradeDir, "*", SearchOption.AllDirectories))
                {
                    string relativePath = sourceFile.Substring(pendingUpgradeDir.Length).TrimStart(Path.DirectorySeparatorChar);
                    string destFile = Path.Combine(installDir, relativePath);

                    // Skip GVFS.Service.exe — it's our own running binary (locked)
                    // and was already replaced in-place by the installer.
                    if (string.Equals(relativePath, "GVFS.Service.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        filesSkipped++;
                        continue;
                    }

                    string destDir = Path.GetDirectoryName(destFile);
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    if (!TryCopyFileWithRetry(tracer, sourceFile, destFile))
                    {
                        filesFailed++;
                    }
                    else
                    {
                        filesCopied++;
                    }
                }

                tracer.RelatedInfo(
                    $"{nameof(PendingUpgradeHandler)}: Upgrade complete. " +
                    $"Copied={filesCopied}, Skipped={filesSkipped}, Failed={filesFailed}");

                if (filesFailed > 0)
                {
                    tracer.RelatedWarning(
                        $"{nameof(PendingUpgradeHandler)}: {filesFailed} file(s) could not be copied. " +
                        "PendingUpgrade directory retained for retry on next service start.");
                    return true;
                }

                // All files copied — clean up staging directory
                try
                {
                    Directory.Delete(pendingUpgradeDir, recursive: true);
                    tracer.RelatedInfo($"{nameof(PendingUpgradeHandler)}: Staging directory removed");
                }
                catch (Exception ex)
                {
                    tracer.RelatedWarning($"{nameof(PendingUpgradeHandler)}: Failed to remove staging directory: {ex.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                tracer.RelatedError($"{nameof(PendingUpgradeHandler)}: Failed to apply pending upgrade: {ex}");
                return false;
            }
        }

        private static bool TryCopyFileWithRetry(ITracer tracer, string source, string dest)
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    File.Copy(source, dest, overwrite: true);
                    return true;
                }
                catch (IOException ex) when (attempt < MaxRetries - 1)
                {
                    tracer.RelatedWarning(
                        $"{nameof(PendingUpgradeHandler)}: Failed to copy {Path.GetFileName(source)} " +
                        $"(attempt {attempt + 1}/{MaxRetries}): {ex.Message}");
                    System.Threading.Thread.Sleep(1000 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    tracer.RelatedError(
                        $"{nameof(PendingUpgradeHandler)}: Failed to copy {Path.GetFileName(source)}: {ex.Message}");
                    return false;
                }
            }

            return false;
        }
    }
}
