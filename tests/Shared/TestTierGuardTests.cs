// Shared by every Hagicode.Libs test project. Linked in from tests/Shared by tests/Directory.Build.props.
// Each assembly checks its own tests by reflection, so a tier that decays fails the build's test run.
using System.Reflection;
using NUnit.Framework;

namespace HagiCode.Libs.Tests.Shared;

[TestFixture]
public sealed class TestTierGuardTests
{
    private static readonly HashSet<string> KnownCategories = new(StringComparer.Ordinal)
    {
        TestTiers.Integration,
        TestTiers.CriticalRegression,
        TestTiers.External,
        TestTiers.Slow,
        TestTiers.RealCli,
        TestTiers.RealOnlineApi,
        TestTiers.RealCliInvocationContract,
        TestTiers.RealCliWindowsZhCnWhitespaceShim,
        TestTiers.RealCliDiscovery,
        TestTiers.WindowsOnly,
    };

    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "TestAttribute",
        "TestCaseAttribute",
        "TestCaseSourceAttribute",
    };

    private sealed record TestInfo(string Name, IReadOnlyList<string> Categories);

    [Test]
    public void Guard_sees_the_tests_of_this_assembly()
    {
        var tests = EnumerateTests();

        Assert.That(tests, Is.Not.Empty, "The guard found no tests; reflection enumeration is broken.");
    }

    [Test]
    public void Every_category_is_a_tier_marker_or_a_documented_legacy_category()
    {
        var offenders = EnumerateTests()
            .SelectMany(test => test.Categories
                .Where(category => !KnownCategories.Contains(category))
                .Select(category => $"{test.Name} [{category}]"))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Unknown category on test(s); add a tier marker or document the legacy category: " + string.Join(", ", offenders));
    }

    [Test]
    public void RealCli_and_RealOnlineApi_tests_are_External()
    {
        var offenders = EnumerateTests()
            .Where(test => (test.Categories.Contains(TestTiers.RealCli) || test.Categories.Contains(TestTiers.RealOnlineApi))
                           && !test.Categories.Contains(TestTiers.External))
            .Select(test => test.Name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Real-CLI or online-API test(s) must also carry the External category: " + string.Join(", ", offenders));
    }

    [Test]
    public void CriticalRegression_does_not_combine_with_Slow_or_External()
    {
        var offenders = EnumerateTests()
            .Where(test => test.Categories.Contains(TestTiers.CriticalRegression)
                           && (test.Categories.Contains(TestTiers.Slow) || test.Categories.Contains(TestTiers.External)))
            .Select(test => test.Name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "CriticalRegression must not combine with Slow or External: " + string.Join(", ", offenders));
    }

    [Test]
    public void Each_test_project_has_at_least_one_CriticalRegression_test()
    {
        var critical = EnumerateTests()
            .Count(test => test.Categories.Contains(TestTiers.CriticalRegression));

        Assert.That(critical, Is.GreaterThan(0),
            $"{Assembly.GetExecutingAssembly().GetName().Name} has no CriticalRegression test; the critical profile would run nothing here.");
    }

    private static List<TestInfo> EnumerateTests()
    {
        var result = new List<TestInfo>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.IsAbstract)
            {
                continue;
            }

            var typeCategories = CategoriesOf(type);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (!IsTestMethod(method))
                {
                    continue;
                }

                var categories = typeCategories.Concat(CategoriesOf(method)).Distinct(StringComparer.Ordinal).ToList();
                result.Add(new TestInfo($"{type.FullName}.{method.Name}", categories));
            }
        }

        return result;
    }

    private static bool IsTestMethod(MethodInfo method)
    {
        return method.GetCustomAttributes(inherit: true)
            .Any(attribute => TestAttributeNames.Contains(attribute.GetType().Name));
    }

    private static List<string> CategoriesOf(Type type)
    {
        var names = new List<string>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            names.AddRange(current.GetCustomAttributes(typeof(CategoryAttribute), inherit: false)
                .Cast<CategoryAttribute>()
                .Select(attribute => attribute.Name));
        }

        return names;
    }

    private static List<string> CategoriesOf(MemberInfo member)
    {
        return member.GetCustomAttributes(typeof(CategoryAttribute), inherit: true)
            .Cast<CategoryAttribute>()
            .Select(attribute => attribute.Name)
            .ToList();
    }
}
