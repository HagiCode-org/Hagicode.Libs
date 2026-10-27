using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace HagiCode.Libs.Skills.Discovery;

/// <summary>The metadata read from a skill's <c>SKILL.md</c> front matter.</summary>
/// <param name="Name">The non-empty <c>name</c>, or null.</param>
/// <param name="Description">The non-empty <c>description</c>, or null.</param>
/// <param name="Problems">Problems found while reading the front matter. Empty when it is valid.</param>
public sealed record SkillDocumentMetadata(string? Name, string? Description, IReadOnlyList<SkillProblem> Problems);

/// <summary>
/// Reads the YAML front matter at the top of a <c>SKILL.md</c> file. At most
/// <see cref="SkillFolderLimits.MaxFrontMatterBytes" /> bytes are read.
/// </summary>
public sealed class SkillDocumentParser
{
    /// <summary>The file name a skill folder must contain.</summary>
    public const string SkillFileName = "SKILL.md";

    private const string Delimiter = "---";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";

    /// <summary>Reads the front matter of a file. I/O failures are reported as an <see cref="SkillProblemCodes.Unreadable" /> problem.</summary>
    public SkillDocumentMetadata ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            // bufferSize 1 disables buffering so that no byte past the limit is read from the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            return ParseContent(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SkillDocumentMetadata(null, null, [new SkillProblem(SkillProblemCodes.Unreadable, "SKILL.md cannot be read")]);
        }
    }

    /// <summary>Reads the front matter from a stream, reading at most <see cref="SkillFolderLimits.MaxFrontMatterBytes" /> bytes.</summary>
    public SkillDocumentMetadata ParseContent(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var buffer = new byte[SkillFolderLimits.MaxFrontMatterBytes];
        var length = ReadUpTo(content, buffer);
        var windowFull = length == buffer.Length;

        // Decoding a cut multi-byte character only affects the tail after the closing delimiter, which is never used.
        var text = Encoding.UTF8.GetString(buffer, 0, length).TrimStart('﻿');
        if (!TrySplitFrontMatter(text, windowFull, out var frontMatterText, out var problem))
        {
            return problem is null
                ? Missing()
                : new SkillDocumentMetadata(null, null, [problem]);
        }

        return ParseYaml(frontMatterText);
    }

    private static int ReadUpTo(Stream content, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = content.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Finds the front matter text. Returns false with no problem when the file has no front matter, and false with a
    /// problem when the front matter is oversized or not closed.
    /// </summary>
    private static bool TrySplitFrontMatter(string text, bool windowFull, out string frontMatter, out SkillProblem? problem)
    {
        frontMatter = string.Empty;
        problem = null;

        var lineStart = 0;
        var bodyStart = -1;
        var closingStart = -1;
        while (lineStart <= text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var hasNewline = newline >= 0;
            var lineEnd = hasNewline ? newline : text.Length;
            var line = text[lineStart..lineEnd].Trim();

            if (bodyStart < 0)
            {
                if (line != Delimiter)
                {
                    return false;
                }

                bodyStart = hasNewline ? newline + 1 : text.Length;
            }
            else if (line == Delimiter && (hasNewline || !windowFull))
            {
                closingStart = lineStart;
                break;
            }

            if (!hasNewline)
            {
                break;
            }

            lineStart = newline + 1;
        }

        if (bodyStart < 0)
        {
            return false;
        }

        if (closingStart < 0)
        {
            problem = windowFull
                ? new SkillProblem(SkillProblemCodes.OversizedMetadata, "SKILL.md front matter is larger than 32 KiB")
                : new SkillProblem(SkillProblemCodes.UnreadableMetadata, "SKILL.md front matter is not closed");
            return false;
        }

        frontMatter = text[bodyStart..closingStart];
        return true;
    }

    private static SkillDocumentMetadata ParseYaml(string frontMatter)
    {
        YamlMappingNode? mapping;
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(frontMatter));
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            {
                return Missing();
            }

            mapping = yaml.Documents[0].RootNode as YamlMappingNode;
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException or InvalidOperationException)
        {
            return Unreadable();
        }

        if (mapping is null)
        {
            return Unreadable();
        }

        string? name = null;
        string? description = null;
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode keyNode || pair.Value is not YamlScalarNode valueNode)
            {
                continue;
            }

            if (keyNode.Value == NameKey)
            {
                name = NonEmpty(valueNode.Value);
            }
            else if (keyNode.Value == DescriptionKey)
            {
                description = NonEmpty(valueNode.Value);
            }
        }

        List<SkillProblem> problems = [];
        if (name is null)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.MissingName, "SKILL.md front matter has no name"));
        }

        if (description is null)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.MissingDescription, "SKILL.md front matter has no description"));
        }

        return new SkillDocumentMetadata(name, description, problems);
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SkillDocumentMetadata Missing() => new(
        null,
        null,
        [
            new SkillProblem(SkillProblemCodes.MissingName, "SKILL.md front matter has no name"),
            new SkillProblem(SkillProblemCodes.MissingDescription, "SKILL.md front matter has no description"),
        ]);

    private static SkillDocumentMetadata Unreadable() => new(
        null,
        null,
        [new SkillProblem(SkillProblemCodes.UnreadableMetadata, "SKILL.md front matter cannot be read as YAML")]);
}
