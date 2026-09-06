using FluentAssertions;
using Flowline.Core.Configure;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SettingsFileLocatorTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "flowline-locator-" + Guid.NewGuid().ToString("N"));

    public SettingsFileLocatorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    void WriteFile(string name) => File.WriteAllText(Path.Combine(_folder, name), "{}");

    [Fact]
    public void Locate_RoleFilePresent_PrefersItOverTheSharedFile()
    {
        WriteFile("deploymentSettings.prod.json");
        WriteFile("deploymentSettings.json");

        var result = SettingsFileLocator.Locate(_folder, "prod");

        result.Path.Should().EndWith("deploymentSettings.prod.json");
        result.Source.Should().Be(SettingsFileSource.RoleConvention);
        result.Exists.Should().BeTrue();
    }

    [Fact]
    public void Locate_NoRoleFile_FallsBackToTheSharedFile()
    {
        WriteFile("deploymentSettings.json");

        var result = SettingsFileLocator.Locate(_folder, "prod");

        result.Path.Should().EndWith("deploymentSettings.json");
        result.Source.Should().Be(SettingsFileSource.SharedFallback);
        result.Exists.Should().BeTrue();
    }

    // A URL target carries no role name, so there is nothing to build the suffixed filename from. It can only
    // reach the shared file or an explicit path — R5's reason for restricting convention discovery to roles.
    [Fact]
    public void Locate_UrlTargetWithNoRole_UsesTheSharedFile()
    {
        WriteFile("deploymentSettings.prod.json");
        WriteFile("deploymentSettings.json");

        var result = SettingsFileLocator.Locate(_folder, role: null);

        result.Path.Should().EndWith("deploymentSettings.json");
        result.Source.Should().Be(SettingsFileSource.SharedFallback);
    }

    [Fact]
    public void Locate_ExplicitPath_OverridesDiscovery()
    {
        WriteFile("deploymentSettings.prod.json");
        var explicitPath = Path.Combine(_folder, "somewhere-else.json");
        File.WriteAllText(explicitPath, "{}");

        var result = SettingsFileLocator.Locate(_folder, "prod", explicitPath);

        result.Path.Should().Be(Path.GetFullPath(explicitPath));
        result.Source.Should().Be(SettingsFileSource.Explicit);
    }

    // A first pull must not create the shared file and quietly make one environment's values look like every
    // environment's, so an absent role-named file is still the write target when a role is known.
    [Fact]
    public void Locate_NothingOnDiskWithRole_TargetsTheRoleNamedFile()
    {
        var result = SettingsFileLocator.Locate(_folder, "uat");

        result.Path.Should().EndWith("deploymentSettings.uat.json");
        result.Source.Should().Be(SettingsFileSource.RoleConvention);
        result.Exists.Should().BeFalse();
    }

    [Fact]
    public void Locate_RoleCasingIsNormalised()
    {
        WriteFile("deploymentSettings.prod.json");

        SettingsFileLocator.Locate(_folder, "PROD").Path.Should().EndWith("deploymentSettings.prod.json");
    }
}
