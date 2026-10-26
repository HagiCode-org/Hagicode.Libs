using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Opt-in: runs the real CodeBuddy CLI through CodebuddyProvider and the shared process manager (codebuddy-run-steering, task 7.1).
// It needs HAGICODE_REAL_CLI_TESTS and a CodeBuddy login with model quota. It uses the free hy3 model, and a throwaway working directory.
public sealed class CodebuddyRealCliSteeringTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string SteerId = "real-steer-1";
    private const string SteerText = "Also append the word REALSTEER to your final reply.";

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task A_steer_during_a_real_tool_call_is_sent_and_consumed_exactly_once()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

        var workingDirectory = Path.Combine(Path.GetTempPath(), "codebuddy-steer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var provider = new CodebuddyProvider(new CliExecutableResolver(), new CliProcessManager());
            var steering = new CodebuddyRunSteering();
            var outcomes = new List<(string SteerId, CodebuddySteerOutcome Outcome)>();
            steering.OutcomeObserved += (_, args) => outcomes.Add((args.SteerId, args.Outcome));

            var options = new CodebuddyOptions
            {
                Model = "hy3",
                WorkingDirectory = workingDirectory,
                Steering = steering
            };

            var steerResult = CodebuddySteerResult.NotSteerable;
            var index = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await foreach (var message in provider.ExecuteAsync(
                               options,
                               "Use the bash tool exactly once to run: sleep 15; echo TOOL-DONE . Then reply with only the word FINISHED.",
                               cts.Token))
            {
                if (++index == 2)
                {
                    steerResult = await steering.SteerAsync(SteerId, SteerText, cts.Token);
                }
            }

            steerResult.ShouldBe(CodebuddySteerResult.Sent);
            outcomes.ShouldBe([(SteerId, CodebuddySteerOutcome.Consumed)]);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}
