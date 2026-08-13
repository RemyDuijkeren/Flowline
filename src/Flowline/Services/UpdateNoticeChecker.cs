using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;

namespace Flowline.Services;

/// <summary>
/// Orchestrates the "a newer Flowline is out" notice: reads (and, on a stale cache, refreshes) the
/// cached update verdict, then prints one line when the running version is behind. Extracted from
/// <see cref="Flowline.Commands.FlowlineCommand{TSettings}.CheckSetupAsync"/> into its own seam so it's
/// testable without a full command host — <c>FlowlineValidator.Default</c> persists to the user's real
/// cache file on disk, so tests inject their own <see cref="FlowlineValidator"/> instance instead.
/// </summary>
internal static class UpdateNoticeChecker
{
    const string PackageId = "Flowline";

    /// <summary>
    /// Runs inside the setup spinner: gated on interactivity, so a non-interactive console never
    /// touches the cache or the network. Never throws — any failure (network, or persisting the
    /// verdict) is swallowed and surfaced only via <c>console.Verbose</c>. Returns the newer version to
    /// report after the spinner closes, or null when nothing newer is available or the check failed.
    /// </summary>
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
            if (validator.TryGetCachedUpdateVersion(noCache, out var cached))
                return cached;

            var versions = await nuGetVersionClient.GetVersionsAsync(PackageId, cancellationToken);
            if (versions == null)
            {
                // Record the attempt so an offline machine backs off for the TTL instead of paying the
                // timeout on every command. R5 caps queries at one a day; retrying each run breaks that.
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

    /// <summary>Printed after the spinner closes, beside "Prerequisites all good, let's go!". No-op
    /// when nothing newer is available.</summary>
    public static void PrintNotice(IAnsiConsole console, string? newerVersion)
    {
        if (newerVersion == null) return;

        console.Info($"Flowline {Markup.Escape(newerVersion)} is out — you're on {FlowlineVersion.Display}. Update: dotnet tool update -g Flowline");
    }
}
