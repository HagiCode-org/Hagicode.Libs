using HagiCode.Libs.CiSetup.Console;
using Shouldly;

namespace HagiCode.Libs.CiSetup.Tests;

public sealed class CiSetupArgumentParserTests
{
    [Test]
    [Category("CriticalRegression")]
    public void Parse_reads_install_and_verify_flags_without_selection()
    {
        var parsed = CiSetupArgumentParser.Parse(["--install", "--verify"]);

        parsed.Install.ShouldBeTrue();
        parsed.Verify.ShouldBeTrue();
        parsed.VerifyDiscovery.ShouldBeFalse();
        parsed.HasSelection.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Parse_reads_provider_strategy_and_expectations()
    {
        var parsed = CiSetupArgumentParser.Parse(
        [
            "--verify-discovery",
            "--provider", "ClaudeCode",
            "--strategy", "native",
            "--expect-version", "2.1.168",
            "--expect-install-kind", "NativeInstaller",
            "--without-install-dir-on-path"
        ]);

        parsed.VerifyDiscovery.ShouldBeTrue();
        parsed.Provider.ShouldBe("ClaudeCode");
        parsed.Strategy.ShouldBe("native");
        parsed.ExpectVersion.ShouldBe("2.1.168");
        parsed.ExpectInstallKind.ShouldBe("NativeInstaller");
        parsed.WithoutInstallDirOnPath.ShouldBeTrue();
        parsed.HasSelection.ShouldBeTrue();
    }

    [Test]
    public void Parse_records_an_option_that_is_missing_its_value()
    {
        var parsed = CiSetupArgumentParser.Parse(["--install", "--provider"]);

        parsed.Provider.ShouldBeNull();
        parsed.Errors.ShouldContain("--provider requires a value.");
    }

    [Test]
    public void Parse_treats_a_following_option_as_a_missing_value()
    {
        var parsed = CiSetupArgumentParser.Parse(["--install", "--provider", "--strategy", "npm"]);

        parsed.Errors.ShouldContain("--provider requires a value.");
        parsed.Strategy.ShouldBe("npm");
    }

    [Test]
    public void Parse_ignores_unknown_tokens_as_the_original_console_did()
    {
        var parsed = CiSetupArgumentParser.Parse(["--install", "--something-new"]);

        parsed.Install.ShouldBeTrue();
        parsed.Errors.ShouldBeEmpty();
    }
}
