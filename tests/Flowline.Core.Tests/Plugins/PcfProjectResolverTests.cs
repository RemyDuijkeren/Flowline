using Flowline.Core.Plugins;
using FluentAssertions;

namespace Flowline.Core.Tests.Plugins;

public class PcfProjectResolverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public PcfProjectResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void IsPcfProject_WithPcfprojExtension_ShouldReturnTrue()
    {
        // No SDK marker and no manifest in the fixture — isolates the extension check from the other two.
        var projectPath = WriteProject("image-grid-pcf", "image-grid-pcf.pcfproj", PlainCsprojXml());

        PcfProjectResolver.IsPcfProject(projectPath).Should().BeTrue();
    }

    [Fact]
    public void IsPcfProject_WithCsprojWrappingThePcfSdkPackage_ShouldReturnTrue()
    {
        var projectPath = WriteProject("image-grid-pcf", "image-grid-pcf.csproj", PcfProjectXml());

        PcfProjectResolver.IsPcfProject(projectPath).Should().BeTrue();
    }

    [Fact]
    public void IsPcfProject_WithSiblingControlManifest_ShouldReturnTrue()
    {
        // The SDK reference arrives transitively (Directory.Build.props, e.g.), so the csproj carries
        // none of the marker text itself — only the manifest in the control's subfolder gives it away.
        var projectPath = WriteProject("image-grid-pcf", "image-grid-pcf.csproj", PlainCsprojXml());
        Directory.CreateDirectory(Path.Combine(_root, "image-grid-pcf", "ImageGrid"));
        File.WriteAllText(Path.Combine(_root, "image-grid-pcf", "ImageGrid", "ControlManifest.Input.xml"), "<manifest />");

        PcfProjectResolver.IsPcfProject(projectPath).Should().BeTrue();
    }

    [Fact]
    public void IsPcfProject_WithPlainPluginOrWebResourcesCsproj_ShouldReturnFalse()
    {
        var projectPath = WriteProject("Plugins", "Plugins.csproj", PlainCsprojXml());

        PcfProjectResolver.IsPcfProject(projectPath).Should().BeFalse();
    }

    [Fact]
    public void IsPcfProject_WithLockedFile_ShouldReturnFalseNotThrow()
    {
        var projectPath = WriteProject("Locked", "Locked.csproj", PlainCsprojXml());
        using var hold = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.None);

        PcfProjectResolver.IsPcfProject(projectPath).Should().BeFalse();
    }

    static string PcfProjectXml() =>
        "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>" +
        "<ItemGroup><PackageReference Include=\"Microsoft.PowerApps.MSBuild.Pcf\" Version=\"1.*\" /></ItemGroup></Project>";

    static string PlainCsprojXml() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>";

    string WriteProject(string folder, string fileName, string xml)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }
}
