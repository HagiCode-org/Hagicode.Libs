using HagiCode.Libs.CiSetup.Console;
using Shouldly;

namespace HagiCode.Libs.CiSetup.Tests;

public sealed class CiSetupNpmCandidatesTests
{
    [Test]
    public void Npm_native_exe_is_probed_before_the_cmd_and_bat_shims()
    {
        CiSetupEnvironment.NpmExecutableCandidates.ShouldBe(["npm", "npm.exe", "npm.cmd", "npm.bat"]);
    }
}
