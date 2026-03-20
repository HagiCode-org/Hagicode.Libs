using System.Diagnostics;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.CiSetup.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var doInstall = args.Contains("--install");
        var doVerify = args.Contains("--verify");

        if (!doInstall && !doVerify)
        {
            System.Console.Error.WriteLine("Usage: CiSetup.Console [--install] [--verify]");
            System.Console.Error.WriteLine("  --install  Install publicly available CLI tools via npm");
            System.Console.Error.WriteLine("  --verify   Verify CLI tools are on PATH");
            return 1;
        }

        if (doInstall)
        {
            var exitCode = InstallClis();
            if (exitCode != 0)
            {
                return exitCode;
            }
        }

        if (doVerify)
        {
            return VerifyClis();
        }

        return 0;
    }

    private static int InstallClis()
    {
        System.Console.WriteLine("Installing publicly available CLI tools...");

        var hadErrors = false;

        foreach (var descriptor in CliInstallRegistry.Descriptors)
        {
            if (!descriptor.IsPubliclyInstallable)
            {
                System.Console.WriteLine($"  [SKIP] {descriptor.ProviderName} (not publicly installable)");
                continue;
            }

            System.Console.WriteLine($"  [INSTALL] {descriptor.FullPackageSpecifier}...");
            var result = RunProcess("npm", $"install --global \"{descriptor.FullPackageSpecifier}\"");

            if (result.ExitCode == 0)
            {
                System.Console.WriteLine($"  [OK] {descriptor.ProviderName} installed successfully");
            }
            else
            {
                System.Console.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} installation failed (exit code {result.ExitCode})");
                System.Console.Error.WriteLine($"  {result.Error}");
                hadErrors = true;
            }
        }

        return hadErrors ? 1 : 0;
    }

    private static int VerifyClis()
    {
        System.Console.WriteLine("Verifying CLI tool availability on PATH...");

        var resolver = new CliExecutableResolver();
        var hadFailures = false;

        foreach (var descriptor in CliInstallRegistry.Descriptors)
        {
            if (!descriptor.IsPubliclyInstallable)
            {
                System.Console.WriteLine($"  [SKIP] {descriptor.ProviderName} (not publicly installable)");
                continue;
            }

            var path = resolver.ResolveFirstAvailablePath(descriptor.ExecutableCandidates);
            if (path is not null)
            {
                System.Console.WriteLine($"  [OK] {descriptor.ProviderName} found at: {path}");
            }
            else
            {
                System.Console.Error.WriteLine($"  [WARN] {descriptor.ProviderName} not found on PATH (candidates: {string.Join(", ", descriptor.ExecutableCandidates)})");
                hadFailures = true;
            }
        }

        return hadFailures ? 1 : 0;
    }

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, process.StandardError.ReadToEnd());
    }

    private sealed record ProcessResult(int ExitCode, string Error);
}
