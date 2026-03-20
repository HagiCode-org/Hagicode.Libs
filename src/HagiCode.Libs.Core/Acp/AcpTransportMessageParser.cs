namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Splits and sanitizes raw ACP payload frames.
/// </summary>
public static class AcpTransportMessageParser
{
    /// <summary>
    /// Removes leading comment preambles from a payload.
    /// </summary>
    /// <param name="messageText">The raw payload.</param>
    /// <param name="ignoredComment">Any stripped comment text.</param>
    /// <returns>The sanitized JSON payload, or <see langword="null" /> when no JSON remains.</returns>
    public static string? SanitizeIncomingMessage(string messageText, out string? ignoredComment)
    {
        ignoredComment = null;
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return null;
        }

        using var reader = new StringReader(messageText);
        var sanitizedLines = new List<string>();
        var ignoredComments = new List<string>();
        var sawContent = false;

        while (reader.ReadLine() is { } line)
        {
            if (!sawContent)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (IsCommentLine(line))
                {
                    ignoredComments.Add(line.Trim());
                    continue;
                }

                sawContent = true;
            }

            sanitizedLines.Add(line);
        }

        if (ignoredComments.Count > 0)
        {
            ignoredComment = string.Join(" | ", ignoredComments);
        }

        return sanitizedLines.Count == 0 ? null : string.Join(System.Environment.NewLine, sanitizedLines);
    }

    /// <summary>
    /// Splits a transport frame into comment, JSON, and trailing fragments.
    /// </summary>
    /// <param name="messageText">The raw frame.</param>
    /// <returns>The split payload fragments.</returns>
    public static IReadOnlyList<string> SplitIncomingPayloads(string messageText)
    {
        var payloads = new List<string>();
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return payloads;
        }

        var span = messageText.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            while (index < span.Length && char.IsWhiteSpace(span[index]))
            {
                index++;
            }

            if (index >= span.Length)
            {
                break;
            }

            if (span[index] == '/' && index + 1 < span.Length && span[index + 1] == '/')
            {
                var lineEnd = index;
                while (lineEnd < span.Length && span[lineEnd] != '\n' && span[lineEnd] != '\r')
                {
                    lineEnd++;
                }

                payloads.Add(messageText[index..lineEnd]);
                index = lineEnd;
                continue;
            }

            if (TryReadJsonPayload(span, index, out var payloadEnd))
            {
                payloads.Add(messageText[index..payloadEnd]);
                index = payloadEnd;
                continue;
            }

            var fragmentEnd = index;
            while (fragmentEnd < span.Length && span[fragmentEnd] != '\n' && span[fragmentEnd] != '\r')
            {
                fragmentEnd++;
            }

            payloads.Add(messageText[index..fragmentEnd]);
            index = fragmentEnd;
        }

        return payloads;
    }

    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 1 && trimmed[0] == '/' && trimmed[1] == '/';
    }

    private static bool TryReadJsonPayload(ReadOnlySpan<char> source, int startIndex, out int payloadEnd)
    {
        payloadEnd = startIndex;
        if (startIndex >= source.Length)
        {
            return false;
        }

        var opener = source[startIndex];
        if (opener != '{' && opener != '[')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = startIndex; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{' || current == '[')
            {
                depth++;
                continue;
            }

            if (current == '}' || current == ']')
            {
                depth--;
                if (depth == 0)
                {
                    payloadEnd = index + 1;
                    return true;
                }
            }
        }

        return false;
    }
}
