using System.Text.Json;

namespace HagiCode.Libs.Providers.Pooling;

internal static class CliPoolFingerprintBuilder
{
    public static string Build(params object?[] parts)
    {
        return JsonSerializer.Serialize(parts);
    }
}
