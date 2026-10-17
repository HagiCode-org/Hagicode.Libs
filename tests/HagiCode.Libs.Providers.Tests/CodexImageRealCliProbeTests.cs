using System.IO.Compression;
using System.Text;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Internal;
using ManagedCode.CodexSharpSDK.Models;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// End-to-end probe against a real <c>codex</c> executable: one interactive (app-server) turn and one exec turn, each with a
/// small solid-blue PNG. The answer must name the colour, which proves the image reached the model on both channels. The
/// test skips when no Codex CLI is on <c>PATH</c>, and it runs only when <c>HAGICODE_REAL_CLI_TESTS</c> is <c>1</c> or <c>true</c>
/// because each run spends model usage.
/// </summary>
[Category("Integration")]
[Category("Slow")]
public sealed class CodexImageRealCliProbeTests
{
    private const string SwitchVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string Prompt = "What single colour fills this image? Answer with one lowercase English word and nothing else.";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(3);

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task App_server_turn_carries_the_image_and_the_answer_names_its_colour()
    {
        ExternalGate.Require(SwitchVariable);
        var executable = RequireCodexExecutable();
        using var workspace = new ScratchDirectory();

        await using var appServer = await CodexAppServerSession.OpenAsync(
            executable,
            CodexAppServerSession.BuildEnvironment(null, null, null),
            workspace.Path,
            (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
            TimeSpan.FromMinutes(1),
            null,
            CancellationToken.None);
        var thread = new CodexThread(
            null,
            new CodexOptions { CodexExecutablePath = executable },
            new ThreadOptions { WorkingDirectory = workspace.Path, SandboxMode = SandboxMode.ReadOnly },
            null,
            appServer);

        var answer = await RunForFinalMessageAsync(await thread.RunStreamedAsync(Prompt, [CreateBlueImage()]));

        answer.ShouldContain("blue", Case.Insensitive);
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Exec_turn_carries_the_image_and_the_answer_names_its_colour()
    {
        ExternalGate.Require(SwitchVariable);
        var executable = RequireCodexExecutable();
        using var workspace = new ScratchDirectory();
        using var client = new CodexClient(new CodexClientOptions
        {
            AutoStart = true,
            CodexOptions = new CodexOptions { CodexExecutablePath = executable }
        });
        var thread = client.StartThread(new ThreadOptions
        {
            WorkingDirectory = workspace.Path,
            SandboxMode = SandboxMode.ReadOnly,
            SkipGitRepoCheck = true,
            Ephemeral = true,
        });

        var answer = await RunForFinalMessageAsync(await thread.RunStreamedAsync(Prompt, [CreateBlueImage()]));

        answer.ShouldContain("blue", Case.Insensitive);
    }

    private static string RequireCodexExecutable()
    {
        if (!CodexCliLocator.TryResolvePathExecutable(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), out var executable))
        {
            Assert.Ignore("No codex executable is on PATH, so the image probe is skipped.");
        }

        return executable;
    }

    private static async Task<string> RunForFinalMessageAsync(RunStreamedResult result)
    {
        string? finalMessage = null;
        using var timeout = new CancellationTokenSource(RunTimeout);
        await foreach (var threadEvent in result.Events.WithCancellation(timeout.Token))
        {
            switch (threadEvent)
            {
                case ItemCompletedEvent { Item: AgentMessageItem message }:
                    finalMessage = message.Text;
                    break;
                case TurnFailedEvent failed:
                    throw new InvalidOperationException($"The codex turn failed: {failed.Error.Message}");
                case ThreadErrorEvent error:
                    throw new InvalidOperationException($"The codex thread reported an error: {error.Message}");
            }
        }

        finalMessage.ShouldNotBeNull();
        return finalMessage;
    }

    private static CodexImageAttachment CreateBlueImage() =>
        new("image/png", CreateSolidPng(128, 128, red: 0x00, green: 0x00, blue: 0xFF));

    /// <summary>Encodes a solid-colour 8-bit RGB PNG, so the probe needs no imaging dependency.</summary>
    private static byte[] CreateSolidPng(int width, int height, byte red, byte green, byte blue)
    {
        var scanlines = new MemoryStream();
        using (var zlib = new ZLibStream(scanlines, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + (width * 3)];
            for (var x = 0; x < width; x++)
            {
                row[1 + (x * 3)] = red;
                row[2 + (x * 3)] = green;
                row[3 + (x * 3)] = blue;
            }

            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", scanlines.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        destination.Write(length);
        destination.Write(typeBytes);
        destination.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        destination.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-codex-probe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
