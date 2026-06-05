# gvfs-install.ps1
#
# Per-user GVFS install/upgrade for the user-level install model.
#
# Run from a non-elevated PowerShell session. The script first checks
# whether one-time admin setup is required (ProjFS feature, boot helper,
# scheduled task) and re-executes gvfs-setup.ps1 elevated only if any of
# those is missing or has drifted. After admin setup is current, the
# rest of the install runs without elevation:
#
#   1. Create %LocalAppData%\Programs\GVFS\Versions\<version>\
#   2. Copy the GVFS payload into the new version directory
#   3. Atomically swap %LocalAppData%\Programs\GVFS\Current junction to
#      point at the new version
#   4. Ensure %LocalAppData%\Programs\GVFS\Current is in the user PATH
#   5. Register a per-user logon scheduled task to run automount
#   6. GC: delete any but the 2 most recent Versions\<X> folders
#
# Idempotent: re-running with the same payload is a fast no-op.

[CmdletBinding()]
param(
    # Directory containing the GVFS binaries to install. Typically the
    # output of GVFS.Payload (i.e. out\GVFS.Payload\bin\<cfg>\win-x64\).
    # Defaults to the script's own directory (prototype mode - just
    # lays out the install/setup/helper scripts so the script-only
    # install path can be validated independently of a real build).
    [string]$PayloadDir = '',

    # Version string for this install. Drives the Versions\<X>\
    # directory name and the user-PATH stability check.
    [string]$Version = '0.0.0-prototype',

    # Force re-copy and junction re-creation even if nothing changed.
    [switch]$Force,

    # Print what would happen, change nothing.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# PS 5.1 quirk: $PSScriptRoot is empty in param-default expressions.
# Resolve in the body where it's correctly populated.
if (-not $PayloadDir) { $PayloadDir = $PSScriptRoot }
$scriptDir = $PSScriptRoot

$gvfsRoot     = Join-Path $env:LocalAppData 'Programs\GVFS'
$versionsDir  = Join-Path $gvfsRoot 'Versions'
$targetDir    = Join-Path $versionsDir $Version
$currentLink  = Join-Path $gvfsRoot 'Current'

# Per-user data root - replaces the default %ProgramData%\GVFS\ and
# %ProgramFiles%\GVFS\ProgramData\ paths that require admin to write.
# Set as user env vars below; consumed by WindowsPlatform.Shared.cs
# via Environment.GetEnvironmentVariable("GVFS_COMMON_APPDATA_ROOT")
# and "GVFS_SECURE_DATA_ROOT".
$userDataRoot = Join-Path $env:LocalAppData 'GVFS'

# Control scripts (helper + task XML + setup + this script) that need
# to be co-located with the binaries inside Versions\<X>\ so that
# upgrade re-invocations resolve the right helper/task definition.
$controlScripts = @(
    'gvfs-install.ps1',
    'gvfs-setup.ps1',
    'gvfs-boot-helper.ps1',
    'gvfs-boot-helper-task.xml'
)

$setupScript  = Join-Path $scriptDir 'gvfs-setup.ps1'
$helperScript = Join-Path $scriptDir 'gvfs-boot-helper.ps1'
$taskXml      = Join-Path $scriptDir 'gvfs-boot-helper-task.xml'

function L([string]$msg) { Write-Host "[install] $msg" }
function W([string]$msg) { Write-Warning "[install] $msg" }
function Run([string]$desc, [scriptblock]$action) {
    if ($DryRun) { L "DRYRUN: $desc" } else { L $desc; & $action }
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash $path -Algorithm SHA256).Hash
}

# Returns the reason elevation is needed, or $null if everything is
# already current. All checks must work without admin.
function Get-AdminSetupReason {
    # 1. ProjFS optional feature (proxied via prjflt service + native lib)
    $svc = Get-Service -Name prjflt -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') {
        return "prjflt service not running (ProjFS feature likely not enabled)"
    }
    if (-not (Test-Path 'C:\Windows\System32\ProjectedFSLib.dll')) {
        return "ProjectedFSLib.dll missing from System32 (ProjFS feature not installed)"
    }

    # 2. Boot helper script installed and content matches ours
    $helperDst = 'C:\Program Files\GVFS\bin\gvfs-boot-helper.ps1'
    if (-not (Test-Path $helperDst)) {
        return "boot helper script not installed at $helperDst"
    }
    if ((Get-FileSha256 $helperDst) -ne (Get-FileSha256 $helperScript)) {
        return "boot helper script content has drifted from intended"
    }

    # 3. Scheduled task registered and matches our intended template
    $task = Get-ScheduledTask -TaskName BootHelper -TaskPath '\GVFS\' -ErrorAction SilentlyContinue
    if (-not $task) {
        return "scheduled task GVFS\BootHelper not registered"
    }
    # Compute the same hash setup.ps1 embeds in the registered task's
    # Description, then look for it. This avoids any need to normalize
    # the heavy reformatting Task Scheduler does on registered XML
    # (reorders sections, drops defaults, adds UseUnifiedSchedulingEngine,
    # etc.) - we only care that the registered task came from this
    # exact source template.
    $tplBytes = [System.Text.Encoding]::UTF8.GetBytes((Get-Content $taskXml -Raw))
    $tplHash = [System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::Create().ComputeHash($tplBytes)
    ).Replace('-', '').Substring(0, 16)
    $marker = "[gvfs-task-hash=$tplHash]"
    if (-not $task.Description -or -not $task.Description.Contains($marker)) {
        return "registered scheduled task is stale (expected $marker in description)"
    }

    return $null
}

L "===== gvfs-install.ps1 starting ====="
L "Payload dir:  $PayloadDir"
L "Version:      $Version"
L "GVFS root:    $gvfsRoot"
L "DryRun:       $DryRun"
L "Force:        $Force"
L ""

# ---- VALIDATE INPUTS ----
foreach ($f in @($setupScript, $helperScript, $taskXml)) {
    if (-not (Test-Path $f)) {
        throw "Required source file not found: $f"
    }
}

# ---- STEP 1: ADMIN SETUP CHECK ----
L "STEP 1: Check whether admin setup is current"
$reason = Get-AdminSetupReason
if ($reason) {
    L "  Admin setup needed: $reason"
    if ($DryRun) {
        L "  DRYRUN: would re-execute $setupScript elevated."
    } else {
        $setupLog = Join-Path $env:TEMP 'gvfs-setup.log'
        # Pre-clear so we don't read stale output if Start-Process fails before setup runs
        if (Test-Path $setupLog) { Remove-Item $setupLog -Force }
        L "  Re-executing $setupScript elevated (you should see one UAC prompt)..."
        # Always pass -Force when re-elevating, since the only reason
        # we're here is that something is missing or has drifted; we
        # want setup to refresh everything authoritative rather than
        # short-circuit on "already registered".
        $proc = Start-Process -FilePath powershell.exe `
            -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$setupScript,'-Force' `
            -Verb RunAs -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            L ""
            L "  gvfs-setup.ps1 exited with code $($proc.ExitCode)."
            if (Test-Path $setupLog) {
                L "  --- setup transcript ($setupLog) ---"
                Get-Content $setupLog | ForEach-Object { L "    $_" }
                L "  --- end transcript ---"
            } else {
                L "  No setup transcript found at $setupLog (setup may not have started)."
            }
            throw "gvfs-setup.ps1 failed; see transcript above for details."
        }
        L "  Admin setup completed."
        # Re-check; if still failing, something went wrong
        $reason2 = Get-AdminSetupReason
        if ($reason2) {
            throw "Admin setup did not resolve all gaps. Remaining reason: $reason2"
        }
    }
} else {
    L "  Admin setup is current; no UAC prompt needed."
}
L ""

# ---- STEP 2: STAGE NEW VERSION DIRECTORY ----
L "STEP 2: Stage payload into $targetDir"

# Detect payload type: real GVFS payload contains gvfs.exe.
$isRealPayload = Test-Path (Join-Path $PayloadDir 'GVFS.exe')
L "  Payload type: $(if ($isRealPayload) {'real GVFS payload (contains GVFS.exe)'} else {'prototype scripts (no GVFS.exe found)'})"

if ((Test-Path $targetDir) -and -not $Force) {
    L "  Version directory already exists; will re-copy on top of it."
} else {
    Run "  Creating $targetDir" { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
}

Run "  Copying payload (recursive)" {
    # robocopy mirror-ish: copy tree, retry briefly on locks, suppress
    # noise. /XJ skips junctions (we don't want to follow nested ones).
    # robocopy exit codes 0-7 are success; 8+ is real failure.
    $rc = (Start-Process robocopy.exe -ArgumentList "`"$PayloadDir`"","`"$targetDir`"",'/E','/MT:8','/R:2','/W:5','/XJ','/NFL','/NDL','/NJH','/NJS','/NC','/NS','/NP' -Wait -PassThru -NoNewWindow).ExitCode
    if ($rc -ge 8) {
        throw "robocopy failed with exit code $rc"
    }
}

# Always ensure the control scripts are present alongside the binaries
# in the staged version so subsequent `gvfs-install.ps1` invocations
# from that directory can find their helper / task / setup siblings.
# In prototype mode (PayloadDir == $scriptDir) these were already
# copied above; in real-payload mode they need to be added.
if ($isRealPayload -or $PayloadDir -ne $scriptDir) {
    Run "  Copying control scripts ($($controlScripts -join ', '))" {
        foreach ($name in $controlScripts) {
            $src = Join-Path $scriptDir $name
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $targetDir $name) -Force
            }
        }
    }
}
L ""

# ---- STEP 3: ATOMIC JUNCTION SWAP ----
L "STEP 3: Update Current junction"
$linkInfo = Get-Item $currentLink -ErrorAction SilentlyContinue
$linkTarget = $null
if ($linkInfo) {
    $linkTarget = $linkInfo.Target | Select-Object -First 1
    L "  Current junction exists, target = $linkTarget"
}
if ($linkTarget -ne $targetDir) {
    if ($linkInfo) {
        Run "  Removing existing Current junction" {
            # Remove-Item on a junction removes the link, not the target
            cmd /c rmdir "`"$currentLink`"" | Out-Null
        }
    }
    Run "  Creating junction Current -> $targetDir" {
        cmd /c mklink /J "`"$currentLink`"" "`"$targetDir`"" | Out-Null
    }
} else {
    L "  Current already points at the target version, skipping."
}
L ""

# ---- STEP 4: USER PATH ----
L "STEP 4: Ensure $currentLink is in user PATH"
$userPath = [Environment]::GetEnvironmentVariable('PATH','User')
$pathEntries = @()
if ($userPath) { $pathEntries = $userPath -split ';' | Where-Object { $_ } }
if ($pathEntries -contains $currentLink) {
    L "  Already in user PATH; skipping."
} else {
    Run "  Adding to user PATH" {
        $newPath = if ($pathEntries) { (@($currentLink) + $pathEntries) -join ';' } else { $currentLink }
        [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    }
    L "  Note: new shells will pick up the PATH change; current shell will not."
}
L ""

# ---- STEP 4B: USER DATA-ROOT ENV VARS ----
# Redirect the two GVFS data roots into a per-user directory so that
# (a) the user can write to them without elevation and (b) per-user
# state (repo registry, logs, cache tokens) lives under the user's
# profile rather than admin-only %ProgramData% / %ProgramFiles%.
L "STEP 4B: User data-root env vars"
$envVarMap = @{
    'GVFS_COMMON_APPDATA_ROOT' = $userDataRoot
    'GVFS_SECURE_DATA_ROOT'    = $userDataRoot
}
foreach ($kv in $envVarMap.GetEnumerator()) {
    $cur = [Environment]::GetEnvironmentVariable($kv.Key, 'User')
    if ($cur -eq $kv.Value) {
        L "  $($kv.Key): already set to $($kv.Value); skipping."
    } else {
        Run "  Setting $($kv.Key) = $($kv.Value) (was: $cur)" {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, 'User')
        }
    }
}
if (-not $DryRun -and -not (Test-Path $userDataRoot)) {
    Run "  Creating $userDataRoot" { New-Item -ItemType Directory -Path $userDataRoot -Force | Out-Null }
}
L ""

# ---- STEP 5: USER LOGON AUTOMOUNT TASK ----
L "STEP 5: Per-user logon automount task"
$automountTaskName = 'AutoMount'
$automountTaskPath = '\GVFS\'
if (-not $isRealPayload) {
    L "  SKIPPED (prototype scripts payload - no gvfs.exe to invoke)."
} else {
    $gvfsExe = Join-Path $currentLink 'GVFS.exe'
    # Register a per-user logon task that runs `gvfs service --mount-all`
    # at logon. Per-user means no admin needed to register / unregister.
    # Points at Current\GVFS.exe rather than the versioned path so
    # version upgrades (via Current junction swap) take effect on next
    # logon automatically.
    $existing = Get-ScheduledTask -TaskName $automountTaskName -TaskPath $automountTaskPath -ErrorAction SilentlyContinue
    if ($existing -and -not $Force) {
        L "  Per-user task '$automountTaskPath$automountTaskName' already exists; skipping."
    } else {
        if ($existing) {
            Run "  Removing existing automount task" {
                Unregister-ScheduledTask -TaskName $automountTaskName -TaskPath $automountTaskPath -Confirm:$false
            }
        }
        Run "  Registering automount task (logon trigger, runs as user)" {
            $action = New-ScheduledTaskAction -Execute $gvfsExe -Argument 'service --mount-all'
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:UserName
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
            $principal = New-ScheduledTaskPrincipal -UserId $env:UserName -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask `
                -TaskName $automountTaskName `
                -TaskPath $automountTaskPath `
                -Action $action `
                -Trigger $trigger `
                -Settings $settings `
                -Principal $principal `
                -Description "Mount all registered GVFS repos for $env:UserName at logon." | Out-Null
        }
    }
}
L ""

# ---- STEP 6: GC OLD VERSIONS ----
L "STEP 6: GC old version directories (keep most recent 2)"
$allVersions = Get-ChildItem $versionsDir -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending
$keepCount = 2
if ($allVersions.Count -le $keepCount) {
    L "  Only $($allVersions.Count) version(s) present; nothing to GC."
} else {
    $toDelete = $allVersions | Select-Object -Skip $keepCount
    $current = (Get-Item $currentLink -ErrorAction SilentlyContinue).Target | Select-Object -First 1
    foreach ($v in $toDelete) {
        if ($v.FullName -eq $current) {
            L "  SKIP $($v.Name) - currently linked"
            continue
        }
        # TODO: also skip if any running process has a binary open from this dir
        # For prototype, simple time-based GC is sufficient
        Run "  Deleting $($v.FullName)" { Remove-Item $v.FullName -Recurse -Force }
    }
}
L ""

L "===== gvfs-install.ps1 done ====="
L ""
L "Verify:"
L "  Test-Path $currentLink     # should be True"
L "  (Get-Item $currentLink).Target  # should point at version $Version"
L "  Open a NEW shell, run: `$env:PATH -split ';' | Where { `$_ -match 'GVFS' }"
