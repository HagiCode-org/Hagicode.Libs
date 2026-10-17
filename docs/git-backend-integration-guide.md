# Git wrapper: backend integration guide

This guide covers `HagiCode.Libs.Git` (`IGitClient`) for hosts that consume it, the timing suite that measures every operation on each operating system, and the baseline and post-swap numbers for the HagiCode backend.

The package reference is in `src/HagiCode.Libs.Git/README.md`, which also lists the operations, the failure codes and the minimum git version (2.26).

## Registration

```csharp
services.AddHagiCodeGit();
```

The host then injects `IGitClient`. The client is a singleton, and each call starts a short-lived `git` process unless the operation is one of the documented no-process fast paths (repository validity, `HEAD` branch, and scanning).

## Consumption rules

- Use the typed result. Check `Success`, then read `Value` or `Failure.Code`. Do not parse `Failure.Details` to decide behavior.
- Map failure codes at the adapter, not in the wrapper. The backend adapter owns its own response codes.
- Build commit messages (co-author trailers, `[skip ci]`) before calling `CommitAsync`. The wrapper records the message exactly as given.
- Pass cancellation tokens. A cancelled call throws `OperationCanceledException` after the process tree is terminated.

## Timing suite

The suite lives in `tests/HagiCode.Libs.Git.Tests`. Each test runs against a real git. The `TimedGitClient` decorator records every call with a monotonic clock. Each run writes:

- `git-timing-<platform>-<arch>.json`: metadata (OS, architecture, .NET runtime, git version, file system, UTC timestamp, warm-up and measured counts), raw records, and per-operation statistics (count, min, mean, median, p95, max, total, failures).
- `git-timing-<platform>-<arch>.csv`: raw records.

Statistics use the nearest-rank percentile, so small samples are deterministic.

Dedicated scenarios run 2 warm-up and 7 measured iterations on the generated repository (1,000 files across 50 folders, 200 commits):

| Scenario | Operations |
| --- | --- |
| `read-path` | repository check, root discovery, remote URL, identity, current branch, branch exists, branch name, dirty probe (clean and dirty trees), status (clean and dirty trees), scan |
| `write-path` | stage, unstage, stage all, unstage all, commit, push, pull, rebase (success) |
| `clone` | ensure-clone of the generated repository into a fresh target |

The workflow `.github/workflows/git-cross-platform.yml` runs the full profile on `linux-x64`, `windows-x64`, `osx-x64` and `osx-arm64`, uploads each runner's files even when tests fail, and writes the comparison table to the job summary. Durations are informational only.

## Baseline (pre-swap backend)

Captured before the backend moves to the wrapper, to serve as the reference for the post-swap comparison.

Captured on the local machine (Fedora 44, .NET 10.0.12, git 2.56.0) against the pre-swap backend `GitService`. Raw records: `docs/git-backend-baseline-linux-x64.json`. Fixture: generated repository, 200 commits x 5 files (1,000 files in 50 folders), built with `git fast-import`; remote is a local bare repository. Two warm-up and seven measured iterations per scenario, milliseconds. The backend baseline has no `IsRepository`, identity or file-list status operations, because `IGitService` has none.

| Operation | Scenario | count | min | median | p95 | max |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| BranchExistsAsync | read-path | 7 | 0.14 | 0.21 | 0.30 | 0.30 |
| CommitAsync | write-path | 7 | 4.67 | 4.80 | 5.21 | 5.21 |
| EnsureClonedAsync | clone | 7 | 30.63 | 31.02 | 32.40 | 32.40 |
| FindRepositoryRootAsync | read-path | 7 | 0.01 | 0.01 | 0.02 | 0.02 |
| GetCurrentBranchAsync | read-path | 7 | 0.21 | 0.25 | 0.29 | 0.29 |
| GetRemoteUrlAsync | read-path | 7 | 0.30 | 0.35 | 0.40 | 0.40 |
| GetStatusAsync | read-path | 14 | 2.44 | 9.30 | 29.16 | 29.16 |
| IsBranchNameValidAsync | read-path | 7 | 0.09 | 0.10 | 0.15 | 0.15 |
| ProbeDirtyAsync | read-path | 14 | 2.41 | 8.97 | 17.51 | 17.51 |
| PullAsync | write-path | 7 | 18.69 | 20.40 | 27.86 | 27.86 |
| PushAsync | write-path | 7 | 12.62 | 14.34 | 16.35 | 16.35 |
| RebaseAsync | write-path | 7 | 29.43 | 31.24 | 32.37 | 32.37 |
| ScanForRepositoriesAsync | read-path | 7 | 0.05 | 0.06 | 0.07 | 0.07 |
| StageAllAsync | write-path | 7 | 4.87 | 5.48 | 10.30 | 10.30 |
| StageFileAsync | write-path | 7 | 1.23 | 1.33 | 1.50 | 1.50 |
| UnstageAllAsync | write-path | 7 | 4.10 | 4.62 | 9.81 | 9.81 |
| UnstageFileAsync | write-path | 7 | 1.06 | 1.19 | 1.22 | 1.22 |

## Post-swap comparison

Recorded after the backend adapter is in place. Read-path regressions are called out here, not hidden.

Captured with the same fixture, iteration counts and harness as the baseline, after the backend adapter replaced the engine. Raw records: `docs/git-backend-postswap-linux-x64.json`. Milliseconds. Negative change is faster.

| Operation | Scenario | median before | median after | p95 before | p95 after | change in median |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| BranchExistsAsync | read-path | 0.21 | 1.12 | 0.30 | 1.63 | +431% |
| CommitAsync | write-path | 4.80 | 9.64 | 5.21 | 10.72 | +101% |
| EnsureClonedAsync | clone | 31.02 | 23.89 | 32.40 | 27.05 | -23% |
| FindRepositoryRootAsync | read-path | 0.01 | 0.02 | 0.02 | 0.03 | +51% |
| GetCurrentBranchAsync | read-path | 0.25 | 0.04 | 0.29 | 0.04 | -85% |
| GetRemoteUrlAsync | read-path | 0.35 | 1.12 | 0.40 | 9.95 | +217% |
| GetStatusAsync | read-path | 9.30 | 8.76 | 29.16 | 22.48 | -6% |
| IsBranchNameValidAsync | read-path | 0.10 | 0.96 | 0.15 | 1.10 | +818% |
| ProbeDirtyAsync | read-path | 8.97 | 8.90 | 17.51 | 17.17 | -1% |
| PullAsync | write-path | 20.40 | 22.12 | 27.86 | 24.89 | +8% |
| PushAsync | write-path | 14.34 | 16.24 | 16.35 | 18.90 | +13% |
| RebaseAsync | write-path | 31.24 | 35.49 | 32.37 | 43.06 | +14% |
| ScanForRepositoriesAsync | read-path | 0.06 | 0.08 | 0.07 | 0.12 | +31% |
| StageAllAsync | write-path | 5.48 | 5.86 | 10.30 | 7.73 | +7% |
| StageFileAsync | write-path | 1.33 | 2.27 | 1.50 | 2.31 | +71% |
| UnstageAllAsync | write-path | 4.62 | 6.71 | 9.81 | 7.35 | +45% |
| UnstageFileAsync | write-path | 1.19 | 3.02 | 1.22 | 3.42 | +153% |

**Read-path regressions (called out, not hidden).** Three read operations now start a `git` process where the LibGit2Sharp version did the work in-process:

- `BranchExistsAsync` (median 0.21 to 1.12 ms, p95 0.30 to 1.63 ms): one `for-each-ref` process per call.
- `IsBranchNameValidAsync` (median 0.10 to 0.96 ms): one `check-ref-format` process per call.
- `GetRemoteUrlAsync` (median 0.35 to 1.12 ms, p95 0.40 to 9.95 ms): one `config --local --get` process per call.

Each is still around a millisecond, and the regression is process-spawn cost on this machine, not a change in result. The design's no-spawn fast paths cover repository validity and the current branch (`GetCurrentBranchAsync` is faster, 0.25 to 0.04 ms). They do not cover these three reads. Reading `.git/config` and `packed-refs` directly for the remote, branch existence and name checks would remove the cost, but it would also re-implement git's parsing, so it is left as a follow-up decision rather than done silently.

**Write path and remote sync** (commit, stage, unstage, push, pull, rebase) move by roughly +7% to +150% in median. Single-file staging and unstaging show the largest relative change, but their absolute cost is about 1 to 3 ms. Push, pull and rebase are dominated by the local bare-remote round trip, not by the wrapper.

**Clone** is faster after the swap (31 to 24 ms median), because the wrapper clones with the CLI instead of LibGit2Sharp.

**Status and dirty probe** are unchanged within noise (ProbeDirty median 8.97 to 8.90 ms; GetStatus median 9.30 to 8.76 ms). The status read still runs only the probe, as the adapter test asserts.

No operation failed in either run.
