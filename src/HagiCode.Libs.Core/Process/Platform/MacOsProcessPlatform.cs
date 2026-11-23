using System.Diagnostics;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Reads processes on macOS through <c>ps</c>. <c>ps</c> needs no native interop and no extra entitlement.
/// Every <c>ps</c> run uses <c>LC_ALL=C</c> and <c>TZ=UTC</c> so that <c>lstart</c> parses as a fixed UTC form.
/// </summary>
/// <remarks>
/// A real macOS run is not verified in this repository: no macOS host is available here. Parser behaviour is covered by tests on recorded <c>ps</c> output.
/// </remarks>
internal sealed class MacOsProcessPlatform : IProcessPlatform
{
    private const string PsPath = "/bin/ps";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public IReadOnlyList<ProcessTableRow> ReadProcessTable()
    {
        var (exitCode, output) = RunPs("-A", "-o", "pid=,ppid=,comm=");
        if (exitCode != 0)
        {
            return [];
        }

        var rows = new List<ProcessTableRow>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // A malformed line is skipped rather than failing the whole table.
            if (PsOutputParser.ParseTableLine(line) is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public ProcessDetails? ReadDetails(int pid, bool includeCommandLine)
    {
        var (exitCode, output) = RunPs("-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "pid=,ppid=,lstart=,rss=,time=,comm=");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var detail = PsOutputParser.ParseDetailLine(output.Trim());
        if (detail is null)
        {
            return new ProcessDetails(null, null, null, null, null, ProcessAccessibility.Denied);
        }

        var accessibility = ProcessAccessibility.Accessible;
        string? commandLine = null;
        if (includeCommandLine)
        {
            var (commandExitCode, commandOutput) = RunPs("-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "command=");
            commandLine = commandExitCode == 0 && !string.IsNullOrWhiteSpace(commandOutput) ? commandOutput.Trim() : null;
            if (commandLine is null)
            {
                accessibility = ProcessAccessibility.Partial;
            }
        }

        return new ProcessDetails(detail.StartedAtUtc, detail.CpuTime, detail.MemoryBytes, commandLine, detail.Name, accessibility);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ReadContainmentMembers(int rootPid) => [];

    /// <inheritdoc />
    public ProcessKillResult Kill(int pid) => PosixProcessControl.Kill(pid);

    private static (int ExitCode, string Output) RunPs(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PsPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["TZ"] = "UTC";

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(RunTimeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, string.Empty);
            }

            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
}
