using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Services;

// KTD1: the one seam every --env-driven command (push, sync, generate, init, clone, provision)
// resolves its target through. Turns a role keyword or URL into a canonical environment URL, the role
// it belongs to, and whether resolving it wrote anything to .flowline (KD1/KD3). onlyRole set adds the
// R3 gate — a command restricted to one role refuses any other target before any PAC profile is
// resolved or .flowline is touched, so the caller passes the environment-info lookup in rather than
// this class resolving one itself (keeps the gate testable without a real pac subprocess). onlyRole
// null means any role is acceptable (clone, generate).
public sealed record EnvironmentTargetResult(string Url, EnvironmentRole? Role, bool Saved);

public class EnvironmentTargetResolver(IAnsiConsole console)
{
    public async Task<EnvironmentTargetResult> ResolveAsync(
        string? envOption,
        ProjectConfig config,
        EnvironmentRole? onlyRole,
        bool isInteractive,
        FlowlineSettings settings,
        Func<string, CancellationToken, Task<EnvironmentInfo?>> getEnvironmentInfoByUrl,
        CancellationToken cancellationToken)
    {
        var value = string.IsNullOrWhiteSpace(envOption)
            ? onlyRole?.ToString().ToLowerInvariant() ?? "dev"
            : envOption.Trim();

        // Role keyword (R5): resolves straight from .flowline, never touches Dataverse.
        if (TryParseRole(value, out var keywordRole))
        {
            var configuredUrl = config.GetUrl(keywordRole);
            if (string.IsNullOrWhiteSpace(configuredUrl))
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"{keywordRole.ConfigKey()} isn't set in .flowline — add it, or pass --env <url>.");

            if (onlyRole is { } required && keywordRole != required)
                throw RoleOnlyRefusal($"'{value}' targets {keywordRole.UpperLabel()}", required);

            return new EnvironmentTargetResult(configuredUrl, keywordRole, Saved: false);
        }

        // URL already saved under a role (KD1: addressed the same way every time) — compared
        // normalised (trailing slash, case) so a stray slash doesn't look like a brand-new environment.
        var matchedRole = MatchConfiguredUrl(value, config);
        if (matchedRole is not null)
        {
            if (onlyRole is { } required && matchedRole != required)
                throw RoleOnlyRefusal($"'{value}' is your {matchedRole.Value.UpperLabel()} environment", required);

            return new EnvironmentTargetResult(config.GetUrl(matchedRole.Value)!, matchedRole, Saved: false);
        }

        // Neither a keyword nor a URL — fail before spending a network round-trip on something like a
        // typo'd role keyword ('staging'), which Uri.TryCreate can't tell apart from garbage input.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{value}' isn't a role (dev, test, uat, prod) or a URL — pass --env <dev|test|uat|prod|url>.");

        // A URL Flowline hasn't seen — read its type through the profile-less lookup (KTD2: never
        // PacUtils.GetPartsFromEnvUrl, which exits the process on a regex miss) so the role gate below
        // runs before any profile is resolved or connection made.
        var envInfo = await getEnvironmentInfoByUrl(value, cancellationToken);
        var envType = envInfo?.Type;

        if (onlyRole == EnvironmentRole.Dev && string.Equals(envType, "Production", StringComparison.OrdinalIgnoreCase))
            throw RoleOnlyRefusal($"'{value}' is a Production environment", EnvironmentRole.Dev);

        // provision's mirror of the check above: a source that isn't Production-typed can't be copied
        // from as prod, whatever role it looks like it plays.
        if (onlyRole == EnvironmentRole.Prod && envType is not null && !string.Equals(envType, "Production", StringComparison.OrdinalIgnoreCase))
            throw RoleOnlyRefusal($"'{value}' is a {envType} environment", EnvironmentRole.Prod);

        var inference = EnvironmentRoleInference.Infer(value, envType);

        // R3 with the inferred role in view: a Sandbox whose name says -test or -uat is a TEST or UAT
        // environment as far as Flowline can tell, so a role-restricted command refuses it here rather
        // than saving it under TestUrl and using it against that role. An uninferred Sandbox falls
        // through to the ask-or-fail path below, which on a role-restricted command can only end in
        // that role or "use once".
        if (onlyRole is { } gateRole && inference.Role is { } inferredRole && ToEnvironmentRole(inferredRole) != gateRole)
            throw RoleOnlyRefusal($"'{value}' looks like a {ToEnvironmentRole(inferredRole).UpperLabel()} environment ({inference.Source})", gateRole);

        return isInteractive
            ? await ResolveNewUrlInteractivelyAsync(value, inference, onlyRole, config, settings, cancellationToken)
            : ResolveNewUrlNonInteractively(value, inference, onlyRole, config, settings);
    }

    // R7: non-interactive save, or R8's failure when nothing could be inferred.
    EnvironmentTargetResult ResolveNewUrlNonInteractively(string url, RoleInferenceResult inference, EnvironmentRole? onlyRole, ProjectConfig config, FlowlineSettings settings)
    {
        if (inference.Role is not { } inferred)
        {
            var hint = onlyRole is { } required
                ? $"""add "{required.ConfigKey()}": "{url}" to .flowline."""
                : $"""add "DevUrl": "{url}" to .flowline, or "TestUrl" or "UatUrl" if that is what it is.""";
            throw new FlowlineException(ExitCode.ValidationFailed, $"Can't tell which role '{url}' belongs to — {hint}");
        }

        return SaveRole(ToEnvironmentRole(inferred), url, inference.Source, config, settings);
    }

    // R6: interactive save — role picker pre-selected on the inferred role (listed first, so a bare
    // Enter accepts it, mirroring CloneCommand.ResolveRoleAsync), with no pre-selection when nothing
    // was inferred (R8) and an escape hatch to use the URL once without saving. The offered roles stay
    // consistent with the gate that already ran: a role-restricted command offers only that role
    // (picking another here would silently defeat the R3 gate), and PROD is only ever offered
    // unrestricted when it's the inferred role — mirrors CloneCommand.ResolveRoleAsync, which never
    // lets a Sandbox/unknown-typed env land on Prod.
    async Task<EnvironmentTargetResult> ResolveNewUrlInteractivelyAsync(
        string url, RoleInferenceResult inference, EnvironmentRole? onlyRole, ProjectConfig config, FlowlineSettings settings, CancellationToken cancellationToken)
    {
        var inferredRole = inference.Role is { } r ? ToEnvironmentRole(r) : (EnvironmentRole?)null;

        EnvironmentRole[] roles = onlyRole is { } required
            ? [required]
            : inferredRole == EnvironmentRole.Prod
                ? [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod]
                : [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat];
        var ordered = inferredRole is { } ir ? roles.OrderByDescending(r => r == ir) : roles.AsEnumerable();

        const string useOnceLabel = "Use this URL once — don't save it";
        var choices = ordered
            .Select(r => (Label: r.UpperLabel(), Role: (EnvironmentRole?)r))
            .Append((Label: useOnceLabel, Role: (EnvironmentRole?)null))
            .ToList();

        var prompt = new SelectionPrompt<(string Label, EnvironmentRole? Role)>()
            .Title(FlowlineConsoleExtensions.Question($"Save [bold]{url}[/] under which .flowline role?"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);

        var (_, selectedRole) = await console.PromptAsync(prompt, cancellationToken);

        if (selectedRole is not { } role)
        {
            console.Skip($"Using {url} once — not saved to .flowline");
            return new EnvironmentTargetResult(url, null, Saved: false);
        }

        // Only the accepted inferred role carries the "(inferred from ...)" reason — a role the user
        // picked themselves wasn't inferred, even if it happens to match.
        var saveReason = role == inferredRole ? inference.Source : null;
        return SaveRole(role, url, saveReason, config, settings);
    }

    EnvironmentTargetResult SaveRole(EnvironmentRole role, string url, string? saveReason, ProjectConfig config, FlowlineSettings settings)
    {
        var before = config.GetUrl(role);
        var after = config.GetOrUpdateUrl(role, url, settings, saveReason)!;

        // The overwrite prompt was declined: the stored URL stays, but the user named this one, so this
        // run targets it once rather than silently retargeting onto the environment they just refused to
        // replace.
        if (!string.Equals(NormalizeForCompare(after), NormalizeForCompare(url), StringComparison.Ordinal))
        {
            console.Skip($"Using {url} once — not saved to .flowline");
            return new EnvironmentTargetResult(url, role, Saved: false);
        }

        var saved = !string.Equals(NormalizeForCompare(before), NormalizeForCompare(after), StringComparison.Ordinal);
        return new EnvironmentTargetResult(after, role, saved);
    }

    static EnvironmentRole? MatchConfiguredUrl(string url, ProjectConfig config)
    {
        var normalized = NormalizeForCompare(url);
        foreach (var role in EnvironmentRoles.All)
        {
            var configured = config.GetUrl(role);
            if (!string.IsNullOrWhiteSpace(configured) && NormalizeForCompare(configured) == normalized)
                return role;
        }
        return null;
    }

    // Reused by standalone push/generate (no ProjectConfig to resolve a keyword against) to refuse a bare
    // role keyword before it's silently treated as a literal URL.
    internal static bool IsRoleKeyword(string value) => TryParseRole(value, out _);

    // Standalone push/generate call ResolveStandaloneEnvironmentUrl directly (no ProjectConfig, so this
    // class's own ResolveAsync doesn't apply) — that method only checks for a blank value, so a bare
    // keyword like "dev" would otherwise flow through as if it were a literal URL. One shared guard for
    // both callers, called before ResolveStandaloneEnvironmentUrl.
    internal static void EnsureUsableStandaloneEnv(string? env)
    {
        if (!string.IsNullOrWhiteSpace(env) && IsRoleKeyword(env))
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Standalone mode has no .flowline to resolve a role against — pass --env <url>.");
    }

    static bool TryParseRole(string value, out EnvironmentRole role)
    {
        var parsed = EnvironmentRoles.TryParse(value);
        role = parsed ?? default;
        return parsed is not null;
    }

    static EnvironmentRole ToEnvironmentRole(InferredRole role) => role switch
    {
        InferredRole.Dev => EnvironmentRole.Dev,
        InferredRole.Test => EnvironmentRole.Test,
        InferredRole.Uat => EnvironmentRole.Uat,
        InferredRole.Prod => EnvironmentRole.Prod,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    static string NormalizeForCompare(string? url) => (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    static FlowlineException RoleOnlyRefusal(string detail, EnvironmentRole role) =>
        new(ExitCode.ValidationFailed,
            $"{detail} — this command only accepts {role.UpperLabel()}. Use --env {role.ToString().ToLowerInvariant()} or a {role.UpperLabel()} environment URL.");
}
