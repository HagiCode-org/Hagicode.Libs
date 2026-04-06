using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CopilotExecutablePathPolicyTests
{
    [Fact]
    public void SelectPreferredPath_skips_vscode_copilot_chat_shim()
    {
        var resolved = CopilotExecutablePathPolicy.SelectPreferredPath(
        [
            "/home/test/.vscode-server/data/User/globalStorage/github.copilot-chat/copilotCli/copilot",
            "/home/test/.nvm/versions/node/v24.14.0/bin/copilot"
        ]);

        resolved.ShouldBe("/home/test/.nvm/versions/node/v24.14.0/bin/copilot");
    }

    [Fact]
    public void SelectPreferredPath_falls_back_to_vscode_copilot_chat_shim_when_no_real_cli_exists()
    {
        var resolved = CopilotExecutablePathPolicy.SelectPreferredPath(
        [
            "/home/test/.vscode-server/data/User/globalStorage/github.copilot-chat/copilotCli/copilot"
        ]);

        resolved.ShouldBe("/home/test/.vscode-server/data/User/globalStorage/github.copilot-chat/copilotCli/copilot");
    }
}
