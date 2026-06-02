using HagiCode.Libs.Prompts.Configuration;
using HagiCode.Libs.Prompts.Diagnostics;
using HagiCode.Libs.Prompts.FileSystem;
using HagiCode.Libs.Prompts.Models;
using HagiCode.Libs.Prompts.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HagiCode.Libs.Prompts.Tests;

public sealed class FilePromptCatalogTests
{
    [Fact]
    public void Resolve_prefers_override_prompt_for_same_scenario_and_locale()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "default", PromptSource.Default);
        workspace.AddPrompt("apply", "en-US", "override", PromptSource.Override);

        var catalog = CreateCatalog(workspace);

        var prompt = catalog.Resolve("apply", "en-US");

        prompt.ShouldNotBeNull();
        prompt.Definition.Source.ShouldBe(PromptSource.Override);
        prompt.Definition.TemplateContent.ShouldBe("override");
        catalog.GetAllPrompts().Count.ShouldBe(1);
    }

    [Fact]
    public void Resolve_falls_back_to_default_locale_when_exact_match_is_missing()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "hello");

        var catalog = CreateCatalog(workspace);

        var prompt = catalog.Resolve("apply", "fr-FR");

        prompt.ShouldNotBeNull();
        prompt.UsedFallback.ShouldBeTrue();
        prompt.RequestedLocale.ShouldBe("fr-FR");
        prompt.ResolvedLocale.ShouldBe("en-US");
    }

    [Fact]
    public void Diagnostics_report_orphan_metadata_and_templates()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "valid");
        workspace.WriteMetadataOnly("archive", "en-US");
        workspace.WriteTemplateOnly("dangling", "en-US", "dangling");

        var catalog = CreateCatalog(workspace);
        var diagnostics = catalog.GetSnapshot();

        diagnostics.Issues.Any(static issue => issue.Kind == PromptCatalogIssueKind.MissingTemplate).ShouldBeTrue();
        diagnostics.Issues.Any(static issue => issue.Kind == PromptCatalogIssueKind.MissingMetadata).ShouldBeTrue();
    }

    [Fact]
    public void Reload_refreshes_definitions_and_invalidates_renderer_cache()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "Hello {{name}}");

        var renderer = CreateRenderer(new PromptManagementOptions());
        var catalog = CreateCatalog(workspace, renderer: renderer);

        renderer.Render(catalog.Resolve("apply", "en-US")!.Definition, new Dictionary<string, object?> { ["name"] = "Alice" })
            .ShouldBe("Hello Alice");

        workspace.AddPrompt("apply", "en-US", "Hi {{name}}", overwrite: true);
        catalog.Reload();

        renderer.Render(catalog.Resolve("apply", "en-US")!.Definition, new Dictionary<string, object?> { ["name"] = "Bob" })
            .ShouldBe("Hi Bob");
    }

    [Fact]
    public void Strict_template_validation_throws_when_handlebars_syntax_is_invalid()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "{{#if flag}}broken");

        Should.Throw<InvalidOperationException>(() => CreateCatalog(workspace, options: new PromptManagementOptions
        {
            RootPath = workspace.RootPath,
            OverridePath = workspace.OverridePath,
            TemplateValidationMode = PromptTemplateValidationMode.Strict,
        }));
    }

    [Fact]
    public void Lenient_template_validation_records_issue_and_keeps_prompt_visible()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "{{#if flag}}broken");

        var catalog = CreateCatalog(workspace, options: new PromptManagementOptions
        {
            RootPath = workspace.RootPath,
            OverridePath = workspace.OverridePath,
            TemplateValidationMode = PromptTemplateValidationMode.Lenient,
        });

        catalog.Resolve("apply", "en-US").ShouldNotBeNull();
        catalog.GetSnapshot().Issues.Any(static issue => issue.Kind == PromptCatalogIssueKind.InvalidTemplateSyntax).ShouldBeTrue();
    }

    [Fact]
    public void ValidateCompleteness_reports_missing_scenario_locale_combinations()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("apply", "en-US", "apply en");
        workspace.AddPrompt("apply", "zh-CN", "apply zh");
        workspace.AddPrompt("archive", "en-US", "archive en");

        var catalog = CreateCatalog(workspace);
        var report = catalog.ValidateCompleteness(["apply", "archive"], ["en-US", "zh-CN"]);

        report.TotalRequired.ShouldBe(4);
        report.Available.ShouldBe(3);
        report.Missing.Count.ShouldBe(1);
        report.Missing[0].Scenario.ShouldBe("archive");
        report.Missing[0].Locale.ShouldBe("zh-CN");
    }

    [Fact]
    public void Renderer_registers_expected_built_in_helpers()
    {
        using var workspace = new PromptTestWorkspace();
        workspace.AddPrompt("helpers", "en-US", "{{eq role \"admin\"}}|{{not isGuest}}|{{join tags \", \"}}|{{formatDate createdAt}}|{{json payload}}");

        var renderer = CreateRenderer(new PromptManagementOptions());
        var catalog = CreateCatalog(workspace, renderer: renderer);
        var prompt = catalog.Resolve("helpers", "en-US");

        var rendered = renderer.Render(prompt!.Definition, new Dictionary<string, object?>
        {
            ["role"] = "admin",
            ["isGuest"] = false,
            ["tags"] = new[] { "alpha", "beta" },
            ["createdAt"] = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ["payload"] = new { enabled = true },
        });

        rendered.ShouldContain("true|true|alpha, beta|2024-01-02T03:04:05.000Z|");
        rendered.ShouldContain("\"enabled\":true");
    }

    private static FilePromptCatalog CreateCatalog(
        PromptTestWorkspace workspace,
        PromptManagementOptions? options = null,
        IPromptRenderer? renderer = null)
    {
        options ??= new PromptManagementOptions
        {
            RootPath = workspace.RootPath,
            OverridePath = workspace.OverridePath,
        };

        renderer ??= CreateRenderer(options);
        return new FilePromptCatalog(
            Options.Create(options),
            renderer,
            NullLogger<FilePromptCatalog>.Instance);
    }

    private static IPromptRenderer CreateRenderer(PromptManagementOptions options)
    {
        return new HandlebarsPromptRenderer(Options.Create(options), NullLogger<HandlebarsPromptRenderer>.Instance);
    }

    private sealed class PromptTestWorkspace : IDisposable
    {
        public PromptTestWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hagicode-prompts-{Guid.NewGuid():N}");
            RootPath = Path.Combine(root, "Prompts");
            OverridePath = Path.Combine(root, "OverridePrompts");
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(OverridePath);
        }

        public string RootPath { get; }

        public string OverridePath { get; }

        public void AddPrompt(string scenario, string locale, string templateContent, PromptSource source = PromptSource.Default, bool overwrite = false)
        {
            var targetDirectory = source == PromptSource.Override ? OverridePath : RootPath;
            var metadataPath = Path.Combine(targetDirectory, $"{scenario}.{locale}.json");
            var templatePath = Path.Combine(targetDirectory, $"{scenario}.{locale}.hbs");
            if (!overwrite && (File.Exists(metadataPath) || File.Exists(templatePath)))
            {
                throw new InvalidOperationException($"Prompt '{scenario}.{locale}' already exists in {targetDirectory}.");
            }

            File.WriteAllText(metadataPath, $$"""
            {
              "scenario": "{{scenario}}",
              "locale": "{{locale}}",
              "version": "2.0.0",
              "syntax": "handlebars",
              "parameters": []
            }
            """);
            File.WriteAllText(templatePath, templateContent);
        }

        public void WriteMetadataOnly(string scenario, string locale)
        {
            var metadataPath = Path.Combine(RootPath, $"{scenario}.{locale}.json");
            File.WriteAllText(metadataPath, $$"""
            {
              "scenario": "{{scenario}}",
              "locale": "{{locale}}"
            }
            """);
        }

        public void WriteTemplateOnly(string scenario, string locale, string templateContent)
        {
            File.WriteAllText(Path.Combine(RootPath, $"{scenario}.{locale}.hbs"), templateContent);
        }

        public void Dispose()
        {
            var root = Directory.GetParent(RootPath)?.FullName;
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
