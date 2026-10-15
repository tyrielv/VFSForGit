# Blocked processes: experimental prototype

The `gvfs.blocked-processes` configuration prevents selected executables from starting work that requires virtual filesystem callbacks.
The default list is empty.
An empty list preserves normal callback behavior.

The policy returns `E_ACCESSDENIED` (`0x80070005`).
It does not return an empty directory or pretend that a projected file does not exist.

## Configure the list

Use a build that includes this prototype.
An older build ignores the key.

From the repository's `src` directory, set a semicolon-separated list of executable names:

```powershell
git config --local gvfs.blocked-processes "windirstat.exe;rg.exe"
```

Unmount and remount the enlistment:

```powershell
gvfs unmount ..
gvfs mount ..
```

To disable the policy locally, set an empty value:

```powershell
git config --local gvfs.blocked-processes ""
```

Then unmount and remount the enlistment.
An empty local value also overrides a list from the global Git configuration.
To restore an inherited value, remove the local key instead.

Names match without case sensitivity.
For example, `rg.exe` matches both `RG.EXE` and `C:\tools\rg.exe` in callback metadata.
It does not match `my-rg.exe`, `rg`, or `rg.exe.backup`.
Whitespace and duplicate names do not change the policy.
Paths and wildcard patterns are invalid configuration.
An invalid value prevents mount.
The CLI currently reports a generic mount failure and points to the log.
The mount log identifies the configuration key and the required format.

## Callback behavior

`FileSystemCallbacks` reads the list at mount and applies the process-name policy.
The callback checks use an in-memory name set.
They do not start Git or query the process table.
`WindowsFileSystemVirtualizer` consults that policy at these entry points:

| Callback | Denial prevents |
| --- | --- |
| `StartDirectoryEnumerationCallback` | Projection enumeration, size lookup, and enumeration state allocation |
| `GetPlaceholderInfoCallback` | File and directory placeholder creation |
| `GetFileDataCallback` | Content lookup, download, and hydration |

The checks run before projection access or asynchronous work registration.
They also apply to the in-memory enumeration path and special files such as `.gitignore`.
Enumeration continuation and cleanup retain their normal behavior.

The mount log records the first denial for each executable name and callback type.
Each event includes the process ID, path, operation, and configuration key.
This bound prevents recursive scans from producing one log entry per path.
The next heartbeat reports the total denied callback count.

## Observed experience

The experiments used a small test directory, not a complete repository scan.
The directory contained 18 projected files.
Ripgrep's default hidden-file filter selected 13 of them.

| Program and operation | Blocked result | Allowed result |
| --- | --- | --- |
| ripgrep 15.2.0 content search | Exit 2, `Access is denied. (os error 5)`, no new placeholders or hydrations | Exit 0, 13 new file placeholders and 13 hydrations |
| WinDirStat 2.9.3 `/SaveTo` report | Exit 0, no console error, CSV root row with zero files | Exit 0, 18 files, 407 logical bytes, zero physical bytes, no new placeholders or hydrations |
| `cmd.exe` with `dir /s /b` | Nonzero exit, no new placeholders | An allowed caller can still enumerate and read |
| `powershell.exe` with recursive `Get-ChildItem -ErrorAction Stop` | Nonzero exit, no new placeholders | An allowed caller can still enumerate and read |

The WinDirStat result is not an empty-directory response from GVFS.
WinDirStat receives an enumeration error but reports an empty result with a successful exit code.
This behavior can mislead users and report consumers.
The experiment covers report mode, not the interactive GUI, duplicate detection, or older WinDirStat versions.

The blocked direct-file ripgrep search also returned exit 2 for an absent file.
After an allowed search hydrated the file, the blocked direct-file search returned exit 0.

The allowed WinDirStat metadata scan did not trigger runaway hydration.
A blanket process block can therefore deny an operation that is safe in this test.
Other versions, scan modes, and nested trees need separate measurements.

## Limits

This policy is a resource control, not a security boundary.
ProjFS does not call GVFS for every access to local data.
A blocked process can read a file that another process already hydrated.
It can also access ordinary local files.
Existing on-disk entries and already populated directories can bypass some callbacks.

The policy permits requests without a process image name.
Process names are not authenticated identities.
A renamed executable or a helper process can bypass a name-based rule.
The policy does not inspect raw-volume access or requests that bypass these callbacks.

ProjFS supplies an executable name, not a shell command.
A rule for `cmd.exe` cannot distinguish `dir /s` from other CMD operations.
A PowerShell rule also affects other commands in that host.
Neither rule prevents an unlisted child executable from accessing the repository.
A rule for Git or GVFS executables can disrupt repository operations.

## Experience improvements to explore

| Approach | Benefit | Constraint |
| --- | --- | --- |
| Separate enumeration, placeholder, and hydration permissions | Metadata tools can work without content downloads | Deep traversal can still need directory placeholders and size lookups |
| A visible notification with a bounded rate | Explains the policy even when an application hides filesystem errors | A filesystem error cannot carry a custom application message |
| Explicit, scope-limited opt-in | Permits an intentional search without unrestricted repository access | Needs limits for files, bytes, time, and concurrent requests |
| A labelled local-only scan | Supports inspection of data already on disk | Must exclude unhydrated placeholders and state that results are incomplete |
| Index-based path discovery | Lists tracked paths without recursive filesystem traversal | Content search still requires explicit, bounded data access |
| Shell integration for recursive commands | Can warn or enforce scope before a built-in command starts | Requires shell cooperation rather than callback command detection |
| Tool-specific integration | Lets scanners report denied scopes and distinguish logical size from local size | Requires cooperation from the tool |

A successful response with no projected entries is another possible experiment.
It risks silent incomplete results and still permits entries that ProjFS merges from disk.
A file-not-found response also needs separate analysis of ProjFS negative-cache behavior.
The current prototype uses an explicit error and verifies that allowed callers can access the same paths after denial.

## Reproduce the tests

Build the unit tests and the non-AOT functional-test payload with the commands in [AGENTS.md](../AGENTS.md).

Run these unit-test fixtures:

```powershell
out\GVFS.UnitTests\bin\Debug\net10.0-windows10.0.17763.0\win-x64\GVFS.UnitTests.exe --test=GVFS.UnitTests.Windows.Virtualization.WindowsFileSystemVirtualizerTests,GVFS.UnitTests.Virtualization.FileSystemCallbacksTests
```

Run the filesystem tests:

```powershell
src\scripts\RunFunctionalTests-Dev.ps1 -Configuration Debug -Arch x64 --test=GVFS.FunctionalTests.Tests.EnlistmentPerTestCase.BlockedProcessTests
```

The scanner experiment is explicit and does not run with the fixture by default.
To run it, select a portable scanner:

```powershell
$env:GVFS_TEST_SCANNER_EXE = "C:\tools\rg.exe"
src\scripts\RunFunctionalTests-Dev.ps1 -Configuration Debug -Arch x64 --test=GVFS.FunctionalTests.Tests.EnlistmentPerTestCase.BlockedProcessTests.RecordScannerExperience
```

Use the same command with a path to `WinDirStat.exe` for its report experiment.
The test runs each scanner first with the policy enabled, then with an empty list.
It records exit codes, output, reports, placeholder deltas, and mount heartbeat counters.
The ripgrep experiment also compares direct access to an absent file and a hydrated file.
Each test uses a disposable enlistment and removes it after unmount.
