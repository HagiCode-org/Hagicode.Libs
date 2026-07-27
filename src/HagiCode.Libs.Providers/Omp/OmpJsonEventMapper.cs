using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Omp;

internal sealed class OmpJsonEventMapper
{
    public IReadOnlyList<CliMessage> Normalize(ProcessResult result, string? requestedSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = CreateStreamingState(requestedSessionId);
        var messages = new List<CliMessage>();

        using var reader = new StringReader(result.StandardOutput ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            messages.AddRange(state.ProcessOutputLine(line));
        }

        messages.AddRange(state.Complete(result.ExitCode, result.StandardError));
        return messages;
    }

    internal StreamingState CreateStreamingState(string? requestedSessionId = null)
    {
        return new StreamingState(requestedSessionId);
    }

    internal sealed class StreamingState(string? requestedSessionId)
    {
        private readonly string? _requestedSessionId = NormalizeOptional(requestedSessionId);
        private readonly List<string> _invalidOutputLines = [];
        private readonly Dictionary<string, ToolCallSnapshot> _toolCallSnapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<int, PendingToolCallDelta> _pendingToolCallDeltas = new();
        private readonly HashSet<string> _emittedToolResultKeys = new(StringComparer.Ordinal);

        private string? _sessionId;
        private string? _assistantText;
        private string? _assistantModel;
        private string? _assistantProvider;
        private string? _stopReason;
        private string? _errorText;
        private CliMessage? _terminalMessage;
        private string? _pendingThinkingText;
        private JsonElement? _pendingThinkingMessage;
        private string? _lastAssistantTextSnapshot;

        public IReadOnlyList<CliMessage> ProcessOutputLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return [];
            }

            JsonElement eventElement;
            try
            {
                using var document = JsonDocument.Parse(line);
                var rootElement = document.RootElement;
                if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("type", out _))
                {
                    _invalidOutputLines.Add(line);
                    return [];
                }

                eventElement = JsonSerializer.SerializeToElement(rootElement);
            }
            catch (JsonException)
            {
                _invalidOutputLines.Add(line);
                return [];
            }

            var messages = new List<CliMessage>();
            var eventType = GetString(eventElement, "type");
            switch (eventType)
            {
                case "turn_start":
                    ClearPendingThinking();
                    _pendingToolCallDeltas.Clear();
                    break;

                case "session":
                    _sessionId ??= GetString(eventElement, "id");
                    if (!string.IsNullOrWhiteSpace(_sessionId))
                    {
                        messages.Add(CreateSessionLifecycleMessage(_sessionId!, eventElement, _requestedSessionId));
                    }

                    break;

                case "message_update":
                    ProcessMessageUpdate(eventElement, messages);
                    break;

                case "message_end":
                    messages.AddRange(ProcessToolResultMessageEnd(eventElement));

                    if (TryGetAssistantMessage(eventElement, out var assistantMessage))
                    {
                        CaptureAssistantState(
                            assistantMessage,
                            ref _assistantText,
                            ref _assistantModel,
                            ref _assistantProvider,
                            ref _stopReason,
                            ref _errorText);

                        if (!string.Equals(GetString(assistantMessage, "stopReason"), "toolUse", StringComparison.OrdinalIgnoreCase))
                        {
                            messages.AddRange(DrainBufferedThinkingMessages());
                        }
                        else
                        {
                            ClearPendingThinking();
                        }

                        // Keep whitespace-only assistant snapshots: "\n"/" " are Markdown block boundaries.
                        if (!string.IsNullOrEmpty(_assistantText))
                        {
                            if (TryCreateAssistantMessage(_assistantText!, assistantMessage) is { } assistantMessageDelta)
                            {
                                messages.Add(assistantMessageDelta);
                            }
                        }
                    }

                    break;

                case "turn_end":
                    messages.AddRange(ProcessTurnEndToolResults(eventElement));

                    if (TryGetAssistantMessage(eventElement, out var turnMessage))
                    {
                        CaptureAssistantState(
                            turnMessage,
                            ref _assistantText,
                            ref _assistantModel,
                            ref _assistantProvider,
                            ref _stopReason,
                            ref _errorText);

                        if (!string.Equals(GetString(turnMessage, "stopReason"), "toolUse", StringComparison.OrdinalIgnoreCase))
                        {
                            messages.AddRange(DrainBufferedThinkingMessages());
                        }
                        else
                        {
                            ClearPendingThinking();
                        }

                        _terminalMessage = CreateTerminalMessage(
                            _sessionId,
                            _assistantText,
                            _lastAssistantTextSnapshot,
                            turnMessage,
                            sourceEventType: eventType);
                    }

                    break;

                case "agent_end":
                    messages.AddRange(DrainBufferedThinkingMessages());

                    if (_terminalMessage is null && TryGetLastAssistantMessage(eventElement, out var finalAssistantMessage))
                    {
                        CaptureAssistantState(
                            finalAssistantMessage,
                            ref _assistantText,
                            ref _assistantModel,
                            ref _assistantProvider,
                            ref _stopReason,
                            ref _errorText);
                        _terminalMessage = CreateTerminalMessage(
                            _sessionId,
                            _assistantText,
                            _lastAssistantTextSnapshot,
                            finalAssistantMessage,
                            sourceEventType: eventType);
                    }

                    break;
            }

            return messages;
        }

        public IReadOnlyList<CliMessage> Complete(int exitCode, string? standardError)
        {
            CliMessage? terminalMessage = _terminalMessage;
            if (exitCode != 0)
            {
                terminalMessage = CreateTerminalFailedMessage(
                    _sessionId,
                    BuildProcessFailureText(exitCode, standardError, _invalidOutputLines, _errorText),
                    _assistantModel,
                    _assistantProvider,
                    _stopReason ?? "exit_code",
                    exitCode,
                    standardError,
                    _invalidOutputLines);
            }
            else if (terminalMessage is null)
            {
                terminalMessage = CreateFallbackTerminalMessage(
                    exitCode,
                    standardError,
                    _invalidOutputLines,
                    _sessionId,
                    _assistantText,
                    _lastAssistantTextSnapshot,
                    _assistantModel,
                    _assistantProvider,
                    _stopReason,
                    _errorText);
            }

            return terminalMessage is null ? [] : [terminalMessage];
        }

        private void ProcessMessageUpdate(JsonElement eventElement, List<CliMessage> messages)
        {
            if (!eventElement.TryGetProperty("assistantMessageEvent", out var assistantEvent)
                || assistantEvent.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var updateType = GetString(assistantEvent, "type");
            var hasSnapshot = TryResolveAssistantSnapshot(eventElement, assistantEvent, out var assistantMessage);
            if (hasSnapshot)
            {
                CaptureAssistantState(
                    assistantMessage,
                    ref _assistantText,
                    ref _assistantModel,
                    ref _assistantProvider,
                    ref _stopReason,
                    ref _errorText);
            }
            else
            {
                assistantMessage = CreateSyntheticAssistantMessage(
                    _assistantModel,
                    _assistantProvider);
            }

            if (IsThinkingUpdateType(updateType))
            {
                // Real OMP print/json streams thinking as incremental deltas without a partial snapshot.
                var thinkingDelta = GetString(assistantEvent, "delta");
                if (!string.IsNullOrEmpty(thinkingDelta))
                {
                    BufferThinkingSnapshot((_pendingThinkingText ?? string.Empty) + thinkingDelta, assistantMessage);
                }
                else if (IsEndUpdateType(updateType, "thinking")
                         && !string.IsNullOrEmpty(GetString(assistantEvent, "content")))
                {
                    BufferThinkingSnapshot(GetString(assistantEvent, "content")!, assistantMessage);
                }
                else if (hasSnapshot)
                {
                    var thinkingText = ExtractAssistantThinking(assistantMessage);
                    // Preserve whitespace-only thinking fragments for parity with other providers.
                    if (!string.IsNullOrEmpty(thinkingText))
                    {
                        BufferThinkingSnapshot(thinkingText!, assistantMessage);
                    }
                }
            }

            if (IsTextUpdateType(updateType))
            {
                var textDelta = GetString(assistantEvent, "delta");
                if (!string.IsNullOrEmpty(textDelta))
                {
                    // Emit the raw delta immediately so consumers see stream progress.
                    _assistantText = (_assistantText ?? string.Empty) + textDelta;
                    _lastAssistantTextSnapshot = _assistantText;
                    messages.Add(CreateAssistantMessage(_sessionId, textDelta!, assistantMessage));
                }
                else if (IsEndUpdateType(updateType, "text")
                         && !string.IsNullOrEmpty(GetString(assistantEvent, "content")))
                {
                    var completedText = GetString(assistantEvent, "content")!;
                    _assistantText = completedText;
                    if (TryCreateAssistantMessage(completedText, assistantMessage) is { } completedDelta)
                    {
                        messages.Add(completedDelta);
                    }
                }
                else if (hasSnapshot)
                {
                    var assistantText = ExtractAssistantText(assistantMessage);
                    // Preserve whitespace-only assistant text (newlines/spaces) required by Markdown.
                    if (!string.IsNullOrEmpty(assistantText))
                    {
                        if (TryCreateAssistantMessage(assistantText!, assistantMessage) is { } assistantMessageDelta)
                        {
                            messages.Add(assistantMessageDelta);
                        }
                    }
                }
            }

            if (IsToolCallUpdateType(updateType))
            {
                if (hasSnapshot)
                {
                    messages.AddRange(ProcessToolCallUpdate(assistantMessage));
                }
                else if (assistantEvent.TryGetProperty("toolCall", out var toolCallElement)
                         && toolCallElement.ValueKind == JsonValueKind.Object)
                {
                    messages.AddRange(ProcessToolCallUpdate(WrapToolCallAsAssistantMessage(toolCallElement, assistantMessage)));
                }
                else if (!string.IsNullOrEmpty(GetString(assistantEvent, "delta")))
                {
                    // toolcall_delta without partial: keep accumulating args text by contentIndex.
                    messages.AddRange(ProcessToolCallDeltaEvent(assistantEvent, assistantMessage));
                }
            }
        }

        private void BufferThinkingSnapshot(string thinkingText, JsonElement assistantMessage)
        {
            _pendingThinkingText = thinkingText;
            _pendingThinkingMessage = JsonSerializer.SerializeToElement(assistantMessage);
        }

        private IReadOnlyList<CliMessage> DrainBufferedThinkingMessages()
        {
            // Whitespace-only thinking is still a real fragment; only drop null/empty.
            if (string.IsNullOrEmpty(_pendingThinkingText) || _pendingThinkingMessage is not { } pendingThinkingMessage)
            {
                ClearPendingThinking();
                return [];
            }

            var thinkingMessage = CreateAssistantThoughtMessage(_sessionId, _pendingThinkingText!, pendingThinkingMessage);
            ClearPendingThinking();
            return [thinkingMessage];
        }

        private CliMessage? TryCreateAssistantMessage(string text, JsonElement assistantMessage)
        {
            var delta = ReconcileAssistantTextSnapshot(text);
            if (string.IsNullOrEmpty(delta))
            {
                return null;
            }

            return CreateAssistantMessage(_sessionId, delta, assistantMessage);
        }

        private string? ReconcileAssistantTextSnapshot(string text)
        {
            if (_lastAssistantTextSnapshot is null)
            {
                _lastAssistantTextSnapshot = text;
                return text;
            }

            if (text.StartsWith(_lastAssistantTextSnapshot, StringComparison.Ordinal))
            {
                var delta = text[_lastAssistantTextSnapshot.Length..];
                _lastAssistantTextSnapshot = text;
                return delta.Length == 0 ? null : delta;
            }

            if (_lastAssistantTextSnapshot.StartsWith(text, StringComparison.Ordinal))
            {
                return null;
            }

            _lastAssistantTextSnapshot = text;
            return text;
        }

        private void ClearPendingThinking()
        {
            _pendingThinkingText = null;
            _pendingThinkingMessage = null;
        }

        private IReadOnlyList<CliMessage> ProcessToolCallDeltaEvent(
            JsonElement assistantEvent,
            JsonElement assistantMessage)
        {
            var contentIndex = TryGetInt32(assistantEvent, "contentIndex");
            var delta = GetString(assistantEvent, "delta");
            if (contentIndex is null || string.IsNullOrEmpty(delta))
            {
                return [];
            }

            if (!_pendingToolCallDeltas.TryGetValue(contentIndex.Value, out var pending))
            {
                pending = new PendingToolCallDelta();
                _pendingToolCallDeltas[contentIndex.Value] = pending;
            }

            pending.ArgumentsJson = (pending.ArgumentsJson ?? string.Empty) + delta;
            // Prefer toolCall payload if present on later events.
            if (assistantEvent.TryGetProperty("toolCall", out var toolCallElement)
                && toolCallElement.ValueKind == JsonValueKind.Object)
            {
                pending.ToolCallId = NormalizeOptional(GetString(toolCallElement, "id")) ?? pending.ToolCallId;
                pending.ToolName = NormalizeOptional(GetString(toolCallElement, "name")) ?? pending.ToolName;
                var resolvedArgs = ResolveToolCallArguments(toolCallElement);
                if (!string.IsNullOrEmpty(resolvedArgs))
                {
                    pending.ArgumentsJson = resolvedArgs;
                }
            }

            var toolCallId = pending.ToolCallId ?? $"omp-tool-{contentIndex.Value}";
            var toolName = pending.ToolName ?? "tool_call";
            var isFirstObservation = !_toolCallSnapshots.ContainsKey(toolCallId);
            _toolCallSnapshots[toolCallId] = new ToolCallSnapshot(toolName, pending.ArgumentsJson);
            return
            [
                CreateToolLifecycleMessage(
                    isFirstObservation ? "tool.call" : "tool.update",
                    _sessionId,
                    toolCallId,
                    toolName,
                    "running",
                    rawInput: ParseJsonOrString(pending.ArgumentsJson),
                    rawOutput: null,
                    text: null,
                    sourceMessage: assistantMessage)
            ];
        }

        private IReadOnlyList<CliMessage> ProcessToolCallUpdate(JsonElement assistantMessage)
        {
            var messages = new List<CliMessage>();

            foreach (var toolCall in EnumerateToolCalls(assistantMessage))
            {
                var toolCallId = NormalizeOptional(GetString(toolCall, "id"));
                if (toolCallId is null)
                {
                    continue;
                }

                var toolName = NormalizeOptional(GetString(toolCall, "name")) ?? "tool_call";
                var argumentsJson = ResolveToolCallArguments(toolCall);
                var isFirstObservation = !_toolCallSnapshots.TryGetValue(toolCallId, out var previousSnapshot);
                var hasChanged = !isFirstObservation &&
                                 (!string.Equals(previousSnapshot!.Name, toolName, StringComparison.Ordinal) ||
                                  !string.Equals(previousSnapshot.ArgumentsJson, argumentsJson, StringComparison.Ordinal));

                if (!isFirstObservation && !hasChanged)
                {
                    continue;
                }

                _toolCallSnapshots[toolCallId] = new ToolCallSnapshot(toolName, argumentsJson);
                messages.Add(CreateToolLifecycleMessage(
                    isFirstObservation ? "tool.call" : "tool.update",
                    _sessionId,
                    toolCallId,
                    toolName,
                    "running",
                    rawInput: ParseJsonOrString(argumentsJson),
                    rawOutput: null,
                    text: null,
                    sourceMessage: assistantMessage));
            }

            return messages;
        }

        private IReadOnlyList<CliMessage> ProcessToolResultMessageEnd(JsonElement eventElement)
        {
            if (!TryGetToolResultMessage(eventElement, out var toolResultMessage))
            {
                return [];
            }

            return ProcessToolResult(toolResultMessage);
        }

        private IReadOnlyList<CliMessage> ProcessTurnEndToolResults(JsonElement eventElement)
        {
            if (!eventElement.TryGetProperty("toolResults", out var toolResultsElement) ||
                toolResultsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var messages = new List<CliMessage>();
            foreach (var toolResult in toolResultsElement.EnumerateArray())
            {
                if (toolResult.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetString(toolResult, "role"), "toolResult", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                messages.AddRange(ProcessToolResult(toolResult));
            }

            return messages;
        }

        private IReadOnlyList<CliMessage> ProcessToolResult(JsonElement toolResultMessage)
        {
            var toolResultKey = BuildToolResultKey(toolResultMessage);
            if (!_emittedToolResultKeys.Add(toolResultKey))
            {
                return [];
            }

            var toolCallId = NormalizeOptional(GetString(toolResultMessage, "toolCallId"));
            var toolName = NormalizeOptional(GetString(toolResultMessage, "toolName"));
            if (toolCallId is not null && _toolCallSnapshots.TryGetValue(toolCallId, out var snapshot))
            {
                toolName ??= snapshot.Name;
                _toolCallSnapshots.Remove(toolCallId);
            }

            var status = TryGetBoolean(toolResultMessage, "isError") == true ? "failed" : "completed";
            var extractedText = ExtractToolResultText(toolResultMessage);
            var rawOutput = ResolveToolResultOutput(toolResultMessage, extractedText);
            return
            [
                CreateToolLifecycleMessage(
                    string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "tool.failed" : "tool.completed",
                    _sessionId,
                    toolCallId,
                    toolName ?? "tool_call",
                    status,
                    rawInput: null,
                    rawOutput: rawOutput,
                    text: extractedText,
                    sourceMessage: toolResultMessage)
            ];
        }
    }

    private static bool IsThinkingUpdateType(string? updateType)
    {
        return updateType?.StartsWith("thinking_", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsTextUpdateType(string? updateType)
    {
        return updateType?.StartsWith("text_", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsToolCallUpdateType(string? updateType)
    {
        return updateType?.StartsWith("toolcall_", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void CaptureAssistantState(
        JsonElement assistantMessage,
        ref string? assistantText,
        ref string? assistantModel,
        ref string? assistantProvider,
        ref string? stopReason,
        ref string? errorText)
    {
        assistantText = ExtractAssistantText(assistantMessage) ?? assistantText;
        assistantModel = GetString(assistantMessage, "responseModel") ?? GetString(assistantMessage, "model") ?? assistantModel;
        assistantProvider = GetString(assistantMessage, "provider") ?? assistantProvider;
        stopReason = GetString(assistantMessage, "stopReason") ?? stopReason;
        errorText = GetString(assistantMessage, "errorMessage") ?? errorText;
    }

    private static CliMessage? CreateTerminalMessage(
        string? sessionId,
        string? assistantText,
        string? lastAssistantTextSnapshot,
        JsonElement assistantMessage,
        string sourceEventType)
    {
        var stopReason = GetString(assistantMessage, "stopReason");
        var provider = GetString(assistantMessage, "provider");
        var model = GetString(assistantMessage, "responseModel") ?? GetString(assistantMessage, "model");
        if (string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stopReason, "aborted", StringComparison.OrdinalIgnoreCase))
        {
            var errorText = GetString(assistantMessage, "errorMessage")
                            ?? assistantText
                            ?? "Pi reported a failed assistant turn.";

            return CreateTerminalFailedMessage(sessionId, errorText, model, provider, stopReason, exitCode: null, stderr: null, invalidOutputLines: null, sourceEventType, assistantMessage);
        }

        return CreateTerminalCompletedMessage(
            sessionId,
            ResolveTerminalCompletedText(assistantText, lastAssistantTextSnapshot),
            model,
            provider,
            stopReason,
            sourceEventType,
            assistantMessage);
    }

    private static CliMessage CreateFallbackTerminalMessage(
        int exitCode,
        string? standardError,
        IReadOnlyList<string> invalidOutputLines,
        string? sessionId,
        string? assistantText,
        string? lastAssistantTextSnapshot,
        string? assistantModel,
        string? assistantProvider,
        string? stopReason,
        string? errorText)
    {
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            return CreateTerminalFailedMessage(sessionId, errorText!, assistantModel, assistantProvider, stopReason ?? "error", exitCode, standardError, invalidOutputLines);
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            return CreateTerminalFailedMessage(sessionId, standardError.Trim(), assistantModel, assistantProvider, stopReason ?? "stderr", exitCode, standardError, invalidOutputLines);
        }

        if (invalidOutputLines.Count > 0)
        {
            return CreateTerminalFailedMessage(
                sessionId,
                $"Pi returned non-JSON output:{Environment.NewLine}{string.Join(Environment.NewLine, invalidOutputLines)}",
                assistantModel,
                assistantProvider,
                stopReason ?? "invalid_json",
                exitCode,
                standardError,
                invalidOutputLines);
        }

        // Whitespace-only assistant text is still a successful completion payload.
        if (!string.IsNullOrEmpty(assistantText))
        {
            return CreateTerminalCompletedMessage(
                sessionId,
                ResolveTerminalCompletedText(assistantText, lastAssistantTextSnapshot),
                assistantModel,
                assistantProvider,
                stopReason,
                sourceEventType: "fallback");
        }

        return CreateTerminalFailedMessage(
            sessionId,
            "Pi JSON output ended without a completion or failure event.",
            assistantModel,
            assistantProvider,
            stopReason ?? "missing_terminal_event",
            exitCode,
            standardError,
            invalidOutputLines);
    }

    private static CliMessage CreateSessionLifecycleMessage(string sessionId, JsonElement sessionEvent, string? requestedSessionId)
    {
        var requested = NormalizeOptional(requestedSessionId);
        var isResumed = requested is not null && string.Equals(sessionId, requested, StringComparison.Ordinal);
        var isRestarted = requested is not null && !isResumed;
        var messageType = isResumed ? "session.resumed" : "session.started";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = messageType,
            ["session_id"] = sessionId,
            ["sessionId"] = sessionId,
            ["resumeMode"] = isResumed ? "resumed" : isRestarted ? "restarted" : "started",
        };

        AddIfNotEmpty(payload, "requested_session_id", requested);
        AddIfNotEmpty(payload, "requestedSessionId", requested);
        AddIfNotEmpty(payload, "cwd", GetString(sessionEvent, "cwd"));
        AddIfNotEmpty(payload, "timestamp", GetString(sessionEvent, "timestamp"));
        AddIfPresent(payload, "version", TryGetInt32(sessionEvent, "version"));

        if (requested is not null)
        {
            payload["resumed"] = isResumed;
            payload["restarted"] = isRestarted;
        }

        return new CliMessage(messageType, JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage CreateAssistantMessage(string? sessionId, string text, JsonElement assistantMessage)
    {
        return CreateAssistantSnapshotMessage("assistant", sessionId, text, assistantMessage);
    }

    private static CliMessage CreateAssistantThoughtMessage(string? sessionId, string text, JsonElement assistantMessage)
    {
        return CreateAssistantSnapshotMessage("assistant.thought", sessionId, text, assistantMessage);
    }

    private static CliMessage CreateAssistantSnapshotMessage(
        string messageType,
        string? sessionId,
        string text,
        JsonElement assistantMessage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = messageType,
            ["text"] = text,
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "provider", GetString(assistantMessage, "provider"));
        AddIfNotEmpty(payload, "model", GetString(assistantMessage, "model"));
        AddIfNotEmpty(payload, "response_model", GetString(assistantMessage, "responseModel"));
        AddIfNotEmpty(payload, "response_id", GetString(assistantMessage, "responseId"));
        AddIfNotEmpty(payload, "stop_reason", GetString(assistantMessage, "stopReason"));
        AddJsonPropertyIfPresent(payload, "usage", assistantMessage, "usage");
        AddJsonPropertyIfPresent(payload, "timestamp", assistantMessage, "timestamp");

        return new CliMessage(messageType, JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage CreateTerminalCompletedMessage(
        string? sessionId,
        string? text,
        string? model,
        string? provider,
        string? stopReason,
        string? sourceEventType,
        JsonElement? sourceMessage = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.completed",
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "text", text);
        AddIfNotEmpty(payload, "model", model);
        AddIfNotEmpty(payload, "provider", provider);
        AddIfNotEmpty(payload, "stop_reason", stopReason);
        AddIfNotEmpty(payload, "source_event_type", sourceEventType);
        AddJsonPropertyIfPresent(payload, "usage", sourceMessage, "usage");
        AddJsonPropertyIfPresent(payload, "timestamp", sourceMessage, "timestamp");

        return new CliMessage("terminal.completed", JsonSerializer.SerializeToElement(payload));
    }

    private static string? ResolveTerminalCompletedText(string? terminalText, string? lastAssistantTextSnapshot)
    {
        var normalizedTerminalText = NormalizeOptional(terminalText);
        if (normalizedTerminalText is null)
        {
            return null;
        }

        var normalizedAssistantSnapshot = NormalizeOptional(lastAssistantTextSnapshot);
        if (normalizedAssistantSnapshot is not null &&
            string.Equals(normalizedTerminalText, normalizedAssistantSnapshot, StringComparison.Ordinal))
        {
            return null;
        }

        return normalizedTerminalText;
    }

    private static CliMessage CreateTerminalFailedMessage(
        string? sessionId,
        string diagnosticText,
        string? model,
        string? provider,
        string? stopReason,
        int? exitCode,
        string? stderr,
        IReadOnlyList<string>? invalidOutputLines,
        string? sourceEventType = null,
        JsonElement? sourceMessage = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = diagnosticText,
            ["error"] = diagnosticText,
            ["message"] = diagnosticText,
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "model", model);
        AddIfNotEmpty(payload, "provider", provider);
        AddIfNotEmpty(payload, "stop_reason", stopReason);
        AddIfNotEmpty(payload, "stderr", NormalizeOptional(stderr));
        AddIfNotEmpty(payload, "source_event_type", sourceEventType);
        AddIfPresent(payload, "exit_code", exitCode);
        AddJsonPropertyIfPresent(payload, "usage", sourceMessage, "usage");
        AddJsonPropertyIfPresent(payload, "timestamp", sourceMessage, "timestamp");

        if (invalidOutputLines is { Count: > 0 })
        {
            payload["invalid_output_lines"] = invalidOutputLines.ToArray();
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private static string BuildProcessFailureText(
        int exitCode,
        string? standardError,
        IReadOnlyList<string> invalidOutputLines,
        string? errorText)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(errorText))
        {
            builder.Append(errorText.Trim());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            AppendDiagnosticLine(builder, standardError.Trim());
        }

        if (invalidOutputLines.Count > 0)
        {
            AppendDiagnosticLine(builder, string.Join(Environment.NewLine, invalidOutputLines));
        }

        if (builder.Length == 0)
        {
            builder.Append($"Pi exited with code {exitCode}.");
        }
        else
        {
            AppendDiagnosticLine(builder, $"Pi exited with code {exitCode}.");
        }

        return builder.ToString();
    }

    private static void AppendDiagnosticLine(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(value);
    }

    private static string? ExtractAssistantText(JsonElement assistantMessage)
    {
        return ExtractAssistantContentByType(assistantMessage, "text", "text");
    }

    private static string? ExtractAssistantThinking(JsonElement assistantMessage)
    {
        return ExtractAssistantContentByType(assistantMessage, "thinking", "thinking");
    }

    private static string? ExtractToolResultText(JsonElement toolResultMessage)
    {
        if (!toolResultMessage.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var contentItem in contentElement.EnumerateArray())
        {
            if (string.Equals(GetString(contentItem, "type"), "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = GetString(contentItem, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text);
                }
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? ExtractAssistantContentByType(
        JsonElement assistantMessage,
        string contentType,
        string contentPropertyName)
    {
        if (!assistantMessage.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var contentItem in contentElement.EnumerateArray())
        {
            if (!string.Equals(GetString(contentItem, "type"), contentType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = GetString(contentItem, contentPropertyName);
            // Keep non-empty whitespace fragments ("\n", " ") so Markdown block/list/table structure survives.
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            builder.Append(text);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool TryGetAssistantMessage(JsonElement eventElement, out JsonElement assistantMessage)
    {
        if (eventElement.TryGetProperty("message", out assistantMessage)
            && assistantMessage.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(assistantMessage, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        assistantMessage = default;
        return false;
    }

    private static bool TryResolveAssistantSnapshot(
        JsonElement eventElement,
        JsonElement assistantEvent,
        out JsonElement assistantMessage)
    {
        if (assistantEvent.TryGetProperty("partial", out assistantMessage)
            && assistantMessage.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(assistantMessage, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetAssistantMessage(eventElement, out assistantMessage);
    }

    private static bool IsEndUpdateType(string? updateType, string contentKind)
    {
        return string.Equals(updateType, $"{contentKind}_end", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement CreateSyntheticAssistantMessage(string? model, string? provider)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = Array.Empty<object>(),
        };

        AddIfNotEmpty(payload, "model", model);
        AddIfNotEmpty(payload, "provider", provider);
        return JsonSerializer.SerializeToElement(payload);
    }

    private static JsonElement WrapToolCallAsAssistantMessage(JsonElement toolCall, JsonElement fallbackAssistantMessage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = new object[] { toolCall },
        };

        AddIfNotEmpty(payload, "provider", GetString(fallbackAssistantMessage, "provider"));
        AddIfNotEmpty(payload, "model", GetString(fallbackAssistantMessage, "model"));
        AddIfNotEmpty(payload, "responseModel", GetString(fallbackAssistantMessage, "responseModel"));
        AddIfNotEmpty(payload, "responseId", GetString(fallbackAssistantMessage, "responseId"));
        AddIfNotEmpty(payload, "stopReason", GetString(fallbackAssistantMessage, "stopReason"));
        return JsonSerializer.SerializeToElement(payload);
    }

    private static bool TryGetToolResultMessage(JsonElement eventElement, out JsonElement toolResultMessage)
    {
        if (eventElement.TryGetProperty("message", out toolResultMessage)
            && toolResultMessage.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(toolResultMessage, "role"), "toolResult", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        toolResultMessage = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateToolCalls(JsonElement assistantMessage)
    {
        if (!assistantMessage.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var contentItem in contentElement.EnumerateArray())
        {
            if (contentItem.ValueKind == JsonValueKind.Object &&
                string.Equals(GetString(contentItem, "type"), "toolCall", StringComparison.OrdinalIgnoreCase))
            {
                yield return contentItem;
            }
        }
    }

    private static string? ResolveToolCallArguments(JsonElement toolCall)
    {
        if (toolCall.TryGetProperty("arguments", out var argumentsElement) &&
            argumentsElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return argumentsElement.GetRawText();
        }

        return NormalizeOptional(GetString(toolCall, "partialArgs"));
    }

    private static object? ResolveToolResultOutput(JsonElement toolResultMessage, string? extractedText)
    {
        if (toolResultMessage.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return contentElement;
        }

        if (!string.IsNullOrWhiteSpace(extractedText))
        {
            return extractedText;
        }

        return null;
    }

    private static string BuildToolResultKey(JsonElement toolResultMessage)
    {
        var toolCallId = NormalizeOptional(GetString(toolResultMessage, "toolCallId"));
        if (toolCallId is not null)
        {
            return toolCallId;
        }

        var toolName = NormalizeOptional(GetString(toolResultMessage, "toolName")) ?? "tool";
        var timestamp = TryGetScalarString(toolResultMessage, "timestamp") ?? Guid.NewGuid().ToString("N");
        return $"{toolName}:{timestamp}";
    }

    private static CliMessage CreateToolLifecycleMessage(
        string messageType,
        string? sessionId,
        string? toolCallId,
        string toolName,
        string status,
        object? rawInput,
        object? rawOutput,
        string? text,
        JsonElement? sourceMessage = null)
    {
        var update = new Dictionary<string, object?>
        {
            ["title"] = toolName,
            ["kind"] = toolName,
            ["status"] = status,
        };

        AddIfNotEmpty(update, "toolCallId", toolCallId);
        if (rawInput != null)
        {
            update["rawInput"] = rawInput;
        }

        if (rawOutput != null)
        {
            update["rawOutput"] = rawOutput;
        }

        AddIfNotEmpty(update, "message", text);
        AddJsonPropertyIfPresent(update, "timestamp", sourceMessage, "timestamp");

        var payload = new Dictionary<string, object?>
        {
            ["type"] = messageType,
            ["tool_name"] = toolName,
            ["status"] = status,
            ["update"] = update,
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "tool_call_id", toolCallId);
        AddIfNotEmpty(payload, "text", text);
        AddJsonPropertyIfPresent(payload, "timestamp", sourceMessage, "timestamp");

        return new CliMessage(messageType, JsonSerializer.SerializeToElement(payload));
    }

    private static object? ParseJsonOrString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.SerializeToElement(document.RootElement);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool TryGetLastAssistantMessage(JsonElement eventElement, out JsonElement assistantMessage)
    {
        if (eventElement.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            var assistantMessages = messagesElement
                .EnumerateArray()
                .Where(static message => string.Equals(GetString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (assistantMessages.Length > 0)
            {
                assistantMessage = assistantMessages[^1];
                return true;
            }
        }

        assistantMessage = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String)
        {
            return valueElement.GetString();
        }

        return null;
    }

    private static string? TryGetScalarString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var valueElement))
        {
            return null;
        }

        return valueElement.ValueKind switch
        {
            JsonValueKind.String => valueElement.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => valueElement.GetRawText(),
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.Number
            && valueElement.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var valueElement)
            && (valueElement.ValueKind == JsonValueKind.True || valueElement.ValueKind == JsonValueKind.False))
        {
            return valueElement.GetBoolean();
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void AddSessionId(Dictionary<string, object?> payload, string? sessionId)
    {
        AddIfNotEmpty(payload, "session_id", sessionId);
        AddIfNotEmpty(payload, "sessionId", sessionId);
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }

    private static void AddIfPresent<T>(Dictionary<string, object?> payload, string key, T? value)
        where T : struct
    {
        if (value.HasValue)
        {
            payload[key] = value.Value;
        }
    }

    private static void AddJsonPropertyIfPresent(
        Dictionary<string, object?> payload,
        string targetKey,
        JsonElement? source,
        string sourcePropertyName)
    {
        if (source is not { ValueKind: JsonValueKind.Object } sourceElement
            || !sourceElement.TryGetProperty(sourcePropertyName, out var valueElement)
            || valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        payload[targetKey] = JsonSerializer.SerializeToElement(valueElement);
    }

    private sealed record ToolCallSnapshot(string Name, string? ArgumentsJson);

    private sealed class PendingToolCallDelta
    {
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
        public string? ArgumentsJson { get; set; }
    }
}
