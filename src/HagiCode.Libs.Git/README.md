# HagiCode.Libs.Git

A provider-agnostic git wrapper for HagiCode hosts. It drives the `git` command-line tool as its single engine and returns typed results with stable failure codes instead of throwing for expected failures.

## Usage via dependency injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using HagiCode.Libs.Git;

var services = new ServiceCollection();
services.AddHagiCodeGit(options =>
{
    // Optional. Defaults: git is resolved from PATH; 2 min for local commands, 10 min for network commands.
    options.CommandTimeout = TimeSpan.FromMinutes(2);
});

using var provider = services.BuildServiceProvider();
IGitClient git = provider.GetRequiredService<IGitClient>();

var status = await git.GetStatusAsync("/path/to/repository");
if (status.Success)
{
    Console.WriteLine($"{status.Value!.Branch}: {status.Value.Entries.Count} changed paths");
}
else
{
    Console.WriteLine($"{status.Failure!.Code}: {status.Failure.Message}");
}
```

The sample is compiled and run by `Readme_usage_sample_runs_as_documented` in `tests/HagiCode.Libs.Git.Tests/DependencyInjectionTests.cs`.

## Operations

| Operation | Method | Notes |
| --- | --- | --- |
| Repository check | `IsRepositoryAsync` | `.git` directory, or `.git` file for worktrees and submodules. Reads the file system only. |
| Root discovery | `FindRepositoryRootAsync` | Walks up to the nearest folder with a `.git` directory. |
| Remote URL | `GetRemoteUrlAsync` | `origin`. A missing remote is a success with `null`. |
| Identity | `GetIdentityAsync` | `user.name` / `user.email`, local over global, per value, with scope. Read-only. |
| Config value | `GetConfigValueAsync` | One key in one scope (local or global). Read-only. Unset is a successful `null`. |
| Tracking | `GetTrackingInfoAsync` | Upstream and ahead/behind counts of the current branch. `null` when detached or without an upstream. |
| Current branch | `GetCurrentBranchAsync` | Read from `HEAD`. Detached returns `HEAD`. Unborn returns its name. |
| Branch exists | `BranchExistsAsync` | Local branches, and remote-tracking branches by short name. |
| Branch name | `IsBranchNameValidAsync` | `git check-ref-format refs/heads/<name>`. |
| Dirty probe | `ProbeDirtyAsync` | One `git status` process: boolean, branch and upstream tracking, with no file list. Inconclusive when git cannot finish. |
| Status | `GetStatusAsync` | `git status --porcelain=2 -z`: staged, unstaged, untracked, renamed, conflicted. Ignored paths are omitted. |
| Stage / unstage | `StageAsync`, `UnstageAsync` | One path. Paths outside the repository are rejected. |
| Stage all / unstage all | `StageAllAsync`, `UnstageAllAsync` | Return the number of paths targeted. |
| Commit | `CommitAsync` | Message through stdin, recorded verbatim. Requires a staged change and a complete identity. |
| Push / pull | `PushAsync`, `PullAsync` | Upstream required. Pull is fast-forward only. Never prompts for credentials. |
| Rebase | `RebaseAsync` | Refuses a dirty tree. Aborts automatically on conflict and reports the state either way. |
| Clone | `EnsureCloneAsync` | Clones when absent, reuses a matching `origin`, and never deletes existing content. |
| Scan | `ScanForRepositoriesAsync` | Depth, per-level folder cap, hidden-folder exclusion and name patterns. Does not descend into a repository. |

## Failure codes

Every code is in `GitFailureCode`. The values are stable.

| Code | Meaning |
| --- | --- |
| `InvalidRepository` | Path is empty, missing, or not a repository. |
| `GitUnavailable` | No `git` executable. The message names the searched PATH entries. |
| `InvalidArgument` | The caller passed an unusable argument. |
| `PathOutsideRepository` | A path resolves outside the repository root. |
| `NoUpstream` | No upstream tracking branch, or detached HEAD. |
| `AuthenticationFailed` | The remote asked for credentials that are not available. |
| `NonFastForward` | A push was rejected because it is not a fast-forward. |
| `RemoteRejected` | The remote rejected the request for another reason. |
| `FastForwardRequired` | A fast-forward-only pull could not complete. |
| `WorkingTreeDirty` | Rebase refused a dirty tree. |
| `NothingStaged` | Commit with no staged changes. |
| `IdentityNotConfigured` | No complete `user.name` / `user.email`. |
| `RebaseConflictAborted` | Rebase conflicted and was aborted; the repository was restored. |
| `RebaseFailed` | Rebase failed without a clean restore. |
| `CloneConflict` | The clone target conflicts with existing content. Nothing was deleted. |
| `CloneFailed` | The clone command failed. |
| `CommandFailed` | A git command failed with no more specific classification. |
| `Timeout` | The command exceeded its timeout and its process tree was terminated. |

Cancellation is not a result code. A cancelled call throws `OperationCanceledException`, and the git process tree is terminated before the exception propagates.

## Supported git version

Git **2.26 or later** is required. `GetIdentityAsync` uses `git config --show-scope`, which first appeared in 2.26. The test suite records the git version of each run.

## Behavior notes

- The client never writes git configuration.
- Commits run with `--no-verify`, so git hooks do not run. The previous LibGit2Sharp implementation never ran them either.
- Every command runs with `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `LC_ALL=C` and `LANG=C`, and reads use `GIT_OPTIONAL_LOCKS=0`.
- Paths are passed as literal pathspecs, so `*` and `[` in a file name are not globs.
- Scanning and root discovery check for a `.git` directory, as the backend always has. Only `IsRepositoryAsync` also accepts a `.git` file.
