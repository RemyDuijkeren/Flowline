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
/// It holds no project state. The caller passes the config and whether it runs stand-alone, so one
/// instance serves every command. The rules it applies (what a target is, which type a role may point at)
/// live in <see cref="EnvironmentTarget"/>.
/// </remarks>
public sealed class EnvironmentService(
    IAnsiConsole console,
    ProfileResolutionService profiles,
    FlowlineValidator validator,
    FlowlineRuntimeOptions options)
{
    /// <summary>
    /// Reads a role out of a positional target, or fails before anything resolves it.
    /// </summary>
    /// <remarks>
    /// A role has nothing to resolve against outside a project. Anything that is neither a role nor an
    /// http(s) URL fails with <paramref name="notATarget"/>'s message, which carries the caller's fix line.
    /// </remarks>
    public EnvironmentRole? CheckTarget(string target, bool standalone, Func<string> notATarget)
    {
        var role = EnvironmentRoles.TryParse(target);

        if (standalone && role is not null)
            throw new FlowlineException(ExitCode.ConfigInvalid, EnvironmentTarget.StandaloneRole(target));

        if (role is null && !EnvironmentTarget.IsUrl(target))
            throw new FlowlineException(ExitCode.ValidationFailed, notATarget());

        return role;
    }

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
            $"Checking {(role is { } r ? r.Label().ToLowerInvariant() : "environment")} [bold]{Markup.Escape(url)}[/]...",
            _ => validator.GetEnvironmentInfoByUrlAsync(url, profile, options, options.NoCache, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"{(role is { } nf ? $"{nf.Label()} environment" : "Environment")} not found at {url}. Check the URL or your PAC login.");

        switch (skipTypeGuard ? EnvironmentTypeMismatch.None : EnvironmentTarget.CheckType(role, env.Type, devOnly))
        {
            case EnvironmentTypeMismatch.NotProduction:
                throw new FlowlineException(ExitCode.ValidationFailed, "That environment isn't Production type — verify the URL in .flowline points to a Production environment.");
            case EnvironmentTypeMismatch.Production:
                throw new FlowlineException(ExitCode.ValidationFailed, "That's a Production environment — use a sandbox or dev instead.");
        }

        console.Ok($"{(role is { } ok ? $"{ok.Label()} env" : "Env")} [bold]{Markup.Escape(env.DisplayName ?? url)}[/] ({Markup.Escape(env.EnvironmentUrl ?? url)}) exists");
        return (env, profile);
    }
}
