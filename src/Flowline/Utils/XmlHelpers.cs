using System.Xml.Linq;

namespace Flowline.Utils;

// Centralizes XML-parsing quirks shared by DataverseContextGenerator and SolutionChangeSummary — both
// read PAC-unpacked or `git show`-sourced XML, which can surface a leading UTF-8 BOM char that
// XDocument.Parse rejects, and both resolve a Dataverse LocalizedName/label/displayname by preferring
// languagecode="1033" (en-US).
public static class XmlHelpers
{
    public static string StripBom(string text) => text.TrimStart('﻿');

    public static XDocument Parse(string xml) => XDocument.Parse(StripBom(xml));

    public static XElement? PreferLanguage(IEnumerable<XElement> elements, string languageCode = "1033") =>
        elements.FirstOrDefault(e => (string?)e.Attribute("languagecode") == languageCode) ?? elements.FirstOrDefault();
}
