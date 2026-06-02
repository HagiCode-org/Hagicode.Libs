using HagiCode.Libs.Prompts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Prompts.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddHagiCodePrompts_registers_catalog_renderer_and_diagnostics_services()
    {
        using var workspace = new PromptWorkspace();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHagiCodePrompts(options =>
        {
            options.RootPath = workspace.RootPath;
            options.OverridePath = workspace.OverridePath;
            options.TemplateValidationMode = PromptTemplateValidationMode.Strict;
        });

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IPromptCatalog>();
        var diagnostics = provider.GetRequiredService<IPromptDiagnosticsService>();
        var renderer = provider.GetRequiredService<IPromptRenderer>();

        catalog.ShouldNotBeNull();
        diagnostics.ShouldNotBeNull();
        renderer.ShouldNotBeNull();
        ReferenceEquals(catalog, diagnostics).ShouldBeTrue();
        catalog.Resolve("apply", "en-US").ShouldNotBeNull();
    }

    private sealed class PromptWorkspace : IDisposable
    {
        public PromptWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hagicode-prompts-di-{Guid.NewGuid():N}");
            RootPath = Path.Combine(root, "Prompts");
            OverridePath = Path.Combine(root, "OverridePrompts");
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(OverridePath);
            File.WriteAllText(Path.Combine(RootPath, "apply.en-US.json"), "{\"scenario\":\"apply\",\"locale\":\"en-US\"}");
            File.WriteAllText(Path.Combine(RootPath, "apply.en-US.hbs"), "hello");
        }

        public string RootPath { get; }

        public string OverridePath { get; }

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
