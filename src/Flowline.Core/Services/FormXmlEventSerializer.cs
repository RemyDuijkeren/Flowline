using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

// Assumes formxml has no XML namespace (matches the verified ground truth - Dataverse formxml is
// namespace-free). Element("...") lookups here are namespace-sensitive and would silently miss
// matches if that ever changed.
public static class FormXmlEventSerializer
{
    public static IReadOnlySet<FormHandler> GetHandlers(XDocument form, FormEventType evt)
    {
        var result = new HashSet<FormHandler>();

        var handlersElement = FindEvent(form, evt)?.Element("Handlers");
        if (handlersElement is null)
            return result;

        foreach (var handler in handlersElement.Elements("Handler"))
        {
            var functionName = handler.Attribute("functionName")?.Value ?? "";
            var libraryName = handler.Attribute("libraryName")?.Value ?? "";
            var rawGuid = handler.Attribute("handlerUniqueId")?.Value ?? "";
            if (!Guid.TryParse(rawGuid, out var guid))
                guid = Guid.Empty;
            var parameters = handler.Attribute("parameters")?.Value ?? "";
            result.Add(new FormHandler(functionName, libraryName, guid, parameters));
        }

        return result;
    }

    public static IReadOnlySet<FormLibraryEntry> GetLibraries(XDocument form)
    {
        var result = new HashSet<FormLibraryEntry>();

        var formLibrariesElement = form.Root?.Element("formLibraries");
        if (formLibrariesElement is null)
            return result;

        foreach (var library in formLibrariesElement.Elements("Library"))
        {
            var name = library.Attribute("name")?.Value ?? "";
            var rawGuid = library.Attribute("libraryUniqueId")?.Value ?? "";
            if (!Guid.TryParse(rawGuid, out var guid))
                guid = Guid.Empty;
            result.Add(new FormLibraryEntry(name, guid));
        }

        return result;
    }

    public static void SetHandlers(XDocument form, FormEventType evt, IReadOnlySet<FormHandler> desired)
    {
        var root = form.Root ?? throw new InvalidOperationException("Form XML has no root element.");

        var eventsElement = root.Element("events");
        if (eventsElement is null)
        {
            eventsElement = new XElement("events");
            root.Add(eventsElement);
        }

        var eventName = EventName(evt);
        var eventElement = eventsElement.Elements("event")
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, eventName, StringComparison.OrdinalIgnoreCase));
        if (eventElement is null)
        {
            eventElement = new XElement("event",
                new XAttribute("name", eventName),
                new XAttribute("application", "true"),
                new XAttribute("active", "true"));
            eventsElement.Add(eventElement);
        }

        var handlersElement = eventElement.Element("Handlers");
        if (handlersElement is null)
        {
            handlersElement = new XElement("Handlers");
            eventElement.Add(handlersElement);
        }

        handlersElement.Elements("Handler").Remove();
        foreach (var handler in desired)
        {
            handlersElement.Add(new XElement("Handler",
                new XAttribute("functionName", handler.FunctionName),
                new XAttribute("libraryName", handler.LibraryName),
                new XAttribute("handlerUniqueId", $"{{{handler.HandlerUniqueId}}}"),
                new XAttribute("enabled", "true"),
                new XAttribute("parameters", handler.Parameters),
                new XAttribute("passExecutionContext", "true")));
        }
    }

    public static void SetLibraries(XDocument form, IReadOnlySet<FormLibraryEntry> desired)
    {
        var root = form.Root ?? throw new InvalidOperationException("Form XML has no root element.");

        var formLibrariesElement = root.Element("formLibraries");
        if (formLibrariesElement is null)
        {
            formLibrariesElement = new XElement("formLibraries");
            root.Add(formLibrariesElement);
        }

        formLibrariesElement.Elements("Library").Remove();
        foreach (var library in desired)
        {
            formLibrariesElement.Add(new XElement("Library",
                new XAttribute("name", library.Name),
                new XAttribute("libraryUniqueId", $"{{{library.LibraryUniqueId}}}")));
        }
    }

    public static (string? FunctionName, bool Found) ResolveFunction(string builtJsContent, string requestedFunctionName, string autoNamespace)
    {
        var escaped = Regex.Escape(requestedFunctionName);

        var namespacedMatch = Regex.Match(builtJsContent, $@"exports\.(?<name>{escaped})\s*=", RegexOptions.IgnoreCase);
        if (namespacedMatch.Success)
            return ($"{autoNamespace}.{namespacedMatch.Groups["name"].Value}", true);

        var bareMatch = Regex.Match(builtJsContent, $@"function\s+(?<name>{escaped})\s*\(|const\s+(?<name>{escaped})\s*=", RegexOptions.IgnoreCase);
        if (bareMatch.Success)
            return (bareMatch.Groups["name"].Value, true);

        return (null, false);
    }

    static XElement? FindEvent(XDocument form, FormEventType evt)
    {
        var eventName = EventName(evt);
        return form.Root?
            .Element("events")?
            .Elements("event")
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, eventName, StringComparison.OrdinalIgnoreCase));
    }

    static string EventName(FormEventType evt) => evt.ToString().ToLowerInvariant();
}
