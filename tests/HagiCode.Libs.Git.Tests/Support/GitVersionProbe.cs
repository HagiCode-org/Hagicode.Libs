namespace HagiCode.Libs.Git.Tests.Support;

/// <summary>Reads <c>git --version</c> for timing metadata. Returns <c>unavailable</c> when git cannot run.</summary>
public static class GitVersionProbe
{
    public static string Read()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "hagicode-git-version-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var result = new GitCli(new Dictionary<string, string?>()).Run(root, "--version");
                return result.ExitCode == 0 ? result.StandardOutput.Trim() : "unavailable";
            }
            finally
            {
                TestGitEnvironment.DeleteTree(root);
            }
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }
}
