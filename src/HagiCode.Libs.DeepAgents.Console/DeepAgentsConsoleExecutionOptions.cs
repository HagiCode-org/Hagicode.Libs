using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console;

public sealed record DeepAgentsConsoleExecutionOptions(
    string? ExecutablePath,
    string? RepositoryPath,
    string? WorkspacePath,
    string? Model,
    string? ModeId,
    string? AgentName,
    string? AgentDescription,
    bool Verbose,
    bool ToolcallEnabled,
    string? ToolcallCaseName,
    IReadOnlyList<string> SkillsDirectories,
    IReadOnlyList<string> MemoryFiles,
    IReadOnlyList<string> ExtraArguments)
{
    public static DeepAgentsConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? executablePath = null;
        string? repositoryPath = null;
        string? workspacePath = null;
        string? model = null;
        string? modeId = null;
        string? agentName = null;
        string? agentDescription = null;
        var verbose = false;
        var toolcallEnabled = false;
        string? toolcallCaseName = null;
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
                case "--workspace":
                    workspacePath = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--mode-id":
                    modeId = NormalizeModeId(ReadValue(args, ref index, argument));
                    break;
                case "--name":
                    agentName = ReadValue(args, ref index, argument);
                    break;
                case "--description":
                    agentDescription = ReadValue(args, ref index, argument);
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "--toolcall":
                    toolcallEnabled = true;
                    break;
                case "--toolcall-case":
                    toolcallEnabled = true;
                    toolcallCaseName = ReadValue(args, ref index, argument);
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
            workspacePath,
            model,
            modeId,
            agentName,
            agentDescription,
            verbose,
            toolcallEnabled,
            toolcallCaseName,
            skillsDirectories,
            memoryFiles,
            extraArguments);
    }

    public DeepAgentsOptions CreateBaseOptions()
    {
        return new DeepAgentsOptions
        {
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkspacePath) ? null : WorkspacePath,
            WorkspaceRoot = string.IsNullOrWhiteSpace(WorkspacePath) ? null : WorkspacePath,
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            ModeId = string.IsNullOrWhiteSpace(ModeId) ? null : ModeId,
            AgentName = string.IsNullOrWhiteSpace(AgentName) ? null : AgentName,
            AgentDescription = string.IsNullOrWhiteSpace(AgentDescription) ? null : AgentDescription,
            SkillsDirectories = SkillsDirectories,
            MemoryFiles = MemoryFiles,
            ExtraArguments = ExtraArguments
        };
    }

    public bool UsesBypassMode => string.Equals(ModeId, "bypassPermissions", StringComparison.Ordinal);

    public bool HasToolcallSelection => !string.IsNullOrWhiteSpace(ToolcallCaseName);

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

    private static string NormalizeModeId(string rawModeId)
    {
        return rawModeId
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "bypass" or "bypasspermissions" => "bypassPermissions",
            _ => rawModeId.Trim()
        };
    }
}
