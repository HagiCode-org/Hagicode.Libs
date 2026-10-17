using System.Text;

namespace HagiCode.Libs.Git.Tests.Support;

/// <summary>
/// A repository of fixed size used by the timing scenarios so results are comparable across runners:
/// 200 commits, 5 files added per commit (1,000 files), spread across 50 folders. Built with <c>git fast-import</c>, so it
/// takes well under a second.
/// </summary>
public static class GeneratedRepository
{
    public const int Commits = 200;
    public const int FilesPerCommit = 5;
    public const int Folders = 50;

    public static string FileName(int index) => $"folder{index % Folders:D2}/file{index:D4}.txt";

    /// <summary>Creates the repository at <paramref name="path"/> with the given committer email and checks out <c>main</c>.</summary>
    public static string Create(GitCli git, string path, string email)
    {
        Directory.CreateDirectory(path);
        git.Must(path, "init", "--quiet");

        var stream = new StringBuilder();
        for (var commit = 0; commit < Commits; commit++)
        {
            var message = $"generated commit {commit}\n";
            stream.Append("commit refs/heads/main\n");
            stream.Append($"mark :{commit + 1}\n");
            stream.Append($"committer Hagicode Test <{email}> {1700000000 + commit} +0000\n");
            stream.Append($"data {Encoding.UTF8.GetByteCount(message)}\n{message}");
            if (commit > 0)
            {
                stream.Append($"from :{commit}\n");
            }

            for (var file = 0; file < FilesPerCommit; file++)
            {
                var index = commit * FilesPerCommit + file;
                var content = $"file {index}\n";
                stream.Append($"M 100644 inline {FileName(index)}\n");
                stream.Append($"data {Encoding.UTF8.GetByteCount(content)}\n{content}");
            }

            stream.Append('\n');
        }

        var import = git.RunWithInput(path, stream.ToString(), "fast-import", "--quiet");
        if (import.ExitCode != 0)
        {
            throw new InvalidOperationException($"fast-import failed: {import.StandardError}");
        }

        git.Must(path, "reset", "--hard", "--quiet");
        return path;
    }
}
