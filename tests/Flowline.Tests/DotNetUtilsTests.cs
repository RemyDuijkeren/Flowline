using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

public class DotNetUtilsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));
    string _cdsprojPath = null!;

    const string BaseCdsproj =
        """
        <Project Sdk="Microsoft.PowerApps.MSBuild.Solution/1.0.0.0">
          <PropertyGroup>
            <SolutionRootPath>src\</SolutionRootPath>
          </PropertyGroup>
        </Project>
        """;

    const string CdsprojWithMapping =
        """
        <Project Sdk="Microsoft.PowerApps.MSBuild.Solution/1.0.0.0">
          <PropertyGroup>
            <SolutionRootPath>src\</SolutionRootPath>
          </PropertyGroup>
          <PropertyGroup>
            <SolutionPackageMapFilePath>$(MSBuildProjectDirectory)\MappingBuild.xml</SolutionPackageMapFilePath>
          </PropertyGroup>
        </Project>
        """;

    public DotNetUtilsTests()
    {
        Directory.CreateDirectory(_dir);
        _cdsprojPath = Path.Combine(_dir, "MySolution.cdsproj");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task EnsureMapFilePathAsync_FileNotFound_ReturnsError()
    {
        var result = await DotNetUtils.EnsureMapFilePathAsync("nonexistent.cdsproj", useMapping: true);

        result.Should().Be(1);
    }

    [Fact]
    public async Task EnsureMapFilePathAsync_UseMappingTrue_MappingAbsent_Injects()
    {
        await File.WriteAllTextAsync(_cdsprojPath, BaseCdsproj);

        var result = await DotNetUtils.EnsureMapFilePathAsync(_cdsprojPath, useMapping: true);
        var content = await File.ReadAllTextAsync(_cdsprojPath);

        result.Should().Be(0);
        content.Should().Contain("SolutionPackageMapFilePath");
        content.Should().Contain("MappingBuild.xml");
    }

    [Fact]
    public async Task EnsureMapFilePathAsync_UseMappingTrue_MappingPresent_IsNoOp()
    {
        await File.WriteAllTextAsync(_cdsprojPath, CdsprojWithMapping);

        var result = await DotNetUtils.EnsureMapFilePathAsync(_cdsprojPath, useMapping: true);
        var content = await File.ReadAllTextAsync(_cdsprojPath);

        result.Should().Be(0);
        content.Should().Be(CdsprojWithMapping);
    }

    [Fact]
    public async Task EnsureMapFilePathAsync_UseMappingFalse_MappingPresent_Removes()
    {
        await File.WriteAllTextAsync(_cdsprojPath, CdsprojWithMapping);

        var result = await DotNetUtils.EnsureMapFilePathAsync(_cdsprojPath, useMapping: false);
        var content = await File.ReadAllTextAsync(_cdsprojPath);

        result.Should().Be(0);
        content.Should().NotContain("SolutionPackageMapFilePath");
    }

    [Fact]
    public async Task EnsureMapFilePathAsync_UseMappingFalse_MappingAbsent_IsNoOp()
    {
        await File.WriteAllTextAsync(_cdsprojPath, BaseCdsproj);

        var result = await DotNetUtils.EnsureMapFilePathAsync(_cdsprojPath, useMapping: false);
        var content = await File.ReadAllTextAsync(_cdsprojPath);

        result.Should().Be(0);
        content.Should().Be(BaseCdsproj);
    }
}
