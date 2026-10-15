namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Writes output atomically: bytes go to a temporary file in the destination directory and are moved into place only
/// after they are fully flushed. A failure removes the temporary file and leaves any existing destination untouched.
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>
    /// Refuses a destination that resolves to the source file. Symbolic links are resolved on both sides. Case
    /// comparison follows the platform: case-insensitive on Windows and macOS, case-sensitive on Linux.
    /// </summary>
    internal static void EnsureNotSameFile(string sourcePath, string destinationPath)
    {
        var source = ResolveFinal(sourcePath);
        var destination = ResolveFinal(destinationPath);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(source, destination, comparison))
        {
            throw new AvifConversionException(AvifErrorCode.InvalidOptions, "The destination is the same file as the source.");
        }
    }

    /// <summary>Checks the destination directory and the overwrite rule before any work starts.</summary>
    internal static void ValidateDestination(string destinationPath, bool overwrite)
    {
        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new AvifConversionException(AvifErrorCode.InvalidOptions, "The destination directory does not exist.");
        }

        if (!overwrite && File.Exists(full))
        {
            throw new AvifConversionException(AvifErrorCode.DestinationExists, "The destination file already exists and overwrite was not requested.");
        }
    }

    /// <summary>Writes the bytes atomically. Call only after a successful encode.</summary>
    internal static void Write(string destinationPath, ReadOnlySpan<byte> bytes, bool overwrite)
    {
        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!overwrite && File.Exists(full))
            {
                throw new AvifConversionException(AvifErrorCode.DestinationExists, "The destination file already exists and overwrite was not requested.");
            }

            File.Move(temp, full, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(full))
        {
            throw new AvifConversionException(AvifErrorCode.DestinationExists, "The destination file already exists and overwrite was not requested.");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string ResolveFinal(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: the temporary file name is unique and does not affect the destination.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
