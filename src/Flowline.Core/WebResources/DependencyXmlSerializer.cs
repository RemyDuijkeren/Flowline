using System.Xml.Linq;
using Flowline.Core.Models;

namespace Flowline.Core.WebResources;

public static class DependencyXmlSerializer
{
    public static IReadOnlySet<DependencyLibrary> Deserialize(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return new HashSet<DependencyLibrary>();

        var doc = XDocument.Parse(xml);
        var result = new HashSet<DependencyLibrary>();

        foreach (var library in doc.Descendants("Library"))
        {
            var name = library.Attribute("name")?.Value ?? "";
            var displayName = library.Attribute("displayName")?.Value ?? "";
            var rawGuid = library.Attribute("libraryUniqueId")?.Value ?? "";
            if (!Guid.TryParse(rawGuid, out var guid))
                guid = Guid.Empty;
            result.Add(new DependencyLibrary(name, displayName, guid));
        }

        return result;
    }

    public static string? Serialize(IReadOnlySet<DependencyLibrary> libs)
    {
        if (libs.Count == 0)
            return null;

        var dependency = new XElement("Dependency",
            new XAttribute("componentType", "WebResource"),
            libs.Select(lib => new XElement("Library",
                new XAttribute("name", lib.Name),
                new XAttribute("displayName", lib.DisplayName),
                new XAttribute("languagecode", ""),
                new XAttribute("description", ""),
                new XAttribute("libraryUniqueId", $"{{{lib.LibraryUniqueId}}}"))));

        return new XDocument(new XElement("Dependencies", dependency)).ToString(SaveOptions.DisableFormatting);
    }
}
