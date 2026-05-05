using Flowline.Validation;
using FluentAssertions;

namespace Flowline.Tests;

public class ValidationCacheTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));
    readonly string _cachePath;

    public ValidationCacheTests()
    {
        Directory.CreateDirectory(_tempDir);
        _cachePath = Path.Combine(_tempDir, "validation-cache.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EnsureDotNetAsync_UsesFreshCache()
    {
        var callCount = 0;
        var validator = CreateValidator(new ValidationProbes
        {
            CheckDotNetAsync = (_, _) =>
            {
                callCount++;
                return Task.FromResult("9.0.100");
            }
        });

        await validator.EnsureDotNetAsync(new FlowlineSettings(), CancellationToken.None);
        var result = await validator.EnsureDotNetAsync(new FlowlineSettings(), CancellationToken.None);

        callCount.Should().Be(1);
        result.Version.Should().Be("9.0.100");
    }

    [Fact]
    public async Task EnsureDotNetAsync_NoCacheRefreshesFreshCache()
    {
        var callCount = 0;
        var validator = CreateValidator(new ValidationProbes
        {
            CheckDotNetAsync = (_, _) =>
            {
                callCount++;
                return Task.FromResult($"9.0.{callCount}");
            }
        });

        await validator.EnsureDotNetAsync(new FlowlineSettings(), CancellationToken.None);
        var result = await validator.EnsureDotNetAsync(new FlowlineSettings { NoCache = true }, CancellationToken.None);

        callCount.Should().Be(2);
        result.Version.Should().Be("9.0.2");
    }

    [Fact]
    public async Task EnsureDotNetAsync_RefreshesStaleCache()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache
        {
            ToolChecks =
            {
                ["dotnet"] = new ValidationCacheEntry<ToolCheckResult>
                {
                    CheckedAtUtc = DateTimeOffset.UtcNow.AddDays(-8),
                    Value = new ToolCheckResult { Version = "old" }
                }
            }
        });

        var callCount = 0;
        var validator = CreateValidator(new ValidationProbes
        {
            CheckDotNetAsync = (_, _) =>
            {
                callCount++;
                return Task.FromResult("new");
            }
        });

        var result = await validator.EnsureDotNetAsync(new FlowlineSettings(), CancellationToken.None);

        callCount.Should().Be(1);
        result.Version.Should().Be("new");
    }

    [Fact]
    public async Task FailedChecks_AreNotPersisted()
    {
        var validator = CreateValidator(new ValidationProbes
        {
            CheckDotNetAsync = (_, _) => throw new InvalidOperationException("missing")
        });

        Func<Task> act = () => validator.EnsureDotNetAsync(new FlowlineSettings(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        new ValidationCacheStore(_cachePath).Load().ToolChecks.Should().NotContainKey("dotnet");
    }

    [Fact]
    public async Task EnvironmentAndSolutionKeys_AreNormalized()
    {
        var envCalls = 0;
        var solutionCalls = 0;
        var validator = CreateValidator(new ValidationProbes
        {
            GetEnvironmentAsync = (_, _, _) =>
            {
                envCalls++;
                return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo
                {
                    EnvironmentUrl = "https://contoso.crm4.dynamics.com/",
                    DisplayName = "Contoso",
                    Type = "Sandbox"
                });
            },
            GetSolutionsAsync = (_, _, _) =>
            {
                solutionCalls++;
                return Task.FromResult(new List<SolutionInfo>
                {
                    new() { SolutionUniqueName = "ContosoCore", IsManaged = false }
                });
            }
        });

        await validator.GetEnvironmentInfoByUrlAsync("HTTPS://CONTOSO.CRM4.DYNAMICS.COM/", new FlowlineSettings(), CancellationToken.None);
        await validator.GetEnvironmentInfoByUrlAsync("https://contoso.crm4.dynamics.com", new FlowlineSettings(), CancellationToken.None);
        await validator.GetSolutionInfoAsync("HTTPS://CONTOSO.CRM4.DYNAMICS.COM/", "CONTOSOCORE", false, new FlowlineSettings(), CancellationToken.None);
        await validator.GetSolutionInfoAsync("https://contoso.crm4.dynamics.com", "contosocore", false, new FlowlineSettings(), CancellationToken.None);

        envCalls.Should().Be(1);
        solutionCalls.Should().Be(1);
    }

    FlowlineValidator CreateValidator(ValidationProbes probes) =>
        new(new ValidationCacheStore(_cachePath), probes);
}
