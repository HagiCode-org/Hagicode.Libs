using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using HagiCode.Libs.Tests.Shared;
using NUnit.Framework;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Gate and factories for the real-CLI tests of the Antigravity feature classes. A real test runs only when
/// <c>HAGICODE_REAL_CLI_TESTS=1</c> (or <c>true</c>) and <c>agy</c> is on PATH. Otherwise it is reported as skipped with the
/// reason. Nothing here mocks the CLI: a real test that cannot pass against agy must fail.
/// </summary>
internal static class AntigravityRealCli
{
    public const string GateVariable = "HAGICODE_REAL_CLI_TESTS";

    private static readonly string[] ExecutableCandidates = ["agy"];

    /// <summary>Skips the calling test unless the gate is on and agy is found. Returns the resolved agy path otherwise.</summary>
    public static string RequireAgy()
    {
        ExternalGate.Require(GateVariable);

        var executablePath = new CliExecutableResolver().ResolveFirstAvailablePath(ExecutableCandidates);
        if (executablePath is null)
        {
            Assert.Ignore("agy is not on PATH. Install the Antigravity CLI to run this real-CLI test.");
        }

        return executablePath!;
    }

    /// <summary>
    /// Optional model for real runs, from <c>HAGICODE_REAL_AGY_MODEL</c>. Unset, agy starts on its own default model, which can be
    /// quota-blocked on some accounts; set it to a native id such as <c>claude-sonnet-4-6</c> to run the same tests on that model.
    /// </summary>
    public static string? RealModel =>
        Environment.GetEnvironmentVariable("HAGICODE_REAL_AGY_MODEL") is { Length: > 0 } model ? model : null;

    /// <summary>The production provider, wired to the real process manager and the real executable resolver.</summary>
    public static AntigravityProvider CreateProvider() =>
        new(new CliExecutableResolver(), new CliProcessManager(), null);

    /// <summary>A raw ACP session client over a real <c>agy acp --stdio</c> process, for tests that need the wire (images, steer).</summary>
    public static AcpSessionClient CreateSessionClient(string agyPath, string workingDirectory) =>
        new(new SubprocessAcpTransport(
            new CliProcessManager(),
            new ProcessStartContext
            {
                ExecutablePath = agyPath,
                Arguments = ["acp", "--stdio"],
                WorkingDirectory = workingDirectory
            }));

    /// <summary>A 1x1 solid red PNG (generated and checked with a valid CRC), for the image feature's real check.</summary>
    public const string RedPixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    /// <summary>
    /// Sends one prompt on a raw session and returns the agent text streamed for it. Notifications are read in the background
    /// while the prompt runs, and for one second after it returns, so late chunks are included.
    /// </summary>
    public static async Task<string> PromptForTextAsync(
        AcpSessionClient client,
        string sessionId,
        string prompt,
        IReadOnlyList<AcpImageBlock>? images = null)
    {
        using var collectionCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var text = new StringBuilder();
        var collector = Task.Run(async () =>
        {
            try
            {
                await foreach (var notification in client.ReceiveNotificationsAsync(collectionCancellation.Token))
                {
                    if (AgentMessageChunkText(notification) is { } chunk)
                    {
                        lock (text)
                        {
                            text.Append(chunk);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await client.SendPromptAsync(sessionId, prompt, images ?? Array.Empty<AcpImageBlock>()).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await collectionCancellation.CancelAsync().ConfigureAwait(false);
        await collector.ConfigureAwait(false);

        lock (text)
        {
            return text.ToString();
        }
    }

    private static string? AgentMessageChunkText(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            !notification.Parameters.TryGetProperty("update", out var update) ||
            update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("sessionUpdate", out var kind) ||
            kind.GetString() != "agent_message_chunk" ||
            !update.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return text.GetString();
    }
}
