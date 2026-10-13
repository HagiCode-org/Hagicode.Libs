using System.Reflection;
using System.Security.Cryptography;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// The bundled Pi extension that registers the <c>ask_user_question</c> tool. It ships inside the provider assembly, because
/// a package consumer or a single-file host has no other file next to it, and is written out once for Pi to load with
/// <c>-e</c>.
/// </summary>
internal static class PiQuestionExtension
{
    internal const string FileName = "ask-user-question.ts";
    internal const string ResourceName = "HagiCode.Libs.Providers.Pi.Extensions.ask-user-question.ts";

    /// <summary>
    /// Writes the extension below the temp path and returns the file to pass to <c>-e</c>.
    /// </summary>
    public static string EnsureExtracted()
    {
        return EnsureExtracted(Path.Combine(Path.GetTempPath(), "hagicode-libs", "pi-extensions"));
    }

    /// <summary>
    /// Writes the extension to <c>&lt;rootDirectory&gt;/&lt;content hash&gt;/ask-user-question.ts</c> unless a file with
    /// exactly that content is already there, and returns its path.
    /// </summary>
    /// <remarks>
    /// The directory is named after the hash of the content, so a newer library version never reuses a file of an older
    /// one and concurrent runs of one version agree on the path. The file is written under a unique temporary name and then
    /// moved into place, so a reader never sees a partial file and two writers cannot interleave. When the move loses a race
    /// (or the target is held open on Windows) and the file that is there has the right content, that file is used.
    /// </remarks>
    internal static string EnsureExtracted(string rootDirectory)
    {
        var content = ReadResource();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content))[..16];
        var directory = Path.Combine(rootDirectory, hash);
        var path = Path.Combine(directory, FileName);
        if (HasContent(path, content))
        {
            return path;
        }

        CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch (IOException) when (HasContent(path, content))
            {
                // Another run put the same file there first.
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return path;
    }

    private static byte[] ReadResource()
    {
        using var stream = typeof(PiQuestionExtension).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"The embedded Pi extension '{ResourceName}' is missing from {typeof(PiQuestionExtension).Assembly.GetName().Name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool HasContent(string path, byte[] content)
    {
        try
        {
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CreateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        // The extension is code Pi runs: keep what this call creates private to the current user where the platform allows.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
