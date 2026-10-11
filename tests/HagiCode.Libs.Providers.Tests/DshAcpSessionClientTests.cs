using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshAcpSessionClientTests
{
    [Fact]
    public async Task StartSessionAsync_sends_provider_and_model_as_dsh_selector_value()
    {
        var inner = new RecordingAcpSessionClient();
        var client = new DshAcpSessionClient(inner, "omniroute");

        await client.StartSessionAsync("/tmp/project", null, "claude/claude-haiku-5-5");

        var modelCall = inner.Calls.Single(static call => call.Method == "session/set_config_option");
        modelCall.Parameters.GetProperty("configId").GetString().ShouldBe("model");
        modelCall.Parameters.GetProperty("value").GetString().ShouldBe("[\"omniroute\",\"claude/claude-haiku-5-5\"]");
    }

    [Fact]
    public async Task StartSessionAsync_forwards_model_unsplit_and_trims_boundary_whitespace()
    {
        var inner = new RecordingAcpSessionClient();
        var client = new DshAcpSessionClient(inner, "  openrouter  ");

        await client.StartSessionAsync("/tmp/project", null, "  vendor/model/variant  ");

        inner.Calls.Single(static call => call.Method == "session/set_config_option")
            .Parameters.GetProperty("value").GetString().ShouldBe("[\"openrouter\",\"vendor/model/variant\"]");
    }

    [Fact]
    public async Task StartSessionAsync_skips_model_selection_when_model_is_blank()
    {
        var inner = new RecordingAcpSessionClient();
        var client = new DshAcpSessionClient(inner, "omniroute");

        await client.StartSessionAsync("/tmp/project", null, "   ");

        inner.Calls.Select(static call => call.Method).ShouldBe(["session/new"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task StartSessionAsync_rejects_model_without_provider_before_creating_session(string? provider)
    {
        var inner = new RecordingAcpSessionClient();
        var client = new DshAcpSessionClient(inner, provider);

        var exception = await Should.ThrowAsync<ArgumentException>(() => client.StartSessionAsync("/tmp/project", null, "claude/claude-haiku-5-5"));

        exception.Message.ShouldContain("DshOptions.Provider");
        inner.Calls.ShouldBeEmpty();
    }

    private sealed class RecordingAcpSessionClient : IAcpSessionClient
    {
        public List<(string Method, JsonElement Parameters)> Calls { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Task.FromResult(method == "session/new"
                ? JsonSerializer.SerializeToElement(new { sessionId = "session-1" })
                : JsonSerializer.SerializeToElement(new { }));
        }

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("DshAcpSessionClient owns session start.");
        }

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public string? GetDiagnosticSummary()
        {
            return null;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
