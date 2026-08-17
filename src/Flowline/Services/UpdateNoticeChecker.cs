using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;

namespace Flowline.Services;

/// <summary>The "a newer Flowline is out" notice. Takes the validator as a parameter rather than
/// reaching for <c>FlowlineValidator.Default</c>, which writes to the user's real cache file.</summary>
internal static class UpdateNoticeChecker
{
    const string PackageId = "Flowline";

    /// <summary>Runs inside the setup spinner. Never throws — the notice must not be able to fail a
    /// command. Returns what <see cref="PrintNotice"/> should say once the spinner closes.</summary>
    public static async Task<string?> CheckAsync(
        IAnsiConsole console,
        FlowlineValidator validator,
        NuGetVersionClient nuGetVersionClient,
        bool noCache,
        CancellationToken cancellationToken)
    {
        if (!console.Profile.Capabilities.Interactive) return null;

        try
        {
            // Re-check the cached verdict against the running version instead of trusting it: once the
            // user takes the advice and updates, the entry is still fresh but no longer true.
            if (validator.TryGetCachedUpdateVersion(noCache, out var cached))
                return cached == null ? null : UpdateVersionComparer.GetNewerVersion(FlowlineVersion.Display, [cached]);

            var versions = await nuGetVersionClient.GetVersionsAsync(PackageId, cancellationToken);
            if (versions == null)
            {
                // Record the attempt so an offline machine backs off for the TTL instead of paying the
                // timeout on every command. R5 caps queries at one a day; retrying each run breaks that.
                // A Ctrl+C is not evidence NuGet is unreachable, so it must not buy a day of silence.
                if (!cancellationToken.IsCancellationRequested)
                    validator.SaveUpdateCheck(null);
                console.Verbose("Couldn't check for a newer Flowline version — skipping.");
                return null;
            }

            var newerVersion = UpdateVersionComparer.GetNewerVersion(FlowlineVersion.Display, versions);
            validator.SaveUpdateCheck(newerVersion);
            return newerVersion;
        }
        catch (Exception ex)
        {
            console.Verbose($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Printed after the spinner closes, beside "Prerequisites all good, let's go!".</summary>
    public static void PrintNotice(IAnsiConsole console, string? newerVersion)
    {
        if (newerVersion == null) return;

        console.Warning($"Flowline {Markup.Escape(newerVersion)} is out — you're on {FlowlineVersion.Display}. Update: dotnet tool update -g Flowline");
    }
}
