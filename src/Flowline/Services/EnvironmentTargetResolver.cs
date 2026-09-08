using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Services;

// KTD1: the one seam every --env-driven command (push, sync, generate, init, clone) resolves its
// target through. Turns a role keyword or URL into a canonical environment URL, the role it belongs
// to, and whether resolving it wrote anything to .flowline (KD1/KD3). devOnly:true adds the R3 gate —
// a DEV-only command refuses a non-DEV target before any PAC profile is resolved or .flowline is
// touched, so the caller passes the environment-info lookup in rather than this class resolving one
// itself (keeps the gate testable without a real pac subprocess).
public sealed record EnvironmentTargetResult(string Url, EnvironmentRole? Role, bool Saved);

public class EnvironmentTargetResolver(IAnsiConsole console)
{
    public async Task<EnvironmentTargetResult> ResolveAsync(
        string? envOption,
        ProjectConfig config,
        bool devOnly,
        bool isInteractive,
        FlowlineSettings settings,
        Func<string, CancellationToken, Task<EnvironmentInfo?>> getEnvironmentInfoByUrl,
        CancellationToken cancellationToken)
    {
        var value = string.IsNullOrWhiteSpace(envOption) ? "dev" : envOption.Trim();

        // Role keyword (R5): resolves straight from .flowline, never touches Dataverse.
        if (TryParseRole(value, out var keywordRole))
        {
            var configuredUrl = config.GetUrl(keywordRole);
            if (string.IsNullOrWhiteSpace(configuredUrl))
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"{RoleKey(keywordRole)} isn't set in .flowline — add it, or pass --env <url>.");

            if (devOnly && keywordRole != EnvironmentRole.Dev)
                throw DevOnlyRefusal($"'{value}' targets {RoleLabel(keywordRole)}");

            return new EnvironmentTargetResult(configuredUrl, keywordRole, Saved: false);
        }

        // URL already saved under a role (KD1: addressed the same way every time) — compared
        // normalised (trailing slash, case) so a stray slash doesn't look like a brand-new environment.
        var matchedRole = MatchConfiguredUrl(value, config);
        if (matchedRole is not null)
        {
            if (devOnly && matchedRole != EnvironmentRole.Dev)
                throw DevOnlyRefusal($"'{value}' is your {RoleLabel(matchedRole.Value)} environment");

            return new EnvironmentTargetResult(config.GetUrl(matchedRole.Value)!, matchedRole, Saved: false);
        }

        // Neither a keyword nor a URL — fail before spending a network round-trip on something like a
        // typo'd role keyword ('staging'), which Uri.TryCreate can't tell apart from garbage input.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{value}' isn't a role (dev, test, uat, prod) or a URL — pass --env <dev|test|uat|prod|url>.");

        // A URL Flowline hasn't seen — read its type through the profile-less lookup (KTD2: never
        // PacUtils.GetPartsFromEnvUrl, which exits the process on a regex miss) so the DEV-only gate
        // below runs before any profile is resolved or connection made.
        var envInfo = await getEnvironmentInfoByUrl(value, cancellationToken);
        var envType = envInfo?.Type;

        if (devOnly && string.Equals(envType, "Production", StringComparison.OrdinalIgnoreCase))
            throw DevOnlyRefusal($"'{value}' is a Production environment");

        var inference = EnvironmentRoleInference.Infer(value, envType);

        return isInteractive
            ? await ResolveNewUrlInteractivelyAsync(value, inference, devOnly, config, settings, cancellationToken)
            : ResolveNewUrlNonInteractively(value, inference, config, settings);
    }

    // R7: non-interactive save, or R8's failure when nothing could be inferred.
    static EnvironmentTargetResult ResolveNewUrlNonInteractively(string url, RoleInferenceResult inference, ProjectConfig config, FlowlineSettings settings)
    {
        if (inference.Role is not { } inferred)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"""Can't tell which role '{url}' belongs to — add "DevUrl": "{url}" to .flowline, or "TestUrl" or "UatUrl" if that is what it is.""");

        return SaveRole(ToEnvironmentRole(inferred), url, inference.Source, config, settings);
    }

    // R6: interactive save — role picker pre-selected on the inferred role (listed first, so a bare
    // Enter accepts it, mirroring CloneCommand.ResolveRoleAsync), with no pre-selection when nothing
    // was inferred (R8) and an escape hatch to use the URL once without saving. The offered roles stay
    // consistent with the gate that already ran: a DEV-only command offers only DEV (picking PROD here
    // would silently defeat the R3 gate), and PROD is only ever offered when it's the inferred role —
    // mirrors CloneCommand.ResolveRoleAsync, which never lets a Sandbox/unknown-typed env land on Prod.
    async Task<EnvironmentTargetResult> ResolveNewUrlInteractivelyAsync(
        string url, RoleInferenceResult inference, bool devOnly, ProjectConfig config, FlowlineSettings settings, CancellationToken cancellationToken)
    {
        var inferredRole = inference.Role is { } r ? ToEnvironmentRole(r) : (EnvironmentRole?)null;

        EnvironmentRole[] roles = devOnly
            ? [EnvironmentRole.Dev]
            : inferredRole == EnvironmentRole.Prod
                ? [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod]
                : [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat];
        var ordered = inferredRole is { } ir ? roles.OrderByDescending(r => r == ir) : roles.AsEnumerable();

        const string useOnceLabel = "Use this URL once — don't save it";
        var choices = ordered
            .Select(r => (Label: RoleLabel(r), Role: (EnvironmentRole?)r))
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

    static EnvironmentTargetResult SaveRole(EnvironmentRole role, string url, string? saveReason, ProjectConfig config, FlowlineSettings settings)
    {
        var before = config.GetUrl(role);
        var after = config.GetOrUpdateUrl(role, url, settings, saveReason)!;
        var saved = !string.Equals(NormalizeForCompare(before), NormalizeForCompare(after), StringComparison.Ordinal);
        return new EnvironmentTargetResult(after, role, saved);
    }

    static EnvironmentRole? MatchConfiguredUrl(string url, ProjectConfig config)
    {
        var normalized = NormalizeForCompare(url);
        foreach (var role in new[] { EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod })
        {
            var configured = config.GetUrl(role);
            if (!string.IsNullOrWhiteSpace(configured) && NormalizeForCompare(configured) == normalized)
                return role;
        }
        return null;
    }

    static bool TryParseRole(string value, out EnvironmentRole role)
    {
        switch (value.ToLowerInvariant())
        {
            case "dev": role = EnvironmentRole.Dev; return true;
            case "test": role = EnvironmentRole.Test; return true;
            case "uat": role = EnvironmentRole.Uat; return true;
            case "prod": role = EnvironmentRole.Prod; return true;
            default: role = default; return false;
        }
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

    static string RoleLabel(EnvironmentRole role) => role switch
    {
        EnvironmentRole.Dev => "DEV",
        EnvironmentRole.Test => "TEST",
        EnvironmentRole.Uat => "UAT",
        EnvironmentRole.Prod => "PROD",
        _ => role.ToString()
    };

    static string RoleKey(EnvironmentRole role) => role switch
    {
        EnvironmentRole.Dev => "DevUrl",
        EnvironmentRole.Test => "TestUrl",
        EnvironmentRole.Uat => "UatUrl",
        EnvironmentRole.Prod => "ProdUrl",
        _ => role.ToString()
    };

    static FlowlineException DevOnlyRefusal(string detail) =>
        new(ExitCode.ValidationFailed, $"{detail} — this command only runs against DEV. Use --env dev or a DEV environment URL.");
}
