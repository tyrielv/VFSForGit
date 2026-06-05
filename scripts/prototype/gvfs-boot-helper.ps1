# gvfs-boot-helper.ps1
#
# Boot-time + volume-mount helper for the user-level GVFS install model.
#
# Lives at %ProgramFiles%\GVFS\bin\gvfs-boot-helper.ps1
# Invoked by the scheduled task "GVFS\BootHelper" on two triggers:
#   1. AT_SYSTEM_START   - re-attach prjflt to every NTFS/ReFS volume
#                          after a reboot (FilterAttach state does NOT
#                          persist across reboots).
#   2. WMI Win32_VolumeChangeEvent EventType=2 (volume mounted)
#      - attach prjflt to a newly-mounted drive (USB plug-in, new
#        partition creation, VHD mount, etc.)
#
# Runs as LocalSystem (configured by the scheduled task) so it has
# SE_LOAD_DRIVER_PRIVILEGE for FilterAttach and HKLM write access for
# the Dev Drive allowed-filters registry.
#
# Logs to %ProgramData%\GVFS\boot-helper.log (HKLM-writable from SYSTEM).
#
# Single source of truth for "what GVFS needs from the OS":
#   1. PrjFlt in the Dev Drive allowed-filters list (machine-wide, idempotent)
#   2. PrjFlt attached to every eligible NTFS/ReFS volume (idempotent)
#
# Idempotent everywhere: NameCollision is treated as success; the
# Dev Drive allow-list set is no-op if already configured. The helper
# is safe to run repeatedly (every boot + every volume mount).

[CmdletBinding()]
param(
    # If provided, only attempt to attach to this single drive letter.
    # Used by the volume-mount trigger to scope work narrowly. When
    # absent, all NTFS/ReFS volumes are processed (boot trigger path),
    # and the Dev Drive allow-list is also reconciled.
    [string]$DriveLetter
)

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:ProgramData 'GVFS'
$logPath = Join-Path $logDir 'boot-helper.log'
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

function L([string]$msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $logPath -Value $line -Encoding UTF8
}

function Ensure-PrjFltDevDriveAllowed {
    # Dev Drives consult a machine-wide allow-list at mount time to
    # decide which minifilters may attach. Without PrjFlt in the list,
    # GVFS cannot work on Dev Drives even if we call FilterAttach.
    # Set unconditionally; fsutil is a no-op if already set.
    try {
        $out = (& fsutil.exe devdrv setFiltersAllowed PrjFlt 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -eq 0) {
            L "DevDrive allow-list: PrjFlt allowed (output: $out)"
        }
        else {
            # Non-fatal: on older Windows builds without Dev Drive
            # support, fsutil devdrv may fail. Log and continue.
            L "DevDrive allow-list: fsutil exit=$LASTEXITCODE (likely no Dev Drive support on this OS) output=$out"
        }
    }
    catch {
        L "DevDrive allow-list: exception (likely no Dev Drive support): $_"
    }
}

function Attach-PrjFltToVolume([string]$drive) {
    $output = (& fltmc.exe attach PrjFlt "${drive}:" 2>&1 | Out-String).Trim()
    $exit = $LASTEXITCODE
    # NameCollision is success-equivalent: filter is already attached.
    # Check the output BEFORE the exit code because fltmc returns exit
    # 1 for NameCollision (despite it being benign).
    if ($output -match 'instance already exists' -or
        $output -match 'instance name collision' -or
        $output -match '0x801f0012') {
        L "OK   ${drive}: already attached (NameCollision)"
        return $true
    }
    if ($exit -ne 0) {
        L "FAIL ${drive}: exit=$exit output=$output"
        return $false
    }
    L "OK   ${drive}: attached (output: $output)"
    return $true
}

try {
    L "===== gvfs-boot-helper.ps1 starting (DriveLetter='$DriveLetter') ====="

    if ($DriveLetter) {
        # Single-volume mode (volume-mount trigger)
        $drive = $DriveLetter.TrimEnd(':').TrimEnd('\').ToUpperInvariant()
        if ($drive.Length -ne 1) {
            L "ERROR: invalid DriveLetter '$DriveLetter' (parsed='$drive')"
            exit 2
        }
        $vol = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue
        if (-not $vol) {
            L "SKIP ${drive}: volume not found"
            exit 0
        }
        if ($vol.FileSystemType -notin @('NTFS','ReFS')) {
            L "SKIP ${drive}: filesystem=$($vol.FileSystemType) (not NTFS/ReFS)"
            exit 0
        }
        Attach-PrjFltToVolume $drive | Out-Null
    }
    else {
        # All-volumes mode (boot trigger). Reconcile both the Dev Drive
        # allow-list AND per-volume attachments. Cheap; idempotent.
        Ensure-PrjFltDevDriveAllowed
        $volumes = Get-Volume |
            Where-Object {
                $_.DriveLetter -and
                $_.FileSystemType -in @('NTFS','ReFS')
            }
        L "Found $(@($volumes).Count) eligible volume(s)"
        foreach ($v in $volumes) {
            Attach-PrjFltToVolume ([string]$v.DriveLetter) | Out-Null
        }
    }

    L "===== gvfs-boot-helper.ps1 done ====="
}
catch {
    L "EXCEPTION: $_"
    L $_.ScriptStackTrace
    exit 3
}
