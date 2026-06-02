# HagiCode.Libs.Prompts

`HagiCode.Libs.Prompts` extracts the file-backed Handlebars prompt management core from `hagicode-core` into a reusable .NET 10 package. It loads `{scenario}.{locale}.json` plus `{scenario}.{locale}.hbs` pairs, merges an optional override directory, resolves locale fallback, caches compiled templates, and reports diagnostics without forcing host applications to read prompt files directly.

## What is included

- `IPromptCatalog` for effective prompt enumeration, locale-aware resolution, and reload.
- `IPromptDiagnosticsService` for orphan file reporting and scenario-locale completeness validation.
- `IPromptRenderer` for Handlebars rendering, syntax validation, built-in helpers, and compiled-template cache invalidation.
- `AddHagiCodePrompts()` for dependency injection registration.

## Install

```bash
dotnet add package HagiCode.Libs.Prompts
```

## Configuration

```csharp
using HagiCode.Libs.Prompts;
using HagiCode.Libs.Prompts.Configuration;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodePrompts(options =>
{
    options.RootPath = "/app/Resources/Prompts";
    options.OverridePath = "/app/Resources/OverridePrompts";
    options.DefaultLocale = "en-US";
    options.TemplateValidationMode = PromptTemplateValidationMode.Strict;
});

await using var provider = services.BuildServiceProvider();
var catalog = provider.GetRequiredService<IPromptCatalog>();
var diagnostics = provider.GetRequiredService<IPromptDiagnosticsService>();
var renderer = provider.GetRequiredService<IPromptRenderer>();
```

When `OverridePath` is omitted, the library defaults to a sibling `OverridePrompts` directory next to `RootPath`, matching the current backend convention.

## Minimal usage

```csharp
var prompt = catalog.Resolve("openspec-v1-apply", "fr-FR");
if (prompt is null)
{
    return;
}

Console.WriteLine(prompt.UsedFallback);      // true when it fell back to DefaultLocale
Console.WriteLine(prompt.Definition.Source); // Default or Override

var text = renderer.Render(prompt.Definition, new Dictionary<string, object?>
{
    ["change_name"] = "extract-hbs-prompt-management-to-libs"
});
```

## Diagnostics

```csharp
var snapshot = diagnostics.GetSnapshot();
foreach (var issue in snapshot.Issues)
{
    Console.WriteLine($"{issue.Kind}: {issue.Message}");
}

var completeness = diagnostics.ValidateCompleteness(
    ["openspec-v1-apply", "openspec-v1-archive"],
    ["en-US", "zh-CN"]);
```

`ValidateCompleteness()` returns every missing scenario-locale combination so hosts can fail startup, emit audit logs, or expose an admin report.

## Built-in helpers

The default Handlebars renderer registers these helpers:

- `eq left right`
- `not value`
- `formatDate value`
- `json value`
- `join values separator`

## Migration mapping from `hagicode-core`

- `FilePromptLoaderV2` -> `FilePromptCatalog`
- `HandlebarsTemplateRenderer` -> `HandlebarsPromptRenderer`
- `TemplateLinkResolver` link/orphan checks -> `PromptFileLocator` plus `IPromptDiagnosticsService`
- `PromptLoaderStatistics` and startup completeness checks -> `PromptCatalogDiagnostics` plus `PromptCompletenessReport`

This package intentionally stays free of ABP and web-layer DTOs so `hagicode-core` can add an adapter layer later without dragging backend-specific contracts into shared libraries.
