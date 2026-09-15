# Sparse-index benchmark harness

This harness measures full-index and sparse-index Git operations in a VFS for
Git enlistment. It writes one trace2 file per sample and reports median, IQR,
minimum, and maximum wall-clock times.

Run the harness only in a clean experiment enlistment. It creates temporary
commits and branches. It restores the original branch and index after each
sample.

```powershell
.\benchmarks\run-sparse-index-benchmarks.ps1 `
  -Repo C:\repos\experiment\src `
  -GitPath 'C:\Program Files\Git\cmd\git.exe' `
  -RepoLabel large-repo `
  -OutputRoot C:\benchmark-data\raw `
  -ScratchRoot C:\benchmark-data\scratch `
  -ConfirmExperimentRepo `
  -IndexMode full `
  -ExpectedFullIndexEntries 1000000 `
  -Runs 5
```

Trace2 records can contain repository paths, remote URLs, and Git
configuration. Set `OutputRoot` outside the source repository. Review trace
content before sharing it.

## Workloads

The harness measures these workloads:

| Operation | Workload |
|---|---|
| `status-cache` | Clean status through the GVFS serialized status cache |
| `status-no-cache` | Clean status with `--no-deserialize` |
| `merge` | Merge one generated file change without a commit |
| `rebase` | Rebase seven generated commits onto an independent commit |
| `cherry-pick` | Cherry-pick one generated file change without a commit |
| `add` | Add one modified tracked file |
| `commit` | Commit one staged file change on detached `HEAD` |
| `reset` | Run a mixed reset with one staged file change |
| `checkout-branch` | Checkout branches with a one-file tree difference |
| `checkout-noop` | Checkout branches at the same commit |
| `checkout-path` | Restore one modified tracked file |
| `switch` | Switch branches with a one-file tree difference |
| `stash` | Stash one modified tracked file |
| `diff` | Diff one modified tracked file |
| `log` | Show one commit with file statistics |
| `blame` | Blame the first line of one tracked file |
| `clean` | Remove one generated untracked file |

Use `-Operations` to select a subset. The harness runs one unreported warmup
by default. Warmups also write trace2 records.

## Index modes

The full-index arm rejects a true `index.sparse` value and rejects initial
sparse-directory entries. Set `ExpectedFullIndexEntries` to detect the wrong
revision or index shape before a long run.

The sparse-index arm requires these settings:

```text
index.sparse=true
core.sparseCheckout=true
core.sparseCheckoutCone=true
```

The sparse arm also requires `-OutsideConePath`. Use a tracked, projected file
outside the cone. The harness verifies that a sparse-directory entry represents
the supplied path. It checks for sparse-directory entries before and after
every sample. It stops if the index expands and does not collapse again.

This harness labels all samples as warm. It retains normal operating-system
and GVFS caches. It does not claim to produce cold-cache measurements.

## Output

Each run directory contains:

- `run-metadata.json` with repository state and cleanup results.
- `results.csv` with one row per measured sample.
- `summary.csv` with median, IQR, minimum, and maximum wall-clock times.
- One trace2 JSON file per warmup and measured sample.
- Standard output and standard error for each Git invocation.

The CSV records index bytes, on-disk entry counts, sparse-directory counts,
GVFS projection bytes, expansion reasons, and selected trace2 regions.
Top-level and descendant-process region totals use separate columns.
