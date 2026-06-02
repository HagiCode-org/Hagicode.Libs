using System.Text.Json;
using HagiCode.Libs.Prompts.Configuration;
using HagiCode.Libs.Prompts.Diagnostics;
using HagiCode.Libs.Prompts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HagiCode.Libs.Prompts.FileSystem;

/// <summary>
/// Loads prompt metadata and Handlebars templates from disk and exposes a merged prompt catalog.
/// </summary>
public sealed class FilePromptCatalog : IPromptCatalog, IPromptDiagnosticsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<FilePromptCatalog> _logger;
    private readonly PromptManagementOptions _options;
    private readonly IPromptRenderer _renderer;
    private readonly PromptFileLocator _fileLocator = new();
    private readonly object _sync = new();

    private PromptCatalogState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilePromptCatalog"/> class.
    /// </summary>
    public FilePromptCatalog(
        IOptions<PromptManagementOptions> options,
        IPromptRenderer renderer,
        ILogger<FilePromptCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _renderer = renderer;
        _logger = logger;
        _state = LoadState();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PromptDefinition> GetAllPrompts()
    {
        var state = _state;
        return state.EffectiveDefinitions.Values
            .OrderBy(static definition => definition.Scenario, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static definition => definition.Locale, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public PromptResolutionResult? Resolve(string scenario, string? locale = null)
    {
        if (string.IsNullOrWhiteSpace(scenario))
        {
            throw new ArgumentException("Scenario cannot be null or empty.", nameof(scenario));
        }

        var requestedLocale = string.IsNullOrWhiteSpace(locale) ? _options.DefaultLocale : locale.Trim();
        var state = _state;

        if (state.EffectiveDefinitions.TryGetValue(BuildCatalogKey(scenario, requestedLocale), out var definition))
        {
            return new PromptResolutionResult
            {
                Definition = definition,
                RequestedLocale = requestedLocale,
                ResolvedLocale = definition.Locale,
                UsedFallback = false,
            };
        }

        if (!string.Equals(requestedLocale, _options.DefaultLocale, StringComparison.OrdinalIgnoreCase) &&
            state.EffectiveDefinitions.TryGetValue(BuildCatalogKey(scenario, _options.DefaultLocale), out definition))
        {
            return new PromptResolutionResult
            {
                Definition = definition,
                RequestedLocale = requestedLocale,
                ResolvedLocale = definition.Locale,
                UsedFallback = true,
            };
        }

        return null;
    }

    /// <inheritdoc />
    public void Reload()
    {
        lock (_sync)
        {
            _renderer.InvalidateCache();
            _state = LoadState();
        }
    }

    /// <inheritdoc />
    public PromptCatalogDiagnostics GetSnapshot()
    {
        var state = _state;
        return new PromptCatalogDiagnostics
        {
            GeneratedAtUtc = state.GeneratedAtUtc,
            EffectivePromptCount = state.EffectiveDefinitions.Count,
            DefaultPromptCount = state.EffectiveDefinitions.Values.Count(static definition => definition.Source == PromptSource.Default),
            OverridePromptCount = state.EffectiveDefinitions.Values.Count(static definition => definition.Source == PromptSource.Override),
            Issues = state.Issues.ToArray(),
        };
    }

    /// <inheritdoc />
    public PromptCompletenessReport ValidateCompleteness(IEnumerable<string> requiredScenarios, IEnumerable<string> requiredLocales)
    {
        ArgumentNullException.ThrowIfNull(requiredScenarios);
        ArgumentNullException.ThrowIfNull(requiredLocales);

        var scenarios = requiredScenarios
            .Where(static scenario => !string.IsNullOrWhiteSpace(scenario))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var locales = requiredLocales
            .Where(static locale => !string.IsNullOrWhiteSpace(locale))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var state = _state;
        var missing = new List<PromptCompletenessGap>();
        var available = 0;

        foreach (var scenario in scenarios)
        {
            foreach (var locale in locales)
            {
                if (state.EffectiveDefinitions.ContainsKey(BuildCatalogKey(scenario, locale)))
                {
                    available++;
                    continue;
                }

                missing.Add(new PromptCompletenessGap
                {
                    Scenario = scenario,
                    Locale = locale,
                });
            }
        }

        return new PromptCompletenessReport
        {
            TotalRequired = scenarios.Length * locales.Length,
            Available = available,
            Missing = missing,
        };
    }

    private PromptCatalogState LoadState()
    {
        var rootPath = ResolvePath(_options.RootPath);
        var overridePath = ResolveOptionalPath(_options.OverridePath) ?? Path.Combine(Path.GetDirectoryName(rootPath) ?? rootPath, "OverridePrompts");
        var issues = new List<PromptCatalogIssue>();
        var effectiveDefinitions = new Dictionary<string, PromptDefinition>(StringComparer.OrdinalIgnoreCase);

        var defaultScan = _fileLocator.ScanDirectory(rootPath, PromptSource.Default, requiredDirectory: true);
        var overrideScan = _fileLocator.ScanDirectory(overridePath, PromptSource.Override, requiredDirectory: false);

        issues.AddRange(defaultScan.Issues);
        issues.AddRange(overrideScan.Issues);

        if (_options.StrictMode && defaultScan.IsMissingRequiredDirectory)
        {
            throw new DirectoryNotFoundException($"Prompt root directory not found: {rootPath}");
        }

        LoadDefinitions(defaultScan.Pairs, effectiveDefinitions, issues);
        LoadDefinitions(overrideScan.Pairs, effectiveDefinitions, issues);

        _logger.LogInformation(
            "Loaded {PromptCount} prompt definitions from {RootPath} with {IssueCount} diagnostics.",
            effectiveDefinitions.Count,
            rootPath,
            issues.Count);

        return new PromptCatalogState(
            DateTimeOffset.UtcNow,
            effectiveDefinitions,
            issues);
    }

    private void LoadDefinitions(
        IReadOnlyCollection<PromptFilePair> pairs,
        IDictionary<string, PromptDefinition> effectiveDefinitions,
        ICollection<PromptCatalogIssue> issues)
    {
        foreach (var pair in pairs)
        {
            var definition = TryLoadDefinition(pair, issues);
            if (definition is null)
            {
                continue;
            }

            effectiveDefinitions[BuildCatalogKey(definition.Scenario, definition.Locale)] = definition;
        }
    }

    private PromptDefinition? TryLoadDefinition(PromptFilePair pair, ICollection<PromptCatalogIssue> issues)
    {
        try
        {
            var metadataJson = File.ReadAllText(pair.MetadataPath);
            var metadata = JsonSerializer.Deserialize<PromptMetadata>(metadataJson, SerializerOptions);
            if (metadata is null)
            {
                issues.Add(BuildIssue(PromptCatalogIssueKind.InvalidMetadata, pair.Source, pair.MetadataPath, "Prompt metadata could not be deserialized."));
                return null;
            }

            if (string.IsNullOrWhiteSpace(metadata.Scenario) || string.IsNullOrWhiteSpace(metadata.Locale))
            {
                issues.Add(new PromptCatalogIssue
                {
                    Kind = PromptCatalogIssueKind.InvalidMetadata,
                    Source = pair.Source,
                    FilePath = pair.MetadataPath,
                    Message = "Prompt metadata must include both scenario and locale.",
                    Scenario = metadata.Scenario,
                    Locale = metadata.Locale,
                });
                return null;
            }

            metadata.Source = pair.Source;
            metadata.LastModifiedAt = Max(File.GetLastWriteTimeUtc(pair.MetadataPath), File.GetLastWriteTimeUtc(pair.TemplatePath));

            var templateContent = File.ReadAllText(pair.TemplatePath);
            var validation = _renderer.ValidateSyntax(templateContent, pair.TemplatePath);
            if (!validation.IsValid)
            {
                issues.Add(new PromptCatalogIssue
                {
                    Kind = PromptCatalogIssueKind.InvalidTemplateSyntax,
                    Source = pair.Source,
                    FilePath = pair.TemplatePath,
                    Scenario = metadata.Scenario,
                    Locale = metadata.Locale,
                    Message = string.Join(" ", validation.Errors),
                });

                if (_options.TemplateValidationMode == PromptTemplateValidationMode.Strict)
                {
                    throw new InvalidOperationException($"Template validation failed for '{pair.TemplatePath}': {string.Join("; ", validation.Errors)}");
                }
            }

            return new PromptDefinition
            {
                Metadata = metadata,
                MetadataPath = pair.MetadataPath,
                TemplatePath = pair.TemplatePath,
                TemplateContent = templateContent,
                TemplateValidation = validation,
                IsRuntimeReady = validation.IsValid || _options.TemplateValidationMode == PromptTemplateValidationMode.Lenient,
            };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            issues.Add(BuildIssue(PromptCatalogIssueKind.InvalidMetadata, pair.Source, pair.MetadataPath, ex.Message));
            return null;
        }
        catch (IOException ex)
        {
            issues.Add(BuildIssue(PromptCatalogIssueKind.InvalidMetadata, pair.Source, pair.MetadataPath, ex.Message));
            return null;
        }
    }

    private static PromptCatalogIssue BuildIssue(PromptCatalogIssueKind kind, PromptSource source, string filePath, string message)
    {
        return new PromptCatalogIssue
        {
            Kind = kind,
            Source = source,
            FilePath = filePath,
            Message = message,
        };
    }

    private static DateTimeOffset Max(DateTime left, DateTime right)
    {
        return new DateTimeOffset(left >= right ? left : right, TimeSpan.Zero);
    }

    private static string BuildCatalogKey(string scenario, string locale)
    {
        return $"{scenario}:{locale}";
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private static string? ResolveOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return ResolvePath(path);
    }
}
