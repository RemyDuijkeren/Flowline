using Flowline;
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
        await act.Should().ThrowAsync<Exception>().WithMessage("Only the MSI-installed Power Platform CLI was found, but it is not supported by Flowline due to inaccurate exit codes. Please install the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
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
        await act.Should().ThrowAsync<Exception>().WithMessage("Only the MSI-installed Power Platform CLI was found, but it is not supported by Flowline due to inaccurate exit codes. Please install the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
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
        await act.Should().ThrowAsync<Exception>().WithMessage("Power Platform CLI is not installed.");
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
