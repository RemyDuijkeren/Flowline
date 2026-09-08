namespace Flowline.Commands;

// The four named places a Flowline project addresses (CONCEPTS.md "Environment role"). Its own file
// so it's reachable from Config and Services without pulling in FlowlineCommand's command-pipeline
// machinery — the enum has no dependency on any of it.
public enum EnvironmentRole { Prod, Uat, Test, Dev }

public static class EnvironmentRoles
{
    // Dev-first: the order pickers list roles in and the order configured URLs are scanned.
    public static readonly EnvironmentRole[] All = [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod];

    /// <summary>The .flowline key that holds this role's URL.</summary>
    public static string ConfigKey(this EnvironmentRole role) => role switch
    {
        EnvironmentRole.Prod => "ProdUrl",
        EnvironmentRole.Uat  => "UatUrl",
        EnvironmentRole.Test => "TestUrl",
        EnvironmentRole.Dev  => "DevUrl",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    /// <summary>The upper-case label status lines use ("DEV set to ...").</summary>
    public static string UpperLabel(this EnvironmentRole role) => role switch
    {
        EnvironmentRole.Prod => "PROD",
        EnvironmentRole.Uat  => "UAT",
        EnvironmentRole.Test => "TEST",
        EnvironmentRole.Dev  => "DEV",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    /// <summary>Case-insensitive dev/test/uat/prod keyword to role, or null when it isn't one. The one
    /// mapping DriftCommand.TryResolveRole and EnvironmentTargetResolver.TryParseRole both delegate to.</summary>
    public static EnvironmentRole? TryParse(string value) => value.ToLowerInvariant() switch
    {
        "prod" => EnvironmentRole.Prod,
        "uat"  => EnvironmentRole.Uat,
        "test" => EnvironmentRole.Test,
        "dev"  => EnvironmentRole.Dev,
        _      => null
    };
}
