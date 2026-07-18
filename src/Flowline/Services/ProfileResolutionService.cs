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
        EmitStatusLine(profile);
        console.Verbose($"Matched profile: {profile.Name ?? "(unnamed)"}, Kind: {profile.Kind}, URL: {profile.Resource}");
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
            .Title("Multiple PAC auth profiles match — select one:")
            .UseConverter(FormatCandidate)
            .AddChoices(candidates);

        var selected = console.Prompt(prompt);

        EmitStatusLine(selected);
        await EnsureActiveProfileAsync(selected, cancellationToken);
        return selected;
    }

    // R2/R3/R4/R5: guard the resolved profile against PAC CLI's globally active profile. Runs once
    // per ResolveAsync call (R8) — nothing is cached across calls, so a command resolving multiple
    // URLs re-checks independently each time.
    async Task EnsureActiveProfileAsync(PacProfile profile, CancellationToken cancellationToken)
    {
        var isActive = IsProfileActiveOverride ?? dataverseConnector.IsProfileActive;
        if (isActive(profile)) return;

        var allProfiles = GetPacProfilesOverride?.Invoke() ?? dataverseConnector.GetPacProfiles().ToList();

        if (runtimeOptions.AutoSwitchProfile)
        {
            await SwitchProfileAsync(profile, allProfiles, cancellationToken);
            return;
        }

        if (!IsInteractive())
            throw BuildMismatchException(profile, allProfiles);

        RenderProfileTable(allProfiles, isActive, profile);

        var confirmed = console.Prompt(
            new ConfirmationPrompt($"Switch active PAC auth profile to '{profile.Name ?? "(unnamed)"}'?")
            {
                DefaultValue = false,
                // Spectre's ConfirmationPrompt defaults DefaultValueStyle to green, which collides with
                // the green highlight on the target row in the table above — dim it so '(n)' doesn't
                // read as another way to select the highlighted profile.
                DefaultValueStyle = new Style(foreground: Color.Grey)
            });

        if (!confirmed)
            throw BuildMismatchException(profile, allProfiles);

        await SwitchProfileAsync(profile, allProfiles, cancellationToken);
    }

    async Task SwitchProfileAsync(PacProfile profile, IReadOnlyList<PacProfile> allProfiles, CancellationToken cancellationToken)
    {
        var select = SelectAuthProfileOverride ?? PacUtils.SelectAuthProfileAsync;
        await select(profile, allProfiles, cancellationToken);

        // pac auth select exiting 0 only means the process ran without error — re-read the auth
        // profile file to confirm the switch actually took effect before reporting success.
        var isActive = IsProfileActiveOverride ?? dataverseConnector.IsProfileActive;
        if (!isActive(profile))
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"pac auth select reported success, but PAC auth profile '{profile.Name ?? "(unnamed)"}' still isn't active — check 'pac auth list' and try again.");

        console.Info($"Switched active PAC auth profile to '{Markup.Escape(profile.Name ?? "(unnamed)")}'");
    }

    FlowlineException BuildMismatchException(PacProfile profile, IReadOnlyList<PacProfile> allProfiles)
    {
        var (argName, argValue) = PacUtils.BuildAuthSelectArgs(profile, allProfiles);
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"PAC auth profile '{profile.Name ?? "(unnamed)"}' isn't the active PAC CLI profile — run: pac auth select {argName} '{argValue}'");
    }

    // Column order/naming mirrors 'pac auth list' (Index, Kind, Name, User, Environment) so the table
    // reads familiarly to anyone who already knows that command. Index is 1-based, matching what
    // 'pac auth select --index' expects; the active profile's index carries a trailing '*' instead of
    // a separate Active column. The row for `target` (the profile Flowline is about to switch to, if
    // confirmed) is highlighted green so it's unambiguous which row the y/n prompt below refers to.
    // Cloud/Type aren't shown — PacProfile doesn't carry them and they add no value for this table's
    // one job: sanity-check the switch target.
    void RenderProfileTable(IReadOnlyList<PacProfile> allProfiles, Func<PacProfile, bool> isActive, PacProfile target)
    {
        var table = new Table().AddColumn("Index").AddColumn("Kind").AddColumn("Name").AddColumn("User").AddColumn("Environment");
        for (var i = 0; i < allProfiles.Count; i++)
        {
            var p = allProfiles[i];
            var index = isActive(p) ? $"{i + 1}*" : (i + 1).ToString();

            string Cell(string text) => p == target ? $"[green]{text}[/]" : text;

            table.AddRow(
                Cell(index),
                Cell(Markup.Escape(p.Kind ?? "")),
                Cell(Markup.Escape(p.Name ?? "(unnamed)")),
                Cell(Markup.Escape(p.User ?? "")),
                Cell(FormatEnvironment(p)));
        }
        console.Write(table);
    }

    // "FriendlyName (Url)" when a display name is available, otherwise the bare URL — shared by the
    // profile table and the resolved-profile status line so both describe an environment the same way.
    static string FormatEnvironment(PacProfile profile) =>
        string.IsNullOrEmpty(profile.FriendlyName)
            ? Markup.Escape(profile.Resource ?? "")
            : $"{Markup.Escape(profile.FriendlyName)} ({Markup.Escape(profile.Resource ?? "")})";

    bool IsInteractive() => IsInteractiveOverride?.Invoke() ?? ConsoleHelper.IsInteractive(settings: null);

    FlowlineException BuildNotFoundError(string environmentUrl)
    {
        var suggestion = BuildNameSuggestion(environmentUrl);
        var url = environmentUrl.TrimEnd('/');
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"No PAC auth profile found for {url}\nRun: pac auth create --environment {url} --name \"{suggestion}\"");
    }

    // "Resolved", not "Using" — this fires before the active-profile guard runs, so the profile isn't
    // necessarily active yet (that's exactly what the guard below may still need to fix).
    void EmitStatusLine(PacProfile profile)
    {
        var environment = FormatEnvironment(profile);
        var status = string.IsNullOrEmpty(profile.Name)
            ? $"Resolved PAC auth profile (unnamed, {Markup.Escape(profile.Kind ?? "")}) — {environment}"
            : $"Resolved PAC auth profile '{Markup.Escape(profile.Name)}' ({Markup.Escape(profile.Kind ?? "")}) — {environment}";
        console.Info(status);
    }

    static string FormatCandidate(PacProfile p) =>
        string.IsNullOrEmpty(p.Name)
            ? $"(unnamed, {Markup.Escape(p.Kind ?? "")}) — {Markup.Escape(p.Resource ?? "")}"
            : $"'{Markup.Escape(p.Name)}' ({Markup.Escape(p.Kind ?? "")}) — {Markup.Escape(p.Resource ?? "")}";

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
