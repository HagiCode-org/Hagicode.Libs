using System.Text;

namespace HagiCode.Libs.Core.Process;

internal static class CommandPreviewFormatter
{
    public static string Format(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIfNeeded(executablePath));

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteIfNeeded(argument));
        }

        return builder.ToString();
    }

    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
