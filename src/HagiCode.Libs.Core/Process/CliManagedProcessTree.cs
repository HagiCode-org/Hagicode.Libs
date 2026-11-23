using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Pairs one alive managed process with the snapshot of its process tree.
/// </summary>
/// <param name="Process">The managed process that is the root of the tree.</param>
/// <param name="Tree">The captured tree under that process.</param>
public sealed record CliManagedProcessTree(CliManagedProcessInfo Process, ProcessTreeRootSnapshot Tree);
