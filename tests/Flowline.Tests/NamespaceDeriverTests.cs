using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

public class NamespaceDeriverTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public NamespaceDeriverTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // Helper: create Plugins/Plugins.csproj with given content
    void CreateCsproj(string content)
    {
        var pluginsDir = Path.Combine(_tempDir, "Plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, "Plugins.csproj"), content);
    }

    [Fact]
    public void Derive_NoCsproj_ReturnsSolutionNameModels()
    {
        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("MyApp.Models");
    }

    [Fact]
    public void Derive_RootNamespace_ReturnsRootNamespaceDotModels()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>Contoso.Plugins</RootNamespace></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public void Derive_PackageIdNoRootNamespace_ReturnsPackageIdDotModels()
    {
        CreateCsproj("<Project><PropertyGroup><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public void Derive_EmptyRootNamespace_FallsBackToPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace></RootNamespace><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public void Derive_NeitherRootNamespaceNorPackageId_ReturnsFilenameModels()
    {
        CreateCsproj("<Project><PropertyGroup></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Plugins.Models");
    }

    [Fact]
    public void Derive_RootNamespaceTakesPrecedenceOverPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>Root.NS</RootNamespace><PackageId>Pkg.Id</PackageId></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Root.NS.Models");
    }

    [Fact]
    public void Derive_WhitespaceOnlyRootNamespace_FallsBackToPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>   </RootNamespace><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public void Derive_InvalidXmlCsproj_ReturnsSolutionNameModels()
    {
        var pluginsDir = Path.Combine(_tempDir, "Plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, "Plugins.csproj"), "not xml <<<");

        var result = NamespaceDeriver.Derive(_tempDir, "MyApp");

        result.Should().Be("MyApp.Models");
    }
}
