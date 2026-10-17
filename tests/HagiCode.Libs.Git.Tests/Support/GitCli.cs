using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HagiCode.Libs.Git.Tests.Support;

/// <summary>
/// Runs the real <c>git</c> executable directly, outside the wrapper. Used to build fixtures and to verify what the wrapper
/// did. Calls made here are fixture setup and are never timed.
/// </summary>
public sealed class GitCli
{
    private readonly IReadOnlyDictionary<string, string?> _environment;

    public GitCli(IReadOnlyDictionary<string, string?> environment)
    {
        _environment = environment;
    }

    /// <summary>Runs git and returns the captured streams. Does not throw for a non-zero exit code.</summary>
    public GitCliResult Run(string? workingDirectory, params string[] arguments)
    {
        return RunWithInput(workingDirectory, stdin: null, arguments);
    }

    /// <summary>Runs git with the given text on stdin.</summary>
    public GitCliResult RunWithInput(string? workingDirectory, string? stdin, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in _environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("git could not be started for test setup.", exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.Write(stdin ?? string.Empty);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // git exited before reading stdin; its exit code describes the outcome.
        }
        process.WaitForExit();

        return new GitCliResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>Runs git and throws with the captured output when it exits non-zero. Returns trimmed stdout.</summary>
    public string Must(string? workingDirectory, params string[] arguments)
    {
        var result = Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        }

        return result.StandardOutput.TrimEnd('\r', '\n');
    }
}

/// <summary>The captured result of a direct git invocation.</summary>
public sealed record GitCliResult(int ExitCode, string StandardOutput, string StandardError);
