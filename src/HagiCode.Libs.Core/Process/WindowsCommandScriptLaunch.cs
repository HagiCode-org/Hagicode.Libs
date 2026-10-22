using System.Diagnostics;
using System.Text;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The <c>cmd.exe</c> start information for one Windows command script (<c>.cmd</c> or <c>.bat</c>).
/// </summary>
/// <param name="FileName">The <c>cmd.exe</c> in the Windows system directory.</param>
/// <param name="Arguments">The verified argument string for <c>/d /v:off /s /c "..."</c>.</param>
internal sealed record WindowsCommandScriptStart(string FileName, string Arguments);

/// <summary>
/// Builds and verifies the <c>cmd.exe</c> command line that runs a resolved Windows command script.
/// Each argument is escaped for the C runtime and quoted for <c>cmd.exe</c>, then the whole line is walked with a model of
/// <c>cmd.exe</c> parsing. An argument that could make a live shell metacharacter or a variable expansion is rejected before any process starts.
/// </summary>
internal static class WindowsCommandScriptLaunch
{
    private const string ShellFileName = "cmd.exe";
    private const string ShellArgumentsPrefix = "/d /v:off /s /c \"";
    private const string ShellArgumentsSuffix = "\"";
    private const string LiveMetacharacters = "&|<>^()";
    private const string QuotingMetacharacters = "&|<>^()\",;=";

    /// <summary>
    /// Sets the executable and arguments of a start information. A Windows command script is started through the verified <c>cmd.exe</c> launch.
    /// Every other path keeps <see cref="ProcessStartInfo.FileName" /> and <see cref="ProcessStartInfo.ArgumentList" />.
    /// </summary>
    /// <param name="startInfo">The start information to configure.</param>
    /// <param name="executablePath">The resolved CLI path.</param>
    /// <param name="arguments">The arguments, in order.</param>
    /// <param name="isWindows">Whether the launch runs on Windows.</param>
    internal static void ConfigureExecutable(ProcessStartInfo startInfo, string executablePath, IReadOnlyList<string> arguments, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (isWindows && IsCommandScript(executablePath))
        {
            var launch = Create(executablePath, arguments);
            startInfo.FileName = launch.FileName;
            startInfo.Arguments = launch.Arguments;
            return;
        }

        startInfo.FileName = executablePath;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    /// <summary>
    /// Determines whether a path is a Windows command script (<c>.cmd</c> or <c>.bat</c>).
    /// </summary>
    /// <param name="executablePath">The path to test.</param>
    /// <returns><see langword="true" /> for a command script; otherwise <see langword="false" />.</returns>
    internal static bool IsCommandScript(string executablePath)
    {
        var extension = Path.GetExtension(executablePath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the start information for a command script, using the <c>cmd.exe</c> in the Windows system directory.
    /// </summary>
    /// <param name="scriptPath">The resolved <c>.cmd</c> or <c>.bat</c> path.</param>
    /// <param name="arguments">The arguments to pass to the script, in order.</param>
    /// <returns>The shell file name and the argument string.</returns>
    /// <exception cref="CliCommandScriptArgumentException">An argument cannot be made inert for <c>cmd.exe</c>.</exception>
    internal static WindowsCommandScriptStart Create(string scriptPath, IReadOnlyList<string> arguments)
    {
        return Create(scriptPath, arguments, System.Environment.SystemDirectory);
    }

    /// <summary>
    /// Creates the start information for a command script, using the <c>cmd.exe</c> in the supplied system directory.
    /// </summary>
    /// <param name="scriptPath">The resolved <c>.cmd</c> or <c>.bat</c> path.</param>
    /// <param name="arguments">The arguments to pass to the script, in order.</param>
    /// <param name="systemDirectory">The Windows system directory that holds <c>cmd.exe</c>.</param>
    /// <returns>The shell file name and the argument string.</returns>
    /// <exception cref="CliCommandScriptArgumentException">An argument cannot be made inert for <c>cmd.exe</c>.</exception>
    internal static WindowsCommandScriptStart Create(string scriptPath, IReadOnlyList<string> arguments, string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(systemDirectory);

        ValidateScriptPath(scriptPath);

        var line = new StringBuilder();
        line.Append('"').Append(scriptPath).Append('"');
        VerifySegmentInert(line.ToString(), argumentIndex: null);

        for (var index = 0; index < arguments.Count; index++)
        {
            var escaped = EscapeArgument(arguments[index], index);
            VerifySegmentInert(escaped, index);
            line.Append(' ').Append(escaped);
        }

        var fileName = Path.Combine(systemDirectory, ShellFileName);
        return new WindowsCommandScriptStart(fileName, ShellArgumentsPrefix + line + ShellArgumentsSuffix);
    }

    /// <summary>
    /// Escapes one argument for the C runtime and quotes it for <c>cmd.exe</c>. Quoting applies when the value is empty or holds
    /// whitespace or one of <c>&amp; | &lt; &gt; ^ ( ) " , ; =</c>.
    /// </summary>
    /// <param name="value">The raw argument.</param>
    /// <param name="argumentIndex">The argument index, used only for the rejection message.</param>
    /// <returns>The escaped argument text.</returns>
    internal static string EscapeArgument(string value, int argumentIndex)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateArgument(value, argumentIndex);

        var wrap = value.Length == 0 || value.Any(static character => char.IsWhiteSpace(character) || QuotingMetacharacters.Contains(character));
        var builder = new StringBuilder(value.Length + 2);
        if (wrap)
        {
            builder.Append('"');
        }

        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
            }

            backslashes = 0;
        }

        // Trailing backslashes are literal only outside quotes, so they are doubled when the closing quote follows.
        builder.Append('\\', wrap ? backslashes * 2 : backslashes);
        if (wrap)
        {
            builder.Append('"');
        }

        return builder.ToString();
    }

    private static void ValidateScriptPath(string scriptPath)
    {
        if (scriptPath.Any(static character => character is '\0' or '\r' or '\n' or '"' or '%'))
        {
            throw new CliCommandScriptArgumentException(
                argumentIndex: null,
                rule: "the script path contains a control character, a quote or a percent sign");
        }
    }

    private static void ValidateArgument(string value, int argumentIndex)
    {
        if (value.Any(static character => character is '\0' or '\r' or '\n'))
        {
            throw new CliCommandScriptArgumentException(argumentIndex, "it contains a NUL, carriage return or line feed");
        }

        if (value.Contains('%'))
        {
            throw new CliCommandScriptArgumentException(argumentIndex, "it contains '%', which cmd.exe expands even inside quotes");
        }

        if (value.Count(static character => character == '"') % 2 != 0)
        {
            throw new CliCommandScriptArgumentException(argumentIndex, "it has an odd number of double quotes");
        }
    }

    /// <summary>
    /// Walks one escaped segment with the <c>cmd.exe</c> quote model: each <c>"</c> toggles the quote state, and outside quotes
    /// <c>&amp; | &lt; &gt; ^ ( )</c> are live. The segment starts and ends outside quotes.
    /// </summary>
    private static void VerifySegmentInert(string segment, int? argumentIndex)
    {
        var insideQuotes = false;
        foreach (var character in segment)
        {
            if (character == '"')
            {
                insideQuotes = !insideQuotes;
                continue;
            }

            if (!insideQuotes && LiveMetacharacters.Contains(character))
            {
                throw new CliCommandScriptArgumentException(
                    argumentIndex,
                    "it contains a shell metacharacter that cmd.exe would read outside quotes");
            }
        }

        if (insideQuotes)
        {
            throw new CliCommandScriptArgumentException(argumentIndex, "its quoting does not close");
        }
    }
}
