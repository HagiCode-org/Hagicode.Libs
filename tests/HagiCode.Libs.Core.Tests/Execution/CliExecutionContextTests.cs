using System.Text;
using HagiCode.Libs.Core.Execution;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Execution;

public sealed class CliExecutionContextTests
{
    [Fact]
    public void Create_propagates_input_encoding_into_process_start_context()
    {
        var context = CliExecutionContext.Create(new CliExecutionRequest
        {
            ExecutablePath = "codex",
            Arguments = ["exec", "--experimental-json"],
            InputEncoding = Encoding.Unicode
        });

        context.InputEncoding.WebName.ShouldBe(Encoding.Unicode.WebName);
        context.ToProcessStartContext().InputEncoding.WebName.ShouldBe(Encoding.Unicode.WebName);
    }
}
