using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

internal static class RealCliInvocationTestHarness
{
    private static readonly string[] ActionableFailureMarkers =
    [
        "auth",
        "authenticate",
        "authentication",
        "credential",
        "credentials",
        "token",
        "api key",
        "login",
        "log in",
        "logged in",
        "sign in",
        "signin",
        "unauthorized",
        "forbidden",
        "permission",
        "not authenticated",
        "run",
        "configure",
        "setup",
        "configure",
        "rejected",
        "denied",
        "timeout",
        "timed out",
        "未登录",
        "登录",
        "认证",
        "授权",
        "凭据",
        "令牌",
        "密钥",
        "拒绝",
        "超时",
        "请先",
        "请运行"
    ];

    private static readonly string[] DiscoveryOnlyMarkers =
    [
        "not found",
        "was not found",
        "unable to locate",
        "could not find",
        "found on path"
    ];

    public static async Task<string> CaptureFailureMessageAsync<TOptions>(
        ICliProvider<TOptions> provider,
        TOptions options,
        string prompt,
        TimeSpan timeout)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        using var cancellationTokenSource = new CancellationTokenSource(timeout);
        var failureMessages = new List<string>();

        try
        {
            await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationTokenSource.Token))
            {
                var extracted = ExtractFailureCandidates(message);
                if (extracted.Count > 0)
                {
                    failureMessages.AddRange(extracted);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            failureMessages.Add($"The CLI invocation timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            failureMessages.Add(ex.Message);
        }

        var combined = string.Join(
            Environment.NewLine,
            failureMessages
                .Select(static message => message.Trim())
                .Where(static message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal));

        combined.ShouldNotBeNullOrWhiteSpace("The real CLI invocation should surface a failure message.");
        return combined;
    }

    public static void AssertActionableFailure(string providerName, string failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        failureMessage.ShouldNotContain("\uFEFF");
        failureMessage.ShouldNotContain("\uFFFD");

        foreach (var marker in DiscoveryOnlyMarkers)
        {
            failureMessage.ShouldNotContain(marker, Case.Insensitive);
        }

        ContainsActionableFailureMarker(failureMessage).ShouldBeTrue(
            $"Expected {providerName} to surface an actionable authentication/configuration failure, but got:{Environment.NewLine}{failureMessage}");
    }

    private static IReadOnlyList<string> ExtractFailureCandidates(CliMessage message)
    {
        var candidates = EnumerateStringValues(message.Content)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        if (IsTerminalFailureType(message.Type))
        {
            return candidates;
        }

        return candidates
            .Where(ContainsActionableFailureMarker)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        yield return value;
                    }

                    yield break;
                }
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in EnumerateStringValues(property.Value))
                    {
                        yield return value;
                    }
                }

                yield break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in EnumerateStringValues(item))
                    {
                        yield return value;
                    }
                }

                yield break;
            default:
                yield break;
        }
    }

    private static bool ContainsActionableFailureMarker(string message)
    {
        foreach (var marker in ActionableFailureMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTerminalFailureType(string? messageType)
    {
        return string.Equals(messageType, "error", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "turn.failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "terminal.failed", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RealCliInvocationSandbox : IRuntimeEnvironmentResolver, IDisposable
{
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly string[] SensitiveEnvironmentKeys =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_API_KEY",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CODEX_API_KEY",
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "COPILOT_TOKEN"
    ];

    private readonly string _rootDirectory;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private bool _disposed;

    public RealCliInvocationSandbox()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-real-cli-{Guid.NewGuid():N}");
        WorkingDirectory = Path.Combine(_rootDirectory, "workspace");
        HomeDirectory = Path.Combine(_rootDirectory, "home");
        ConfigDirectory = Path.Combine(_rootDirectory, "config");
        AppDataDirectory = Path.Combine(_rootDirectory, "appdata");
        LocalAppDataDirectory = Path.Combine(_rootDirectory, "localappdata");
        TempDirectory = Path.Combine(_rootDirectory, "tmp");

        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(WorkingDirectory);
        Directory.CreateDirectory(HomeDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LocalAppDataDirectory);
        Directory.CreateDirectory(TempDirectory);

        File.WriteAllText(Path.Combine(WorkingDirectory, "README.md"), "# real cli sandbox");

        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Where(static entry => entry.Key is string)
            .ToDictionary(
                static entry => (string)entry.Key,
                static entry => entry.Value?.ToString(),
                StringComparer.Ordinal);

        foreach (var key in SensitiveEnvironmentKeys)
        {
            environment.Remove(key);
        }

        environment["HOME"] = HomeDirectory;
        environment["USERPROFILE"] = HomeDirectory;
        environment["APPDATA"] = AppDataDirectory;
        environment["LOCALAPPDATA"] = LocalAppDataDirectory;
        environment["XDG_CONFIG_HOME"] = ConfigDirectory;
        environment["XDG_CACHE_HOME"] = Path.Combine(_rootDirectory, "cache");
        environment["XDG_STATE_HOME"] = Path.Combine(_rootDirectory, "state");
        environment["TMPDIR"] = TempDirectory;
        environment["TMP"] = TempDirectory;
        environment["TEMP"] = TempDirectory;
        environment["NO_COLOR"] = "1";
        environment["CI"] = "1";
        environment["HAGICODE_REAL_CLI_AUTH_SANDBOX"] = "1";

        Directory.CreateDirectory(environment["XDG_CACHE_HOME"]!);
        Directory.CreateDirectory(environment["XDG_STATE_HOME"]!);

        _environment = new ReadOnlyDictionary<string, string?>(environment);
    }

    public string WorkingDirectory { get; }

    public string HomeDirectory { get; }

    public string ConfigDirectory { get; }

    public string AppDataDirectory { get; }

    public string LocalAppDataDirectory { get; }

    public string TempDirectory { get; }

    public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_environment);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteDirectoryWithRetries(_rootDirectory);
    }

    internal static void DeleteDirectoryWithRetries(
        string rootDirectory,
        int maxAttempts = 8,
        TimeSpan? retryDelay = null,
        Action<string>? deleteDirectory = null,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        deleteDirectory ??= static directory => Directory.Delete(directory, recursive: true);
        sleep ??= static delay => Thread.Sleep(delay);
        var delayBetweenAttempts = retryDelay ?? DeleteRetryDelay;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(rootDirectory))
                {
                    return;
                }

                NormalizeAttributesRecursively(rootDirectory);
                deleteDirectory(rootDirectory);

                if (!Directory.Exists(rootDirectory))
                {
                    return;
                }
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (IsRetryableDeleteException(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                sleep(delayBetweenAttempts);
                continue;
            }
            catch (Exception ex) when (IsRetryableDeleteException(ex))
            {
                lastException = ex;
            }

            if (attempt < maxAttempts)
            {
                sleep(delayBetweenAttempts);
                continue;
            }

            break;
        }

        throw new IOException(
            $"Failed to delete sandbox directory '{rootDirectory}' after {maxAttempts} attempts.",
            lastException);
    }

    private static bool IsRetryableDeleteException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    private static void NormalizeAttributesRecursively(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            TrySetAttributesToNormal(currentDirectory);

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                TrySetAttributesToNormal(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                pendingDirectories.Push(directory);
            }
        }
    }

    private static void TrySetAttributesToNormal(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }
}
