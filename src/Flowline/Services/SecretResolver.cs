using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class SecretResolver(IAnsiConsole console)
{
    /// <summary>
    /// Resolves the client secret using the following chain:
    /// 1. --secret flag (secretFlag parameter)
    /// 2. AZURE_CLIENT_SECRET environment variable
    /// 3. Interactive prompt (when running interactively)
    /// 4. Throws FlowlineException (non-interactive with no secret available)
    ///
    /// Security: the resolved value is never logged.
    /// </summary>
    public Task<string> ResolveAsync(PacProfile profile, string? secretFlag)
    {
        if (!string.IsNullOrEmpty(secretFlag))
            return Task.FromResult(secretFlag);

        var envSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        if (!string.IsNullOrEmpty(envSecret))
            return Task.FromResult(envSecret);

        if (IsInteractive())
        {
            var profileLabel = profile.Name ?? profile.ApplicationId ?? "unknown";
            var secret = console.Prompt(
                new TextPrompt<string>($"Enter client secret for '{profileLabel}' (client ID: {profile.ApplicationId}):")
                    .Secret());
            return Task.FromResult(secret);
        }

        throw new FlowlineException(ExitCode.NotAuthenticated,
            $"Client secret required for '{profile.Name ?? profile.ApplicationId ?? "unknown"}' — set AZURE_CLIENT_SECRET env var or use --secret flag");
    }

    protected virtual bool IsInteractive() => ConsoleHelper.IsInteractive(null);
}
