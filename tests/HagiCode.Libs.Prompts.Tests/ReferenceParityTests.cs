using HagiCode.Libs.Prompts.Configuration;
using HagiCode.Libs.Prompts.Diagnostics;
using HagiCode.Libs.Prompts.FileSystem;
using HagiCode.Libs.Prompts.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HagiCode.Libs.Prompts.Tests;

public sealed class ReferenceParityTests
{
    [Fact]
    public void Catalog_loads_hagicode_core_prompt_directory_structure()
    {
        var monorepoRoot = FindMonorepoRoot();
        var rootPath = Path.Combine(monorepoRoot, "repos", "hagicode-core", "src", "PCode.Web", "Resources", "Prompts");
        var overridePath = Path.Combine(monorepoRoot, "repos", "hagicode-core", "src", "PCode.Web", "Resources", "OverridePrompts");

        var options = new PromptManagementOptions
        {
            RootPath = rootPath,
            OverridePath = overridePath,
            TemplateValidationMode = PromptTemplateValidationMode.Strict,
            DefaultLocale = "en-US",
        };

        var catalog = new FilePromptCatalog(
            Options.Create(options),
            new HandlebarsPromptRenderer(Options.Create(options), NullLogger<HandlebarsPromptRenderer>.Instance),
            NullLogger<FilePromptCatalog>.Instance);

        catalog.GetAllPrompts().Count.ShouldBeGreaterThan(10);

        var applyPrompt = catalog.Resolve("openspec-v1-apply", "en-US");
        applyPrompt.ShouldNotBeNull();
        applyPrompt.Definition.Source.ShouldBe(HagiCode.Libs.Prompts.Models.PromptSource.Default);

        var fallbackPrompt = catalog.Resolve("openspec-v1-apply", "fr-FR");
        fallbackPrompt.ShouldNotBeNull();
        fallbackPrompt.UsedFallback.ShouldBeTrue();
        fallbackPrompt.ResolvedLocale.ShouldBe("en-US");

        var diagnostics = catalog.GetSnapshot();
        diagnostics.Issues.Any(static issue => issue.Kind == PromptCatalogIssueKind.MissingMetadata || issue.Kind == PromptCatalogIssueKind.MissingTemplate)
            .ShouldBeFalse();
    }

    private static string FindMonorepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "repos", "hagicode-core", "src", "PCode.Web", "Resources", "Prompts");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the monorepo root from the test output directory.");
    }
}
