# Test tiers and run profiles

Hagicode.Libs tests are NUnit 4.6.1 tests. Each test carries zero or more tier markers. A test with no marker is a unit test. The profiles below select tests with one filter each, and the default `dotnet test` runs the `regression` profile.

Run every command from `repos/Hagicode.Libs/`. Counts are measured on 2026-10-15 with all switches unset.

## Profiles

| Profile | Selects | Command | Tests |
| --- | --- | --- | --- |
| `critical` | `CriticalRegression` | `dotnet test HagiCode.Libs.slnx -p:TestProfile=critical` | 21 |
| `unit` | no `Integration`, `External` or `Slow` | `dotnet test HagiCode.Libs.slnx -p:TestProfile=unit` | 1153 |
| `regression` (default) | no `External` or `Slow` | `dotnet test HagiCode.Libs.slnx` | 1363 |
| `full` | every test | `dotnet test HagiCode.Libs.slnx -p:TestProfile=full` | 1440 |

Wall-clock of the test runs, per project, summed over the six projects (Libs has no profile before this change, so the baseline is the full run):

| Profile | Before (baseline full run) | After (`--no-build`) | Change |
| --- | --- | --- | --- |
| `full` | 62 s, 1389 tests | 46 s, 1440 tests | -26 % |
| `regression` (default) | 62 s (no profile existed), 1389 tests | 30 s, 1363 tests | -52 % |
| `unit` | not measured before | 22 s, 1153 tests | |

The baseline wall-clock includes build time for projects rebuilt in that run, so the comparison is approximate.

The profile is an MSBuild property defined in `tests/Directory.Build.props`. A filter passed on the command line replaces the profile. For example, the CI step that selects the real-CLI invocation contract is `dotnet test HagiCode.Libs.slnx --filter "TestCategory=RealCliInvocationContract"`.

## NUnit filter expressions

Categories are NUnit `[Category]` attributes, so the filter property is `TestCategory`:

- `TestCategory=CriticalRegression`
- `TestCategory!=Integration&TestCategory!=External&TestCategory!=Slow` (the `unit` profile)
- `TestCategory!=External&TestCategory!=Slow` (the `regression` profile)

## Markers

| Marker | Meaning |
| --- | --- |
| `Integration` | The fixture starts an in-process host, a child process, a listening socket or a database. |
| `CriticalRegression` | A fast, hermetic smoke test. It must not carry `Slow` or `External`, and each test project keeps at least one. |
| `External` | The test needs a real CLI or online API. It reports Skipped with a reason that names its switch unless the switch is set. |
| `Slow` | The test takes more than 5 seconds in a `full` run. |

Existing categories stay and keep their meaning: `RealCli`, `RealOnlineApi`, `RealCliInvocationContract`, `RealCliWindowsZhCnWhitespaceShim`, `RealCliDiscovery` (real-install discovery, `HAGICODE_REAL_CLI_TESTS`, see `docs/agent-cli-discovery-guide.md`) and `WindowsOnly`. Every `RealCli` and `RealOnlineApi` test also carries `External`.

## Switch variables

A switch is on when its value is `1` or `true` (case-insensitive).

| Variable | Gates |
| --- | --- |
| `HAGICODE_REAL_CLI_TESTS` | Real-CLI tests in ConsoleTesting and Providers, the `RealCliInvocationContract` tests, and the `RealCliDiscovery` tests in Core |
| `HAGICODE_REAL_CLI_COPILOT_SESSION_TESTS` | Copilot session tests (also needs `HAGICODE_REAL_CLI_TESTS`) |
| `HAGICODE_LIBS_REAL_CLI_USER_QUESTION` | Real-CLI user-question test for Claude Code |
| `HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION` | Real-CLI user-question test for Pi |
| `HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION` | Real-CLI user-question test for Omp |
| `HAGICODE_REAL_ONLINE_API_TESTS` | Online API tests in Skills (`RealOnlineApiIntegrationTests`) |

The gate is `ExternalGate.Require(switchName)` in `tests/Shared/TestTiers.cs`. It is the only switch parser.

## Guard

`tests/Shared/TestTierGuardTests.cs` is compiled into every Libs test project and fails when:

1. a category is neither a tier marker nor a documented legacy category,
2. a `RealCli` or `RealOnlineApi` test lacks `External`,
3. a `CriticalRegression` test also carries `Slow` or `External`,
4. a project has no `CriticalRegression` test.

## Adding a critical test

Add `[Category("CriticalRegression")]` to a fast test that starts no process, opens no socket or database, and reads no switch. Run `dotnet test HagiCode.Libs.slnx -p:TestProfile=critical` to check that the new test runs. The guard will fail if it is marked `Slow` or `External`.

## Adding a provider row

A provider that is shared by the console dispatch contract or the ACP contract gets one row, not a copy of the shared tests.

Console dispatch (`ConsoleTesting.Tests`):

1. In the provider's `*ConsoleIntegrationTests.cs`, keep the fake provider and add a nested class that derives from `ProviderConsoleDispatchRow`. Fill in the definition, the default-suite scenario names, the expected help text, the runtime and explicit option arguments, and their assertions.
2. Expose it with `internal static ProviderConsoleDispatchRow CreateDispatchRow() => new <Provider>DispatchRow();`.
3. Add `["<default provider name>"] = <Provider>ConsoleIntegrationTests.CreateDispatchRow` to `RowFactories` in `ProviderConsoleDispatchContractTests.cs`.

ACP providers (`Providers.Tests`):

1. In the provider's `*ProviderTests.cs`, add a nested class that derives from `AcpProviderContractRow` and implements the ping, the execution and the multi-line fixture with the file's own `CreateProvider` helper.
2. Expose it with `internal static AcpProviderContractRow CreateContractRow() => new <Provider>AcpContractRow();`.
3. Add the row to `RowFactories` in `AcpProviderContractTests.cs`.

The completeness tests (`Every_provider_console_definition_has_exactly_one_row` and `Every_acp_provider_has_exactly_one_row_or_a_recorded_exemption`) fail when a provider has no row. A provider that cannot use the contract goes into the `Exemptions` table with its reason. Run `dotnet test <project> -p:TestProfile=full --filter "FullyQualifiedName~ProviderConsoleDispatchContractTests|FullyQualifiedName~AcpProviderContractTests"` to check the new row.

## Real-resource runs

```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test HagiCode.Libs.slnx --filter "TestCategory=RealCliInvocationContract"
HAGICODE_REAL_ONLINE_API_TESTS=1 dotnet test HagiCode.Libs.slnx --filter "FullyQualifiedName~RealOnlineApiIntegrationTests"
```

The first command selects 5 tests. They need the real CLIs installed and authenticated. The second needs network access.
