using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>Environment facts written alongside the timing records.</summary>
public sealed record TimingMetadata(
    string OperatingSystem,
    string Platform,
    string Architecture,
    string Runtime,
    string GitVersion,
    string FileSystem,
    string UtcTimestamp,
    int WarmUpIterations,
    int MeasuredIterations);

/// <summary>Writes one JSON and one CSV file for a test run. Nothing is written unless a directory is given.</summary>
public static class TimingWriter
{
    /// <summary>Environment variable that enables timing output. Unset means no files are written.</summary>
    public const string DirectoryVariable = "HAGICODE_LIBS_GIT_TIMING_DIR";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>Returns the configured output directory, or <see langword="null" /> when timing output is disabled.</summary>
    public static string? DirectoryFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DirectoryVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The per-environment file stem, for example <c>git-timing-linux-x64</c>.</summary>
    public static string FileStem(string platform, string architecture) => $"git-timing-{platform}-{architecture}";

    /// <summary>
    /// Writes <c>&lt;stem&gt;.json</c> (metadata, raw records and statistics) and <c>&lt;stem&gt;.csv</c> (raw records) into
    /// <paramref name="directory" />, and returns both paths.
    /// </summary>
    public static (string Json, string Csv) Write(
        string directory,
        TimingMetadata metadata,
        IReadOnlyList<DurationRecord> records,
        IReadOnlyList<TimingSummary> statistics)
    {
        Directory.CreateDirectory(directory);
        var stem = FileStem(metadata.Platform, metadata.Architecture);
        var jsonPath = Path.Combine(directory, stem + ".json");
        var csvPath = Path.Combine(directory, stem + ".csv");

        var document = new
        {
            metadata,
            records,
            statistics
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(csvPath, ToCsv(records), new UTF8Encoding(false));
        return (jsonPath, csvPath);
    }

    /// <summary>Renders the raw records as CSV with a header row. Text fields are quoted when needed.</summary>
    public static string ToCsv(IEnumerable<DurationRecord> records)
    {
        var builder = new StringBuilder();
        builder.Append("operation,scenario,test,iteration,warmup,category,duration_ms,success,failure_code\n");
        foreach (var record in records)
        {
            builder.Append(string.Join(',',
                Field(record.Operation),
                Field(record.Scenario),
                Field(record.TestName),
                record.Iteration.ToString(CultureInfo.InvariantCulture),
                record.WarmUp ? "true" : "false",
                Field(record.Category),
                record.DurationMs.ToString("F4", CultureInfo.InvariantCulture),
                record.Success ? "true" : "false",
                Field(record.FailureCode ?? string.Empty)));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Field(string value)
    {
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Platform name used in file names: <c>windows</c>, <c>linux</c> or <c>osx</c>.</summary>
    public static string PlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "osx" : "linux";
    }

    /// <summary>Architecture name used in file names: <c>x64</c>, <c>arm64</c>, and so on.</summary>
    public static string ArchitectureName() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
    {
        "x64" => "x64",
        "arm64" => "arm64",
        var other => other
    };

    /// <summary>The file system type of the temp directory's volume, or <c>unknown</c> when it cannot be read.</summary>
    public static string FileSystemName()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            var format = string.IsNullOrEmpty(root) ? null : new DriveInfo(root).DriveFormat;
            return string.IsNullOrWhiteSpace(format) ? "unknown" : format;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
