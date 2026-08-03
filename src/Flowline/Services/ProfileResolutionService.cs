using Flowline;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Services;

public class ProfileResolutionService(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions)
{
    /// <summary>Seam for testing — set to override FindBestProfile resolution.</summary>
    internal Func<string, ProfileResolutionResult>? FindBestProfileOverride { get; set; }

    /// <summary>Seam for testing — set to override DataverseConnector.IsProfileActive.</summary>
    internal Func<PacProfile, bool>? IsProfileActiveOverride { get; set; }

    /// <summary>Seam for testing — set to override DataverseConnector.GetPacProfiles.</summary>
    internal Func<IReadOnlyList<PacProfile>>? GetPacProfilesOverride { get; set; }

    /// <summary>Seam for testing — set to override ConsoleHelper.IsInteractive (the global console
    /// capability check can't be driven by an injected TestConsole).</summary>
    internal Func<bool>? IsInteractiveOverride { get; set; }

    /// <summary>Seam for testing — set to override PacUtils.SelectAuthProfileAsync (which shells out
    /// to a real pac.exe subprocess with no mocking seam of its own).</summary>
    internal Func<PacProfile, IReadOnlyList<PacProfile>, CancellationToken, Task>? SelectAuthProfileOverride { get; set; }

    public async Task<PacProfile> ResolveAsync(string environmentUrl, CancellationToken cancellationToken = default)
    {
        var result = FindBestProfileOverride != null
            ? FindBestProfileOverride(environmentUrl)
            : dataverseConnector.FindBestProfile(environmentUrl);

        return result switch
        {
            ProfileFound found       => await HandleFound(found.Profile, cancellationToken),
            ProfileAmbiguous ambig   => await HandleAmbiguousAsync(ambig.Candidates, environmentUrl, cancellationToken),
            ProfileNotFound notFound => throw BuildNotFoundError(notFound.EnvironmentUrl),
            _                        => throw new InvalidOperationException($"Unexpected ProfileResolutionResult: {result.GetType().Name}")
        };
    }

    async Task<PacProfile> HandleFound(PacProfile profile, CancellationToken cancellationToken)
    {
        console.Verbose($"Matched profile: {profile.DisplayName}, Kind: {profile.Kind}, URL: {profile.Resource}");
        await EnsureActiveProfileAsync(profile, cancellationToken);
        return profile;
    }

    async Task<PacProfile> HandleAmbiguousAsync(IReadOnlyList<PacProfile> candidates, string environmentUrl, CancellationToken cancellationToken)
    {
        if (!IsInteractive())
        {
            var lines = string.Join("\n", candidates.Select(FormatCandidate));
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Multiple PAC auth profiles match {environmentUrl} — run: pac auth select --index <n> to set one profile active\n{lines}");
        }

        var prompt = new SelectionPrompt<PacProfile>()
            .Title(FlowlineConsoleExtensions.Question("Multiple PAC auth profiles match — select one:"))
            .UseConverter(FormatCandidate)
            .AddChoices(candidates);

        var selected = await console.PromptAsync(prompt, cancellationToken);

        await EnsureActiveProfileAsync(selected, cancellationToken);
        return selected;
    }

    // R2/R3/R4/R5: guard the resolved profile against PAC CLI's globally active profile. Runs once
    // per ResolveAsync call (R8) — nothing is cached across calls, so a command resolving multiple
    // URLs re-checks independently each time.
    async Task EnsureActiveProfileAsync(PacProfile profile, CancellationToken cancellationToken)
    {
        var isActive = IsProfileActiveOverride ?? dataverseConnector.IsProfileActive;
        var allProfiles = GetPacProfilesOverride?.Invoke() ?? dataverseConnector.GetPacProfiles().ToList();
        var index = ProfileIndex(profile, allProfiles);

        if (isActive(profile))
        {
            EmitStatusLine(profile, index);
            return;
        }

        if (runtimeOptions.AutoSwitchProfile)
        {
            EmitStatusLine(profile, index);
            await SwitchProfileAsync(profile, allProfiles, cancellationToken);
            return;
        }

        if (!IsInteractive())
        {
            EmitStatusLine(profile, index);
            throw BuildMismatchException(profile, allProfiles);
        }

        // Interactive mismatch: skip the "Resolved..." status line — the active-vs-target line
        // below already names the profile, and the prompt doesn't repeat the name a third time.
        ShowActiveVsTarget(allProfiles, isActive, profile);

        var confirmed = await console.PromptAsync(
            new ConfirmationPrompt(FlowlineConsoleExtensions.Question("Switch active PAC auth profile?")) { DefaultValue = false }, cancellationToken);

        if (!confirmed)
            throw BuildMismatchException(profile, allProfiles);

        await SwitchProfileAsync(profile, allProfiles, cancellationToken);
    }

    async Task SwitchProfileAsync(PacProfile profile, IReadOnlyList<PacProfile> allProfiles, CancellationToken cancellationToken)
    {
        var select = SelectAuthProfileOverride ?? PacUtils.SelectAuthProfileAsync;
        await select(profile, allProfiles, cancellationToken);

        // pac auth select exiting 0 only means the process ran without error — invalidate the cached
        // auth profile file and re-read it to confirm the switch actually took effect before reporting
        // success (DataverseConnector caches the parsed file for the process lifetime; see LoadPacAuthProfiles).
        if (IsProfileActiveOverride == null)
            dataverseConnector.InvalidateAuthProfilesCache();
        var isActive = IsProfileActiveOverride ?? dataverseConnector.IsProfileActive;
        if (!isActive(profile))
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"pac auth select reported success, but PAC auth profile '{profile.DisplayName}' still isn't active — check 'pac auth list' and try again.");

        console.Info($"Switched active PAC auth profile to '{Markup.Escape(profile.DisplayName)}'");
    }

    FlowlineException BuildMismatchException(PacProfile profile, IReadOnlyList<PacProfile> allProfiles)
    {
        var (argName, argValue) = PacUtils.BuildAuthSelectArgs(profile, allProfiles);
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"PAC auth profile '{profile.DisplayName}' isn't the active PAC CLI profile — run: pac auth select {argName} '{argValue}'");
    }

    // A full profile table was tried and dropped here (buried the one useful comparison among
    // unrelated rows); a single-line "Active -> Target" was tried next and also dropped (still too
    // long with full URLs on both sides, which the decision actually needs to see). Two lines,
    // Active italicized to read as secondary/context and Target bold as the actual decision.
    void ShowActiveVsTarget(IReadOnlyList<PacProfile> allProfiles, Func<PacProfile, bool> isActive, PacProfile target)
    {
        var current = allProfiles.FirstOrDefault(p => p.Kind == target.Kind && isActive(p));
        var currentLabel = current != null ? FormatProfileLabel(current) : "(none)";
        console.MarkupLine($"Active: [dim]{currentLabel}[/]");
        console.MarkupLine($"Target: [bold]{FormatProfileLabel(target)}[/]");
    }

    static string FormatProfileLabel(PacProfile p) =>
        string.IsNullOrEmpty(p.Name)
            ? $"({Markup.Escape(p.DisplayName)}) — {Markup.Escape(p.EnvironmentLabel)}"
            : $"'{Markup.Escape(p.DisplayName)}' — {Markup.Escape(p.EnvironmentLabel)}";

    bool IsInteractive() => IsInteractiveOverride?.Invoke() ?? ConsoleHelper.IsInteractive();

    FlowlineException BuildNotFoundError(string environmentUrl)
    {
        var suggestion = BuildNameSuggestion(environmentUrl);
        var url = environmentUrl.TrimEnd('/');
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"No PAC auth profile found for {url}\nRun: pac auth create --environment {url} --name \"{suggestion}\"");
    }

    // "Resolved", not "Using" — this fires before the active-profile guard runs, so the profile isn't
    // necessarily active yet (that's exactly what the guard below may still need to fix). index is the
    // profile's 1-based position in `pac auth list` (what `pac auth select --index <n>` takes) — shown
    // so the user can cross-reference the resolved profile against that list; omitted when unresolved (-1).
    void EmitStatusLine(PacProfile profile, int index)
    {
        var pos = index > 0 ? $"#{index} " : "";
        var status = string.IsNullOrEmpty(profile.Name)
            ? $"Resolved PAC auth profile {pos}({Markup.Escape(profile.DisplayName)}, {Markup.Escape(profile.Kind ?? "")}) — {Markup.Escape(profile.EnvironmentLabel)}"
            : $"Resolved PAC auth profile {pos}'{Markup.Escape(profile.DisplayName)}' ({Markup.Escape(profile.Kind ?? "")}) — {Markup.Escape(profile.EnvironmentLabel)}";
        console.Info(status);
    }

    // 1-based position in the loaded profile list — matches `pac auth list` numbering and the
    // --index arg from BuildAuthSelectArgs. Record value-equality, same as BuildAuthSelectArgs. -1 = not found.
    static int ProfileIndex(PacProfile profile, IReadOnlyList<PacProfile> allProfiles)
    {
        for (var i = 0; i < allProfiles.Count; i++)
            if (allProfiles[i] == profile)
                return i + 1;
        return -1;
    }

    static string FormatCandidate(PacProfile p) =>
        string.IsNullOrEmpty(p.Name)
            ? $"({Markup.Escape(p.DisplayName)}, {Markup.Escape(p.Kind ?? "")}) — {Markup.Escape(p.EnvironmentLabel)}"
            : $"'{Markup.Escape(p.DisplayName)}' ({Markup.Escape(p.Kind ?? "")}) — {Markup.Escape(p.EnvironmentLabel)}";

    internal static string BuildNameSuggestion(string environmentUrl)
    {
        // Extract host, take first segment before first dot
        if (!Uri.TryCreate(environmentUrl.Contains("://") ? environmentUrl : "https://" + environmentUrl, UriKind.Absolute, out var uri))
            return "MyOrg";

        var firstSegment = uri.Host.Split('.')[0]; // e.g. "automatevalue-dev"
        var parts = firstSegment.Split('-');
        return string.Join("-", parts.Select(p => p.Length > 0
            ? char.ToUpperInvariant(p[0]) + p[1..]
            : p));
    }
}
