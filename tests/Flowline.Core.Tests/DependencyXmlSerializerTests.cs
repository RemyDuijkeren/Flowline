using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Tests;

public class DependencyXmlSerializerTests
{
    const string SingleDepXml =
        """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/example1.js" displayName="example1.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";

    const string MultiDepXml =
        """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/example1.js" displayName="example1.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/><Library name="av_Sol/example2.js" displayName="example2.js" languagecode="" description="" libraryUniqueId="{0189e308-1bd6-e674-d7ee-db73a97a896e}"/></Dependency></Dependencies>""";

    // --- Deserialize ---

    [Fact]
    public void Deserialize_Null_ReturnsEmptySet()
    {
        var result = DependencyXmlSerializer.Deserialize(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsEmptySet()
    {
        var result = DependencyXmlSerializer.Deserialize("");
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_WhitespaceString_ReturnsEmptySet()
    {
        var result = DependencyXmlSerializer.Deserialize("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_SingleDependency_ReturnsSingleLibrary()
    {
        var result = DependencyXmlSerializer.Deserialize(SingleDepXml);

        Assert.Single(result);
        var lib = result.Single();
        Assert.Equal("av_Sol/example1.js", lib.Name);
        Assert.Equal("example1.js", lib.DisplayName);
        Assert.Equal(Guid.Parse("0e58647c-5eb8-e4cc-b94d-19e6acb09469"), lib.LibraryUniqueId);
    }

    [Fact]
    public void Deserialize_MultiDependency_ReturnsAllLibraries()
    {
        var result = DependencyXmlSerializer.Deserialize(MultiDepXml);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.Name == "av_Sol/example1.js");
        Assert.Contains(result, l => l.Name == "av_Sol/example2.js");
    }

    // --- Serialize ---

    [Fact]
    public void Serialize_EmptySet_ReturnsNull()
    {
        var result = DependencyXmlSerializer.Serialize(new HashSet<DependencyLibrary>());
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_SingleLibrary_ProducesCorrectXml()
    {
        var guid = Guid.Parse("0e58647c-5eb8-e4cc-b94d-19e6acb09469");
        var libs = new HashSet<DependencyLibrary> { new("av_Sol/example1.js", "example1.js", guid) };

        var result = DependencyXmlSerializer.Serialize(libs);

        Assert.NotNull(result);
        var doc = XDocument.Parse(result);
        Assert.Equal("Dependencies", doc.Root!.Name.LocalName);
        var dep = doc.Root.Element("Dependency");
        Assert.NotNull(dep);
        Assert.Equal("WebResource", dep!.Attribute("componentType")?.Value);
        var lib = dep.Element("Library");
        Assert.NotNull(lib);
        Assert.Equal("av_Sol/example1.js", lib!.Attribute("name")?.Value);
        Assert.Equal("example1.js", lib.Attribute("displayName")?.Value);
        Assert.Equal("", lib.Attribute("languagecode")?.Value);
        Assert.Equal("", lib.Attribute("description")?.Value);
        Assert.Equal("{0e58647c-5eb8-e4cc-b94d-19e6acb09469}", lib.Attribute("libraryUniqueId")?.Value);
    }

    [Fact]
    public void Serialize_MultipleLibraries_AllPresentUnderOneDependency()
    {
        var libs = new HashSet<DependencyLibrary>
        {
            new("av_Sol/a.js", "a.js", Guid.NewGuid()),
            new("av_Sol/b.js", "b.js", Guid.NewGuid())
        };

        var result = DependencyXmlSerializer.Serialize(libs);

        Assert.NotNull(result);
        var doc = XDocument.Parse(result);
        var dep = doc.Root!.Elements("Dependency").ToList();
        Assert.Single(dep);
        Assert.Equal(2, dep[0].Elements("Library").Count());
    }

    [Fact]
    public void RoundTrip_PreservesNamesAndGuids()
    {
        var guid1 = Guid.Parse("0e58647c-5eb8-e4cc-b94d-19e6acb09469");
        var guid2 = Guid.Parse("0189e308-1bd6-e674-d7ee-db73a97a896e");

        var deserialized = DependencyXmlSerializer.Deserialize(MultiDepXml);
        var reserialized = DependencyXmlSerializer.Serialize(deserialized);

        Assert.NotNull(reserialized);
        var result = DependencyXmlSerializer.Deserialize(reserialized);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.Name == "av_Sol/example1.js" && l.LibraryUniqueId == guid1);
        Assert.Contains(result, l => l.Name == "av_Sol/example2.js" && l.LibraryUniqueId == guid2);
    }
}
