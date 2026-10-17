namespace HagiCode.Libs.Git.Internal;

/// <summary>
/// Classifies failed remote and rebase commands from their output. The substring rules are the ones the backend has used
/// since the LibGit2Sharp era (<c>ClassifyPushFailure</c>, <c>ClassifyPullFailure</c>, <c>LooksLikeRebaseConflict</c>).
/// Git runs with <c>LC_ALL=C</c>, so these strings match regardless of the host language.
/// </summary>
internal static class GitFailureClassifier
{
    private static readonly string[] AuthenticationMarkers =
    [
        "authentication failed",
        "could not read username",
        "could not read password",
        "terminal prompts disabled",
        "invalid username or password",
        "permission denied (publickey)"
    ];

    public static GitFailureCode ClassifyPush(string details)
    {
        if (ContainsAny(details, "has no upstream branch", "no upstream configured", "set-upstream"))
        {
            return GitFailureCode.NoUpstream;
        }

        if (ContainsAny(details, AuthenticationMarkers))
        {
            return GitFailureCode.AuthenticationFailed;
        }

        if (ContainsAny(details, "non-fast-forward", "fetch first"))
        {
            return GitFailureCode.NonFastForward;
        }

        if (ContainsAny(
                details,
                "remote rejected",
                "rejected",
                "failed to push some refs",
                "protected branch hook declined",
                "permission denied",
                "access denied",
                "unable to access"))
        {
            return GitFailureCode.RemoteRejected;
        }

        return GitFailureCode.CommandFailed;
    }

    public static GitFailureCode ClassifyPull(string details)
    {
        if (ContainsAny(details, "no tracking information", "no upstream configured"))
        {
            return GitFailureCode.NoUpstream;
        }

        if (ContainsAny(details, "not possible to fast-forward", "--ff-only", "fast-forward"))
        {
            return GitFailureCode.FastForwardRequired;
        }

        if (ContainsAny(details, AuthenticationMarkers))
        {
            return GitFailureCode.AuthenticationFailed;
        }

        if (ContainsAny(details, "permission denied", "access denied", "unable to access"))
        {
            return GitFailureCode.RemoteRejected;
        }

        return GitFailureCode.CommandFailed;
    }

    public static bool LooksLikeRebaseConflict(string details)
    {
        return ContainsAny(
            details,
            "CONFLICT",
            "could not apply",
            "Resolve all conflicts manually",
            "fix conflicts and then run");
    }

    private static bool ContainsAny(string? source, params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return markers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
