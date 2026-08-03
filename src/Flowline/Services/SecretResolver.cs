using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class SecretResolver(IAnsiConsole console)
{
    /// <summary>
    /// Resolves the client secret using the following chain:
    /// 1. --client-secret flag (secretFlag parameter)
    /// 2. AZURE_CLIENT_SECRET environment variable
    /// 3. Interactive prompt (when running interactively)
    /// 4. Throws FlowlineException (non-interactive with no secret available)
    ///
    /// Security: the resolved value is never logged.
    /// </summary>
    public async Task<string> ResolveAsync(PacProfile profile, string? secretFlag, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(secretFlag))
            return secretFlag;

        var envSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        if (!string.IsNullOrEmpty(envSecret))
            return envSecret;

        if (IsInteractive())
        {
            return await console.PromptAsync(
                new TextPrompt<string>(FlowlineConsoleExtensions.Question($"Enter client secret for '{ResolveProfileLabel(profile)}' (client ID: {profile.ApplicationId}):"))
                    .Secret(), cancellationToken);
        }

        throw new FlowlineException(ExitCode.NotAuthenticated,
            $"Client secret required for '{ResolveProfileLabel(profile)}' — set AZURE_CLIENT_SECRET env var or use --client-secret flag");
    }

    // PAC's authprofiles_v2.json gives an unnamed profile an empty-string Name, not null — a bare ??
    // chain never falls through to ApplicationId for that shape (see
    // DataverseConnector.ResolveProfileLabel's identical fix for the Name/User fallback case).
    static string ResolveProfileLabel(PacProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name
        : !string.IsNullOrWhiteSpace(profile.ApplicationId) ? profile.ApplicationId
        : "unknown";

    bool IsInteractive() => console.Profile.Capabilities.Interactive;
}
