using Flowline;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;

namespace Flowline.Services;

// U4: resolves the target DEV environment for a greenfield create (flag or tenant-wide picker),
// switches to an existing pac auth profile, and refuses anything that isn't Sandbox/Developer.
// Reuses ProfileResolutionService (switch-only auth, KTD5) and FlowlineValidator (cached env lookup)
// rather than reimplementing either — the same primitives FlowlineCommand.GetAndCheckStandaloneEnvironmentAsync
// is built on — but adds the DEV-only whitelist (KTD4) that method doesn't enforce (it only refuses
// Production, not null/unrecognized types). Does not write the .flowline DEV role — that happens only
// after create + scaffold + build succeed (R10), sequenced by the caller.
public class CreateEnvironmentResolver(
    IAnsiConsole console,
    ProfileResolutionService profileResolutionService,
    SubprocessCapture capture)
{
    // KTD4: whitelist, not a Production blocklist — null, empty, and unrecognized types (e.g. "Trial")
    // are refused too, not just Production.
    internal static bool IsCreateEligibleEnvironmentType(string? type) =>
        string.Equals(type, "Sandbox", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "Developer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Seam for testing — overrides ConsoleHelper.IsInteractive (global console capability
    /// check can't be driven by an injected TestConsole).</summary>
    internal Func<bool>? IsInteractiveOverride { get; set; }

    /// <summary>Seam for testing — overrides PacUtils.GetEnvironmentsAsync (shells out to a real
    /// pac.exe subprocess with no mocking seam of its own).</summary>
    internal Func<CancellationToken, Task<List<EnvironmentInfo>>>? GetEnvironmentsOverride { get; set; }

    /// <summary>Seam for testing — overrides FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync.</summary>
    internal Func<string, PacProfile, FlowlineSettings, CancellationToken, Task<EnvironmentInfo?>>? GetEnvironmentInfoByUrlOverride { get; set; }

    public async Task<EnvironmentInfo> ResolveAsync(string? devUrl, FlowlineSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(devUrl))
            return await ResolveGivenUrlAsync(devUrl, settings, cancellationToken);

        // R13: no flag, no TTY — error naming the flag, never prompt or hang.
        if (!IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "DEV environment is required — pass --dev <URL>, or run this interactively to pick one.");

        return await PickInteractivelyAsync(settings, cancellationToken);
    }

    async Task<EnvironmentInfo> ResolveGivenUrlAsync(string devUrl, FlowlineSettings settings, CancellationToken cancellationToken)
    {
        // KTD5: switch-only — ProfileResolutionService errors naming `pac auth create` when no
        // profile matches, and never creates a profile or launches a login (R9/R13).
        var profile = await profileResolutionService.ResolveAsync(devUrl, cancellationToken);

        var getEnvironmentInfo = GetEnvironmentInfoByUrlOverride
            ?? ((url, p, s, ct) => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(url, p, s, ct));
        var env = await console.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]{devUrl}[/]...",
            _ => getEnvironmentInfo(devUrl, profile, settings, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, "Dev environment not found — check the URL or your PAC login.");

        EnsureCreateEligible(env);

        console.Ok($"Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }

    async Task<EnvironmentInfo> PickInteractivelyAsync(FlowlineSettings settings, CancellationToken cancellationToken)
    {
        var getEnvironments = GetEnvironmentsOverride ?? (ct => PacUtils.GetEnvironmentsAsync(capture, ct));
        var environments = await console.Status().FlowlineSpinner().StartAsync(
            "Checking your tenant's environments...",
            _ => getEnvironments(cancellationToken));

        if (environments.Count == 0)
            throw new FlowlineException(ExitCode.NotFound, "No environments found in your tenant — check your PAC login.");

        // R9: frame the choice as picking the project's DEV (source-of-truth) environment; show ALL
        // environments with their type — don't pre-filter, refuse on selection instead (KTD4).
        var prompt = new SelectionPrompt<EnvironmentInfo>()
            .Title(FlowlineConsoleExtensions.Question("Pick this project's DEV (source-of-truth) environment:"))
            .UseConverter(e => $"{e.DisplayName} [dim]({e.Type ?? "unknown"})[/] — {e.EnvironmentUrl}")
            .AddChoices(environments);

        var selected = console.Prompt(prompt);

        EnsureCreateEligible(selected);

        await profileResolutionService.ResolveAsync(selected.EnvironmentUrl!, cancellationToken);

        console.Ok($"Dev: [bold]{selected.DisplayName}[/] ({selected.EnvironmentUrl})");
        return selected;
    }

    static void EnsureCreateEligible(EnvironmentInfo env)
    {
        if (!IsCreateEligibleEnvironmentType(env.Type))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{env.DisplayName}' ({env.Type ?? "unknown"}) isn't a Sandbox or Developer environment — create only runs against DEV-type environments.");
    }

    bool IsInteractive() => IsInteractiveOverride?.Invoke() ?? ConsoleHelper.IsInteractive(settings: null);
}
