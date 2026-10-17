using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Classification rules and URL comparison, checked on captured messages without running git.</summary>
[TestFixture]
[Category(TestTiers.Integration)]
public sealed class FailureClassifierTests
{
    [TestCase("fatal: The current branch main has no upstream branch.", GitFailureCode.NoUpstream)]
    [TestCase("fatal: could not read Username for 'https://example.com': terminal prompts disabled", GitFailureCode.AuthenticationFailed)]
    [TestCase(" ! [rejected] main -> main (fetch first)", GitFailureCode.NonFastForward)]
    [TestCase("remote: error: protected branch hook declined", GitFailureCode.RemoteRejected)]
    [TestCase("fatal: something unexpected", GitFailureCode.CommandFailed)]
    public void Push_messages_map_to_their_codes(string details, GitFailureCode expected)
    {
        Assert.That(GitFailureClassifier.ClassifyPush(details), Is.EqualTo(expected));
    }

    [TestCase("There is no tracking information for the current branch.", GitFailureCode.NoUpstream)]
    [TestCase("fatal: Not possible to fast-forward, aborting.", GitFailureCode.FastForwardRequired)]
    [TestCase("fatal: Authentication failed for 'https://example.com/x.git'", GitFailureCode.AuthenticationFailed)]
    [TestCase("ERROR: Permission denied", GitFailureCode.RemoteRejected)]
    [TestCase("fatal: boom", GitFailureCode.CommandFailed)]
    public void Pull_messages_map_to_their_codes(string details, GitFailureCode expected)
    {
        Assert.That(GitFailureClassifier.ClassifyPull(details), Is.EqualTo(expected));
    }

    [TestCase("CONFLICT (content): Merge conflict in README.md", true)]
    [TestCase("error: could not apply 1234abc", true)]
    [TestCase("Everything up to date", false)]
    public void Rebase_conflict_markers_are_recognized(string details, bool expected)
    {
        Assert.That(GitFailureClassifier.LooksLikeRebaseConflict(details), Is.EqualTo(expected));
    }

    [TestCase("https://example.com/team/app.git", "https://example.com/team/app", true)]
    [TestCase("https://EXAMPLE.com/team/app/", "https://example.com/team/app", true)]
    [TestCase("https://example.com/team/app", "https://example.com/team/other", false)]
    [TestCase(null, "https://example.com/team/app", false)]
    public void Remote_urls_compare_after_normalization(string? left, string right, bool expected)
    {
        Assert.That(GitRemoteUrlComparer.Matches(left, right), Is.EqualTo(expected));
    }
}
