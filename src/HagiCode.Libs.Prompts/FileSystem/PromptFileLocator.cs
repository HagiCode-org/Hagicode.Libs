using HagiCode.Libs.Prompts.Diagnostics;
using HagiCode.Libs.Prompts.Models;

namespace HagiCode.Libs.Prompts.FileSystem;

/// <summary>
/// Scans a prompt directory for co-located metadata and template files.
/// </summary>
internal sealed class PromptFileLocator
{
    public PromptFileScanResult ScanDirectory(string directoryPath, PromptSource source, bool requiredDirectory)
    {
        if (!Directory.Exists(directoryPath))
        {
            var issues = requiredDirectory
                ? new List<PromptCatalogIssue>
                {
                    new()
                    {
                        Kind = PromptCatalogIssueKind.RootDirectoryMissing,
                        Source = source,
                        FilePath = directoryPath,
                        Message = $"Prompt directory not found: {directoryPath}",
                    },
                }
                : new List<PromptCatalogIssue>();

            return new PromptFileScanResult([], issues, requiredDirectory);
        }

        var metadataFiles = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .ToDictionary(static file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase);
        var templateFiles = Directory.EnumerateFiles(directoryPath, "*.hbs", SearchOption.TopDirectoryOnly)
            .ToDictionary(static file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase);

        var issuesList = new List<PromptCatalogIssue>();
        foreach (var metadataOnly in metadataFiles.Keys.Except(templateFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issuesList.Add(new PromptCatalogIssue
            {
                Kind = PromptCatalogIssueKind.MissingTemplate,
                Source = source,
                FilePath = metadataFiles[metadataOnly],
                Message = $"Metadata file '{metadataOnly}.json' does not have a matching '.hbs' template.",
            });
        }

        foreach (var templateOnly in templateFiles.Keys.Except(metadataFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issuesList.Add(new PromptCatalogIssue
            {
                Kind = PromptCatalogIssueKind.MissingMetadata,
                Source = source,
                FilePath = templateFiles[templateOnly],
                Message = $"Template file '{templateOnly}.hbs' does not have a matching '.json' metadata file.",
            });
        }

        var pairs = metadataFiles.Keys.Intersect(templateFiles.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => new PromptFilePair(metadataFiles[key], templateFiles[key], source))
            .ToArray();

        return new PromptFileScanResult(pairs, issuesList, isMissingRequiredDirectory: false);
    }
}

internal sealed record PromptFilePair(string MetadataPath, string TemplatePath, PromptSource Source);

internal sealed class PromptFileScanResult
{
    public PromptFileScanResult(
        IReadOnlyCollection<PromptFilePair> pairs,
        IReadOnlyCollection<PromptCatalogIssue> issues,
        bool isMissingRequiredDirectory)
    {
        Pairs = pairs;
        Issues = issues;
        IsMissingRequiredDirectory = isMissingRequiredDirectory;
    }

    public IReadOnlyCollection<PromptFilePair> Pairs { get; }

    public IReadOnlyCollection<PromptCatalogIssue> Issues { get; }

    public bool IsMissingRequiredDirectory { get; }
}
