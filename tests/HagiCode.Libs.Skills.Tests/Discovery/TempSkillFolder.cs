using System.Text;

namespace HagiCode.Libs.Skills.Tests;

/// <summary>A unique temporary folder for discovery tests. It is deleted on dispose.</summary>
internal sealed class TempSkillFolder : IDisposable
{
    public const string ValidFrontMatter = "---\nname: demo\ndescription: A demo skill\n---\n\n# Demo\n";

    public TempSkillFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "hagi-skill-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string WriteFile(string relativePath, string content)
    {
        var fullPath = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    public string WriteSkill(string slug, string content = ValidFrontMatter) => WriteFile($"{slug}/SKILL.md", content);

    public static string SkillWith(string name, string description) =>
        $"---\nname: {name}\ndescription: {description}\n---\n";

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a test that changed permissions restores them before the folder is disposed.
        }
    }
}
