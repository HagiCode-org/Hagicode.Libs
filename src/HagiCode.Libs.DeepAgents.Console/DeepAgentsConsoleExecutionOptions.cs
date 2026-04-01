using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console;

public sealed record DeepAgentsConsoleExecutionOptions(
    string? ExecutablePath,
    string? RepositoryPath,
    string? Model,
    string? WorkspaceRoot,
    string? AgentName,
    string? AgentDescription,
    IReadOnlyList<string> SkillsDirectories,
    IReadOnlyList<string> MemoryFiles,
    IReadOnlyList<string> ExtraArguments)
{
    public static DeepAgentsConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? executablePath = null;
        string? repositoryPath = null;
        string? model = null;
        string? workspaceRoot = null;
        string? agentName = null;
        string? agentDescription = null;
        var skillsDirectories = new List<string>();
        var memoryFiles = new List<string>();
        var extraArguments = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--workspace":
                    workspaceRoot = ReadValue(args, ref index, argument);
                    break;
                case "--name":
                    agentName = ReadValue(args, ref index, argument);
                    break;
                case "--description":
                    agentDescription = ReadValue(args, ref index, argument);
                    break;
                case "--skill":
                    skillsDirectories.Add(ReadValue(args, ref index, argument));
                    break;
                case "--memory":
                    memoryFiles.Add(ReadValue(args, ref index, argument));
                    break;
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new DeepAgentsConsoleExecutionOptions(
            executablePath,
            repositoryPath,
            model,
            workspaceRoot,
            agentName,
            agentDescription,
            skillsDirectories,
            memoryFiles,
            extraArguments);
    }

    public DeepAgentsOptions CreateBaseOptions()
    {
        return new DeepAgentsOptions
        {
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            WorkspaceRoot = string.IsNullOrWhiteSpace(WorkspaceRoot) ? null : WorkspaceRoot,
            AgentName = string.IsNullOrWhiteSpace(AgentName) ? null : AgentName,
            AgentDescription = string.IsNullOrWhiteSpace(AgentDescription) ? null : AgentDescription,
            SkillsDirectories = SkillsDirectories,
            MemoryFiles = MemoryFiles,
            ExtraArguments = ExtraArguments
        };
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ReadRawValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return args[index];
    }
}
