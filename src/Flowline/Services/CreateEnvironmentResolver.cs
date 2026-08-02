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

// U4: resolves a target environment (flag or tenant-wide picker) and switches to an existing pac auth
// profile. Two entry points for the two callers (they differ by whether a Dataverse write follows):
//   - ResolveCreateTargetAsync (init / clone's create-new): DEV target, filtered/guarded to Sandbox+
//     Developer (KTD4) plus a create-new-environment escape hatch — a create writes to Dataverse.
//   - ResolveSourceAsync (clone-existing): the source of truth (usually PROD), any type, no guard —
//     clone-existing writes nothing to Dataverse. The caller assigns the .flowline role from the type.
// Reuses ProfileResolutionService (switch-only auth, KTD5) and FlowlineValidator (cached env lookup)
// rather than reimplementing either. Does not write the .flowline role — that is sequenced by the caller
// (after create + scaffold + build for init, R10).
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

    // Clone source types: everything except Default and Teams — those aren't ALM environments and
    // can't serve as a project's source of truth. Blocklist, not whitelist: unknown/null types stay
    // listed rather than silently vanishing from the picker.
    internal static bool IsSelectableSourceType(string? type) =>
        !string.Equals(type, "Default", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(type, "Teams", StringComparison.OrdinalIgnoreCase);

    /// <summary>Seam for testing — overrides ConsoleHelper.IsInteractive (global console capability
    /// check can't be driven by an injected TestConsole).</summary>
    internal Func<bool>? IsInteractiveOverride { get; set; }

    /// <summary>Seam for testing — overrides PacUtils.GetEnvironmentsAsync (shells out to a real
    /// pac.exe subprocess with no mocking seam of its own).</summary>
    internal Func<CancellationToken, Task<List<EnvironmentInfo>>>? GetEnvironmentsOverride { get; set; }

    /// <summary>Seam for testing — overrides FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync.</summary>
    internal Func<string, PacProfile, FlowlineSettings, CancellationToken, Task<EnvironmentInfo?>>? GetEnvironmentInfoByUrlOverride { get; set; }

    // INIT (greenfield create): resolve the DEV target. The picker is filtered to create-eligible types
    // (Sandbox/Developer, KTD4) — Spectre has no non-selectable item, so filter rather than gray-out —
    // plus a "+ Create new environment" escape hatch. Returns null when the user picks that hatch: advice
    // is already emitted and the caller should exit 0 (env creation stays with `provision`, not here).
    public async Task<EnvironmentInfo?> ResolveCreateTargetAsync(string? devUrl, FlowlineSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(devUrl))
            return await ResolveGivenUrlAsync(devUrl, settings, requireEligible: true, "Dev", cancellationToken);

        // R13: no flag, no TTY — error naming the flag, never prompt or hang.
        if (!IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "DEV environment is required — pass --dev <URL>, or run this interactively to pick one.");

        return await PickCreateTargetAsync(cancellationToken);
    }

    // CLONE (adopt existing): resolve the environment to clone from — the source of truth, usually PROD
    // (see AGENTS.md). No type guard: clone-existing writes nothing to Dataverse, so any environment is a
    // valid source. The caller assigns the .flowline role from the chosen environment's type.
    public async Task<EnvironmentInfo> ResolveSourceAsync(string? sourceUrl, FlowlineSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
            return await ResolveGivenUrlAsync(sourceUrl, settings, requireEligible: false, "Cloning from", cancellationToken);

        if (!IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Environment is required — pass a role URL (e.g. --prod <URL>), or run this interactively to pick one.");

        return await PickSourceAsync(cancellationToken);
    }

    async Task<EnvironmentInfo> ResolveGivenUrlAsync(string url, FlowlineSettings settings, bool requireEligible, string confirmLabel, CancellationToken cancellationToken)
    {
        // KTD5: switch-only — ProfileResolutionService errors naming `pac auth create` when no
        // profile matches, and never creates a profile or launches a login (R9/R13).
        var profile = await profileResolutionService.ResolveAsync(url, cancellationToken);

        var getEnvironmentInfo = GetEnvironmentInfoByUrlOverride
            ?? ((u, p, s, ct) => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(u, p, s, ct));
        var env = await console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{url}[/]...",
            _ => getEnvironmentInfo(url, profile, settings, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, "Environment not found — check the URL or your PAC login.");

        if (requireEligible)
            EnsureCreateEligible(env);

        console.Ok($"{confirmLabel}: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }

    async Task<List<EnvironmentInfo>> FetchEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var getEnvironments = GetEnvironmentsOverride ?? (ct => PacUtils.GetEnvironmentsAsync(capture, ct));
        var environments = await console.Status().FlowlineSpinner().StartAsync(
            "Checking your tenant's environments...",
            _ => getEnvironments(cancellationToken));

        if (environments.Count == 0)
            throw new FlowlineException(ExitCode.NotFound, "No environments found in your tenant — check your PAC login.");

        return environments;
    }

    async Task<EnvironmentInfo?> PickCreateTargetAsync(CancellationToken cancellationToken)
    {
        var environments = await FetchEnvironmentsAsync(cancellationToken);

        // Filter to create-eligible types (KTD4) rather than listing all and refusing on selection —
        // Spectre offers no non-selectable item, so an ineligible env is hidden, not grayed.
        var eligible = environments.Where(e => IsCreateEligibleEnvironmentType(e.Type)).ToList();

        // (Label, Env) tuple so the create-new escape hatch — which has no environment to hang off — sits
        // in the same SelectionPrompt (mirrors CloneCommand's solution picker).
        const string createNewLabel = "[italic]+ Create new environment for DEV role[/]";
        var choices = eligible
            .Select(e => (Label: $"{e.DisplayName} [dim]({e.Type})[/] — {e.EnvironmentUrl}", Env: (EnvironmentInfo?)e))
            .Append((Label: createNewLabel, Env: (EnvironmentInfo?)null))
            .ToList();

        var prompt = new SelectionPrompt<(string Label, EnvironmentInfo? Env)>()
            .Title(FlowlineConsoleExtensions.Question("Pick this project's DEV environment:"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);

        var selected = await console.PromptAsync(prompt, cancellationToken);

        if (selected.Env is null)
        {
            // Neutral next-step tone, not a red error: env creation is `provision`'s job (scope boundary),
            // and the caller exits 0. Covers both the has-prod (branch a dev) and no-prod (make a sandbox)
            // cases, since provision requires a prod to copy from.
            console.CannotContinue(
                "Can't create a DEV environment from here.",
                "Run 'flowline provision dev --prod <prod-url>' to create DEV - or create a environment in the Power Platform admin center, then re-run 'flowline init'.");
            return null;
        }

        await profileResolutionService.ResolveAsync(selected.Env.EnvironmentUrl!, cancellationToken);
        console.Ok($"Dev: [bold]{selected.Env.DisplayName}[/] ({selected.Env.EnvironmentUrl})");
        return selected.Env;
    }

    async Task<EnvironmentInfo> PickSourceAsync(CancellationToken cancellationToken)
    {
        var environments = await FetchEnvironmentsAsync(cancellationToken);

        // No role guard (R11/R17) — Production, Sandbox and Developer are all valid clone sources. Only
        // Default and Teams are dropped: Spectre has no non-selectable item, so hide rather than gray out.
        // Title frames the default model: the source of truth is usually PROD (see AGENTS.md).
        var selectable = environments.Where(e => IsSelectableSourceType(e.Type)).ToList();

        if (selectable.Count == 0)
            throw new FlowlineException(ExitCode.NotFound,
                "No environments to clone from — your tenant has only Default and Teams environments.");

        var prompt = new SelectionPrompt<EnvironmentInfo>()
            .Title(FlowlineConsoleExtensions.Question("Pick the environment to clone from (usually PROD — if it has unmanaged solutions):"))
            .UseConverter(e => $"{e.DisplayName} [dim]({e.Type ?? "unknown"})[/] — {e.EnvironmentUrl}")
            .AddChoices(selectable);

        var selected = await console.PromptAsync(prompt, cancellationToken);

        await profileResolutionService.ResolveAsync(selected.EnvironmentUrl!, cancellationToken);
        console.Ok($"Cloning from: [bold]{selected.DisplayName}[/] ({selected.EnvironmentUrl})");
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
