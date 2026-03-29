using System.Text.Json;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Minimal standalone runner that external hosts can reuse to keep the shared OpenCode runtime alive.
/// </summary>
public static class OpenCodeStandaloneServerProgram
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = ParseArgs(args);
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var result = await host.WarmupAsync(options, cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        }));

        if (result.Status != OpenCodeStandaloneServerStatus.Ready)
        {
            return 1;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static OpenCodeStandaloneServerOptions ParseArgs(IReadOnlyList<string> args)
    {
        string? executablePath = null;
        string? baseUrl = null;
        string? workingDirectory = null;
        string? workspace = null;
        var extraArguments = new List<string>();
        var environmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal);
        TimeSpan? startupTimeout = null;
        TimeSpan? requestTimeout = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--base-url=", StringComparison.Ordinal))
            {
                baseUrl = arg["--base-url=".Length..];
                continue;
            }

            if (arg.StartsWith("--working-directory=", StringComparison.Ordinal))
            {
                workingDirectory = arg["--working-directory=".Length..];
                continue;
            }

            if (arg.StartsWith("--workspace=", StringComparison.Ordinal))
            {
                workspace = arg["--workspace=".Length..];
                continue;
            }

            if (arg.StartsWith("--executable-path=", StringComparison.Ordinal))
            {
                executablePath = arg["--executable-path=".Length..];
                continue;
            }

            if (arg.StartsWith("--startup-timeout-seconds=", StringComparison.Ordinal) &&
                int.TryParse(arg["--startup-timeout-seconds=".Length..], out var startupSeconds))
            {
                startupTimeout = TimeSpan.FromSeconds(startupSeconds);
                continue;
            }

            if (arg.StartsWith("--request-timeout-seconds=", StringComparison.Ordinal) &&
                int.TryParse(arg["--request-timeout-seconds=".Length..], out var requestSeconds))
            {
                requestTimeout = TimeSpan.FromSeconds(requestSeconds);
                continue;
            }

            if (arg.StartsWith("--env=", StringComparison.Ordinal))
            {
                var assignment = arg["--env=".Length..];
                var separatorIndex = assignment.IndexOf('=');
                if (separatorIndex > 0)
                {
                    environmentVariables[assignment[..separatorIndex]] = assignment[(separatorIndex + 1)..];
                }
                continue;
            }

            if (arg.StartsWith("--extra-arg=", StringComparison.Ordinal))
            {
                extraArguments.Add(arg["--extra-arg=".Length..]);
                continue;
            }

        }

        return new OpenCodeStandaloneServerOptions
        {
            ExecutablePath = executablePath,
            BaseUrl = baseUrl,
            WorkingDirectory = workingDirectory,
            Workspace = workspace,
            StartupTimeout = startupTimeout,
            RequestTimeout = requestTimeout,
            EnvironmentVariables = environmentVariables,
            ExtraArguments = extraArguments,
        };
    }
}
