---
public: true
multilingual: false
style: product
---

# README requirements

## Product focus

- Introduce `HagiCode.Libs` as a set of .NET 10 NuGet libraries that give applications ready-made integrations with AI coding CLIs (Claude Code, Codex, Copilot, OpenCode, Pi and the other providers in `src/`), plus prompt catalog loading and rendering, a typed skills online API client, in-process AVIF image conversion and Git repository exploration.
- Address .NET developers who add the `HagiCode.Libs.*` packages to their applications; this repository is a developer-facing library, so usage-level detail belongs in the README.
- Open with the workspace description, the license and package badges, and the project list, then show how to use the libraries.

## Required content

- Keep the projects list, getting started, provider usage, shared pooling behavior, skills package usage and design goals at the usage level the current README carries.
- Link to the `docs/` guides for detail on AVIF, Git backend, skills API and CLI discovery instead of copying them.
- Keep the license badge and statement consistent with `LICENSE`.

## Exclusions

- Exclude NuGet API keys, tokens, secret values, internal hosts, machine-specific paths and private links.
- Keep repository maintenance instructions out of the README: NuGet publishing workflow, cross-platform CLI discovery validation lanes, real-CLI test recipes, internal architecture, CI and the contribution workflow.
- Do not claim support for a provider or platform that `src/` and `tests/` do not show.
