using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandlebarsDotNet;
using HagiCode.Libs.Prompts.Configuration;
using HagiCode.Libs.Prompts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HagiCode.Libs.Prompts.Rendering;

/// <summary>
/// Renders prompt definitions with Handlebars and caches compiled templates.
/// </summary>
public sealed class HandlebarsPromptRenderer : IPromptRenderer
{
    private readonly IHandlebars _handlebars;
    private readonly ILogger<HandlebarsPromptRenderer> _logger;
    private readonly ConcurrentDictionary<string, HandlebarsTemplate<object, object>> _templateCache = new(StringComparer.Ordinal);
    private readonly int _cacheSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="HandlebarsPromptRenderer"/> class.
    /// </summary>
    public HandlebarsPromptRenderer(
        IOptions<PromptManagementOptions> options,
        ILogger<HandlebarsPromptRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _cacheSize = options.Value.TemplateCacheSize > 0 ? options.Value.TemplateCacheSize : 100;
        _handlebars = Handlebars.Create();

        RegisterBuiltInHelpers(_handlebars);
    }

    /// <inheritdoc />
    public string Render(PromptDefinition definition, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.TemplateContent))
        {
            throw new ArgumentException("Prompt template content cannot be null or empty.", nameof(definition));
        }

        var compiledTemplate = GetOrCompile(definition.TemplateContent);
        var parameterBag = parameters?.ToDictionary(static pair => pair.Key, static pair => pair.Value) ?? new Dictionary<string, object?>();
        var rendered = compiledTemplate(parameterBag);

        return rendered.Replace("True", "true", StringComparison.Ordinal)
            .Replace("False", "false", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public PromptTemplateValidationResult ValidateSyntax(string templateContent, string? templatePath = null)
    {
        if (string.IsNullOrWhiteSpace(templateContent))
        {
            return PromptTemplateValidationResult.Failure("Template content is null or empty.");
        }

        try
        {
            _handlebars.Compile(templateContent);
            return PromptTemplateValidationResult.Success();
        }
        catch (HandlebarsException ex)
        {
            _logger.LogWarning(ex, "Handlebars syntax validation failed for {TemplatePath}", templatePath ?? "inline");
            return PromptTemplateValidationResult.Failure($"Handlebars syntax error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        _templateCache.Clear();
    }

    private HandlebarsTemplate<object, object> GetOrCompile(string templateContent)
    {
        var cacheKey = ComputeHash(templateContent);
        if (_templateCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var compiled = _handlebars.Compile(templateContent);

        if (_cacheSize > 0 && _templateCache.Count >= _cacheSize)
        {
            var firstKey = _templateCache.Keys.FirstOrDefault();
            if (firstKey is not null)
            {
                _templateCache.TryRemove(firstKey, out _);
            }
        }

        _templateCache[cacheKey] = compiled;
        return compiled;
    }

    private static void RegisterBuiltInHelpers(IHandlebars handlebars)
    {
        handlebars.RegisterHelper("eq", static (writer, _, arguments) =>
        {
            var result = arguments.Length >= 2 && Equals(arguments[0], arguments[1]);
            writer.WriteSafeString(result ? "true" : "false");
        });

        handlebars.RegisterHelper("not", static (writer, _, arguments) =>
        {
            var result = arguments.Length == 0 || IsFalsey(arguments[0]);
            writer.WriteSafeString(result ? "true" : "false");
        });

        handlebars.RegisterHelper("formatDate", static (writer, _, arguments) =>
        {
            if (arguments.Length == 0)
            {
                writer.WriteSafeString(string.Empty);
                return;
            }

            var formatted = arguments[0] switch
            {
                DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DateTime dateTime => dateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                _ => string.Empty,
            };

            writer.WriteSafeString(formatted);
        });

        handlebars.RegisterHelper("json", static (writer, _, arguments) =>
        {
            writer.WriteSafeString(arguments.Length == 0 ? "null" : JsonSerializer.Serialize(arguments[0]));
        });

        handlebars.RegisterHelper("join", static (writer, _, arguments) =>
        {
            if (arguments.Length < 2 || arguments[0] is not System.Collections.IEnumerable enumerable || arguments[0] is string)
            {
                writer.WriteSafeString(string.Empty);
                return;
            }

            var separator = arguments[1]?.ToString() ?? ", ";
            var values = enumerable.Cast<object?>().Select(static value => value?.ToString() ?? string.Empty);
            writer.WriteSafeString(string.Join(separator, values));
        });
    }

    private static bool IsFalsey(object? value)
    {
        return value switch
        {
            null => true,
            false => true,
            string text => string.IsNullOrEmpty(text),
            System.Collections.IEnumerable enumerable when value is not string => !enumerable.Cast<object?>().Any(),
            _ => false,
        };
    }

    private static string ComputeHash(string templateContent)
    {
        var bytes = Encoding.UTF8.GetBytes(templateContent);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
