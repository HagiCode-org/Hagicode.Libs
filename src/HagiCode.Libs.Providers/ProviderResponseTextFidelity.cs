using System.Text.Json;

namespace HagiCode.Libs.Providers;

internal static class ProviderResponseTextFidelity
{
    public static bool HasText(string? text)
    {
        return !string.IsNullOrEmpty(text);
    }

    public static bool TryGetText(JsonElement element, out string? text, params string[] propertyNames)
    {
        text = null;
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var propertyElement) &&
                propertyElement.ValueKind == JsonValueKind.String)
            {
                var candidate = propertyElement.GetString();
                if (HasText(candidate))
                {
                    text = candidate;
                    return true;
                }
            }
        }

        return false;
    }
}
