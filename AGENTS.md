# HagiCode.Libs - Agent Configuration

## Root Configuration

Inherits all behavior from `/AGENTS.md` at the monorepo root. Local rules extend or override the root file for this repository.

## Project Context

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration. Contains multiple project libraries for transport, prompts, providers, skills, and Git exploration.

## Working Directory

Run commands from `repos/Hagicode.Libs/`.

## Key Commands

```bash
dotnet build HagiCode.Libs.slnx
dotnet test HagiCode.Libs.slnx
```

## Key Paths

- `src/HagiCode.Libs.Core/`: transport, process management, executable discovery
- `src/HagiCode.Libs.Prompts/`: Handlebars prompt catalog
- `src/HagiCode.Libs.Providers/`: provider abstractions (Claude, Copilot, Codex, Gemini, etc.)
- `src/HagiCode.Libs.Skills/`: skills infrastructure and online API client
- `src/HagiCode.Libs.Imaging/`: in-process AVIF conversion with bundled libvips native libraries (tests: `tests/HagiCode.Libs.Imaging.Tests/`, backend guide: `docs/avif-backend-integration-guide.md`)
- `src/HagiCode.Libs.Exploration/`: Git repository discovery and state inspection
- `tests/`: NUnit coverage for each project; shared test settings, packages and global usings are in `tests/Directory.Build.props`

## Agent Guidelines

- Use `dotnet` CLI for all build and test operations.
- Keep library abstractions provider-agnostic; new provider implementations should extend existing base classes.
- Treat the solution file (`.slnx`) as the authoritative build entrypoint.
- If changing public API surfaces, update corresponding NUnit tests.

## References

- `README.md`
