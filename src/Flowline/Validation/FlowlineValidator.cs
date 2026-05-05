using System.Reflection;
using Spectre.Console;

namespace Flowline.Validation;

public sealed class FlowlineValidator
{
    static readonly TimeSpan ToolTtl = TimeSpan.FromDays(7);
    static readonly TimeSpan GitRepoTtl = TimeSpan.FromDays(1);
    static readonly TimeSpan EnvironmentTtl = TimeSpan.FromHours(1);
    static readonly TimeSpan SolutionTtl = TimeSpan.FromHours(1);

    readonly ValidationCacheStore _store;
    readonly ValidationProbes _probes;

    public static FlowlineValidator Default { get; } = new(new ValidationCacheStore(), new ValidationProbes());

    public FlowlineValidator(ValidationCacheStore store, ValidationProbes probes)
    {
        _store = store;
        _probes = probes;
    }

    public async Task<ToolCheckResult> EnsureDotNetAsync(FlowlineSettings settings, CancellationToken cancellationToken)
    {
        return await GetOrRunToolCheckAsync(
            "dotnet",
            ToolTtl,
            settings,
            cancellationToken,
            async () => new ToolCheckResult { Version = await _probes.CheckDotNetAsync(settings.Verbose, cancellationToken) });
    }

    public async Task<ToolCheckResult> EnsurePacCliAsync(FlowlineSettings settings, CancellationToken cancellationToken)
    {
        return await GetOrRunToolCheckAsync(
            "pac",
            ToolTtl,
            settings,
            cancellationToken,
            async () =>
            {
                var (version, installType) = await _probes.CheckPacAsync(settings.Verbose, cancellationToken);
                return new ToolCheckResult { Version = version, InstallType = installType };
            });
    }

    public async Task<ToolCheckResult> EnsureGitAsync(FlowlineSettings settings, CancellationToken cancellationToken)
    {
        return await GetOrRunToolCheckAsync(
            "git",
            ToolTtl,
            settings,
            cancellationToken,
            async () => new ToolCheckResult { Version = await _probes.CheckGitAsync(settings.Verbose, cancellationToken) });
    }

    public async Task EnsureGitRepoAsync(string rootFolder, FlowlineSettings settings, CancellationToken cancellationToken)
    {
        var key = NormalizePath(rootFolder);
        var cache = _store.Load();

        if (!settings.NoCache &&
            cache.GitRepos.TryGetValue(key, out var cached) &&
            IsFresh(cached.CheckedAtUtc, GitRepoTtl))
        {
            if (settings.Verbose) AnsiConsole.MarkupLine("[dim]Using cached Git repo check[/]");
            return;
        }

        await _probes.CheckGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);
        cache = _store.Load();
        cache.GitRepos[key] = NewEntry(new GitRepoCheckResult { RootFolder = key });
        cache.FlowlineVersion = GetFlowlineVersion();
        _store.Save(cache);
    }

    public async Task<EnvironmentInfo?> GetEnvironmentInfoByUrlAsync(
        string environmentUrl,
        FlowlineSettings settings,
        CancellationToken cancellationToken)
    {
        var key = NormalizeEnvironmentUrl(environmentUrl);
        var cache = _store.Load();

        if (!settings.NoCache &&
            cache.Environments.TryGetValue(key, out var cached) &&
            IsFresh(cached.CheckedAtUtc, EnvironmentTtl))
        {
            if (settings.Verbose) AnsiConsole.MarkupLine("[dim]Using cached environment check[/]");
            return cached.Value;
        }

        var env = await _probes.GetEnvironmentAsync(environmentUrl, settings.Verbose, cancellationToken);
        if (env != null)
        {
            cache = _store.Load();
            cache.Environments[key] = NewEntry(env);
            cache.FlowlineVersion = GetFlowlineVersion();
            _store.Save(cache);
        }

        return env;
    }

    public async Task<SolutionInfo?> GetSolutionInfoAsync(
        string environmentUrl,
        string solutionName,
        bool includeManaged,
        FlowlineSettings settings,
        CancellationToken cancellationToken)
    {
        var key = SolutionKey(environmentUrl, solutionName, includeManaged);
        var cache = _store.Load();

        if (!settings.NoCache &&
            cache.Solutions.TryGetValue(key, out var cached) &&
            IsFresh(cached.CheckedAtUtc, SolutionTtl))
        {
            if (settings.Verbose) AnsiConsole.MarkupLine("[dim]Using cached solution check[/]");
            return cached.Value;
        }

        var solutions = await _probes.GetSolutionsAsync(environmentUrl, settings.Verbose, cancellationToken);
        var solution = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(solutionName, StringComparison.OrdinalIgnoreCase) == true);
        if (solution != null)
        {
            cache = _store.Load();
            cache.Solutions[key] = NewEntry(solution);
            cache.FlowlineVersion = GetFlowlineVersion();
            _store.Save(cache);
        }

        return solution;
    }

    async Task<ToolCheckResult> GetOrRunToolCheckAsync(
        string key,
        TimeSpan ttl,
        FlowlineSettings settings,
        CancellationToken cancellationToken,
        Func<Task<ToolCheckResult>> checkAsync)
    {
        var cache = _store.Load();
        if (!settings.NoCache &&
            cache.ToolChecks.TryGetValue(key, out var cached) &&
            IsFresh(cached.CheckedAtUtc, ttl))
        {
            if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Using cached {key} check[/]");
            return cached.Value;
        }

        var result = await checkAsync();
        cache = _store.Load();
        cache.ToolChecks[key] = NewEntry(result);
        cache.FlowlineVersion = GetFlowlineVersion();
        _store.Save(cache);
        return result;
    }

    static ValidationCacheEntry<T> NewEntry<T>(T value) => new()
    {
        CheckedAtUtc = DateTimeOffset.UtcNow,
        Value = value
    };

    static bool IsFresh(DateTimeOffset checkedAtUtc, TimeSpan ttl) =>
        DateTimeOffset.UtcNow - checkedAtUtc <= ttl;

    internal static string NormalizeEnvironmentUrl(string environmentUrl) =>
        environmentUrl.Trim().TrimEnd('/').ToLowerInvariant();

    internal static string SolutionKey(string environmentUrl, string solutionName, bool includeManaged) =>
        $"{NormalizeEnvironmentUrl(environmentUrl)}|{solutionName.Trim().ToLowerInvariant()}|{(includeManaged ? "managed" : "unmanaged")}";

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();

    static string? GetFlowlineVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
}
