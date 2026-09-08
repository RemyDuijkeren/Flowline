namespace Flowline.Core.Services;

// Mirrors Flowline.Commands.EnvironmentRole (Prod/Uat/Test/Dev) without referencing it — Core must
// never reference Flowline (AGENTS.md project boundary rule). EnvironmentTargetResolver maps this to
// the real EnvironmentRole.
public enum InferredRole { Dev, Test, Uat, Prod }

// CONCEPTS.md "Role inference": the role a URL not yet in .flowline gets assigned, so it can be saved
// under a key without the user naming one. Null Role means nothing could be inferred.
public readonly record struct RoleInferenceResult(InferredRole? Role, string? Source)
{
    public static readonly RoleInferenceResult None = new(null, null);
}

// Pure — no I/O, no exceptions, so callers never need a try/catch around a URL they don't control yet.
public static class EnvironmentRoleInference
{
    // First hit wins (R8): the host suffix before the .crm segment, then the environment type. A
    // Sandbox (or unknown/null type) with no suffix hit stays uninferred — Dataverse reports a test or
    // UAT sandbox with the same type as a DEV one, so type alone can't tell them apart.
    public static RoleInferenceResult Infer(string? url, string? environmentType)
    {
        if (InferFromHostSuffix(url) is { } suffixRole)
            return new RoleInferenceResult(suffixRole, "URL suffix");

        if (string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return new RoleInferenceResult(InferredRole.Prod, "environment type");

        if (string.Equals(environmentType, "Developer", StringComparison.OrdinalIgnoreCase))
            return new RoleInferenceResult(InferredRole.Dev, "environment type");

        return RoleInferenceResult.None;
    }

    static InferredRole? InferFromHostSuffix(string? url)
    {
        // Uri.TryCreate never throws — a URL the parser can't read just falls through to the type step.
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var hostLabel = uri.Host.Split('.')[0]; // e.g. "contoso-dev" in contoso-dev.crm4.dynamics.com

        if (hostLabel.EndsWith("-dev", StringComparison.OrdinalIgnoreCase)) return InferredRole.Dev;
        if (hostLabel.EndsWith("-test", StringComparison.OrdinalIgnoreCase)) return InferredRole.Test;
        if (hostLabel.EndsWith("-uat", StringComparison.OrdinalIgnoreCase)) return InferredRole.Uat;
        return null;
    }
}
