using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

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
            var exitCode = await InstallClisAsync();
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

    private static async Task<int> InstallClisAsync()
    {
        System.Console.WriteLine("Installing publicly available CLI tools...");

        var npmExecutable = ResolveNpmExecutable();
        if (npmExecutable is null)
        {
            System.Console.Error.WriteLine("  [FAIL] npm was not found on PATH. Ensure Node.js is installed before running CI setup.");
            return 1;
        }

        var hadErrors = false;
        var processManager = new CliProcessManager();

        foreach (var descriptor in CliInstallRegistry.PubliclyInstallable)
        {
            System.Console.WriteLine($"  [INSTALL] {descriptor.FullPackageSpecifier}...");
            var result = await processManager.ExecuteAsync(new ProcessStartContext
            {
                ExecutablePath = npmExecutable,
                Arguments = ["install", "--global", descriptor.FullPackageSpecifier]
            });

            if (result.ExitCode == 0)
            {
                System.Console.WriteLine($"  [OK] {descriptor.ProviderName} installed successfully");
            }
            else
            {
                System.Console.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} installation failed (exit code {result.ExitCode})");
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    System.Console.Error.WriteLine($"  {result.StandardError.Trim()}");
                }

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

        foreach (var descriptor in CliInstallRegistry.PubliclyInstallable)
        {
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

    private static string? ResolveNpmExecutable()
    {
        var resolver = new CliExecutableResolver();
        return resolver.ResolveFirstAvailablePath(["npm", "npm.cmd", "npm.exe", "npm.bat"]);
    }
}
