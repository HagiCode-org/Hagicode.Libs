using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Implements Claude Code CLI integration.
/// </summary>
public class ClaudeCodeProvider : ICliProvider<ClaudeCodeOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["claude", "claude-code"];
    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public ClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "claude-code";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        ClaudeCodeOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException("Unable to locate the Claude Code executable.");

        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = options.WorkingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

        await using var transport = CreateTransport(startContext);
        await transport.ConnectAsync(cancellationToken);
        await transport.SendAsync(CreatePromptMessage(prompt, options), cancellationToken);

        await foreach (var message in transport.ReceiveAsync(cancellationToken))
        {
            yield return message;
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
            var executablePath = _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "Claude Code executable was not found."
                };
            }

            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = ["--version"],
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(10)
                },
                cancellationToken);

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = result.ExitCode == 0,
                Version = result.ExitCode == 0 ? result.StandardOutput.Trim() : null,
                ErrorMessage = result.ExitCode == 0 ? null : result.StandardError.Trim()
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal virtual IReadOnlyList<string> BuildCommandArguments(ClaudeCodeOptions options)
    {
        var arguments = new List<string>
        {
            "--output-format",
            "stream-json",
            "--verbose",
            "--input-format",
            "stream-json"
        };

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.AddRange(["--model", options.Model]);
        }

        if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
        {
            arguments.AddRange(["--system-prompt", options.SystemPrompt]);
        }

        if (!string.IsNullOrWhiteSpace(options.AppendSystemPrompt))
        {
            arguments.AddRange(["--append-system-prompt", options.AppendSystemPrompt]);
        }

        if (options.MaxTurns is { } maxTurns)
        {
            arguments.AddRange(["--max-turns", maxTurns.ToString()]);
        }

        if (options.AllowedTools.Count > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', options.AllowedTools)]);
        }

        if (options.DisallowedTools.Count > 0)
        {
            arguments.AddRange(["--disallowedTools", string.Join(',', options.DisallowedTools)]);
        }

        if (!string.IsNullOrWhiteSpace(options.PermissionMode))
        {
            arguments.AddRange(["--permission-mode", options.PermissionMode]);
        }

        if (options.ContinueConversation)
        {
            arguments.Add("--continue");
        }

        if (!string.IsNullOrWhiteSpace(options.Resume))
        {
            arguments.AddRange(["--resume", options.Resume]);
        }

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            arguments.AddRange(["--session-id", options.SessionId]);
        }

        foreach (var directory in options.AddDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        if (!string.IsNullOrWhiteSpace(options.McpServersPath))
        {
            arguments.AddRange(["--mcp-config", options.McpServersPath]);
        }
        else if (options.McpServers.Count > 0)
        {
            arguments.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = options.McpServers })]);
        }

        foreach (var extraArgument in options.ExtraArgs)
        {
            arguments.Add($"--{extraArgument.Key}");
            if (extraArgument.Value is not null)
            {
                arguments.Add(extraArgument.Value);
            }
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        ClaudeCodeOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-csharp"
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            environment["ANTHROPIC_AUTH_TOKEN"] = options.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            environment["ANTHROPIC_BASE_URL"] = options.BaseUrl;
        }

        return environment;
    }

    /// <summary>
    /// Creates the transport used for one execution.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The transport instance.</returns>
    protected virtual ICliTransport CreateTransport(ProcessStartContext startContext)
    {
        return new SubprocessTransport(_processManager, startContext);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken);
    }

    private string? ResolveExecutablePath(ClaudeCodeOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
    }

    private static CliMessage CreatePromptMessage(string prompt, ClaudeCodeOptions options)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt
            },
            ["session_id"] = options.SessionId
        });

        return new CliMessage("user", payload);
    }
}
