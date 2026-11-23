namespace HagiCode.Libs.Core.Process.Termination;

/// <summary>
/// Describes the typed outcome of a termination request.
/// </summary>
public enum CliProcessTerminationOutcome
{
    /// <summary>
    /// The process, or every process in the tree, was ended.
    /// </summary>
    Terminated,

    /// <summary>
    /// No live process had the identity, or it exited before it could be signalled.
    /// </summary>
    AlreadyExited,

    /// <summary>
    /// The operating system denied access, or the start time could not be read, so the identity cannot be proven. Nothing was terminated.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// A live process has the same id but a different start time. Nothing with that id was terminated.
    /// </summary>
    IdentityMismatch,

    /// <summary>
    /// The root was ended, but at least one descendant was not ended because it was denied or failed.
    /// </summary>
    PartiallyTerminated,

    /// <summary>
    /// Termination failed for a reason that is not one of the expected outcomes. <see cref="CliProcessTerminationResult.Message" /> says why.
    /// </summary>
    Failed,
}

/// <summary>
/// Describes how a termination request was carried out.
/// </summary>
public enum CliProcessTerminationMethod
{
    /// <summary>
    /// One process was ended directly.
    /// </summary>
    Single,

    /// <summary>
    /// The tree was walked by parent links and each process was ended, leaves first.
    /// </summary>
    TreeWalk,

    /// <summary>
    /// The tree was ended by terminating its Windows Job Object, which ends every member including orphans.
    /// </summary>
    Containment,
}

/// <summary>
/// Bounds how long termination waits for a killed process to disappear.
/// </summary>
public sealed record CliProcessTerminationOptions
{
    /// <summary>
    /// Gets the maximum time to wait for each killed process to exit. The default is five seconds.
    /// </summary>
    public TimeSpan ExitWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Describes the outcome for one process that termination attempted or checked.
/// </summary>
/// <param name="Identity">The identity that was attempted.</param>
/// <param name="Outcome">The outcome for that process.</param>
/// <param name="Message">A short explanation, when the outcome is not <see cref="CliProcessTerminationOutcome.Terminated" />.</param>
public sealed record CliProcessTerminationItem(
    ProcessIdentity Identity,
    CliProcessTerminationOutcome Outcome,
    string? Message);

/// <summary>
/// Describes the overall result of a termination request.
/// </summary>
/// <param name="Target">The identity that was requested.</param>
/// <param name="Outcome">The overall outcome.</param>
/// <param name="Method">How the request was carried out.</param>
/// <param name="Items">One entry per process attempted or checked, leaves first.</param>
/// <param name="Message">A short explanation, when useful.</param>
public sealed record CliProcessTerminationResult(
    ProcessIdentity Target,
    CliProcessTerminationOutcome Outcome,
    CliProcessTerminationMethod Method,
    IReadOnlyList<CliProcessTerminationItem> Items,
    string? Message);
