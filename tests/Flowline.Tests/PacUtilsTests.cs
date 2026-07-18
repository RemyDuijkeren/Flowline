using Flowline;
using Flowline.Core;
using Flowline.Core.Models;
using FluentAssertions;

namespace Flowline.Tests;

public class PacUtilsTests : IDisposable
{
    public PacUtilsTests()
    {
        PacUtils.ResetCache();
    }

    public void Dispose()
    {
        PacUtils.ResetCache();
        PacUtils.CheckCommandExistsFunc = null;
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnPacExe_WhenPacExeExists()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "pac.exe", cmd == "pac.exe" ? "Version: 2.6.3 (.NET 10.0)" : ""));

        // Act
        var result = await PacUtils.GetBestPacCommandAsync();

        // Assert
        result.Command.Should().Be("pac.exe");
        result.PrefixArgs.Should().BeNull();
        result.IsDotnetTool.Should().BeTrue();
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnPac_WhenPacExeMissingAndPacDotnetToolExists()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "pac", cmd == "pac" ? "Version: 2.6.3 (.NET 10.0)" : ""));

        // Act
        var result = await PacUtils.GetBestPacCommandAsync();

        // Assert
        result.Command.Should().Be("pac");
        result.PrefixArgs.Should().BeNull();
        result.IsDotnetTool.Should().BeTrue();
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnDnx_WhenPacIsMsiAndDnxExists()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
        {
            if (cmd == "pac") return Task.FromResult((true, "Version: 2.5.1 (.NET Framework 4.8)"));
            if (cmd == "dnx") return Task.FromResult((true, "Version: 2.6.3 (.NET 10.0)"));
            return Task.FromResult((false, ""));
        };

        // Act
        var result = await PacUtils.GetBestPacCommandAsync();

        // Assert
        result.Command.Should().Be("dnx");
        result.PrefixArgs.Should().HaveCount(2);
        result.PrefixArgs.Should().ContainInOrder("microsoft.powerapps.cli.tool", "--yes");
        result.IsDotnetTool.Should().BeTrue();
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldThrow_WhenPacIsMsiAndDnxMissing()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "pac", cmd == "pac" ? "Version: 2.5.1 (.NET Framework 4.8)" : ""));

        // Act
        Func<Task> act = async () => await PacUtils.GetBestPacCommandAsync();

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("Only the MSI-installed Power Platform CLI was found. Flowline needs the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnDnx_WhenMsiLauncherFoundAndDnxExists()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
        {
            if (cmd == "pac.launcher.exe") return Task.FromResult((true, "Version: 2.5.1 (.NET Framework 4.8)"));
            if (cmd == "dnx") return Task.FromResult((true, "Version: 2.6.3 (.NET 10.0)"));
            return Task.FromResult((false, ""));
        };

        // Act
        var result = await PacUtils.GetBestPacCommandAsync();

        // Assert
        result.Command.Should().Be("dnx");
        result.PrefixArgs.Should().HaveCount(2);
        result.PrefixArgs.Should().ContainInOrder("microsoft.powerapps.cli.tool", "--yes");
        result.IsDotnetTool.Should().BeTrue();
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldThrow_WhenOnlyLauncherExistsAndDnxMissing()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "pac.launcher.exe", cmd == "pac.launcher.exe" ? "Version: 2.5.1 (.NET Framework 4.8)" : ""));

        // Act
        Func<Task> act = async () => await PacUtils.GetBestPacCommandAsync();

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("Only the MSI-installed Power Platform CLI was found. Flowline needs the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnDnx_WhenEverythingElseMissing()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", cmd == "dnx" ? "Version: 2.6.3 (.NET 10.0)" : ""));

        // Act
        var result = await PacUtils.GetBestPacCommandAsync();

        // Assert
        result.Command.Should().Be("dnx");
        result.PrefixArgs.Should().HaveCount(2);
        result.PrefixArgs.Should().ContainInOrder("microsoft.powerapps.cli.tool", "--yes");
        result.IsDotnetTool.Should().BeTrue();
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldThrow_WhenAllMissing()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) => Task.FromResult((false, ""));

        // Act
        Func<Task> act = async () => await PacUtils.GetBestPacCommandAsync();

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("Power Platform CLI isn't available.");
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldReturnCachedResult_OnSubsequentCalls()
    {
        // Arrange
        int callCount = 0;
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
        {
            if (cmd == "pac.exe") callCount++;
            return Task.FromResult((cmd == "pac.exe", "Version: 2.6.3 (.NET 10.0)"));
        };

        // Act
        await PacUtils.GetBestPacCommandAsync();
        await PacUtils.GetBestPacCommandAsync();

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetBestPacCommandAsync_ShouldIncludePrefixArgs_WhenDnxSelected()
    {
        // Arrange
        PacUtils.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", cmd == "dnx" ? "Version: 2.6.3 (.NET 10.0)" : ""));

        // Act
        var (command, prefixArgs, isDotnetTool) = await PacUtils.GetBestPacCommandAsync();

        // Assert
        command.Should().Be("dnx");
        prefixArgs.Should().HaveCount(2);
        prefixArgs.Should().ContainInOrder("microsoft.powerapps.cli.tool", "--yes");
        isDotnetTool.Should().BeTrue();
    }
}

public class BuildAuthSelectArgsTests
{
    [Fact]
    public void BuildAuthSelectArgs_ProfileHasName_ReturnsNameArg()
    {
        var profile = new PacProfile { Name = "MyProfile", Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile> { profile };

        var result = PacUtils.BuildAuthSelectArgs(profile, allProfiles);

        result.ArgName.Should().Be("--name");
        result.ArgValue.Should().Be("MyProfile");
    }

    [Fact]
    public void BuildAuthSelectArgs_ProfileHasNoName_ReturnsIndexArgAtItsPosition()
    {
        var first = new PacProfile { Kind = "DATAVERSE", User = "a@contoso.com" };
        var target = new PacProfile { Kind = "DATAVERSE", User = "b@contoso.com" };
        var allProfiles = new List<PacProfile> { first, target };

        var result = PacUtils.BuildAuthSelectArgs(target, allProfiles);

        result.ArgName.Should().Be("--index");
        result.ArgValue.Should().Be("1");
    }

    [Fact]
    public void BuildAuthSelectArgs_ProfileNameIsWhitespace_FallsBackToIndex()
    {
        var profile = new PacProfile { Name = "   ", Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile> { profile };

        var result = PacUtils.BuildAuthSelectArgs(profile, allProfiles);

        result.ArgName.Should().Be("--index");
        result.ArgValue.Should().Be("0");
    }

    [Fact]
    public void BuildAuthSelectArgs_UnnamedProfileNotInList_Throws()
    {
        var profile = new PacProfile { Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile>();

        var act = () => PacUtils.BuildAuthSelectArgs(profile, allProfiles);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.NotAuthenticated);
    }
}

public class ParseVersionFromPacOutputTests
{
    const string FullOutput =
        "Connected as remy@automatevalue.com\r\n" +
        "Connected to... AutomateValue Dev\r\n" +
        "\r\n" +
        "Listing all Solutions from the current Dataverse organization...\r\n" +
        "Unique Name: Cr07982\r\n" +
        "Solution Display Name: AV Default Solution\r\n" +
        "Solution Version: 1.0.0.1\r\n";

    [Fact]
    public void ParseVersionFromPacOutput_ReturnsVersion_WhenFullOutputProvided()
    {
        var result = PacUtils.ParseVersionFromPacOutput(FullOutput);

        result.Should().Be("1.0.0.1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseVersionFromPacOutput_ReturnsNull_WhenOutputIsEmptyOrWhitespace(string output)
    {
        var result = PacUtils.ParseVersionFromPacOutput(output);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseVersionFromPacOutput_ReturnsNull_WhenVersionLineAbsent()
    {
        var output = "Connected as remy@automatevalue.com\r\nListing all Solutions...\r\nUnique Name: Cr07982\r\n";

        var result = PacUtils.ParseVersionFromPacOutput(output);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseVersionFromPacOutput_TrimsWhitespace_WhenVersionHasExtraSpaces()
    {
        var output = "Solution Version:  1.0.0.1  \r\n";

        var result = PacUtils.ParseVersionFromPacOutput(output);

        result.Should().Be("1.0.0.1");
    }
}
