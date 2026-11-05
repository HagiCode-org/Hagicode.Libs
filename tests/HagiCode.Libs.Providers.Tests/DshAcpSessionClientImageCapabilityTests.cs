using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshAcpSessionClientImageCapabilityTests
{
    [Test]
    public async Task SupportsImageInput_is_false_before_initialize()
    {
        var client = new DshAcpSessionClient(new InitializeOnlyAcpSessionClient("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":true}}}"), "omniroute");

        client.SupportsImageInput.ShouldBeFalse();
        await client.DisposeAsync();
    }

    [TestCase("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":true,\"audio\":false}}}", true)]
    [TestCase("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":false}}}", false)]
    [TestCase("{\"agentCapabilities\":{\"promptCapabilities\":{}}}", false)]
    [TestCase("{\"agentCapabilities\":{}}", false)]
    [TestCase("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":\"yes\"}}}", false)]
    [TestCase("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":1}}}", false)]
    [TestCase("{}", false)]
    public async Task InitializeAsync_reads_prompt_image_capability_and_treats_anything_else_as_false(string initializeJson, bool expected)
    {
        var client = new DshAcpSessionClient(new InitializeOnlyAcpSessionClient(initializeJson), "omniroute");

        await client.InitializeAsync();

        client.SupportsImageInput.ShouldBe(expected);
        await client.DisposeAsync();
    }

    [Test]
    public async Task MarkImageInputUnsupported_downgrades_only_this_connection()
    {
        var first = new DshAcpSessionClient(new InitializeOnlyAcpSessionClient("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":true}}}"), "omniroute");
        var second = new DshAcpSessionClient(new InitializeOnlyAcpSessionClient("{\"agentCapabilities\":{\"promptCapabilities\":{\"image\":true}}}"), "omniroute");
        await first.InitializeAsync();
        await second.InitializeAsync();

        first.MarkImageInputUnsupported();

        first.SupportsImageInput.ShouldBeFalse();
        second.SupportsImageInput.ShouldBeTrue();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Test]
    public async Task SendPromptAsync_with_images_is_refused_by_clients_without_inline_support()
    {
        var client = new DshAcpSessionClient(new InitializeOnlyAcpSessionClient("{}"), "omniroute");
        var image = new AcpImageBlock("image/png", "AAEC");

        await Should.ThrowAsync<NotSupportedException>(() => client.SendPromptAsync("session-1", "describe", [image]));
        await client.DisposeAsync();
    }

    private sealed class InitializeOnlyAcpSessionClient(string initializeJson) : IAcpSessionClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonDocument.Parse(initializeJson).RootElement.Clone());
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

        public string? GetDiagnosticSummary() => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
