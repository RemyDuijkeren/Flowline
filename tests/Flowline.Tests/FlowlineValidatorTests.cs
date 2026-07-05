using FluentAssertions;
using Flowline.Validation;

namespace Flowline.Tests;

public class FlowlineValidatorTests
{
    [Theory]
    [InlineData(".NET 10 dnx (One-shot)", true)]
    [InlineData("dnx", true)]
    [InlineData("DNX", true)]
    [InlineData("Dotnet Tool (.NET)", false)]
    [InlineData("MSI Installer (.NET Framework)", false)]
    [InlineData("Unknown", false)]
    public void IsSlowDnxInstall_DetectsDnxInstallTypeOnly(string installType, bool expected) =>
        FlowlineValidator.IsSlowDnxInstall(installType).Should().Be(expected);
}
