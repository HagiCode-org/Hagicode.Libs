using HagiCode.Libs.Core.Environment;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Environment;

[TestFixture]
public sealed class RunEnvironmentTests
{
    [Test]
    public void Merge_InjectedValueIsAddedWhenNothingElseSetsIt()
    {
        var merged = RunEnvironment.Merge(
            new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = "session-a" },
            new Dictionary<string, string?>(),
            new Dictionary<string, string?>());

        merged["HAGI_SESSION_ID"].ShouldBe("session-a");
    }

    [Test]
    public void Merge_ResolvedValueBeatsInjectedValue()
    {
        var merged = RunEnvironment.Merge(
            new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = "injected" },
            new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = "process-environment" },
            new Dictionary<string, string?>());

        merged["HAGI_SESSION_ID"].ShouldBe("process-environment");
    }

    [Test]
    public void Merge_ConfiguredValueBeatsResolvedAndInjectedValues()
    {
        var merged = RunEnvironment.Merge(
            new Dictionary<string, string?> { ["HAGI_PROJECT_NAME"] = "injected" },
            new Dictionary<string, string?> { ["HAGI_PROJECT_NAME"] = "resolved" },
            new Dictionary<string, string?> { ["HAGI_PROJECT_NAME"] = "configured" });

        merged["HAGI_PROJECT_NAME"].ShouldBe("configured");
    }

    [Test]
    public void Merge_BlankInjectedValueIsDropped()
    {
        var merged = RunEnvironment.Merge(
            new Dictionary<string, string?> { ["HAGI_PROJECT_PATH"] = "   ", ["HAGI_PROJECT_ID"] = null },
            new Dictionary<string, string?>(),
            new Dictionary<string, string?>());

        merged.ContainsKey("HAGI_PROJECT_PATH").ShouldBeFalse();
        merged.ContainsKey("HAGI_PROJECT_ID").ShouldBeFalse();
    }

    [Test]
    public void Merge_DoesNotMutateItsInputs()
    {
        var injected = new Dictionary<string, string?> { ["A"] = "1" };
        var resolved = new Dictionary<string, string?> { ["B"] = "2" };
        var configured = new Dictionary<string, string?> { ["C"] = "3" };

        var merged = RunEnvironment.Merge(injected, resolved, configured);

        merged.Count.ShouldBe(3);
        injected.Count.ShouldBe(1);
        resolved.Count.ShouldBe(1);
        configured.Count.ShouldBe(1);
    }
}
