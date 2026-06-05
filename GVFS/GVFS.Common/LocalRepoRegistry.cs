using GVFS.Common.FileSystem;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GVFS.Common
{
    /// <summary>
    /// Direct file-backed fallback for the per-user repo registry, used by
    /// the CLI verbs (mount/unmount/service) when the legacy GVFS.Service
    /// named pipe is unavailable (user-level install model). The on-disk
    /// format is wire-compatible with the service's RepoRegistry so the
    /// two models can co-exist or be migrated between.
    ///
    /// File layout (identical to GVFS.Service.RepoRegistry):
    ///   Line 1:      registry version number (integer, currently 2)
    ///   Lines 2..N:  one JSON object per line, matching the schema of
    ///                GVFS.Service.RepoRegistration
    ///   Path:        {CommonAppDataRoot}\GVFS.Service\repo-registry
    ///                (CommonAppDataRoot honors the GVFS_COMMON_APPDATA_ROOT
    ///                env var, so user-level installs redirect to
    ///                %LocalAppData%\GVFS without touching code.)
    /// </summary>
    public static class LocalRepoRegistry
    {
        public const string RegistryFileName = "repo-registry";
        public const string RegistryTempName = "repo-registry.lock";
        public const string ServiceDataDirName = "GVFS.Service";
        private const int RegistryVersion = 2;
        private static readonly object FileLock = new object();

        public static string GetRegistryDirectory()
        {
            // Matches the path the service-based RepoRegistry uses
            // (GVFSService.Windows.cs computes serviceDataLocation via
            // GetSecureDataRootForGVFSComponent(ServiceName)). Using
            // SecureData rather than CommonAppData keeps the on-disk
            // file location identical between the two models, so a
            // user-level install can read a registry the service wrote
            // (or vice-versa) without migration. In the user-level
            // model, GVFS_SECURE_DATA_ROOT is redirected via env var
            // to %LocalAppData%\GVFS\ so the user has write access.
            return Path.Combine(
                GVFSPlatform.Instance.GetSecureDataRootForGVFSComponent(ServiceDataDirName));
        }

        public static string GetRegistryFilePath()
        {
            return Path.Combine(GetRegistryDirectory(), RegistryFileName);
        }

        /// <summary>
        /// Returns enlistment-root paths for all currently-active registry
        /// entries. Returns an empty list if the registry file does not
        /// exist yet (no repos ever registered).
        /// </summary>
        public static List<string> GetActiveRepoPaths()
        {
            lock (FileLock)
            {
                Dictionary<string, LocalRepoRegistration> all = ReadRegistry();
                return all.Values.Where(r => r.IsActive).Select(r => r.EnlistmentRoot).ToList();
            }
        }

        /// <summary>
        /// Idempotently records the given enlistment root as active. If the
        /// entry already exists it is reactivated; OwnerSID is updated to
        /// the supplied value either way.
        /// </summary>
        public static void Register(string repoRoot, string ownerSID)
        {
            ArgumentNullException.ThrowIfNull(repoRoot);
            lock (FileLock)
            {
                Dictionary<string, LocalRepoRegistration> all = ReadRegistry();
                if (all.TryGetValue(repoRoot, out LocalRepoRegistration existing))
                {
                    existing.IsActive = true;
                    existing.OwnerSID = ownerSID;
                }
                else
                {
                    all[repoRoot] = new LocalRepoRegistration
                    {
                        EnlistmentRoot = repoRoot,
                        OwnerSID = ownerSID,
                        IsActive = true,
                    };
                }
                WriteRegistry(all);
            }
        }

        /// <summary>
        /// Marks the given entry inactive (matches the service's
        /// TryDeactivateRepo semantics — entry is retained so OwnerSID is
        /// preserved for a possible later re-register).
        /// </summary>
        public static void Unregister(string repoRoot)
        {
            ArgumentNullException.ThrowIfNull(repoRoot);
            lock (FileLock)
            {
                Dictionary<string, LocalRepoRegistration> all = ReadRegistry();
                if (all.TryGetValue(repoRoot, out LocalRepoRegistration existing) && existing.IsActive)
                {
                    existing.IsActive = false;
                    WriteRegistry(all);
                }
            }
        }

        private static Dictionary<string, LocalRepoRegistration> ReadRegistry()
        {
            Dictionary<string, LocalRepoRegistration> result =
                new Dictionary<string, LocalRepoRegistration>(GVFSPlatform.Instance.Constants.PathComparer);

            string path = GetRegistryFilePath();
            if (!File.Exists(path))
            {
                return result;
            }

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                string versionLine = reader.ReadLine();
                if (!int.TryParse(versionLine, out int version) || version > RegistryVersion)
                {
                    // Unsupported version - return empty to avoid corrupting
                    // a newer-than-us registry on next write.
                    return result;
                }

                while (!reader.EndOfStream)
                {
                    string entry = reader.ReadLine();
                    if (string.IsNullOrEmpty(entry))
                    {
                        continue;
                    }
                    try
                    {
                        LocalRepoRegistration reg = LocalRepoRegistration.FromJson(entry);
                        if (reg != null && !string.IsNullOrEmpty(reg.EnlistmentRoot))
                        {
                            result[reg.EnlistmentRoot] = reg;
                        }
                    }
                    catch
                    {
                        // Skip malformed lines; matches RepoRegistry.ReadRegistry behavior
                    }
                }
            }

            return result;
        }

        private static void WriteRegistry(Dictionary<string, LocalRepoRegistration> registry)
        {
            string dir = GetRegistryDirectory();
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = Path.Combine(dir, RegistryTempName);
            string finalPath = GetRegistryFilePath();

            using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.WriteLine(RegistryVersion);
                foreach (LocalRepoRegistration reg in registry.Values)
                {
                    writer.WriteLine(reg.ToJson());
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Atomic replace
            if (File.Exists(finalPath))
            {
                File.Replace(tempPath, finalPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
        }
    }
}
