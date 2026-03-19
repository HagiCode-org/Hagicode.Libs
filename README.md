# HagiCode.Libs

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration.

## Projects

- `src/HagiCode.Libs.Core` - transport, process management, executable discovery, and runtime environment resolution.
- `src/HagiCode.Libs.Providers` - provider abstractions, the Claude Code provider, and optional DI registration.
- `src/HagiCode.Libs.Exploration` - Git repository discovery and state inspection.
- `tests/*` - xUnit coverage for each project.

## Getting started

```bash
cd repos/Hagicode.Libs
dotnet build HagiCode.Libs.sln
dotnet test HagiCode.Libs.sln
```

## Design goals

- Zero heavy framework dependencies in the reusable libraries.
- Cross-platform CLI process management for Windows, macOS, and Linux.
- Stream-oriented provider integrations that can power higher-level HagiCode tools.
- Simple provider registration for DI and non-DI consumers.
