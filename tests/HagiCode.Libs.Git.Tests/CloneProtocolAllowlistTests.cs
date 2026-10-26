using HagiCode.Libs.Git.Internal;
using HagiCode.Libs.Git.Tests.Timing;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.6: the clone protocol allowlist, checked against real git. No test contacts a real host.</summary>
[TestFixture]
public sealed class CloneProtocolAllowlistTests : GitTestBase
{
    private static readonly string[] NetworkProtocols = ["https", "http", "ssh", "git"];

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task File_url_clones_when_file_is_in_the_allowlist()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "from-file-url");

        var result = await Client.EnsureCloneAsync(new Uri(remote).AbsoluteUri, target, new GitCloneOptions(["file"]));

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(File.Exists(Path.Combine(target, "README.md")), Is.True);
    }

    [Test]
    public async Task Plain_path_is_treated_as_file_and_clones_when_file_is_allowed()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "from-plain-path");

        var result = await Client.EnsureCloneAsync(remote, target, new GitCloneOptions(["file"]));

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(File.Exists(Path.Combine(target, "README.md")), Is.True);
    }

    [Test]
    public async Task Plain_path_is_refused_when_file_is_not_allowed_and_nothing_is_created()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "refused-plain-path");
        var (client, runner) = CountingClient();

        var result = await client.EnsureCloneAsync(remote, target, new GitCloneOptions(NetworkProtocols));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnsupportedProtocol));
        Assert.That(result.Failure.Message, Does.Contain("'file'"));
        Assert.That(Directory.Exists(target), Is.False, "no directory is created for a refused remote");
        Assert.That(runner.StartedCount, Is.Zero, "no git process is started for a refused remote");
    }

    [Test]
    public async Task File_url_is_refused_by_an_https_http_ssh_git_list_and_nothing_is_created()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "refused-file-url");
        var (client, runner) = CountingClient();

        var result = await client.EnsureCloneAsync(new Uri(remote).AbsoluteUri, target, new GitCloneOptions(NetworkProtocols));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnsupportedProtocol));
        Assert.That(result.Failure.Message, Does.Contain("'file'"));
        Assert.That(Directory.Exists(target), Is.False);
        Assert.That(runner.StartedCount, Is.Zero);
    }

    [Test]
    public async Task Ext_remote_is_refused_without_running_its_command()
    {
        var marker = Path.Combine(Environment.Root, "ext-ran.txt");
        var target = Path.Combine(Environment.Root, "clones", "refused-ext");
        var (client, runner) = CountingClient();

        var result = await client.EnsureCloneAsync($"ext::sh -c touch% {marker}", target, new GitCloneOptions(NetworkProtocols));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnsupportedProtocol));
        Assert.That(result.Failure.Message, Does.Contain("'ext'"));
        Assert.That(Directory.Exists(target), Is.False);
        Assert.That(File.Exists(marker), Is.False, "the helper command must never run");
        Assert.That(runner.StartedCount, Is.Zero);
    }

    [Test]
    public async Task Scp_style_remote_is_treated_as_ssh_and_refused_when_only_https_is_allowed()
    {
        var target = Path.Combine(Environment.Root, "clones", "refused-scp");
        var (client, runner) = CountingClient();

        var result = await client.EnsureCloneAsync("git@host.invalid:org/repo.git", target, new GitCloneOptions(["https"]));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnsupportedProtocol));
        Assert.That(result.Failure.Message, Does.Contain("'ssh'"));
        Assert.That(Directory.Exists(target), Is.False);
        Assert.That(runner.StartedCount, Is.Zero);
    }

    [Test]
    public async Task Scp_style_remote_passes_the_precheck_when_ssh_is_allowed()
    {
        // GIT_SSH_COMMAND=false makes the ssh transport fail at once, so no host is ever contacted.
        var variables = new Dictionary<string, string?>(Environment.Variables) { ["GIT_SSH_COMMAND"] = "false" };
        var client = new TimedGitClient(new GitClient(new GitClientOptions { EnvironmentVariables = variables }));
        var target = Path.Combine(Environment.Root, "clones", "scp-allowed");

        var result = await client.EnsureCloneAsync("git@host.invalid:org/repo.git", target, new GitCloneOptions(["ssh"]));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneFailed), "the ssh transport ran and failed, so the pre-check passed");
        Assert.That(Directory.Exists(target), Is.False, "a failed clone leaves no partial folder");
    }

    [Test]
    public async Task Https_remote_passes_the_precheck_when_https_is_allowed()
    {
        // Port 1 on loopback refuses at once, so git reaches its transport and fails without any external contact.
        var variables = new Dictionary<string, string?>(Environment.Variables)
        {
            ["HTTPS_PROXY"] = null,
            ["https_proxy"] = null,
            ["ALL_PROXY"] = null,
            ["all_proxy"] = null
        };
        var client = new TimedGitClient(new GitClient(new GitClientOptions { EnvironmentVariables = variables }));
        var target = Path.Combine(Environment.Root, "clones", "https-allowed");

        var result = await client.EnsureCloneAsync("https://127.0.0.1:1/org/repo.git", target, new GitCloneOptions(["https"]));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneFailed));
        Assert.That(Directory.Exists(target), Is.False);
    }

    [Test]
    public async Task No_allowlist_keeps_the_existing_behavior_for_the_options_overload()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "no-list");

        var result = await Client.EnsureCloneAsync(remote, target, new GitCloneOptions());

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.ReusedExistingRepository, Is.False);
    }

    [Test]
    public async Task Existing_overload_without_options_still_clones_a_plain_path()
    {
        var remote = NewSeededRemote();
        var target = Path.Combine(Environment.Root, "clones", "legacy-overload");

        var result = await Client.EnsureCloneAsync(remote, target);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
    }

    [Test]
    public async Task An_invalid_allowed_protocol_name_is_an_argument_error_and_nothing_is_created()
    {
        var target = Path.Combine(Environment.Root, "clones", "bad-name");

        var result = await Client.EnsureCloneAsync("https://example.invalid/x.git", target, new GitCloneOptions(["https;x"]));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
        Assert.That(Directory.Exists(target), Is.False);
    }

    private (GitClient Client, GitProcessRunner Runner) CountingClient()
    {
        var runner = new GitProcessRunner(Environment.Variables);
        var client = new GitClient(new GitClientOptions { EnvironmentVariables = Environment.Variables }, resolver: null, runner);
        return (client, runner);
    }

    private string NewSeededRemote()
    {
        var remote = NewBareRemote($"remote-{Guid.NewGuid():N}.git");
        var seed = NewRepositoryWithCommit($"seed-{Guid.NewGuid():N}");
        Git.Must(seed, "remote", "add", "origin", remote);
        Git.Must(seed, "push", "--quiet", "-u", "origin", "main");
        return remote;
    }
}
