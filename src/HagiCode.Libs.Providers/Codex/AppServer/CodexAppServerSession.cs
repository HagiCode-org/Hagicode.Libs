using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HagiCode.Libs.Providers.Codex;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Execution;
using ManagedCode.CodexSharpSDK.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// The interactive Codex transport for one run: a <c>codex app-server</c> process, its JSON-RPC client, and the thread
/// that the run uses.
/// </summary>
/// <remarks>
/// A session is opened when the CLI meets the version floor and the handshake succeeds. The user-input feature is required only
/// when an agent question handler is enabled. Native active-turn steering does not enable or require that feature; if the
/// provider cannot open this transport, it falls back to exec and steering is unavailable for that run.
/// </remarks>
internal sealed class CodexAppServerSession : IAsyncDisposable
{
    /// <summary>The minimum Codex CLI version for the interactive path (gate G6).</summary>
    public static readonly Version MinimumCliVersion = new(0, 163, 0);

    /// <summary>How long the handshake may take before the interactive path is abandoned.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private const string ExecutableOriginatorEnvironmentName = "CODEX_INTERNAL_ORIGINATOR_OVERRIDE";
    private const string SdkOriginator = "codex_sdk_csharp";
    private const string OpenAiBaseUrlEnvironmentName = "OPENAI_BASE_URL";
    private const string CodexApiKeyEnvironmentName = "CODEX_API_KEY";
    private const string ConfigFeaturesKey = "features";
    private const string TextInputType = "text";
    private const string ImageInputType = "image";

    private readonly CodexAppServerProcess _process;
    private readonly CodexAppServerClient _client;
    private readonly ILogger _logger;
    private readonly bool _supportsUserQuestions;
    private CodexRunSteering? _steering;
    private TimeSpan _steerDeliveryTimeout = CodexSessionOptions.DefaultSteerDeliveryTimeout;
    private string? _loadedThreadId;

    private CodexAppServerSession(
        CodexAppServerProcess process,
        CodexAppServerClient client,
        ILogger logger,
        bool supportsUserQuestions)
    {
        _process = process;
        _client = client;
        _logger = logger;
        _supportsUserQuestions = supportsUserQuestions;
    }

    /// <summary>Whether this session has an agent-question handler enabled.</summary>
    public bool SupportsUserQuestions => _supportsUserQuestions;

    /// <summary>
    /// Starts the app-server and runs the handshake. When a user-input handler is present, it also confirms the user-input
    /// feature. Throws when any required step fails, and releases the process in that case.
    /// </summary>
    public static async Task<CodexAppServerSession> OpenAsync(
        string executablePath,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory,
        CodexUserInputHandler? userInputHandler,
        TimeSpan questionTimeout,
        ILogger? logger,
        CancellationToken cancellationToken,
        ICliResourceLimitProvider? resourceLimitProvider = null)
    {
        var effectiveLogger = logger ?? NullLogger.Instance;

        var process = CodexAppServerProcess.Start(executablePath, environment, workingDirectory, effectiveLogger, resourceLimitProvider);
        var client = new CodexAppServerClient(process.Output, process.Input, userInputHandler, questionTimeout, effectiveLogger);
        try
        {
            client.Start();

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(HandshakeTimeout);

            await client.InitializeAsync(handshakeCts.Token).ConfigureAwait(false);
            if (userInputHandler is not null)
            {
                var hasFeature = await client.HasExperimentalFeatureAsync(
                    CodexAppServerClient.UserInputFeatureName,
                    handshakeCts.Token).ConfigureAwait(false);
                if (!hasFeature)
                {
                    throw new NotSupportedException(
                        $"codex app-server does not list the '{CodexAppServerClient.UserInputFeatureName}' feature, so user-input requests are unavailable.");
                }
            }

            return new CodexAppServerSession(process, client, effectiveLogger, userInputHandler is not null);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs one prompt on the run's thread. The thread is started when <paramref name="threadId"/> is empty, and resumed
    /// otherwise, which carries the conversation of a stored exec thread into the app-server (gate G2).
    /// </summary>
    /// <returns>The thread events. The first event is always <see cref="ThreadStartedEvent"/>, so the caller binds the id.</returns>
    public IAsyncEnumerable<ThreadEvent> RunTurnAsync(
        string? threadId,
        string prompt,
        JsonObject threadParameters,
        JsonObject turnParameters,
        CancellationToken cancellationToken = default)
    {
        return RunTurnAsync(threadId, prompt, [], threadParameters, turnParameters, cancellationToken);
    }

    /// <summary>
    /// Runs one prompt with image attachments on the run's thread. The text block comes first, then one image block per
    /// attachment in the supplied order. Image bytes are encoded into the turn input only and are not kept afterwards.
    /// </summary>
    /// <inheritdoc cref="RunTurnAsync(string?, string, JsonObject, JsonObject, CancellationToken)"/>
    public async IAsyncEnumerable<ThreadEvent> RunTurnAsync(
        string? threadId,
        string prompt,
        IReadOnlyList<CodexImageAttachment> imageAttachments,
        JsonObject threadParameters,
        JsonObject turnParameters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(imageAttachments);
        ArgumentNullException.ThrowIfNull(threadParameters);
        ArgumentNullException.ThrowIfNull(turnParameters);

        string activeThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            activeThreadId = await _client.StartThreadAsync(threadParameters, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(_loadedThreadId, threadId, StringComparison.Ordinal))
        {
            activeThreadId = threadId;
        }
        else
        {
            activeThreadId = await _client.ResumeThreadAsync(threadId, threadParameters, cancellationToken).ConfigureAwait(false);
        }

        _loadedThreadId = activeThreadId;
        yield return new ThreadStartedEvent(activeThreadId);

        if (imageAttachments.Count > 0)
        {
            _logger.LogDebug(
                "Codex app-server turn carries {ImageCount} image attachment(s): {Images}",
                imageAttachments.Count,
                string.Join(", ", imageAttachments));
        }

        var input = BuildTurnInput(prompt, imageAttachments);

            await foreach (var threadEvent in _client.RunTurnAsync(activeThreadId, input, turnParameters, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return threadEvent;
        }
    }

    /// <summary>
    /// Builds the <c>turn/start</c> input: the text block, then one <c>image</c> block per attachment as a base64 data URL.
    /// With no attachments the result is the text block alone, exactly as before image support existed.
    /// </summary>
    internal static JsonArray BuildTurnInput(string prompt, IReadOnlyList<CodexImageAttachment> imageAttachments)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(imageAttachments);

        var input = new JsonArray
        {
            new JsonObject
            {
                ["type"] = TextInputType,
                ["text"] = prompt,
                ["text_elements"] = new JsonArray()
            }
        };

        foreach (var attachment in imageAttachments)
        {
            input.Add(new JsonObject
            {
                ["type"] = ImageInputType,
                ["url"] = BuildDataUrl(attachment)
            });
        }

        return input;
    }

    /// <summary>Encodes one attachment as a <c>data:</c> URL. The result is built per turn and is never stored.</summary>
    internal static string BuildDataUrl(CodexImageAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return $"data:{attachment.MediaType};base64,{Convert.ToBase64String(attachment.Data.Span)}";
    }

    /// <summary>
    /// Delivers the answer to an open question of the active turn. Passing <see langword="null"/> declines the question.
    /// </summary>
    /// <returns><see langword="true"/> when this call answered the request.</returns>
    public Task<bool> RespondToUserQuestionAsync(
        string requestId,
        CodexUserQuestionResponse? response,
        CancellationToken cancellationToken)
    {
        return _client.RespondToUserQuestionAsync(requestId, response, cancellationToken);
    }

    /// <summary>Attaches a per-execution channel to this session's native active-turn steering operation.</summary>
    public void AttachSteering(CodexRunSteering steering, TimeSpan deliveryTimeout)
    {
        ArgumentNullException.ThrowIfNull(steering);
        if (deliveryTimeout == Timeout.InfiniteTimeSpan || deliveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryTimeout),
                deliveryTimeout,
                "The steering delivery timeout must be positive and finite.");
        }

        _steerDeliveryTimeout = deliveryTimeout;
        _steering = steering;
        steering.Attach((steerId, text, cancellationToken) =>
        {
            var threadId = Volatile.Read(ref _loadedThreadId);
            return threadId is null
                ? Task.FromResult(CodexSteerResult.NotSteerable)
                : _client.TrySteerAsync(steerId, threadId, text, _steerDeliveryTimeout, steering, cancellationToken);
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _steering?.Detach();
        _steering = null;
        await _client.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the installed CLI is at or above <see cref="MinimumCliVersion"/>. The version is read from the first number
    /// group of the reported text, so suffixes such as <c>-beta</c> are ignored.
    /// </summary>
    public static bool MeetsMinimumCliVersion(string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(installedVersion, @"\d+(\.\d+){1,3}");
        if (!match.Success || !Version.TryParse(match.Value, out var parsed))
        {
            return false;
        }

        // Version.TryParse leaves the missing components at -1 (for "0.163" the build is -1); treat them as 0.
        var normalized = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return normalized >= MinimumCliVersion;
    }

    /// <summary>
    /// Builds the environment of the app-server process, with the same rules the exec path applies to its child process.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        IReadOnlyDictionary<string, string>? environmentOverride,
        string? baseUrl,
        string? apiKey)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (environmentOverride is not null)
        {
            foreach (var (key, value) in environmentOverride)
            {
                environment[key] = value;
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string key && variable.Value is string value)
                {
                    environment[key] = value;
                }
            }
        }

        environment.TryAdd(ExecutableOriginatorEnvironmentName, SdkOriginator);

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            environment[OpenAiBaseUrlEnvironmentName] = baseUrl;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            environment[CodexApiKeyEnvironmentName] = apiKey;
        }

        return environment;
    }

    /// <summary>
    /// Builds the <c>thread/start</c> and <c>thread/resume</c> parameters. The user-input feature is enabled only when the run
    /// has an agent question handler.
    /// </summary>
    public static JsonObject BuildThreadParameters(ThreadOptions threadOptions, JsonObject? config, bool enableUserInput = true)
    {
        ArgumentNullException.ThrowIfNull(threadOptions);

        var configObject = config is null
            ? new JsonObject()
            : (JsonObject)config.DeepClone();
        var features = configObject[ConfigFeaturesKey] as JsonObject ?? new JsonObject();
        if (enableUserInput)
        {
            features[CodexAppServerClient.UserInputFeatureName] = true;
        }
        foreach (var enabled in threadOptions.EnabledFeatures ?? [])
        {
            if (!string.IsNullOrWhiteSpace(enabled))
            {
                features[enabled.Trim()] = true;
            }
        }

        foreach (var disabled in threadOptions.DisabledFeatures ?? [])
        {
            if (!string.IsNullOrWhiteSpace(disabled))
            {
                features[disabled.Trim()] = false;
            }
        }

        configObject[ConfigFeaturesKey] = features;

        var parameters = new JsonObject
        {
            ["config"] = configObject
        };

        if (!string.IsNullOrWhiteSpace(threadOptions.Model))
        {
            parameters["model"] = threadOptions.Model;
        }

        if (!string.IsNullOrWhiteSpace(threadOptions.WorkingDirectory))
        {
            parameters["cwd"] = threadOptions.WorkingDirectory;
        }

        if (threadOptions.SandboxMode is { } sandboxMode)
        {
            parameters["sandbox"] = sandboxMode.ToCliValue();
        }

        if (threadOptions.ApprovalPolicy is { } approvalPolicy)
        {
            parameters["approvalPolicy"] = approvalPolicy.ToCliValue();
        }

        return parameters;
    }

    /// <summary>
    /// Builds the <c>turn/start</c> parameters that are specific to one turn: the reasoning effort, and a writable-roots sandbox
    /// policy when additional directories are configured.
    /// </summary>
    public static JsonObject BuildTurnParameters(ThreadOptions threadOptions)
    {
        ArgumentNullException.ThrowIfNull(threadOptions);

        var parameters = new JsonObject();
        if (threadOptions.ModelReasoningEffort is { } effort)
        {
            parameters["effort"] = effort.ToCliValue();
        }

        if (threadOptions.SandboxMode == SandboxMode.WorkspaceWrite
            && threadOptions.AdditionalDirectories is { Count: > 0 } directories)
        {
            var roots = new JsonArray();
            foreach (var directory in directories.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                roots.Add(directory.Trim());
            }

            parameters["sandboxPolicy"] = new JsonObject
            {
                ["type"] = "workspaceWrite",
                ["writableRoots"] = roots,
                ["networkAccess"] = threadOptions.NetworkAccessEnabled ?? false
            };
        }

        return parameters;
    }
}
