# test-event-trigger.ps1
#
# Verifies that the GVFS\BootHelper scheduled task fires its event
# trigger when a new volume is mounted, without UAC.
#
# Approach:
#   1. Capture the current "last run time" of the BootHelper task.
#   2. Create + attach a small VHD, format as NTFS (this requires
#      elevation, just like creating any new volume does today - the
#      reviewer's design accepts this trade-off).
#   3. Wait up to 30s for the task's LastRunTime to advance.
#   4. Read the helper log to confirm it processed the new drive.
#   5. Clean up the VHD.
#
# MUST be run from an elevated PowerShell session (because creating a
# new volume itself requires elevation - just like the production
# scenario this tests).

#requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$log = Join-Path $here 'test-event-trigger.log'
if (Test-Path $log) { Remove-Item $log -Force }
$vhdPath = Join-Path $here 'event-trigger-test.vhdx'
if (Test-Path $vhdPath) { Remove-Item $vhdPath -Force }

$helperLog = 'C:\ProgramData\GVFS\boot-helper.log'

function L([string]$msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    Add-Content -Path $log -Value $line
    Write-Host $line
}

function Run-Diskpart([string]$script, [string]$label) {
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $tmp -Value $script -Encoding ASCII
        $output = (diskpart /s $tmp 2>&1) -join "`n"
        L "diskpart [$label]:"
        L $output
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

L "===== test-event-trigger.ps1 starting ====="
L ""

# ---- BASELINE ----
$task = Get-ScheduledTask -TaskName 'BootHelper' -TaskPath '\GVFS\' -ErrorAction SilentlyContinue
if (-not $task) {
    L "ERROR: BootHelper scheduled task is not registered."
    L "Run gvfs-setup.ps1 first."
    exit 1
}
$taskInfo0 = Get-ScheduledTaskInfo -TaskName 'BootHelper' -TaskPath '\GVFS\'
L "Baseline task state:"
L "  LastRunTime:   $($taskInfo0.LastRunTime)"
L "  LastTaskResult: 0x$('{0:X8}' -f $taskInfo0.LastTaskResult)"
L ""

# Helper log baseline
$logSizeBefore = 0
if (Test-Path $helperLog) {
    $logSizeBefore = (Get-Item $helperLog).Length
    L "Helper log baseline size: $logSizeBefore bytes"
} else {
    L "Helper log does not exist yet: $helperLog"
}
L ""

$ddDrive = $null
$vhdAttached = $false

try {
    # ---- CREATE + MOUNT VHD ----
    L "===== Creating + attaching VHD ====="
    Run-Diskpart @"
create vdisk file="$vhdPath" maximum=2048 type=expandable
select vdisk file="$vhdPath"
attach vdisk
"@ 'create+attach'
    $vhdAttached = $true
    Start-Sleep -Seconds 2

    $newDisk = Get-Disk | Where-Object { $_.BusType -eq 'File Backed Virtual' } |
               Sort-Object Number -Descending | Select-Object -First 1
    L "New VHD disk number: $($newDisk.Number)"
    Initialize-Disk -Number $newDisk.Number -PartitionStyle GPT
    $ddPart = New-Partition -DiskNumber $newDisk.Number -UseMaximumSize -AssignDriveLetter
    $ddDrive = $ddPart.DriveLetter
    L "Partition assigned $($ddDrive):"
    Format-Volume -DriveLetter $ddDrive -FileSystem NTFS -NewFileSystemLabel 'EvtTrig' -Confirm:$false -Force | Out-Null
    L "Formatted NTFS - this should trigger Microsoft-Windows-Partition event 1006"
    L ""

    # ---- WAIT FOR TASK TO FIRE ----
    L "===== Waiting up to 30s for BootHelper task to fire ====="
    $start = Get-Date
    $fired = $false
    while (((Get-Date) - $start).TotalSeconds -lt 30) {
        Start-Sleep -Seconds 2
        $taskInfo = Get-ScheduledTaskInfo -TaskName 'BootHelper' -TaskPath '\GVFS\'
        if ($taskInfo.LastRunTime -gt $taskInfo0.LastRunTime) {
            $fired = $true
            L "Task fired! LastRunTime moved from $($taskInfo0.LastRunTime) to $($taskInfo.LastRunTime)"
            L "LastTaskResult: 0x$('{0:X8}' -f $taskInfo.LastTaskResult)"
            break
        }
    }
    if (-not $fired) {
        L "TIMEOUT: task LastRunTime did not advance within 30s"
        L "Current task state:"
        $taskInfo = Get-ScheduledTaskInfo -TaskName 'BootHelper' -TaskPath '\GVFS\'
        L "  LastRunTime: $($taskInfo.LastRunTime)"
        L "  NextRunTime: $($taskInfo.NextRunTime)"
    }

    # ---- CHECK HELPER LOG ----
    L ""
    L "===== Helper log content since test started ====="
    if (Test-Path $helperLog) {
        $logSizeAfter = (Get-Item $helperLog).Length
        L "Helper log size: $logSizeBefore -> $logSizeAfter bytes (delta $($logSizeAfter - $logSizeBefore))"
        if ($logSizeAfter -gt $logSizeBefore) {
            # Read only the new content
            $stream = [System.IO.File]::OpenRead($helperLog)
            try {
                $stream.Seek($logSizeBefore, 'Begin') | Out-Null
                $reader = New-Object System.IO.StreamReader($stream)
                $newContent = $reader.ReadToEnd()
            } finally {
                $stream.Dispose()
            }
            L "New log content:"
            L $newContent
        }
    } else {
        L "Helper log still does not exist - task did not run."
    }

    # ---- DIAGNOSTIC: what events DID fire? ----
    L ""
    L "===== DIAGNOSTIC: events from candidate logs in the last 60s ====="
    $cutoff = (Get-Date).AddSeconds(-60)
    foreach ($logName in @(
        'Microsoft-Windows-Partition/Diagnostic',
        'Microsoft-Windows-Storage-Storport/Operational',
        'Microsoft-Windows-Kernel-PnP/Configuration',
        'System'
    )) {
        try {
            $events = Get-WinEvent -FilterHashtable @{LogName=$logName; StartTime=$cutoff} -MaxEvents 10 -ErrorAction Stop
            L "  $logName : $(@($events).Count) events"
            foreach ($e in $events) {
                L ("    {0}  Id={1}  Provider={2}  {3}" -f $e.TimeCreated, $e.Id, $e.ProviderName, ($e.Message -split "`n")[0])
            }
        } catch {
            L "  $logName : (no events or log unavailable: $($_.Exception.Message))"
        }
    }

    # ---- RESULT ----
    L ""
    L "===== RESULT ====="
    if ($fired) {
        L "PASS: BootHelper task fired in response to the volume-mount event."
        L "      The user-level install model is viable (no UAC needed when new"
        L "      drives appear)."
    } else {
        L "FAIL: BootHelper task did not fire within 30s of new volume mount."
        L "      Review the DIAGNOSTIC section above to pick a different event"
        L "      subscription. Update gvfs-boot-helper-task.xml and re-run setup."
    }
}
finally {
    # ---- CLEANUP ----
    L ""
    L "===== CLEANUP ====="
    if ($vhdAttached) {
        try {
            Run-Diskpart @"
select vdisk file="$vhdPath"
detach vdisk
"@ 'cleanup-detach'
        } catch {
            L "WARN: failed to detach VHD - $_"
        }
    }
    if (Test-Path $vhdPath) {
        try {
            Remove-Item $vhdPath -Force
            L "Deleted $vhdPath"
        } catch {
            L "WARN: failed to delete VHD - $_"
        }
    }
}

L ""
L "===== DONE - full log at $log ====="
