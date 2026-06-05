# gvfs-setup.ps1
#
# One-time admin setup for the user-level GVFS install model.
#
# Run ONCE per machine, from an elevated PowerShell session.
# After this completes, all subsequent GVFS installs, upgrades, mounts,
# and reboots run without elevation.
#
# Actions performed:
#   1. Enable the Client-ProjFS Windows optional feature (idempotent)
#   2. Install %ProgramFiles%\GVFS\bin\gvfs-boot-helper.ps1
#      (only replaced if the on-disk content differs from this install)
#   3. Register the scheduled task GVFS\BootHelper (idempotent)
#   4. Run the helper once to set the Dev Drive allow-list and attach
#      PrjFlt to every currently-mounted NTFS/ReFS volume.
#
# Idempotent: safe to re-run. If everything is already configured, the
# script is a no-op and reports each check.

#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [switch]$Force,         # Re-register the task and re-copy the helper even if unchanged
    [switch]$DryRun         # Print what would happen, change nothing
)

$ErrorActionPreference = 'Stop'

# PS 5.1 quirk: $PSScriptRoot is empty in param-default expressions.
# Resolve in the body where it's correctly populated.
if (-not $SourceDir) { $SourceDir = $PSScriptRoot }

# Always write a transcript so install.ps1 (which launched us elevated
# via ShellExecute) can read the output on failure - the elevated
# process gets a fresh console and stdout/stderr don't flow back.
$transcriptPath = Join-Path $env:TEMP 'gvfs-setup.log'
try { Stop-Transcript | Out-Null } catch { }
Start-Transcript -Path $transcriptPath -Force | Out-Null

$installDir   = Join-Path $env:ProgramFiles 'GVFS\bin'
$helperSrc    = Join-Path $SourceDir 'gvfs-boot-helper.ps1'
$helperDst    = Join-Path $installDir 'gvfs-boot-helper.ps1'
$taskXmlSrc   = Join-Path $SourceDir 'gvfs-boot-helper-task.xml'
$taskName     = 'GVFS\BootHelper'

function L([string]$msg) { Write-Host "[setup] $msg" }
function W([string]$msg) { Write-Warning "[setup] $msg" }
function Run([string]$desc, [scriptblock]$action) {
    if ($DryRun) {
        L "DRYRUN: $desc"
    } else {
        L $desc
        & $action
    }
}

try {
    L "===== gvfs-setup.ps1 starting ====="
    L "Transcript: $transcriptPath"
    L "Source dir:  $SourceDir"
    L "Install dir: $installDir"
    L "DryRun:      $DryRun"
    L "Force:       $Force"
    L ""

# ---- VALIDATE INPUTS ----
foreach ($f in @($helperSrc, $taskXmlSrc)) {
    if (-not (Test-Path $f)) {
        throw "Required source file not found: $f"
    }
}

# ---- STEP 1: ENABLE PROJFS OPTIONAL FEATURE ----
L "STEP 1: Client-ProjFS optional feature"
$feature = Get-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -ErrorAction SilentlyContinue
if (-not $feature) {
    W "  Get-WindowsOptionalFeature returned nothing for Client-ProjFS. Older OS or DISM module missing."
} elseif ($feature.State -eq 'Enabled') {
    L "  Already enabled."
} else {
    Run "  Enabling Client-ProjFS (may take a moment)..." {
        Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart | Out-Null
    }
    L "  Enabled (no restart required if the driver was already present)."
}
L ""

# ---- STEP 2: INSTALL THE BOOT HELPER ----
L "STEP 2: Boot helper script"
if (-not (Test-Path $installDir)) {
    Run "  Creating $installDir" { New-Item -ItemType Directory -Path $installDir -Force | Out-Null }
}

# Hash compare so we only touch the file when its contents differ.
# Lets repeat-installs detect "nothing to do" cleanly.
$needsCopy = $true
if ((Test-Path $helperDst) -and -not $Force) {
    $srcHash = (Get-FileHash $helperSrc -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash $helperDst -Algorithm SHA256).Hash
    if ($srcHash -eq $dstHash) {
        L "  Helper unchanged (hash match), skipping copy."
        $needsCopy = $false
    } else {
        L "  Helper differs from on-disk, will replace."
    }
}
if ($needsCopy) {
    Run "  Copying $helperSrc -> $helperDst" { Copy-Item $helperSrc $helperDst -Force }
}
L ""

# ---- STEP 3: REGISTER SCHEDULED TASK ----
L "STEP 3: Scheduled task '$taskName'"
$existing = Get-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\' -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    L "  Task already registered. Use -Force to re-register."
} else {
    if ($existing) {
        Run "  Removing existing task..." { Unregister-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\' -Confirm:$false }
    }
    Run "  Registering task from $taskXmlSrc" {
        $xml = Get-Content $taskXmlSrc -Raw

        # Embed a hash of the SOURCE template (with the placeholder
        # still in place) in the Description so the non-admin install
        # check can detect when the registered task is stale relative
        # to the file on disk. Hashing the pre-substitution template
        # keeps the embedded hash stable across re-substitution.
        $tplBytes = [System.Text.Encoding]::UTF8.GetBytes($xml)
        $tplHash = [System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::Create().ComputeHash($tplBytes)
        ).Replace('-', '').Substring(0, 16)
        $xml = $xml.Replace('__TASK_HASH__', $tplHash)

        Register-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\' -Xml $xml | Out-Null
    }
    # Grant Authenticated Users read + execute rights on the task so
    # non-admin users can: (a) enumerate it and export its XML for the
    # install-time drift check, and (b) fire it on demand via
    # Start-ScheduledTask (useful as a manual fallback when the event
    # trigger doesn't fire, or right after creating a new drive).
    # Without this, the task's default DACL only allows SYSTEM and
    # Administrators access, and a non-admin shell can do neither.
    # SDDL:
    #   GA (GenericAll)             for SYSTEM and Administrators
    #   GRGX (GenericRead+Execute)  for Authenticated Users
    Run "  Setting task DACL to allow Authenticated Users read+execute" {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $folder = $svc.GetFolder('\GVFS')
        $task = $folder.GetTask('BootHelper')
        $task.SetSecurityDescriptor('D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;AU)', 0)
    }
    L "  Registered."
}
L ""

# ---- STEP 4: RUN HELPER ONCE TO ATTACH CURRENT VOLUMES ----
L "STEP 4: Initial attach to current volumes"
if ($DryRun) {
    L "  DRYRUN: would run scheduled task '$taskName' on demand."
} else {
    # Run the task on demand (rather than invoking the script directly)
    # so this exercises the same code path that runs at boot time, with
    # the same SYSTEM context.
    Start-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\'
    # Wait briefly for the task to finish
    $timeout = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $info = Get-ScheduledTaskInfo -TaskName 'BootHelper' -TaskPath '\GVFS\'
        $state = (Get-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\').State
    } while ($state -eq 'Running' -and (Get-Date) -lt $timeout)
    L "  Task last run: $($info.LastRunTime); last result: 0x$('{0:X8}' -f $info.LastTaskResult)"

    $logPath = Join-Path $env:ProgramData 'GVFS\boot-helper.log'
    if (Test-Path $logPath) {
        L "  Recent helper log entries:"
        Get-Content $logPath -Tail 20 | ForEach-Object { L "    $_" }
    }
}
L ""

L "===== gvfs-setup.ps1 done ====="
L ""
L "Next step: from a NON-elevated PowerShell, run gvfs-install.ps1 to"
L "drop the user-level GVFS binaries into %LocalAppData%\Programs\GVFS\."
}
catch {
    L ""
    L "===== gvfs-setup.ps1 FAILED ====="
    L "Exception: $_"
    L $_.ScriptStackTrace
    Stop-Transcript | Out-Null
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
