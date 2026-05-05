namespace Flowline.Validation;

public sealed class ValidationCache
{
    public int SchemaVersion { get; set; } = 1;
    public string? FlowlineVersion { get; set; }
    public Dictionary<string, ValidationCacheEntry<ToolCheckResult>> ToolChecks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ValidationCacheEntry<GitRepoCheckResult>> GitRepos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ValidationCacheEntry<EnvironmentInfo>> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ValidationCacheEntry<SolutionInfo>> Solutions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ValidationCacheEntry<T>
{
    public DateTimeOffset CheckedAtUtc { get; set; }
    public T Value { get; set; } = default!;
}

public sealed class ToolCheckResult
{
    public string Version { get; set; } = "";
    public string? InstallType { get; set; }
}

public sealed class GitRepoCheckResult
{
    public string RootFolder { get; set; } = "";
}
