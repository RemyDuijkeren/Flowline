using Flowline.Core.Config;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Validation;
using Spectre.Console;

namespace Flowline.Core.Environments;

/// <summary>
/// Checks and looks up the environment a command is pointed at: the one place every command does this.
/// </summary>
/// <remarks>
/// It holds no project state. The caller passes the config, so one instance serves every command. The
/// rules it applies (what a target is, which type a role may point at) live in <see cref="EnvironmentTarget"/>,
/// and so does the check a positional target gets before any lookup.
/// </remarks>
public sealed class EnvironmentService(
    IAnsiConsole console,
    ProfileResolutionService profiles,
    FlowlineValidator validator,
    FlowlineRuntimeOptions options)
{
    /// <summary>Looks up a role through its saved URL, or a URL as given.</summary>
    public Task<(EnvironmentInfo Info, PacProfile Profile)> ResolveAsync(
        ProjectConfig config, string target, EnvironmentRole? role, CancellationToken cancellationToken) =>
        role is { } r
            ? CheckByRoleAsync(config, r, cancellationToken)
            : CheckByUrlAsync(target, null, cancellationToken);

    /// <summary>Looks up the environment saved under <paramref name="role"/>, with the type guard.</summary>
    public Task<(EnvironmentInfo Info, PacProfile Profile)> CheckByRoleAsync(
        ProjectConfig config, EnvironmentRole role, CancellationToken cancellationToken) =>
        CheckByUrlAsync(EnvironmentTarget.RequireRoleUrl(config, role), role, cancellationToken);

    /// <summary>
    /// Confirms the environment at <paramref name="url"/> exists and, given a role, that its type fits it.
    /// </summary>
    /// <remarks>
    /// <paramref name="role"/> is null for a URL that belongs to no role; there is then nothing to check the
    /// type against unless the command is DEV-only (<paramref name="devOnly"/>). generate passes
    /// <paramref name="skipTypeGuard"/> because it may read from Production.
    /// </remarks>
    public async Task<(EnvironmentInfo Info, PacProfile Profile)> CheckByUrlAsync(
        string url, EnvironmentRole? role, CancellationToken cancellationToken, PacProfile? resolvedProfile = null,
        bool skipTypeGuard = false, bool devOnly = false)
    {
        // Resolved before opening the status spinner: the profile lookup can prompt on an ambiguous match,
        // and Spectre.Console throws if a prompt runs while a Status display is active.
        var profile = resolvedProfile ?? await profiles.ResolveAsync(url, cancellationToken);
        var env = await console.Status().FlowlineSpinner().StartAsync(
            $"Checking {(role is { } r ? r.Keyword() : "environment")} [bold]{Markup.Escape(url)}[/]...",
            _ => validator.GetEnvironmentInfoByUrlAsync(url, profile, options, options.NoCache, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"{(role is { } nf ? $"{nf.Label()} environment" : "Environment")} not found at {url}. Check the URL or your PAC login.");

        // A role names the .flowline key that holds this URL, so the refusal can say which one to fix. A
        // DEV-only command handed a URL that belongs to no role has no key to name.
        switch (skipTypeGuard ? EnvironmentTypeMismatch.None : EnvironmentTarget.CheckType(role, env.Type, devOnly))
        {
            case EnvironmentTypeMismatch.NotProduction:
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"{EnvironmentRole.Prod.ConfigKey()} in .flowline points to a {env.Type} environment, but PROD must be a Production environment. Point it at your Production environment.");
            case EnvironmentTypeMismatch.Production when role is { } typed:
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"{typed.ConfigKey()} in .flowline points to a Production environment, but {typed.UpperLabel()} must not. Point it at a sandbox.");
            case EnvironmentTypeMismatch.Production:
                throw new FlowlineException(ExitCode.ValidationFailed, "That's a Production environment. Pass --env with a sandbox or dev environment's URL instead.");
        }

        console.Ok($"{(role is { } ok ? $"{ok.Label()} env" : "Env")} [bold]{Markup.Escape(env.DisplayName ?? url)}[/] ({Markup.Escape(env.EnvironmentUrl ?? url)}) exists");
        return (env, profile);
    }
}
