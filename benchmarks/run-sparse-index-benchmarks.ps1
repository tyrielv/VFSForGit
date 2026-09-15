[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [Parameter(Mandatory = $true)]
    [string]$GitPath,

    [Parameter(Mandatory = $true)]
    [string]$RepoLabel,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $true)]
    [string]$ScratchRoot,

    [Parameter(Mandatory = $true)]
    [switch]$ConfirmExperimentRepo,

    [ValidateSet('full', 'sparse')]
    [string]$IndexMode = 'full',

    [ValidateRange(3, 20)]
    [int]$Runs = 5,

    [ValidateRange(0, 5)]
    [int]$Warmups = 1,

    [ValidateRange(10, 600)]
    [int]$StatusCacheTimeoutSeconds = 180,

    [ValidateRange(0, 100000000)]
    [long]$ExpectedFullIndexEntries = 0,

    [string]$TrackedPath = '.gitattributes',

    [string]$OutsideConePath = '',

    [string[]]$Operations = @(
        'status-cache', 'status-no-cache', 'merge', 'rebase', 'cherry-pick',
        'add', 'commit', 'reset', 'checkout-branch', 'checkout-noop',
        'checkout-path', 'switch', 'stash', 'diff', 'log', 'blame', 'clean'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Operations = @(
    $Operations |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ }
)

$supportedOperations = @(
    'status-cache', 'status-no-cache', 'merge', 'rebase', 'cherry-pick',
    'add', 'commit', 'reset', 'checkout-branch', 'checkout-noop',
    'checkout-path', 'switch', 'stash', 'diff', 'log', 'blame', 'clean'
)

foreach ($operation in $Operations) {
    if ($operation -notin $supportedOperations) {
        throw "Unsupported operation: $operation"
    }
}

if (-not $ConfirmExperimentRepo) {
    throw 'Confirm that this is an experiment repository with -ConfirmExperimentRepo.'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw "Repository path does not exist: $Repo"
}
if (-not (Test-Path -LiteralPath $GitPath -PathType Leaf)) {
    throw "Git executable does not exist: $GitPath"
}

$resolvedRepo = (Resolve-Path -LiteralPath $Repo).Path.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $OutputRoot, $ScratchRoot | Out-Null
$resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path.TrimEnd('\')
if ($resolvedOutputRoot.Equals($resolvedRepo, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedOutputRoot.StartsWith($resolvedRepo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the experiment repository because trace2 can contain private configuration.'
}

$gitVersion = (& $GitPath --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not run Git: $GitPath"
}

$gitLabel = $gitVersion -replace '^git version ', '' -replace '[^A-Za-z0-9.-]', '_'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $resolvedOutputRoot ("{0}-{1}-{2}-{3}" -f $timestamp, $RepoLabel, $IndexMode, $gitLabel)
$resultsPath = Join-Path $runRoot 'results.csv'
$summaryPath = Join-Path $runRoot 'summary.csv'
$metadataPath = Join-Path $runRoot 'run-metadata.json'
$indexPath = Join-Path $Repo '.git\index'
$indexLockPath = Join-Path $Repo '.git\index.lock'
$indexBackupPath = Join-Path $ScratchRoot "w2-index-$runId"
$projectionPath = Join-Path (Split-Path -Parent $Repo) '.gvfs\GVFS_projection'
$branchA = "w2-benchmark-$runId-a"
$branchB = "w2-benchmark-$runId-b"
$noopBranchA = "w2-benchmark-$runId-noop-a"
$noopBranchB = "w2-benchmark-$runId-noop-b"
$setupInvocation = 0
$statusCacheProbe = 0
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$stashBefore = ''
$stashCreated = $false

New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function Add-SafeGitOptions([string[]]$Arguments) {
    return @('-c', 'gc.auto=0', '-c', 'maintenance.auto=false') + $Arguments
}

function Get-FileLength([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            if (Test-Path -LiteralPath $Path -PathType Leaf) {
                return (Get-Item -LiteralPath $Path).Length
            }
            return 0
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
    while ($true)
}

function Get-IndexEntryCount {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        [byte[]]$header = [byte[]]::new(12)
        try {
            $stream = [System.IO.File]::Open($indexPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            try {
                if ($stream.Read($header, 0, $header.Length) -ne $header.Length) {
                    throw 'The index header is incomplete.'
                }
            }
            finally {
                $stream.Dispose()
            }
            break
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
    while ($true)

    if ([Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'DIRC') {
        throw 'The index signature is invalid.'
    }

    return [long](
        ([uint32]$header[8] -shl 24) -bor
        ([uint32]$header[9] -shl 16) -bor
        ([uint32]$header[10] -shl 8) -bor
        [uint32]$header[11])
}

function Get-GitOutput([string[]]$Arguments) {
    $argumentsWithSafety = Add-SafeGitOptions -Arguments $Arguments
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $GitPath @argumentsWithSafety 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    if ($exitCode -ne 0) {
        throw "Git failed with exit code $exitCode`: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return @($output)
}

function Get-GitRef([string]$Ref) {
    $arguments = Add-SafeGitOptions -Arguments @('-C', $Repo, 'rev-parse', '-q', '--verify', $Ref)
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $value = & $GitPath @arguments 2> $null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    if ($exitCode -eq 1) {
        return ''
    }
    if ($exitCode -ne 0) {
        throw "Could not resolve ref: $Ref"
    }

    return ($value | Select-Object -Last 1).ToString().Trim()
}

function Get-OptionalBoolConfig([string]$Name) {
    $arguments = Add-SafeGitOptions -Arguments @('-C', $Repo, 'config', '--type=bool', '--get', $Name)
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $value = & $GitPath @arguments 2> $null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    if ($exitCode -eq 1) {
        return ''
    }
    if ($exitCode -ne 0) {
        throw "Could not read Git config: $Name"
    }

    return ($value | Select-Object -Last 1).ToString().Trim()
}

function Invoke-Git(
    [string[]]$Arguments,
    [string]$TracePath = '',
    [string]$OutputLabel = 'setup'
) {
    $argumentsWithSafety = Add-SafeGitOptions -Arguments $Arguments
    if ([string]::IsNullOrWhiteSpace($TracePath)) {
        $script:setupInvocation++
        $outputBase = "setup-{0:D4}-{1}" -f $script:setupInvocation, $OutputLabel
    }
    else {
        $outputBase = [System.IO.Path]::GetFileNameWithoutExtension($TracePath)
    }

    $stdoutPath = Join-Path $runRoot "$outputBase.stdout.txt"
    $stderrPath = Join-Path $runRoot "$outputBase.stderr.txt"
    $oldPreference = $ErrorActionPreference
    $oldTrace = [Environment]::GetEnvironmentVariable('GIT_TRACE2_EVENT')

    if ([string]::IsNullOrWhiteSpace($TracePath)) {
        Remove-Item Env:GIT_TRACE2_EVENT -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_TRACE2_EVENT = $TracePath
    }

    $ErrorActionPreference = 'Continue'
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $GitPath @argumentsWithSafety 1> $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $stopwatch.Stop()
        $ErrorActionPreference = $oldPreference
        if ($null -eq $oldTrace) {
            Remove-Item Env:GIT_TRACE2_EVENT -ErrorAction SilentlyContinue
        }
        else {
            $env:GIT_TRACE2_EVENT = $oldTrace
        }
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        WallMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
    }
}

function Assert-GitSuccess([pscustomobject]$Result, [string]$Context) {
    if ($Result.ExitCode -eq 0) {
        return
    }

    $stderr = Get-Content -LiteralPath $Result.StderrPath -Raw -ErrorAction SilentlyContinue
    throw "$Context failed with exit code $($Result.ExitCode).`n$stderr"
}

function Invoke-SetupGit([string[]]$Arguments, [string]$Label) {
    $result = Invoke-Git -Arguments $Arguments -OutputLabel $Label
    Assert-GitSuccess -Result $result -Context "git $($Arguments -join ' ')"
}

function Get-JsonProperty([object]$Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-TraceTarget([object]$Event) {
    $category = Get-JsonProperty -Object $Event -Name 'category'
    $label = Get-JsonProperty -Object $Event -Name 'label'

    if ($label -eq 'prime_cache_tree') {
        return 'prime_cache_tree'
    }
    if ($category -eq 'cache_tree' -and $label -eq 'fully_valid') {
        return 'cache_tree_fully_valid'
    }
    if ($category -eq 'unpack_trees' -and $label -eq 'unpack_trees') {
        return 'unpack_trees'
    }
    if ($label -eq 'convert_to_sparse') {
        return 'convert_to_sparse'
    }
    if ($label -eq 'ensure_full_index') {
        return 'ensure_full_index'
    }
    if ($label -eq 'expand_index') {
        return 'expand_index'
    }
    if ($label -eq 'do_read_index') {
        return 'do_read_index'
    }
    if ($label -eq 'do_write_index') {
        return 'do_write_index'
    }
    if ($category -eq 'vfs' -and $label -eq 'apply') {
        return 'vfs_apply'
    }

    return $null
}

function New-RegionTotals {
    return @{
        prime_cache_tree = 0.0
        cache_tree_fully_valid = 0.0
        unpack_trees = 0.0
        convert_to_sparse = 0.0
        ensure_full_index = 0.0
        expand_index = 0.0
        do_read_index = 0.0
        do_write_index = 0.0
        vfs_apply = 0.0
    }
}

function Get-TraceSummary([string]$TracePath) {
    $allTotals = New-RegionTotals
    $topTotals = New-RegionTotals
    $activeDepth = @{}
    $expansionReasons = [System.Collections.Generic.List[string]]::new()
    $topReadCounts = [System.Collections.Generic.List[long]]::new()
    $parseErrors = 0
    $topSid = ''
    $statusCacheResult = ''
    $statusCacheRejectReason = ''
    $expansionCount = 0

    foreach ($line in Get-Content -LiteralPath $TracePath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json
        }
        catch {
            $parseErrors++
            continue
        }

        $eventName = Get-JsonProperty -Object $event -Name 'event'
        $sid = [string](Get-JsonProperty -Object $event -Name 'sid')
        if ([string]::IsNullOrWhiteSpace($topSid) -and $eventName -in @('version', 'start')) {
            $topSid = $sid
        }

        $target = Get-TraceTarget -Event $event
        if ($null -ne $target -and $eventName -in @('region_enter', 'region_leave')) {
            $thread = [string](Get-JsonProperty -Object $event -Name 'thread')
            $depthKey = "$sid|$thread|$target"
            $depth = if ($activeDepth.ContainsKey($depthKey)) { [int]$activeDepth[$depthKey] } else { 0 }
            if ($eventName -eq 'region_enter') {
                $activeDepth[$depthKey] = $depth + 1
                if ($target -in @('ensure_full_index', 'expand_index')) {
                    $expansionCount++
                }
            }
            else {
                $duration = Get-JsonProperty -Object $event -Name 't_rel'
                if ($depth -eq 1 -and $null -ne $duration) {
                    $allTotals[$target] += [double]$duration
                    if ($sid -eq $topSid) {
                        $topTotals[$target] += [double]$duration
                    }
                }
                $activeDepth[$depthKey] = [math]::Max(0, $depth - 1)
            }
        }

        if ($eventName -ne 'data') {
            continue
        }

        $key = Get-JsonProperty -Object $event -Name 'key'
        $value = Get-JsonProperty -Object $event -Name 'value'
        if ($key -eq 'expansion-reason' -and $null -ne $value) {
            $expansionReasons.Add([string]$value)
        }
        elseif ($sid -eq $topSid -and $key -eq 'read/cache_nr' -and $null -ne $value) {
            $topReadCounts.Add([long]$value)
        }
        elseif ($sid -eq $topSid -and $key -eq 'deserialize/result' -and $null -ne $value) {
            $statusCacheResult = [string]$value
        }
        elseif ($sid -eq $topSid -and $key -eq 'deserialize/reject' -and $null -ne $value) {
            $statusCacheResult = 'reject'
            $statusCacheRejectReason = [string]$value
        }
    }

    $topFirstRead = 0
    $topMinRead = 0
    $topMaxRead = 0
    if ($topReadCounts.Count -gt 0) {
        $topFirstRead = $topReadCounts[0]
        $topMinRead = ($topReadCounts | Measure-Object -Minimum).Minimum
        $topMaxRead = ($topReadCounts | Measure-Object -Maximum).Maximum
    }

    return [pscustomobject]@{
        Expanded = ($expansionCount -gt 0 -or $expansionReasons.Count -gt 0)
        ExpansionCount = $expansionCount
        ExpansionReasons = $expansionReasons -join ';'
        StatusCacheResult = $statusCacheResult
        StatusCacheRejectReason = $statusCacheRejectReason
        ParseErrors = $parseErrors
        TopFirstReadEntries = $topFirstRead
        TopMinReadEntries = $topMinRead
        TopMaxReadEntries = $topMaxRead
        AllTotals = $allTotals
        TopTotals = $topTotals
    }
}

function Get-SparseDirectoryCount([switch]$Force) {
    if ($IndexMode -eq 'full' -and -not $Force) {
        return 0
    }

    $arguments = Add-SafeGitOptions -Arguments @('-C', $Repo, 'ls-files', '--sparse', '-s')
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $count = (& $GitPath @arguments 2> $null | Where-Object { $_ -like '040000 *' } | Measure-Object -Line).Lines
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    if ($exitCode -ne 0) {
        throw 'Could not count sparse-directory entries.'
    }

    return [long]$count
}

function Invoke-GitMktree([string]$TreeInput) {
    # Build a tree object from newline-delimited 'ls-tree' style entries. Feed the
    # bytes over stdin directly (a PowerShell pipeline would append CR to each
    # line and corrupt the entry names). git mktree normalizes entry order, so
    # callers do not have to pre-sort.
    $arguments = Add-SafeGitOptions -Arguments @('-C', $Repo, 'mktree')
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $GitPath
    foreach ($token in $arguments) { [void]$psi.ArgumentList.Add($token) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($TreeInput)
    $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $process.StandardInput.BaseStream.Flush()
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "git mktree failed with exit code $($process.ExitCode): $stderr"
    }
    return $stdout.Trim()
}

function Get-TreeEntries([string]$TreeIsh) {
    # 'ls-tree' lines for a tree-ish, or an empty array when the tree does not
    # exist yet (a fixture directory that is absent from the parent commit).
    if ([string]::IsNullOrEmpty($TreeIsh)) {
        return @()
    }
    return @(Get-GitOutput -Arguments @('-C', $Repo, 'ls-tree', $TreeIsh))
}

function Get-TreeEntryName([string]$Line) {
    $tab = $Line.IndexOf("`t")
    if ($tab -lt 0) { return $Line }
    return $Line.Substring($tab + 1)
}

function Set-FixtureTreeEntry([string]$TreeIsh, [string[]]$Segments, [string]$Blob) {
    # Recursively produce a new tree equal to $TreeIsh with $Blob installed at the
    # nested path $Segments. Only the directories along $Segments are rebuilt
    # (each an O(entries-in-that-directory) mktree), so a fixture never rewrites
    # the millions of unrelated entries a full-index write-tree would touch, and
    # it stays fully offline (no promisor fetch of missing objects).
    $entries = @(Get-TreeEntries -TreeIsh $TreeIsh)
    $name = $Segments[0]

    if ($Segments.Count -eq 1) {
        $newLine = "100644 blob $Blob`t$name"
    }
    else {
        $childTree = ''
        foreach ($line in $entries) {
            if ((Get-TreeEntryName -Line $line) -eq $name -and $line -match '^\d+ tree ([0-9a-f]+)\t') {
                $childTree = $matches[1]
                break
            }
        }
        $newChild = Set-FixtureTreeEntry -TreeIsh $childTree -Segments $Segments[1..($Segments.Count - 1)] -Blob $Blob
        $newLine = "040000 tree $newChild`t$name"
    }

    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $entries) {
        if ((Get-TreeEntryName -Line $line) -ne $name) { $result.Add($line) }
    }
    $result.Add($newLine)
    return Invoke-GitMktree -TreeInput (($result -join "`n") + "`n")
}

function New-FixtureCommit([string]$Parent, [string]$Path, [string]$Content, [string]$Message) {
    # Construct the fixture commit by editing only the tree path that changes.
    # The original implementation round-tripped the whole index (read-tree +
    # write-tree). On a multi-million-entry index that rebuilds the entire
    # cache-tree, and in a partial clone it triggers a promisor fetch. Editing
    # the tree directly is O(path-depth x directory-width), stays fully offline,
    # and produces an identical fixture commit (same tree, same content).
    $fixtureFile = Join-Path $ScratchRoot "w2-fixture-$([Guid]::NewGuid().ToString('N')).txt"
    try {
        Set-Content -LiteralPath $fixtureFile -Value $Content -Encoding ascii
        $blob = (Get-GitOutput -Arguments @('-C', $Repo, 'hash-object', '-w', $fixtureFile) | Select-Object -Last 1).ToString().Trim()
        $segments = $Path -split '/'
        $tree = Set-FixtureTreeEntry -TreeIsh $Parent -Segments $segments -Blob $blob
        return (Get-GitOutput -Arguments @(
            '-C', $Repo,
            '-c', 'user.name=Sparse Index Benchmark',
            '-c', 'user.email=benchmark@example.invalid',
            'commit-tree', $tree, '-p', $Parent, '-m', $Message
        ) | Select-Object -Last 1).ToString().Trim()
    }
    finally {
        Remove-Item -LiteralPath $fixtureFile -Force -ErrorAction SilentlyContinue
    }
}

function Restore-Index {
    if (Test-Path -LiteralPath $indexLockPath) {
        throw "Cannot restore the index while an index lock exists: $indexLockPath"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            [System.IO.File]::Copy($indexBackupPath, $indexPath, $true)
            return
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
    while ($true)
}

function Restore-Base {
    Invoke-SetupGit -Arguments @('-C', $Repo, 'switch', '--detach', '--quiet', $baseCommit) -Label 'restore-base'
}

function Add-TrackedFileChange([int]$Iteration) {
    Add-Content -LiteralPath (Join-Path $Repo ($benchmarkTrackedPath -replace '/', '\')) -Value "`nSparse index benchmark $runId $Iteration" -Encoding ascii
}

function Restore-TrackedFixturePath {
    # In sparse mode the tracked benchmark path lives inside a collapsed
    # sparse-directory, so it is not an individual index entry: only the parent
    # directory appears as a sparse-directory entry. 'git checkout -- <path>'
    # therefore cannot match it ("did not match any file(s) known to git") and
    # 'git add' would force a full-index expansion. Restore the worktree content
    # directly from the base-commit blob, which needs no pathspec match and no
    # index expansion. In full mode keep the original checkout behaviour.
    if ($IndexMode -ne 'sparse') {
        Invoke-SetupGit -Arguments @('-C', $Repo, 'checkout', '--quiet', '--', $benchmarkTrackedPath) -Label 'cleanup-tracked-path'
        return
    }

    $destination = Join-Path $Repo ($benchmarkTrackedPath -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    $blob = (Get-GitOutput -Arguments @('-C', $Repo, 'rev-parse', "$baseCommit`:$benchmarkTrackedPath") | Select-Object -Last 1).ToString().Trim()
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $GitPath
    foreach ($token in (Add-SafeGitOptions -Arguments @('-C', $Repo, 'cat-file', 'blob', $blob))) {
        [void]$psi.ArgumentList.Add($token)
    }
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    $stream = [System.IO.File]::Create($destination)
    try {
        $process.StandardOutput.BaseStream.CopyTo($stream)
    }
    finally {
        $stream.Dispose()
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Could not restore tracked fixture path from blob ${blob}: git cat-file exited $($process.ExitCode)."
    }
}

function Prepare-Operation([string]$Operation, [int]$Iteration) {
    if ($Operation -in @('status-cache', 'status-no-cache')) {
        return
    }

    Restore-Index
    switch ($Operation) {
        'merge' {
            Restore-Base
        }
        'rebase' {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'switch', '--detach', '--quiet', $linearTip) -Label 'prepare-rebase'
        }
        'cherry-pick' {
            Restore-Base
        }
        { $_ -in @('add', 'checkout-path', 'stash', 'diff') } {
            Restore-Base
            Add-TrackedFileChange -Iteration $Iteration
        }
        { $_ -in @('commit', 'reset') } {
            Restore-Base
            Add-TrackedFileChange -Iteration $Iteration
            Invoke-SetupGit -Arguments @('-C', $Repo, 'add', '--', $benchmarkTrackedPath) -Label "prepare-$Operation"
        }
        'checkout-branch' {
            $sourceBranch = if ($Iteration % 2 -eq 0) { $branchA } else { $branchB }
            Invoke-SetupGit -Arguments @('-C', $Repo, 'checkout', '--quiet', $sourceBranch) -Label 'prepare-checkout-branch'
        }
        'checkout-noop' {
            $sourceBranch = if ($Iteration % 2 -eq 0) { $noopBranchA } else { $noopBranchB }
            Invoke-SetupGit -Arguments @('-C', $Repo, 'checkout', '--quiet', $sourceBranch) -Label 'prepare-checkout-noop'
        }
        'switch' {
            $sourceBranch = if ($Iteration % 2 -eq 0) { $branchA } else { $branchB }
            Invoke-SetupGit -Arguments @('-C', $Repo, 'switch', '--quiet', $sourceBranch) -Label 'prepare-switch'
        }
        'clean' {
            Restore-Base
            $cleanWorktreePath = Join-Path $Repo ($cleanFixturePath -replace '/', '\')
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cleanWorktreePath) | Out-Null
            Set-Content -LiteralPath $cleanWorktreePath -Value "Sparse index benchmark $Iteration" -Encoding ascii
        }
        default {
            Restore-Base
        }
    }

    if ($Operation -eq 'stash') {
        $script:stashBefore = Get-GitRef -Ref 'refs/stash'
        $script:stashCreated = $false
    }
}

function Get-OperationArguments([string]$Operation, [int]$Iteration) {
    switch ($Operation) {
        'status-cache'    { return @('-C', $Repo, 'status', '--porcelain=v1') }
        'status-no-cache' { return @('-C', $Repo, 'status', '--no-deserialize', '--porcelain=v1') }
        'merge'           { return @('-C', $Repo, 'merge', '--no-commit', '--no-ff', $mergeCommit) }
        'rebase'          { return @('-C', $Repo, '-c', 'user.name=Sparse Index Benchmark', '-c', 'user.email=benchmark@example.invalid', 'rebase', '--onto', $ontoCommit, $baseCommit) }
        'cherry-pick'     { return @('-C', $Repo, 'cherry-pick', '--no-commit', $mergeCommit) }
        'add'             { return @('-C', $Repo, 'add', '--', $benchmarkTrackedPath) }
        'commit'          { return @('-C', $Repo, '-c', 'user.name=Sparse Index Benchmark', '-c', 'user.email=benchmark@example.invalid', 'commit', '--no-gpg-sign', '-m', 'Sparse index benchmark commit') }
        'reset'           { return @('-C', $Repo, 'reset', '--mixed', 'HEAD') }
        'checkout-branch' {
            $targetBranch = if ($Iteration % 2 -eq 0) { $branchB } else { $branchA }
            return @('-C', $Repo, 'checkout', '--quiet', $targetBranch)
        }
        'checkout-noop' {
            $targetBranch = if ($Iteration % 2 -eq 0) { $noopBranchB } else { $noopBranchA }
            return @('-C', $Repo, 'checkout', '--quiet', $targetBranch)
        }
        'checkout-path'   { return @('-C', $Repo, 'checkout', '--quiet', '--', $benchmarkTrackedPath) }
        'switch' {
            $targetBranch = if ($Iteration % 2 -eq 0) { $branchB } else { $branchA }
            return @('-C', $Repo, 'switch', '--quiet', $targetBranch)
        }
        'stash'           { return @('-C', $Repo, 'stash', 'push', '--quiet', '-m', "sparse-index-benchmark-$runId", '--', $benchmarkTrackedPath) }
        'diff'            { return @('-C', $Repo, 'diff', '--no-ext-diff', '--', $benchmarkTrackedPath) }
        'log'             { return @('-C', $Repo, 'log', '-1', '--oneline', '--stat') }
        'blame'           { return @('-C', $Repo, 'blame', '-L', '1,1', '--', $benchmarkTrackedPath) }
        'clean'           { return @('-C', $Repo, 'clean', '-f', '--', $cleanFixturePath) }
    }
}

function Invoke-CleanupStep([string]$Description, [scriptblock]$Action) {
    try {
        & $Action
    }
    catch {
        $cleanupErrors.Add("$Description`: $($_.Exception.Message)")
    }
}

function Cleanup-Iteration([string]$Operation) {
    if ($Operation -in @('status-cache', 'status-no-cache')) {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace((Get-GitRef -Ref 'MERGE_HEAD'))) {
        Invoke-CleanupStep -Description 'Abort merge' -Action {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'merge', '--abort') -Label 'cleanup-merge'
        }
    }
    if ((Test-Path -LiteralPath (Join-Path $Repo '.git\rebase-merge')) -or
        (Test-Path -LiteralPath (Join-Path $Repo '.git\rebase-apply'))) {
        Invoke-CleanupStep -Description 'Abort rebase' -Action {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'rebase', '--abort') -Label 'cleanup-rebase'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace((Get-GitRef -Ref 'CHERRY_PICK_HEAD')) -or
        (Test-Path -LiteralPath (Join-Path $Repo '.git\sequencer'))) {
        Invoke-CleanupStep -Description 'Abort cherry-pick' -Action {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'cherry-pick', '--abort') -Label 'cleanup-cherry-pick'
        }
    }
    if ($Operation -eq 'stash' -and $stashCreated) {
        Invoke-CleanupStep -Description 'Drop benchmark stash' -Action {
            $currentStash = Get-GitRef -Ref 'refs/stash'
            if ($currentStash -ne $stashBefore) {
                $message = (Get-GitOutput -Arguments @('-C', $Repo, 'log', '-1', '--format=%s', 'refs/stash') | Select-Object -Last 1).ToString()
                if ($message -notlike "*sparse-index-benchmark-$runId*") {
                    throw 'The newest stash does not belong to this benchmark.'
                }
                Invoke-SetupGit -Arguments @('-C', $Repo, 'stash', 'drop', '--quiet') -Label 'cleanup-stash'
            }
        }
    }

    Invoke-CleanupStep -Description 'Restore base commit' -Action { Restore-Base }
    Invoke-CleanupStep -Description 'Restore original index' -Action { Restore-Index }
    Invoke-CleanupStep -Description 'Restore tracked fixture path' -Action {
        Restore-TrackedFixturePath
    }

    foreach ($path in @($mergeFixturePath, $ontoFixturePath, $cleanFixturePath)) {
        $worktreePath = Join-Path $Repo ($path -replace '/', '\')
        Invoke-CleanupStep -Description "Remove fixture $path" -Action {
            Remove-Item -LiteralPath $worktreePath -Force -ErrorAction SilentlyContinue
        }
    }

    if ($cleanupErrors.Count -gt 0) {
        throw "Benchmark cleanup failed: $($cleanupErrors -join ' | ')"
    }
}

function Wait-StatusCache {
    $deadline = [DateTime]::UtcNow.AddSeconds($StatusCacheTimeoutSeconds)
    do {
        $script:statusCacheProbe++
        $tracePath = Join-Path $runRoot ("status-cache-readiness-{0:D3}.json" -f $script:statusCacheProbe)
        $result = Invoke-Git -Arguments @('-C', $Repo, 'status', '--porcelain=v1') -TracePath $tracePath -OutputLabel 'status-cache-readiness'
        Assert-GitSuccess -Result $result -Context 'status-cache readiness probe'
        $traceSummary = Get-TraceSummary -TracePath $tracePath
        if ($traceSummary.StatusCacheResult -eq 'ok') {
            return
        }
        Start-Sleep -Seconds 2
    }
    while ([DateTime]::UtcNow -lt $deadline)

    throw "The GVFS status cache did not become ready within $StatusCacheTimeoutSeconds seconds."
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    $sorted = @($Values | Sort-Object)
    $position = ($sorted.Count - 1) * $Percentile
    $lower = [math]::Floor($position)
    $upper = [math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $sorted[$lower]
    }

    return $sorted[$lower] + (($sorted[$upper] - $sorted[$lower]) * ($position - $lower))
}

$originalBranchValue = Get-GitOutput -Arguments @('-C', $Repo, 'branch', '--show-current') | Select-Object -Last 1
$originalBranch = if ($null -eq $originalBranchValue) { '' } else { $originalBranchValue.ToString().Trim() }
$baseCommit = (Get-GitOutput -Arguments @('-C', $Repo, 'rev-parse', 'HEAD') | Select-Object -Last 1).ToString().Trim()
$configuredSparse = Get-OptionalBoolConfig -Name 'index.sparse'
$configuredSparseCheckout = Get-OptionalBoolConfig -Name 'core.sparseCheckout'
$configuredSparseCone = Get-OptionalBoolConfig -Name 'core.sparseCheckoutCone'

if ($IndexMode -eq 'full' -and $configuredSparse -eq 'true') {
    throw 'The full-index arm cannot run when index.sparse is true.'
}
if ($IndexMode -eq 'sparse') {
    if ($configuredSparse -ne 'true' -or $configuredSparseCheckout -ne 'true' -or $configuredSparseCone -ne 'true') {
        throw 'The sparse-index arm requires index.sparse, core.sparseCheckout, and core.sparseCheckoutCone.'
    }
    if ([string]::IsNullOrWhiteSpace($OutsideConePath)) {
        throw 'The sparse-index arm requires OutsideConePath.'
    }
}

$benchmarkTrackedPath = if ($IndexMode -eq 'sparse') { $OutsideConePath } else { $TrackedPath }
$benchmarkTrackedPath = $benchmarkTrackedPath -replace '\\', '/'
$trackedMatch = @(Get-GitOutput -Arguments @('-C', $Repo, 'ls-files', '--error-unmatch', '--', $benchmarkTrackedPath))
if ($trackedMatch.Count -eq 0) {
    throw "The benchmark path is not tracked: $benchmarkTrackedPath"
}
if ($IndexMode -eq 'sparse') {
    # A file that lives inside a collapsed directory is not itself listed by
    # 'ls-files --sparse'; its containing directory appears instead as a
    # sparse-directory entry (tag 'S', mode 040000, trailing slash). Confirm the
    # tracked path is collapsed by checking it has a sparse-directory ancestor.
    $sparseDirEntries = @(Get-GitOutput -Arguments @('-C', $Repo, 'ls-files', '--sparse', '-t') |
        ForEach-Object { if ($_ -match '^S\s+(.+)$') { $matches[1].Trim() } })
    $hasSparseAncestor = $false
    foreach ($sparseDir in $sparseDirEntries) {
        if ($benchmarkTrackedPath.StartsWith($sparseDir, [System.StringComparison]::Ordinal)) {
            $hasSparseAncestor = $true
            break
        }
    }
    if (-not $hasSparseAncestor) {
        throw "OutsideConePath is not represented by a sparse-directory entry: $benchmarkTrackedPath"
    }
}
$trackedWorktreePath = Join-Path $Repo ($benchmarkTrackedPath -replace '/', '\')
if (-not (Test-Path -LiteralPath $trackedWorktreePath -PathType Leaf)) {
    throw "The benchmark path is not available in the working tree: $benchmarkTrackedPath"
}

$dirty = Get-GitOutput -Arguments @('-C', $Repo, 'status', '--no-deserialize', '--porcelain=v1')
if (($dirty -join '').Length -ne 0) {
    throw 'The repository must be clean before a benchmark run.'
}

$fixtureDirectory = if ($IndexMode -eq 'sparse') {
    ($benchmarkTrackedPath.Substring(0, $benchmarkTrackedPath.LastIndexOf('/')))
}
else {
    '.sparse-index-benchmark'
}
if ([string]::IsNullOrWhiteSpace($fixtureDirectory)) {
    throw 'The sparse benchmark path must be below a directory.'
}

$mergeFixturePath = "$fixtureDirectory/merge-$runId.txt"
$ontoFixturePath = "$fixtureDirectory/onto-$runId.txt"
$cleanFixturePath = "$fixtureDirectory/untracked-$runId.txt"
$initialIndexBytes = Get-FileLength -Path $indexPath
$initialEntryCount = Get-IndexEntryCount
$expectedEntryCountMatches = $ExpectedFullIndexEntries -eq 0 -or $initialEntryCount -eq $ExpectedFullIndexEntries
if ($IndexMode -eq 'full' -and -not $expectedEntryCountMatches) {
    throw "The full index has $initialEntryCount entries. Expected $ExpectedFullIndexEntries."
}
$initialProjectionBytes = Get-FileLength -Path $projectionPath
$initialSparseDirectoryCount = Get-SparseDirectoryCount -Force
if ($IndexMode -eq 'full' -and $initialSparseDirectoryCount -ne 0) {
    throw 'The full-index arm contains sparse-directory entries.'
}
if ($IndexMode -eq 'sparse' -and $initialSparseDirectoryCount -eq 0) {
    throw 'The sparse-index arm has no sparse-directory entries.'
}

[System.IO.File]::Copy($indexPath, $indexBackupPath, $true)

$mergeCommit = New-FixtureCommit -Parent $baseCommit -Path $mergeFixturePath -Content 'merge 1' -Message 'Sparse index merge fixture'
$ontoCommit = New-FixtureCommit -Parent $baseCommit -Path $ontoFixturePath -Content 'onto 1' -Message 'Sparse index rebase target'
$linearParent = $baseCommit
for ($commitNumber = 1; $commitNumber -le 7; $commitNumber++) {
    $linearParent = New-FixtureCommit -Parent $linearParent -Path $mergeFixturePath -Content "linear $commitNumber" -Message "Sparse index linear fixture $commitNumber"
}
$linearTip = $linearParent

Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', "refs/heads/$branchA", $baseCommit) -Label 'create-branch-a'
Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', "refs/heads/$branchB", $mergeCommit) -Label 'create-branch-b'
Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', "refs/heads/$noopBranchA", $baseCommit) -Label 'create-noop-branch-a'
Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', "refs/heads/$noopBranchB", $baseCommit) -Label 'create-noop-branch-b'

$metadata = [ordered]@{
    schema_version = 2
    started_utc = [DateTime]::UtcNow.ToString('o')
    completed = $false
    repository = $Repo
    repository_label = $RepoLabel
    original_branch = $originalBranch
    head = $baseCommit
    git_path = $GitPath
    git_version = $gitVersion
    index_mode = $IndexMode
    configured_index_sparse = $configuredSparse
    configured_sparse_checkout = $configuredSparseCheckout
    configured_sparse_checkout_cone = $configuredSparseCone
    cache_state = 'warm OS and GVFS caches'
    runs = $Runs
    warmups = $Warmups
    operations = $Operations
    tracked_fixture_path = $benchmarkTrackedPath
    initial_index_bytes = $initialIndexBytes
    initial_index_entries = $initialEntryCount
    expected_full_index_entries = $ExpectedFullIndexEntries
    initial_sparse_directory_entries = $initialSparseDirectoryCount
    initial_projection_bytes = $initialProjectionBytes
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8

$allRows = [System.Collections.Generic.List[object]]::new()
$failure = $null
try {
    foreach ($operation in $Operations) {
        if ($operation -eq 'status-cache') {
            Wait-StatusCache
        }

        for ($warmup = 1; $warmup -le $Warmups; $warmup++) {
            $iteration = -$warmup
            $warmupFailure = $null
            Prepare-Operation -Operation $operation -Iteration $iteration
            try {
                $arguments = Get-OperationArguments -Operation $operation -Iteration $iteration
                $traceName = "{0}-{1}-{2}-{3}-warmup-{4:D2}.json" -f $RepoLabel, $IndexMode, $gitLabel, $operation, $warmup
                $result = Invoke-Git -Arguments $arguments -TracePath (Join-Path $runRoot $traceName) -OutputLabel "$operation-warmup"
                if ($operation -eq 'stash' -and (Get-GitRef -Ref 'refs/stash') -ne $stashBefore) {
                    $stashCreated = $true
                }
                Assert-GitSuccess -Result $result -Context "$operation warmup $warmup"
            }
            catch {
                $warmupFailure = $_
            }

            try {
                Cleanup-Iteration -Operation $operation
            }
            catch {
                if ($null -eq $warmupFailure) {
                    $warmupFailure = $_
                }
                else {
                    $cleanupErrors.Add($_.Exception.Message)
                }
            }
            if ($null -ne $warmupFailure) {
                throw $warmupFailure
            }
        }

        if ($operation -eq 'status-cache') {
            Wait-StatusCache
        }

        for ($run = 1; $run -le $Runs; $run++) {
            $runFailure = $null
            Prepare-Operation -Operation $operation -Iteration $run
            $indexBytesBefore = Get-FileLength -Path $indexPath
            $indexEntriesBefore = Get-IndexEntryCount
            $sparseDirectoriesBefore = Get-SparseDirectoryCount
            $projectionBytesBefore = Get-FileLength -Path $projectionPath
            $traceName = "{0}-{1}-{2}-{3}-{4:D2}.json" -f $RepoLabel, $IndexMode, $gitLabel, $operation, $run
            $tracePath = Join-Path $runRoot $traceName
            try {
                $arguments = Get-OperationArguments -Operation $operation -Iteration $run
                $result = Invoke-Git -Arguments $arguments -TracePath $tracePath -OutputLabel "$operation-$run"
                if ($operation -eq 'stash' -and (Get-GitRef -Ref 'refs/stash') -ne $stashBefore) {
                    $stashCreated = $true
                }
                $traceSummary = Get-TraceSummary -TracePath $tracePath
                Assert-GitSuccess -Result $result -Context "$operation run $run"
                if ($operation -eq 'status-cache' -and $traceSummary.StatusCacheResult -ne 'ok') {
                    throw "status-cache run $run did not use the GVFS serialized cache."
                }
                $indexEntriesAfter = Get-IndexEntryCount
                $sparseDirectoriesAfter = Get-SparseDirectoryCount
                if ($IndexMode -eq 'full' -and
                    ($indexEntriesBefore -lt (0.9 * $initialEntryCount) -or $indexEntriesAfter -lt (0.9 * $initialEntryCount))) {
                    throw "$operation run $run reduced the full index below 90 percent of its initial entry count."
                }
                if ($IndexMode -eq 'sparse' -and ($sparseDirectoriesBefore -eq 0 -or $sparseDirectoriesAfter -eq 0)) {
                    throw "$operation run $run did not retain a sparse index."
                }

                $row = [pscustomobject]@{
                    repository = $RepoLabel
                    index_mode = $IndexMode
                    git_version = $gitVersion
                    cache_state = 'warm'
                    operation = $operation
                    run = $run
                    wall_ms = $result.WallMilliseconds
                    exit_code = $result.ExitCode
                    index_bytes_before = $indexBytesBefore
                    index_bytes_after = Get-FileLength -Path $indexPath
                    index_entries_before = $indexEntriesBefore
                    index_entries_after = $indexEntriesAfter
                    sparse_directories_before = $sparseDirectoriesBefore
                    sparse_directories_after = $sparseDirectoriesAfter
                    top_first_read_entries = $traceSummary.TopFirstReadEntries
                    top_min_read_entries = $traceSummary.TopMinReadEntries
                    top_max_read_entries = $traceSummary.TopMaxReadEntries
                    projection_bytes_before = $projectionBytesBefore
                    projection_bytes_after = Get-FileLength -Path $projectionPath
                    expanded = $traceSummary.Expanded
                    expansion_count = $traceSummary.ExpansionCount
                    expansion_reasons = $traceSummary.ExpansionReasons
                    status_cache_result = if ($operation -eq 'status-no-cache') { 'bypassed' } else { $traceSummary.StatusCacheResult }
                    status_cache_reject_reason = $traceSummary.StatusCacheRejectReason
                    trace_parse_errors = $traceSummary.ParseErrors
                    prime_cache_tree_ms = [math]::Round(1000 * $traceSummary.AllTotals.prime_cache_tree, 3)
                    cache_tree_fully_valid_ms = [math]::Round(1000 * $traceSummary.AllTotals.cache_tree_fully_valid, 3)
                    unpack_trees_ms = [math]::Round(1000 * $traceSummary.AllTotals.unpack_trees, 3)
                    convert_to_sparse_ms = [math]::Round(1000 * $traceSummary.AllTotals.convert_to_sparse, 3)
                    ensure_full_index_ms = [math]::Round(1000 * $traceSummary.AllTotals.ensure_full_index, 3)
                    expand_index_ms = [math]::Round(1000 * $traceSummary.AllTotals.expand_index, 3)
                    do_read_index_ms = [math]::Round(1000 * $traceSummary.AllTotals.do_read_index, 3)
                    do_write_index_ms = [math]::Round(1000 * $traceSummary.AllTotals.do_write_index, 3)
                    vfs_apply_ms = [math]::Round(1000 * $traceSummary.AllTotals.vfs_apply, 3)
                    top_prime_cache_tree_ms = [math]::Round(1000 * $traceSummary.TopTotals.prime_cache_tree, 3)
                    top_cache_tree_fully_valid_ms = [math]::Round(1000 * $traceSummary.TopTotals.cache_tree_fully_valid, 3)
                    top_unpack_trees_ms = [math]::Round(1000 * $traceSummary.TopTotals.unpack_trees, 3)
                    top_convert_to_sparse_ms = [math]::Round(1000 * $traceSummary.TopTotals.convert_to_sparse, 3)
                    top_ensure_full_index_ms = [math]::Round(1000 * $traceSummary.TopTotals.ensure_full_index, 3)
                    top_expand_index_ms = [math]::Round(1000 * $traceSummary.TopTotals.expand_index, 3)
                    top_do_read_index_ms = [math]::Round(1000 * $traceSummary.TopTotals.do_read_index, 3)
                    top_do_write_index_ms = [math]::Round(1000 * $traceSummary.TopTotals.do_write_index, 3)
                    top_vfs_apply_ms = [math]::Round(1000 * $traceSummary.TopTotals.vfs_apply, 3)
                    trace_file = $traceName
                }
                $allRows.Add($row)
                if (Test-Path -LiteralPath $resultsPath) {
                    $row | Export-Csv -LiteralPath $resultsPath -NoTypeInformation -Append
                }
                else {
                    $row | Export-Csv -LiteralPath $resultsPath -NoTypeInformation
                }
            }
            catch {
                $runFailure = $_
            }

            try {
                Cleanup-Iteration -Operation $operation
            }
            catch {
                if ($null -eq $runFailure) {
                    $runFailure = $_
                }
                else {
                    $cleanupErrors.Add($_.Exception.Message)
                }
            }
            if ($null -ne $runFailure) {
                throw $runFailure
            }
        }
    }
}
catch {
    $failure = $_
    $metadata.error = $_.Exception.Message
}
finally {
    Invoke-CleanupStep -Description 'Final iteration cleanup' -Action { Cleanup-Iteration -Operation '' }
    if ([string]::IsNullOrWhiteSpace($originalBranch)) {
        Invoke-CleanupStep -Description 'Restore original detached HEAD' -Action {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'switch', '--detach', '--quiet', $baseCommit) -Label 'restore-original-head'
        }
    }
    else {
        Invoke-CleanupStep -Description 'Restore original branch' -Action {
            Invoke-SetupGit -Arguments @('-C', $Repo, 'switch', '--quiet', $originalBranch) -Label 'restore-original-branch'
        }
    }
    Invoke-CleanupStep -Description 'Restore final index' -Action { Restore-Index }
    Invoke-CleanupStep -Description 'Delete benchmark branch A' -Action {
        Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', '-d', "refs/heads/$branchA") -Label 'delete-branch-a'
    }
    Invoke-CleanupStep -Description 'Delete benchmark branch B' -Action {
        Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', '-d', "refs/heads/$branchB") -Label 'delete-branch-b'
    }
    Invoke-CleanupStep -Description 'Delete no-op benchmark branch A' -Action {
        Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', '-d', "refs/heads/$noopBranchA") -Label 'delete-noop-branch-a'
    }
    Invoke-CleanupStep -Description 'Delete no-op benchmark branch B' -Action {
        Invoke-SetupGit -Arguments @('-C', $Repo, 'update-ref', '-d', "refs/heads/$noopBranchB") -Label 'delete-noop-branch-b'
    }

    $metadata.completed_utc = [DateTime]::UtcNow.ToString('o')
    $metadata.completed = $null -eq $failure -and $cleanupErrors.Count -eq 0
    $metadata.cleanup_errors = @($cleanupErrors)
    $metadata.final_index_bytes = Get-FileLength -Path $indexPath
    try {
        $metadata.final_index_entries = Get-IndexEntryCount
    }
    catch {
        $metadata.final_index_entries_error = $_.Exception.Message
    }
    $metadata.final_projection_bytes = Get-FileLength -Path $projectionPath
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Remove-Item -LiteralPath $indexBackupPath -Force -ErrorAction SilentlyContinue
}

if ($null -ne $failure) {
    throw $failure
}
if ($cleanupErrors.Count -gt 0) {
    throw "Benchmark cleanup failed: $($cleanupErrors -join ' | ')"
}

$summaryRows = foreach ($group in ($allRows | Group-Object operation)) {
    $values = [double[]]@($group.Group.wall_ms)
    [pscustomobject]@{
        repository = $RepoLabel
        index_mode = $IndexMode
        git_version = $gitVersion
        cache_state = 'warm'
        operation = $group.Name
        samples = $values.Count
        median_ms = [math]::Round((Get-Percentile -Values $values -Percentile 0.5), 3)
        q1_ms = [math]::Round((Get-Percentile -Values $values -Percentile 0.25), 3)
        q3_ms = [math]::Round((Get-Percentile -Values $values -Percentile 0.75), 3)
        min_ms = [math]::Round(($values | Measure-Object -Minimum).Minimum, 3)
        max_ms = [math]::Round(($values | Measure-Object -Maximum).Maximum, 3)
    }
}
$summaryRows | Sort-Object operation | Export-Csv -LiteralPath $summaryPath -NoTypeInformation

Write-Output $runRoot
