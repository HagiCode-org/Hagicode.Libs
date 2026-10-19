# HagiCode.Libs - Agent Configuration

## Root Configuration

Inherits all behavior from `/AGENTS.md` at the monorepo root. Local rules extend or override the root file for this repository.

## Project Context

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration. Contains multiple project libraries for transport, prompts, providers, skills, imaging, and console testing.

## Working Directory

Run commands from `repos/Hagicode.Libs/`.

## Key Commands

```bash
dotnet build HagiCode.Libs.slnx
dotnet test HagiCode.Libs.slnx                                  # regression profile (default): skips External and Slow
dotnet test HagiCode.Libs.slnx -p:TestProfile=full              # every test, including real-CLI and online-API tests (skipped unless their switch is set)
dotnet test HagiCode.Libs.slnx -p:TestProfile=unit              # hermetic: no Integration, External or Slow
dotnet test HagiCode.Libs.slnx -p:TestProfile=critical          # CriticalRegression smoke gate
```

Tier markers, switch variables and filter expressions: `docs/testing-tiers.md`.

## Key Paths

- `src/HagiCode.Libs.Core/`: transport, process management, executable discovery
- `src/HagiCode.Libs.Prompts/`: Handlebars prompt catalog
- `src/HagiCode.Libs.Providers/`: provider abstractions (Claude, Copilot, Codex, Gemini, etc.)
- `src/HagiCode.Libs.Skills/`: skills infrastructure and online API client
- `src/HagiCode.Libs.Git/`: provider-agnostic git wrapper over the `git` CLI (`IGitClient`, typed results and failure codes; tests: `tests/HagiCode.Libs.Git.Tests/`, package guide: `src/HagiCode.Libs.Git/README.md`, backend guide: `docs/git-backend-integration-guide.md`)
- `src/HagiCode.Libs.Imaging/`: in-process AVIF conversion with bundled libvips native libraries (tests: `tests/HagiCode.Libs.Imaging.Tests/`, backend guide: `docs/avif-backend-integration-guide.md`)
- `src/HagiCode.Libs.ConsoleTesting/`: shared harness for the per-provider `*.Console` projects (tests: `tests/HagiCode.Libs.ConsoleTesting.Tests/`)
- `src/HagiCode.Libs.Providers/OpenCode/`, `.../Codex/AppServer/`: OpenCode (2.0 authenticated runtimes, question replies via the form API) and Codex app-server providers; both, like Claude Code, publish `agent_work_snapshot` messages for active child-agent work (`HagiCode.Libs.Core.AgentWork`; snapshot contract in `README.md`)
- `src/HagiCode.Libs.CiSetup.Console/`: installs and verifies agent CLIs for the CI discovery lanes (tests: `tests/HagiCode.Libs.CiSetup.Tests/`, usage in `README.md`)
- `global.json` pins the .NET SDK; `Directory.Packages.props` centrally manages NuGet package versions, so add or bump package versions there, not in individual `.csproj` files
- `tests/`: NUnit coverage for each project; shared test settings, packages and global usings are in `tests/Directory.Build.props`
- `tests/HagiCode.Libs.Providers.Tests/Fixtures/`: recorded CLI/server fixtures (Pi, Omp, Reasonix, Codex app-server, OpenCode `v2.0.32`, tool calls); new fixture directories must be copied to the output in the `.csproj` (`CopyToOutputDirectory`)
- `.github/workflows/`: NuGet publish (`nuget-publish.yml`), release draft, and cross-platform/integration lanes for Git, AVIF imaging, CLI discovery and online API; `scripts/git/` (timing aggregation) and `scripts/imaging/` (native layout check) support the Git and Imaging lanes

## Agent Guidelines

- Use `dotnet` CLI for all build and test operations.
- Keep library abstractions provider-agnostic; new provider implementations should extend existing base classes.
- Treat the solution file (`.slnx`) as the authoritative build entrypoint.
- If changing public API surfaces, update corresponding NUnit tests.

## References

- `README.md`
