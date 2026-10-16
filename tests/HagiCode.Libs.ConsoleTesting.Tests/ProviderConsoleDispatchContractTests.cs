using HagiCode.Libs.ConsoleTesting;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

/// <summary>
/// The dispatch behavior shared by every provider console, asserted once with one row per provider.
/// Each row is supplied by its provider's test file (next to that provider's fake), so provider-specific
/// expectations stay in data. The completeness test fails when a console definition has no row.
/// </summary>
public sealed class ProviderConsoleDispatchContractTests
{
    /// <summary>Provider ids deliberately exempt from the completeness check, with the reason. Empty: every console has a row.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exemptions = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, Func<ProviderConsoleDispatchRow>> RowFactories =
        new Dictionary<string, Func<ProviderConsoleDispatchRow>>(StringComparer.Ordinal)
        {
            ["kimi"] = KimiConsoleIntegrationTests.CreateDispatchRow,
            ["gemini"] = GeminiConsoleIntegrationTests.CreateDispatchRow,
            ["junie"] = JunieConsoleIntegrationTests.CreateDispatchRow,
            ["codebuddy"] = CodebuddyConsoleIntegrationTests.CreateDispatchRow,
            ["qodercli"] = QoderCliConsoleIntegrationTests.CreateDispatchRow,
            ["kiro-cli"] = KiroConsoleIntegrationTests.CreateDispatchRow,
            ["reasonix"] = ReasonixConsoleIntegrationTests.CreateDispatchRow,
            ["opencode"] = OpenCodeConsoleIntegrationTests.CreateDispatchRow,
            ["copilot"] = CopilotConsoleIntegrationTests.CreateDispatchRow,
            ["hermes"] = HermesConsoleIntegrationTests.CreateDispatchRow,
            ["codex"] = CodexConsoleIntegrationTests.CreateDispatchRow,
            ["claude-code"] = ClaudeConsoleIntegrationTests.CreateDispatchRow,
            ["deepagents"] = DeepAgentsConsoleIntegrationTests.CreateDispatchRow,
            ["pi"] = PiConsoleIntegrationTests.CreateDispatchRow,
            ["dsh"] = DshConsoleIntegrationTests.CreateDispatchRow,
        };

    private static readonly string[] ForeignProviderNames = ["codex", "claude"];

    /// <summary>One case per provider. NUnit names each case after its provider id argument, for example Method("kimi").</summary>
    public static IEnumerable<TestCaseData> ProviderRows() =>
        RowFactories.Keys.Select(providerId => new TestCaseData(providerId));

    /// <summary>The provider rows, with the CriticalRegression smoke on the Kimi alias row.</summary>
    public static IEnumerable<TestCaseData> CriticalAliasRows() =>
        RowFactories.Keys.Select(providerId =>
        {
            var data = new TestCaseData(providerId);
            return providerId == "kimi" ? data.SetCategory("CriticalRegression") : data;
        });

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Default_suite_runs_every_scenario_of_the_provider(string providerId)
    {
        var row = CreateRow(providerId);
        using var output = new StringWriter();
        var runner = row.CreateRunner(output);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], row.Definition, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        foreach (var scenario in row.DefaultSuiteScenarioNames)
        {
            rendered.ShouldContain($"[PASS] {providerId} / {scenario}");
        }

        rendered.ShouldContain($"Summary: {row.DefaultSuiteScenarioNames.Count}/{row.DefaultSuiteScenarioNames.Count} passed");
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Help_text_names_the_provider_commands_and_its_options(string providerId)
    {
        var row = CreateRow(providerId);
        using var output = new StringWriter();
        var runner = row.CreateRunner(output);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], row.Definition, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        foreach (var option in row.ExpectedHelpText)
        {
            rendered.ShouldContain(option);
        }
    }

    [TestCaseSource(nameof(CriticalAliasRows))]
    public async Task Provider_aliases_are_accepted_by_the_ping_command(string providerId)
    {
        var row = CreateRow(providerId);

        foreach (var alias in row.Definition.AllowedProviderAliases)
        {
            using var output = new StringWriter();
            var runner = row.CreateRunner(output);

            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", alias], row.Definition, runner, output);

            exitCode.ShouldBe(0, $"alias '{alias}' should ping {providerId}");
            output.ToString().ShouldContain($"[PASS] {providerId} / Ping");
        }
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Foreign_provider_names_are_rejected_with_a_dedicated_console_message(string providerId)
    {
        var row = CreateRow(providerId);
        var foreignName = ForeignProviderNames.First(name => !string.Equals(name, providerId, StringComparison.Ordinal));
        using var output = new StringWriter();
        var runner = row.CreateRunner(output);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", foreignName], row.Definition, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Invalid_options_are_reported_as_configuration_failures(string providerId)
    {
        var row = CreateRow(providerId);
        using var output = new StringWriter();
        var runner = row.CreateRunner(output);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            row.InvalidOptionArguments.ToArray(),
            row.Definition,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain(row.InvalidOptionMessage);
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Runtime_options_reach_the_provider_for_the_full_suite(string providerId)
    {
        var row = CreateRow(providerId);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"{providerId}-console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            using var output = new StringWriter();
            var runner = row.CreateRunner(output);

            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                row.RuntimeOptionArguments(repositoryPath).ToArray(),
                row.Definition,
                runner,
                output);

            exitCode.ShouldBe(0);
            row.AssertRuntimeOptions(repositoryPath, output.ToString());
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task Explicit_model_and_auth_options_reach_the_provider(string providerId)
    {
        var row = CreateRow(providerId);
        using var output = new StringWriter();
        var runner = row.CreateRunner(output);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            row.ExplicitOptionArguments.ToArray(),
            row.Definition,
            runner,
            output);

        exitCode.ShouldBe(0);
        row.AssertExplicitOptions();
    }

    [Test]
    public void Every_provider_console_definition_has_exactly_one_row()
    {
        var consoleProviderIds = DiscoverConsoleProviderIds();
        var rowIds = RowFactories.Keys.ToHashSet(StringComparer.Ordinal);

        consoleProviderIds.Except(rowIds).Except(Exemptions.Keys).ShouldBeEmpty(
            "provider consoles without a dispatch contract row (add a row or an exemption with its reason)");
        rowIds.Except(consoleProviderIds).ShouldBeEmpty("dispatch rows without a provider console definition");

        foreach (var (providerId, factory) in RowFactories)
        {
            var row = factory();
            row.ProviderId.ShouldBe(providerId, $"row key '{providerId}' must match its definition's default provider name");
        }
    }

    private static ProviderConsoleDispatchRow CreateRow(string providerId)
    {
        return RowFactories[providerId]();
    }

    /// <summary>
    /// Reads the default provider name of every provider console definition (a static Instance property) in the
    /// referenced HagiCode.Libs console assemblies. Referenced assemblies are loaded explicitly, so a console that no
    /// test touches yet is still discovered.
    /// </summary>
    private static HashSet<string> DiscoverConsoleProviderIds()
    {
        var assemblies = new List<System.Reflection.Assembly> { typeof(ProviderConsoleDispatchContractTests).Assembly };
        foreach (var reference in typeof(ProviderConsoleDispatchContractTests).Assembly.GetReferencedAssemblies())
        {
            var name = reference.Name ?? string.Empty;
            if (name.StartsWith("HagiCode.Libs.", StringComparison.Ordinal) && name.EndsWith(".Console", StringComparison.Ordinal))
            {
                assemblies.Add(System.Reflection.Assembly.Load(reference));
            }
        }

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.Name.EndsWith("ConsoleDefinition", StringComparison.Ordinal))
                {
                    continue;
                }

                var instance = type.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instance?.GetValue(null) is ProviderConsoleDefinition definition)
                {
                    providerIds.Add(definition.DefaultProviderName);
                }
            }
        }

        return providerIds;
    }
}

/// <summary>
/// One provider console's data for the shared dispatch contract. A row owns its fake provider, so each
/// provider test file creates its row from its own fake.
/// </summary>
public abstract class ProviderConsoleDispatchRow
{
    public abstract ProviderConsoleDefinition Definition { get; }

    public string ProviderId => Definition.DefaultProviderName;

    /// <summary>Scenario names the default suite prints as [PASS] lines.</summary>
    public abstract IReadOnlyList<string> DefaultSuiteScenarioNames { get; }

    /// <summary>Text fragments the help text must show, for example an option signature such as "--model &lt;model&gt;".</summary>
    public abstract IReadOnlyList<string> ExpectedHelpText { get; }

    /// <summary>An option that requires a value, used to trigger the missing-value failure.</summary>
    public virtual string MissingValueOption => "--executable";

    /// <summary>Arguments that must fail as a configuration error, by default a value-less <see cref="MissingValueOption"/>.</summary>
    public virtual IReadOnlyList<string> InvalidOptionArguments => ["--test-provider-full", MissingValueOption];

    /// <summary>The message that the failed configuration must print.</summary>
    public virtual string InvalidOptionMessage => $"{MissingValueOption} requires a value";

    /// <summary>Creates a runner over a fresh fake provider that writes to the output.</summary>
    public abstract IProviderConsoleRunner CreateRunner(TextWriter output);

    /// <summary>Arguments for the full suite with a repository path and runtime overrides.</summary>
    public abstract IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath);

    /// <summary>Asserts what the fake provider received for <see cref="RuntimeOptionArguments"/>.</summary>
    public abstract void AssertRuntimeOptions(string repositoryPath, string renderedOutput);

    /// <summary>Arguments for the full suite with explicit model and authentication options.</summary>
    public abstract IReadOnlyList<string> ExplicitOptionArguments { get; }

    /// <summary>Asserts what the fake provider received for <see cref="ExplicitOptionArguments"/>.</summary>
    public abstract void AssertExplicitOptions();
}
