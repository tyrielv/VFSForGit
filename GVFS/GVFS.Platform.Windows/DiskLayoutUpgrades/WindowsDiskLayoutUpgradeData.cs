using GVFS.Common;
using GVFS.DiskLayoutUpgrades;

namespace GVFS.Platform.Windows.DiskLayoutUpgrades
{
    public class WindowsDiskLayoutUpgradeData : IDiskLayoutUpgradeData
    {
        public DiskLayoutUpgrade[] Upgrades
        {
            get
            {
                return new DiskLayoutUpgrade[]
                {
                    new DiskLayout10to11Upgrade_NewOperationType(),
                    new DiskLayout11to12Upgrade_SharedLocalCache(),
                    new DiskLayout12_0To12_1Upgrade_StatusAheadBehind(),
                    new DiskLayout12to13Upgrade_FolderPlaceholder(),
                    new DiskLayout14to15Upgrade_ModifiedPaths(),
                    new DiskLayout15to16Upgrade_GitStatusCache(),
                    new DiskLayout16to17Upgrade_FolderPlaceholderValues(),
                    new DiskLayout17to18Upgrade_TombstoneFolderPlaceholders(),
                    new DiskLayout18to19Upgrade_SqlitePlacholders(),
                };
            }
        }

        public DiskLayoutVersion Version => new DiskLayoutVersion(
                    currentMajorVersion: 19,
                    currentMinorVersion: 0,
                    minimumSupportedMajorVersion: 15);

        public bool TryParseLegacyDiskLayoutVersion(string dotGVFSPath, out int majorVersion)
        {
            majorVersion = 0;
            return false;
        }
    }
}
