using Flowline.Core.Models;
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

    [Fact]
    public async Task GetSolutionInfoAsync_BypassCache_IgnoresFreshCacheEntry()
    {
        var solutionCalls = 0;
        var validator = CreateValidator(new ValidationProbes
        {
            GetSolutionsAsync = (_, _, _) =>
            {
                solutionCalls++;
                return Task.FromResult(new List<SolutionInfo>
                {
                    new() { SolutionUniqueName = "ContosoCore", IsManaged = false, VersionNumber = $"1.0.{solutionCalls}.0" }
                });
            }
        });

        await validator.GetSolutionInfoAsync("https://contoso.crm4.dynamics.com/", "ContosoCore", false, new FlowlineSettings(), CancellationToken.None);
        var result = await validator.GetSolutionInfoAsync("https://contoso.crm4.dynamics.com/", "ContosoCore", false, new FlowlineSettings(), CancellationToken.None, bypassCache: true);

        solutionCalls.Should().Be(2);
        result!.VersionNumber.Should().Be("1.0.2.0");
    }

    [Fact]
    public void ShouldShowWelcomeScreen_ReturnsTrueWhenNoCachedEntry()
    {
        var validator = CreateValidator(new ValidationProbes());

        validator.ShouldShowWelcomeScreen().Should().BeTrue();
    }

    [Fact]
    public void ShouldShowWelcomeScreen_ReturnsFalseWhenShownWithinTtl()
    {
        var validator = CreateValidator(new ValidationProbes());

        validator.ShouldShowWelcomeScreen();
        validator.ShouldShowWelcomeScreen().Should().BeFalse();
    }

    [Fact]
    public void ShouldShowWelcomeScreen_ReturnsTrueWhenCacheIsStale()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { WelcomeShownAtUtc = DateTimeOffset.UtcNow.AddDays(-2) });

        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.ShouldShowWelcomeScreen().Should().BeTrue();
    }

    [Fact]
    public void ShouldShowWelcomeScreen_NoCacheAlwaysReturnsTrue()
    {
        var validator = CreateValidator(new ValidationProbes());

        validator.ShouldShowWelcomeScreen(noCache: true);
        validator.ShouldShowWelcomeScreen(noCache: true).Should().BeTrue();
    }

    [Fact]
    public void ShouldShowWelcomeScreen_PersistsTimestampOnShow()
    {
        var store = new ValidationCacheStore(_cachePath);
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.ShouldShowWelcomeScreen();

        store.Load().WelcomeShownAtUtc.Should().NotBeNull()
            .And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    FlowlineValidator CreateValidator(ValidationProbes probes) =>
        new(new ValidationCacheStore(_cachePath), probes);

    // Real file captured from the pre-U3 build (ValidationCache with no AvailableUpdate field) —
    // proves a cache written by an older Flowline still loads once the new property is added.
    const string PreExistingCacheJson = """
        {
          "SchemaVersion": 1,
          "FlowlineVersion": "1.2.3",
          "ToolChecks": {
            "dotnet": {
              "CheckedAtUtc": "2026-08-13T08:50:45.56708+00:00",
              "Value": {
                "Version": "9.0.100",
                "InstallType": null
              }
            }
          },
          "GitRepos": {},
          "Environments": {},
          "Solutions": {},
          "WelcomeShownAtUtc": "2026-08-13T08:50:45.5673969+00:00"
        }
        """;

    [Fact]
    public void Load_PreExistingCacheWithoutUpdateField_DoesNotResetSchemaOrDiscardToolChecks()
    {
        File.WriteAllText(_cachePath, PreExistingCacheJson);

        var cache = new ValidationCacheStore(_cachePath).Load();

        cache.SchemaVersion.Should().Be(1);
        cache.ToolChecks.Should().ContainKey("dotnet");
        cache.ToolChecks["dotnet"].Value.Version.Should().Be("9.0.100");
        cache.AvailableUpdate.Should().BeNull();
    }

    [Fact]
    public void TryGetCachedUpdateVersion_ReturnsFalseWhenNeverChecked()
    {
        var validator = CreateValidator(new ValidationProbes());

        validator.TryGetCachedUpdateVersion(noCache: false, out var newerVersion).Should().BeFalse();
        newerVersion.Should().BeNull();
    }

    [Fact]
    public void TryGetCachedUpdateVersion_ReturnsTrueWhenCheckedWithinTtl()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache
        {
            AvailableUpdate = new ValidationCacheEntry<string?>
            {
                CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                Value = "2.0.0"
            }
        });
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.TryGetCachedUpdateVersion(noCache: false, out var newerVersion).Should().BeTrue();
        newerVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void TryGetCachedUpdateVersion_ReturnsFalseWhenCheckedOverTtlAgo()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache
        {
            AvailableUpdate = new ValidationCacheEntry<string?>
            {
                CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-25),
                Value = "2.0.0"
            }
        });
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.TryGetCachedUpdateVersion(noCache: false, out var newerVersion).Should().BeFalse();
    }

    [Fact]
    public void TryGetCachedUpdateVersion_NoCacheReturnsFalseEvenWhenFresh()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache
        {
            AvailableUpdate = new ValidationCacheEntry<string?>
            {
                CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                Value = "2.0.0"
            }
        });
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.TryGetCachedUpdateVersion(noCache: true, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetCachedUpdateVersion_NullValueWithFreshTimestamp_ReturnsTrueWithNullVersion()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache
        {
            AvailableUpdate = new ValidationCacheEntry<string?>
            {
                CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                Value = null
            }
        });
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.TryGetCachedUpdateVersion(noCache: false, out var newerVersion).Should().BeTrue();
        newerVersion.Should().BeNull();
    }

    [Fact]
    public void SaveUpdateCheck_ThenTryGetCachedUpdateVersion_RoundTrips()
    {
        var store = new ValidationCacheStore(_cachePath);
        var validator = new FlowlineValidator(store, new ValidationProbes());

        validator.SaveUpdateCheck("3.1.0");

        validator.TryGetCachedUpdateVersion(noCache: false, out var newerVersion).Should().BeTrue();
        newerVersion.Should().Be("3.1.0");
        store.Load().AvailableUpdate!.CheckedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
