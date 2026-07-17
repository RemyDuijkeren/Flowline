using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class CloneCommandTests
{
    // ── HasManagedContent ───────────────────────────────────────────────────
    // Detects whether Package/src already has the managed layer (PAC's "{name}_managed.xml"
    // sibling files, written only when unpacked with --packagetype Both), so clone's re-sync
    // branch can skip when a solution already cloned as managed is re-run unchanged.

    [Fact]
    public void HasManagedContent_NoSrcFolder_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var packageFolder = Path.Combine(root, "Package");
            Directory.CreateDirectory(packageFolder);

            var result = CloneCommand.HasManagedContent(packageFolder);

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasManagedContent_SrcWithOnlyUnmanagedFiles_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var srcFolder = Path.Combine(root, "Package", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Package"));

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasManagedContent_SrcWithManagedFile_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            var srcFolder = Path.Combine(root, "Package", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");
            File.WriteAllText(Path.Combine(srcFolder, "{guid}_managed.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Package"));

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ── ValidateForce / HasForce ────────────────────────────────────────────

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new CloneCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new CloneCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesConfig()
    {
        var settings = new CloneCommand.Settings { Force = ["all"] };

        settings.HasForce("config").Should().BeTrue();
    }
}
