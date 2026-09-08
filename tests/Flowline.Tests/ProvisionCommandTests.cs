using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Infrastructure;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class ProvisionCommandTests
{
    private static SolutionInfo Unmanaged(string uniqueName) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = false };

    private static SolutionInfo Managed(string uniqueName) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = true };

    private static SolutionInfo Unmanaged(string uniqueName, Guid id) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = false, Id = id };

    private static SolutionInfo Managed(string uniqueName, Guid id) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = true, Id = id };

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetUnmanagedExistsAsUnmanagedInProd()
    {
        var target = new[] { Unmanaged("SharedLib") };
        var prod   = new[] { Unmanaged("SharedLib") };

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsManagedInProd_WhenTargetUnmanagedIsManagedInProd()
    {
        var target = new[] { Unmanaged("MySolution") };
        var prod   = new[] { Managed("MySolution") };

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Target.SolutionUniqueName.Should().Be("MySolution");
        result[0].Reason.Should().Be("managed in prod");
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsAbsentFromProd_WhenTargetUnmanagedMissingInProd()
    {
        var target = new[] { Unmanaged("WorkInProgress") };
        var prod   = Array.Empty<SolutionInfo>();

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Target.SolutionUniqueName.Should().Be("WorkInProgress");
        result[0].Reason.Should().Be("absent from prod");
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetHasNoUnmanagedSolutions()
    {
        var target = new[] { Managed("ManagedSolution") };
        var prod   = Array.Empty<SolutionInfo>();

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetIsEmpty()
    {
        ProvisionCommand.FindProblematicSolutions(
            Array.Empty<SolutionInfo>(),
            Array.Empty<SolutionInfo>()).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_IsCaseInsensitive_OnUniqueName()
    {
        var target = new[] { Unmanaged("mysolution") };
        var prod   = new[] { Unmanaged("MySolution") };

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenSameIdButDifferentUniqueName_ProdUnmanaged()
    {
        // The default solutions carry a stable Id but an environment-specific unique name.
        var sharedId = Guid.NewGuid();
        var target = new[] { Unmanaged("Cr2b44a", sharedId) };   // Common Data Services Default Solution in test
        var prod   = new[] { Unmanaged("Cr07982", sharedId) };   // same solution, renamed in prod

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsAbsentFromProd_WhenDifferentNameAndDifferentId()
    {
        var target = new[] { Unmanaged("WorkInProgress", Guid.NewGuid()) };
        var prod   = new[] { Unmanaged("SomethingElse",  Guid.NewGuid()) };

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Reason.Should().Be("absent from prod");
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsManagedInProd_WhenSameIdButProdManaged()
    {
        var sharedId = Guid.NewGuid();
        var target = new[] { Unmanaged("LocalName", sharedId) };
        var prod   = new[] { Managed("ProdName",   sharedId) };

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Reason.Should().Be("managed in prod");
    }

    [Fact]
    public void FindProblematicSolutions_ExcludesSolutionsWithNullUniqueName()
    {
        var target = new[] { new SolutionInfo { SolutionUniqueName = null, IsManaged = false } };
        var prod   = Array.Empty<SolutionInfo>();

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingOverwriteConfigAndAll()
    {
        var settings = new ProvisionCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, ProvisionCommand.ValidSpecifiers, "provision");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("overwrite") && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new ProvisionCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, ProvisionCommand.ValidSpecifiers, "provision");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_Overwrite_DoesNotThrow()
    {
        var settings = new ProvisionCommand.Settings { Force = ["overwrite"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, ProvisionCommand.ValidSpecifiers, "provision");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_Overwrite_ApprovesOverwriteSpecifierOnly()
    {
        var settings = new ProvisionCommand.Settings { Force = ["overwrite"] };

        settings.HasForce("overwrite").Should().BeTrue();
        settings.HasForce("config").Should().BeFalse();
    }

    [Fact]
    public void HasForce_All_ApprovesOverwrite()
    {
        var settings = new ProvisionCommand.Settings { Force = ["all"] };

        settings.HasForce("overwrite").Should().BeTrue();
    }

    [Fact]
    public void BuildOverwritePrompt_NamesTheTargetAndAsksToOverwrite()
    {
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");

        prompt.Should().Contain("ContosoSales Dev");
        prompt.Should().Contain("overwrite");
    }

    [Theory]
    [InlineData(Role.Dev, null, CopyType.Minimal)]
    [InlineData(Role.Test, null, CopyType.Full)]
    [InlineData(Role.Uat, null, CopyType.Full)]
    [InlineData(Role.Test, CopyType.Minimal, CopyType.Minimal)]
    [InlineData(Role.Uat, CopyType.Minimal, CopyType.Minimal)]
    [InlineData(Role.Dev, CopyType.Full, CopyType.Full)]
    public void ResolveCopyType_ExplicitCopyWinsOtherwiseRoleDefault(Role role, CopyType? requested, CopyType expected)
    {
        ProvisionCommand.ResolveCopyType(role, requested).Should().Be(expected);
    }

    // AE7: existing target, no TTY, no --force overwrite -> exit 17 naming --force overwrite; --force all
    // proceeds without prompting. Exercises the exact call ProvisionCommand makes (ConfirmAsync with the
    // "overwrite" specifier), not just the generic gate mechanism.
    [Fact]
    public async Task OverwriteGate_NonInteractive_NoForce_ThrowsForceRequiredNamingForceOverwrite()
    {
        var settings = new ProvisionCommand.Settings { Force = [] };
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");

        var act = () => new TestConsole().ConfirmAsync(prompt, false, settings, "overwrite", CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force overwrite"));
    }

    [Fact]
    public async Task OverwriteGate_NonInteractive_ForceAll_ProceedsWithoutPrompting()
    {
        var settings = new ProvisionCommand.Settings { Force = ["all"] };
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");

        var result = await new TestConsole().ConfirmAsync(prompt, false, settings, "overwrite", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task OverwriteGate_NonInteractive_ForceOverwrite_ProceedsWithoutPrompting()
    {
        var settings = new ProvisionCommand.Settings { Force = ["overwrite"] };
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");

        var result = await new TestConsole().ConfirmAsync(prompt, false, settings, "overwrite", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task OverwriteGate_Interactive_Decline_ReturnsFalse()
    {
        var settings = new ProvisionCommand.Settings { Force = [] };
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("n");

        var result = await console.ConfirmAsync(prompt, false, settings, "overwrite", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task OverwriteGate_Interactive_Accept_ReturnsTrue()
    {
        var settings = new ProvisionCommand.Settings { Force = [] };
        var prompt = ProvisionCommand.BuildOverwritePrompt("ContosoSales Dev");
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("y");

        var result = await console.ConfirmAsync(prompt, false, settings, "overwrite", CancellationToken.None);

        result.Should().BeTrue();
    }

    sealed class ProvisionProbeCommand : Command<ProvisionCommand.Settings>
    {
        public static ProvisionCommand.Settings? Captured;
        protected override int Execute(CommandContext context, ProvisionCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    // KD5: --allow-overwrite is removed with no alias. Spectre.Console.Cli doesn't reject unrecognized
    // long options at parse time (confirmed against the real Program.cs binary too) — it just has
    // nothing left to bind the value to, so this locks in that there's no settings property carrying it.
    [Fact]
    public void Parse_AllowOverwriteFlag_NoLongerBindsToASetting()
    {
        var app = new CommandApp<ProvisionProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["dev", "--allow-overwrite"]);

        result.Should().Be(0);
        ProvisionProbeCommand.Captured!.Role.Should().Be(Role.Dev);
        typeof(ProvisionCommand.Settings).GetProperty("AllowOverwrite").Should().BeNull();
    }
}
