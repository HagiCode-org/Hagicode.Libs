namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Adds the Windows native-executable explanation to a provider's not-found message.
/// </summary>
internal static class CliExecutableNotFoundMessage
{
    /// <summary>
    /// Returns the base message, followed by the reason when the agent needs its native executable and only command scripts exist.
    /// </summary>
    /// <param name="baseMessage">The provider's own not-found sentence.</param>
    /// <param name="providerName">The provider name that keys the install descriptor.</param>
    /// <param name="lookup">The lookup result that found nothing.</param>
    /// <returns>The message to report.</returns>
    public static string Describe(string baseMessage, string providerName, CliExecutableLookupResult lookup)
    {
        if (lookup.NotFoundReason != CliExecutableNotFoundReason.NativeExecutableMissing)
        {
            return baseMessage;
        }

        var npmPackage = CliInstallRegistry.FindByProviderName(providerName)?.NpmPackage;
        var reinstall = string.IsNullOrWhiteSpace(npmPackage)
            ? "Reinstall it with its native installer."
            : $"Reinstall it with its native installer, or run 'npm install -g {npmPackage}' and check that its platform package was installed (npm skips optional dependencies with --omit=optional).";

        return $"{baseMessage} On Windows this agent requires its native executable, but only command script(s) were found: "
               + $"{string.Join(", ", lookup.SkippedCommandScripts)}. {reinstall}";
    }
}
