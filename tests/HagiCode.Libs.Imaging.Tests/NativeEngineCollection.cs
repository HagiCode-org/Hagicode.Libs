namespace HagiCode.Libs.Imaging.Tests;

/// <summary>
/// Every test class that loads libvips runs in this collection, one class at a time. Process-wide allocator and
/// thread-pool growth would otherwise distort the memory bound, and tests that change PATH must not overlap.
/// </summary>
[CollectionDefinition("NativeEngine", DisableParallelization = true)]
public sealed class NativeEngineCollection
{
}
