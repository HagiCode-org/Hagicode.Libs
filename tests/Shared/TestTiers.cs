// Shared by every Hagicode.Libs test project. Linked in from tests/Shared by tests/Directory.Build.props.
// Keep this file free of project-specific references.
using System;
using NUnit.Framework;

namespace HagiCode.Libs.Tests.Shared;

/// <summary>Tier markers and the legacy categories that existing CI filters select on.</summary>
public static class TestTiers
{
    public const string Integration = "Integration";
    public const string CriticalRegression = "CriticalRegression";
    public const string External = "External";
    public const string Slow = "Slow";

    public const string RealCli = "RealCli";
    public const string RealOnlineApi = "RealOnlineApi";
    public const string RealCliInvocationContract = "RealCliInvocationContract";
    public const string RealCliWindowsZhCnWhitespaceShim = "RealCliWindowsZhCnWhitespaceShim";
    public const string RealCliDiscovery = "RealCliDiscovery";
    public const string WindowsOnly = "WindowsOnly";
}

/// <summary>The single switch parser and the NUnit gate for External tests.</summary>
public static class ExternalGate
{
    /// <summary>True when the switch is "1" or "true" (case-insensitive).</summary>
    public static bool IsEnabled(string switchName)
    {
        var value = Environment.GetEnvironmentVariable(switchName);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reports the test as Skipped, with a reason that names the switch, when the switch is unset.</summary>
    public static void Require(string switchName)
    {
        if (!IsEnabled(switchName))
        {
            Assert.Ignore($"Set {switchName}=1 to run this External test against the real resource.");
        }
    }
}
