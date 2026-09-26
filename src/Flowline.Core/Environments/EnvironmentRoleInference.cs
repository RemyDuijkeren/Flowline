using Flowline.Core.Environments;

namespace Flowline.Core.Environments;

// CONCEPTS.md "Role inference": the role a URL not yet in .flowline gets assigned, so it can be saved
// under a key without the user naming one. Null Role means nothing could be inferred.
public readonly record struct RoleInferenceResult(EnvironmentRole? Role, string? Source)
{
    public static readonly RoleInferenceResult None = new(null, null);
}

// Pure — no I/O, no exceptions, so callers never need a try/catch around a URL they don't control yet.
public static class EnvironmentRoleInference
{
    // Naming conventions seen on Dataverse orgs, keyed by the role they mean. PROD is deliberately
    // absent: Production is a type Dataverse reports, never a name someone chose, so it comes from the
    // type step alone. Left out on purpose: sandbox/sbx (a type, not a role), int (integration or
    // internal), demo, train, hotfix (real orgs with no role in the model).
    static readonly Dictionary<string, EnvironmentRole> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dev"] = EnvironmentRole.Dev, ["develop"] = EnvironmentRole.Dev, ["development"] = EnvironmentRole.Dev,
        ["test"] = EnvironmentRole.Test, ["tst"] = EnvironmentRole.Test, ["testing"] = EnvironmentRole.Test,
        ["qa"] = EnvironmentRole.Test, ["sit"] = EnvironmentRole.Test,
        ["uat"] = EnvironmentRole.Uat, ["acc"] = EnvironmentRole.Uat, ["acceptance"] = EnvironmentRole.Uat,
        ["acceptatie"] = EnvironmentRole.Uat, ["preprod"] = EnvironmentRole.Uat,
        ["staging"] = EnvironmentRole.Uat, ["stage"] = EnvironmentRole.Uat, ["stg"] = EnvironmentRole.Uat,
    };

    static readonly char[] DisplayNameSeparators = [' ', '-', '_', '(', ')', '[', ']', '/', ',', '.'];

    // Order (R8): a Production type is a Dataverse fact and beats any name; then a keyword in the URL's
    // host label; then a keyword in the display name; then a Developer type. A Sandbox (or unknown/null
    // type) with no keyword hit stays uninferred — Dataverse reports a test or UAT sandbox with the same
    // type as a DEV one, so type alone can't tell them apart.
    public static RoleInferenceResult Infer(string? url, string? environmentType, string? displayName = null)
    {
        if (string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return new RoleInferenceResult(EnvironmentRole.Prod, "environment type");

        if (InferFromTokens(HostLabelTokens(url)) is { } urlRole)
            return new RoleInferenceResult(urlRole, "URL name");

        if (InferFromTokens(displayName?.Split(DisplayNameSeparators, StringSplitOptions.RemoveEmptyEntries)) is { } nameRole)
            return new RoleInferenceResult(nameRole, "environment name");

        if (string.Equals(environmentType, "Developer", StringComparison.OrdinalIgnoreCase))
            return new RoleInferenceResult(EnvironmentRole.Dev, "environment type");

        return RoleInferenceResult.None;
    }

    static string[]? HostLabelTokens(string? url)
    {
        // Uri.TryCreate never throws — a URL the parser can't read just falls through to the next step.
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Dataverse hosts allow only letters, digits and hyphens, so the hyphen is the one separator:
        // "contoso-dev" in contoso-dev.crm4.dynamics.com.
        return uri.Host.Split('.')[0].Split('-', StringSplitOptions.RemoveEmptyEntries);
    }

    // Whole-token match so devon-crm and testify never hit; trailing digits stripped so dev2 and test01
    // read as their role; the last matching token wins, matching the suffix provision writes.
    static EnvironmentRole? InferFromTokens(string[]? tokens)
    {
        if (tokens is null) return null;

        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            var token = tokens[i].TrimEnd("0123456789".ToCharArray());
            if (token.Length > 0 && Keywords.TryGetValue(token, out var role))
                return role;
        }
        return null;
    }
}
