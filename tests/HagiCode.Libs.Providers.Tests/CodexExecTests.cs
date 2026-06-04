using System.Text;
using ManagedCode.CodexSharpSDK.Execution;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexExecTests
{
    [Fact]
    public void CreateStartInfo_redirects_streams_and_forces_utf8_without_bom()
    {
        var invocation = new CodexProcessInvocation(
            "/tmp/codex",
            ["exec", "--json"],
            new Dictionary<string, string>
            {
                ["PATH"] = "/tmp/runtime"
            },
            "你好，Codex");

        var startInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation);

        startInfo.FileName.ShouldBe("/tmp/codex");
        startInfo.ArgumentList.ShouldBe(["exec", "--json"]);
        startInfo.RedirectStandardInput.ShouldBeTrue();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.StandardInputEncoding.ShouldNotBeNull();
        startInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardInputEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.StandardOutputEncoding.ShouldNotBeNull();
        startInfo.StandardOutputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardOutputEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.StandardErrorEncoding.ShouldNotBeNull();
        startInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardErrorEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.Environment["PATH"].ShouldBe("/tmp/runtime");
    }
}
