using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// Scans assembly metadata for references to process-spawning types. It reads type references directly from the PE file,
/// so it needs no execution of the assembly.
/// </summary>
internal static class ProcessApiScanner
{
    private static readonly string[] ForbiddenNames = ["Process", "ProcessStartInfo"];

    /// <summary>Returns the forbidden type references found in the assembly file, for example <c>System.Diagnostics.Process</c>.</summary>
    internal static IReadOnlyList<string> FindProcessReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var hits = new List<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var typeReference = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeReference.Namespace);
            var name = reader.GetString(typeReference.Name);
            if (ns == "System.Diagnostics" && Array.IndexOf(ForbiddenNames, name) >= 0)
            {
                hits.Add($"{ns}.{name}");
            }
        }

        return hits;
    }
}
