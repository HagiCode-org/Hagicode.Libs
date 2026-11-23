namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Configures persisted ownership tracking for managed CLI subprocesses.
/// </summary>
public sealed class CliProcessOwnershipOptions
{
    /// <summary>
    /// Configuration section name used by hosts that bind these options.
    /// </summary>
    public const string SectionName = "AI:CliProcessOwnership";

    /// <summary>
    /// Gets or sets a value indicating whether ownership persistence is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the local JSON file path used to persist owned subprocess metadata.
    /// </summary>
    public string? StateFilePath { get; set; }

    /// <summary>
    /// Gets or sets the tolerance used when comparing persisted and live process start times.
    /// </summary>
    public TimeSpan StartTimeTolerance { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets a value indicating whether agent CLI processes that carry ownership metadata are placed in a Windows Job Object on start.
    /// The Job ends every member, including orphaned descendants, when the process stops or its handle is disposed.
    /// Set to <see langword="false" /> to keep the previous behavior. Ignored on other platforms.
    /// </summary>
    public bool WindowsJobContainment { get; set; } = true;
}
