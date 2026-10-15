namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>Access to the committed fixture images that are copied next to the test assembly.</summary>
internal static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    internal static string PathOf(string name) => Path.Combine(Root, name);

    internal static byte[] Read(string name) => File.ReadAllBytes(PathOf(name));

    internal static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hagi-imaging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
