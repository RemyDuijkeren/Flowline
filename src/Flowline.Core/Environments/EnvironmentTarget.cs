using Flowline.Core.Config;

namespace Flowline.Core.Environments;

/// <summary>
/// The one definition of what a command may be pointed at, and the messages for each way that goes wrong.
/// </summary>
/// <remarks>
/// Every command that takes an environment as a word (<c>--env</c>, <c>deploy</c>, <c>drift</c>, the
/// <c>settings</c> operations) used to write its own check, and the copies drifted: one command passed any
/// non-role word to the PAC profile lookup, which answered a typo with advice to authenticate against it.
/// Each caller adds its own fix line to the shared sentence, because what to type next differs per command.
/// </remarks>
public static class EnvironmentTarget
{
    static readonly string s_roleList = string.Join(", ", EnvironmentRoles.All.Select(r => r.Keyword()));

    /// <summary>The placeholder a fix line shows for a target: <c>&lt;dev|test|uat|prod|url&gt;</c>.</summary>
    public static readonly string TargetPlaceholder =
        $"<{string.Join('|', EnvironmentRoles.All.Select(r => r.Keyword()))}|url>";

    /// <summary>An absolute http or https URL. A bare host is not one: pass the full URL.</summary>
    public static bool IsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A role keyword or an absolute http(s) URL: the kind of thing a target accepts.</summary>
    public static bool IsTarget(string value) => EnvironmentRoles.TryParse(value) is not null || IsUrl(value);

    /// <summary>The message for a target that is neither a role nor a URL, ending with the caller's fix.</summary>
    public static string NotATarget(string value, string fix) =>
        $"'{value}' isn't a role ({s_roleList}) or a URL. {fix}";

    /// <summary>The message for a role keyword used where there is no <c>.flowline</c> to resolve it from.</summary>
    public static string StandaloneRole(string target) =>
        $"'{target}' is a role name, and roles resolve from .flowline, but there's no Flowline project here. " +
        "Pass the environment URL instead.";

    /// <summary>
    /// The URL saved under <paramref name="role"/>, or a failure that names the <c>.flowline</c> key to fix.
    /// </summary>
    /// <remarks>
    /// A value that isn't a URL used to travel on as one and fail at the PAC profile lookup, so it is
    /// checked here, once, for every command that resolves a role. <paramref name="orElse"/> is the
    /// caller's other way out when nothing is saved, such as <c>pass --env &lt;url&gt;</c>.
    /// </remarks>
    public static string RequireRoleUrl(ProjectConfig config, EnvironmentRole role, string? orElse = null)
    {
        var key = role.ConfigKey();
        var url = config.GetUrl(role);

        if (string.IsNullOrWhiteSpace(url))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"{key} isn't set in .flowline. Add \"{key}\": \"<url>\" to .flowline" +
                (orElse is null ? "." : $", or {orElse}."));

        if (!IsUrl(url))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"{key} in .flowline is '{url}', which isn't an http(s) URL. Set it to the environment's full URL.");

        return url;
    }
}
