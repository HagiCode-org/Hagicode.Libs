using HagiCode.Libs.Core.Transport;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// The lifecycle and whitespace-fidelity behavior shared by the ACP providers, asserted once with one row per provider.
/// Each row is supplied by its provider's test file, next to that provider's fake session client, so provider-specific
/// expectations stay in data. The completeness test fails when an ACP provider has neither a row nor an exemption.
/// </summary>
public sealed class AcpProviderContractTests
{
    private static readonly IReadOnlyDictionary<string, Func<AcpProviderContractRow>> RowFactories =
        new Dictionary<string, Func<AcpProviderContractRow>>(StringComparer.Ordinal)
        {
            ["kimi"] = KimiProviderTests.CreateContractRow,
            ["gemini"] = GeminiProviderTests.CreateContractRow,
            ["junie"] = JunieProviderTests.CreateContractRow,
            ["codebuddy"] = CodebuddyProviderTests.CreateContractRow,
            ["qodercli"] = QoderCliProviderTests.CreateContractRow,
            ["kiro-cli"] = KiroProviderTests.CreateContractRow,
            ["hermes"] = HermesProviderTests.CreateContractRow,
        };

    /// <summary>
    /// ACP providers that are not converted to this contract, with the reason. Their tests use a different fake or
    /// harness and are left in their own files.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Exemptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DeepAgentsProvider"] = "Own ACP harness (DeepAgents toolcall diagnostics); not one of the seven contract providers of this change.",
        ["DshProvider"] = "Has no shared ACP lifecycle test in its file; its ACP session client is covered by its own tests.",
        ["ReasonixProvider"] = "Own scripted ACP transport (ReasonixScriptedAcpTransport); not one of the seven contract providers of this change.",
    };

    public static IEnumerable<TestCaseData> ProviderRows() =>
        RowFactories.Keys.Select(providerId => new TestCaseData(providerId));

    [TestCaseSource(nameof(ProviderRows))]
    public async Task PingAsync_returns_failure_when_executable_is_missing(string providerId)
    {
        var row = CreateRow(providerId);

        var observation = await row.PingWithMissingExecutableAsync();

        observation.Success.ShouldBeFalse();
        observation.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        observation.ErrorMessage.ShouldContain("not found");
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds(string providerId)
    {
        var row = CreateRow(providerId);

        var observation = await row.PingAsync();

        observation.Success.ShouldBeTrue();
        observation.Version.ShouldNotBeNullOrWhiteSpace();
        observation.Version.ShouldContain(row.VersionFragment);
        foreach (var fragment in row.ExtraVersionFragments)
        {
            observation.Version.ShouldContain(fragment);
        }

        observation.InitializeCalls.ShouldBe(1);
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_via_internal_cancellation(string providerId)
    {
        var row = CreateRow(providerId);

        var observation = await row.ExecuteWithoutNotificationsAsync(promptOutputText: null);

        observation.MessageTypes.ShouldBe(["session.started", "assistant", "terminal.completed"]);
        observation.AssistantText.ShouldBe("pong");
    }

    [TestCaseSource(nameof(ProviderRows))]
    public async Task ExecuteAsync_falls_back_to_multiline_prompt_result_without_flattening_line_breaks(string providerId)
    {
        var row = CreateRow(providerId);

        var observation = await row.ExecuteWithoutNotificationsAsync(row.MultilineFallbackText);

        observation.MessageTypes.ShouldBe(["session.started", "assistant", "terminal.completed"]);
        observation.AssistantText.ShouldBe(row.MultilineFallbackText);
        observation.TerminalText.ShouldBe(row.MultilineFallbackText);
    }

    [Test]
    public void Every_acp_provider_has_exactly_one_row_or_a_recorded_exemption()
    {
        var acpProviderTypeNames = DiscoverAcpProviderTypeNames();
        var rowTypeNames = RowFactories.Values.Select(factory => factory().ProviderType.Name).ToHashSet(StringComparer.Ordinal);

        acpProviderTypeNames.Except(rowTypeNames).Except(Exemptions.Keys).ShouldBeEmpty(
            "ACP providers without a contract row (add a row or an exemption with its reason)");
        rowTypeNames.Except(acpProviderTypeNames).ShouldBeEmpty("contract rows for types that are not ACP providers");
        Exemptions.Keys.Except(acpProviderTypeNames).ShouldBeEmpty("exemptions for types that are not ACP providers");
    }

    private static AcpProviderContractRow CreateRow(string providerId)
    {
        return RowFactories[providerId]();
    }

    /// <summary>
    /// An ACP provider is a provider namespace that also has an ACP message mapper. The provider class is the mapper's
    /// namespace plus "Provider".
    /// </summary>
    private static HashSet<string> DiscoverAcpProviderTypeNames()
    {
        var assembly = typeof(ProviderRegistry).Assembly;

        var providerTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is null || !type.Namespace.StartsWith("HagiCode.Libs.Providers.", StringComparison.Ordinal)
                || !type.Name.EndsWith("AcpMessageMapper", StringComparison.Ordinal))
            {
                continue;
            }

            var providerTypeName = type.Name[..^"AcpMessageMapper".Length] + "Provider";
            if (assembly.GetTypes().Any(candidate => candidate.Namespace == type.Namespace && candidate.Name == providerTypeName))
            {
                providerTypeNames.Add(providerTypeName);
            }
        }

        return providerTypeNames;
    }
}

/// <summary>What a Ping observed. InitializeCalls counts the initialize requests the fake session client received.</summary>
public sealed record AcpPingObservation(bool Success, string? ErrorMessage, string? Version, int InitializeCalls);

/// <summary>What one ExecuteAsync run emitted: the message types, and the text of the assistant and terminal messages.</summary>
public sealed record AcpExecutionObservation(IReadOnlyList<string> MessageTypes, string? AssistantText, string? TerminalText)
{
    public static async Task<AcpExecutionObservation> CollectAsync(IAsyncEnumerable<CliMessage> messages)
    {
        var collected = new List<CliMessage>();
        await foreach (var message in messages)
        {
            collected.Add(message);
        }

        return new AcpExecutionObservation(
            collected.Select(static message => message.Type).ToArray(),
            TextAt(collected, 1),
            TextAt(collected, 2));
    }

    private static string? TextAt(IReadOnlyList<CliMessage> messages, int index)
    {
        return index < messages.Count
            ? messages[index].Content.GetProperty("text").GetString()
            : null;
    }
}

/// <summary>
/// One ACP provider's data for the shared contract. Each provider test file implements this with its own
/// CreateProvider helper and fake session client.
/// </summary>
public abstract class AcpProviderContractRow
{
    /// <summary>The provider id, used as the first test-case argument.</summary>
    public abstract string ProviderId { get; }

    /// <summary>The production provider class, used by the completeness check.</summary>
    public abstract Type ProviderType { get; }

    /// <summary>A fragment the Ping version text must contain, normally the provider's CLI name.</summary>
    public abstract string VersionFragment { get; }

    /// <summary>Further fragments the Ping version text must contain (for example a bootstrap label).</summary>
    public virtual IReadOnlyList<string> ExtraVersionFragments => [];

    /// <summary>A multi-line prompt result that the fallback must keep intact.</summary>
    public abstract string MultilineFallbackText { get; }

    /// <summary>Pings a provider whose executable cannot be resolved.</summary>
    public abstract Task<AcpPingObservation> PingWithMissingExecutableAsync();

    /// <summary>Pings a provider whose bootstrap succeeds.</summary>
    public abstract Task<AcpPingObservation> PingAsync();

    /// <summary>
    /// Runs one prompt whose notification loop ends early, so the prompt result is the only text source.
    /// A null <paramref name="promptOutputText"/> keeps the fake's default output.
    /// </summary>
    public abstract Task<AcpExecutionObservation> ExecuteWithoutNotificationsAsync(string? promptOutputText);
}
