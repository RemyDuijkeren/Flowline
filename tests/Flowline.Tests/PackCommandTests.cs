using FluentAssertions;
using Flowline.Commands;
using Flowline.Settings;

namespace Flowline.Tests;

public class PackCommandTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"PackCommand_{Guid.NewGuid():N}");

    public PackCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Settings_ExposePreflightCacheAndForce()
    {
        typeof(PackCommand.Settings).GetProperty(nameof(DataverseSettings.NoCache)).Should().NotBeNull();
        typeof(PackCommand.Settings).GetProperty(nameof(DataverseSettings.Force)).Should().NotBeNull();
    }

    [Fact]
    public void ValidSpecifiers_AreDriftAndAllOnly()
    {
        PackCommand.ValidSpecifiers.Should().BeEquivalentTo("drift", "all");
    }

    [Theory]
    [InlineData("Mapping.xml")]
    [InlineData("mapping.xml")]
    [InlineData("solution-mapping.xml")]
    public void FindMappingFile_FindsMappingFileBesideTheCdsproj(string fileName)
    {
        File.WriteAllText(Path.Combine(_folder, fileName), "<Mapping />");

        PackCommand.FindMappingFile(_folder).Should().Be(fileName);
    }

    [Fact]
    public void FindMappingFile_ReturnsNull_WhenNoMappingFile()
    {
        File.WriteAllText(Path.Combine(_folder, "Contoso.cdsproj"), "<Project />");

        PackCommand.FindMappingFile(_folder).Should().BeNull();
    }

    [Fact]
    public void BuildCheckerUnavailableMessage_PointsAtSkipFlag()
    {
        PackCommand.BuildCheckerUnavailableMessage("Solution check failed to run — check your PAC login and network connection.")
            .Should().Be("Solution check failed to run — check your PAC login and network connection, or pass --skip-solution-check to pack without it.");
    }
}
