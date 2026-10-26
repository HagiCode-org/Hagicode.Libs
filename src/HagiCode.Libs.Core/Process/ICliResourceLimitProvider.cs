namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Supplies the memory and CPU limits for agent CLI launches. The launch layer asks once per launch, so a changed limit applies
/// to later launches only.
/// </summary>
public interface ICliResourceLimitProvider
{
    /// <summary>
    /// Gets the limit for the next launch. The limit may carry a memory size, a CPU percentage or both; a part that is
    /// <see langword="null" /> is not limited.
    /// </summary>
    /// <returns>The active limit, or <see langword="null" /> when agents run without one.</returns>
    CliResourceLimits? GetCurrent();
}
