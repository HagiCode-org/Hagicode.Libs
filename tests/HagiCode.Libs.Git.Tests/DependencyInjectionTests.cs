using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.4: the registration extension and the package's dependency boundary.</summary>
[TestFixture]
public sealed class DependencyInjectionTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public void AddHagiCodeGit_resolves_IGitClient_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeGit();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IGitClient>();
        var second = provider.GetRequiredService<IGitClient>();

        Assert.That(first, Is.InstanceOf<GitClient>());
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public async Task AddHagiCodeGit_applies_the_configured_options()
    {
        var repository = NewRepositoryWithCommit();
        var services = new ServiceCollection();
        services.AddHagiCodeGit(options =>
        {
            options.GitExecutablePath = Path.Combine(Environment.Root, "not-a-git");
        });

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IGitClient>().GetStatusAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.GitUnavailable),
            "the configured path does not exist, so the configured option must have been used");
    }

    [Test]
    public void The_package_has_no_reference_to_backend_assemblies()
    {
        var references = typeof(IGitClient).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        Assert.That(references.Where(name => name.StartsWith("PCode", StringComparison.Ordinal)), Is.Empty);
        Assert.That(references, Does.Contain("HagiCode.Libs.Core"));
    }

    /// <summary>The README sample, kept identical so it is verified on every run.</summary>
    [Test]
    public async Task Readme_usage_sample_runs_as_documented()
    {
        var repository = NewRepositoryWithCommit();
        var services = new ServiceCollection();
        services.AddHagiCodeGit(options =>
        {
            // Optional. Defaults: git is resolved from PATH; 2 min for local commands, 10 min for network commands.
            options.CommandTimeout = TimeSpan.FromMinutes(2);
        });

        using var provider = services.BuildServiceProvider();
        IGitClient git = provider.GetRequiredService<IGitClient>();

        var status = await git.GetStatusAsync(repository);
        if (status.Success)
        {
            Console.WriteLine($"{status.Value!.Branch}: {status.Value.Entries.Count} changed paths");
        }
        else
        {
            Console.WriteLine($"{status.Failure!.Code}: {status.Failure.Message}");
        }

        Assert.That(status.Success, Is.True);
    }
}
