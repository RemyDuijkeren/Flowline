using System.Xml.Linq;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

// Assumes formxml has no XML namespace (matches the verified ground truth - Dataverse formxml is
// namespace-free). Element("...") lookups here are namespace-sensitive and would silently miss
// matches if that ever changed.
public static class FormXmlEventSerializer
{
    public static IReadOnlySet<FormEventHandler> GetHandlers(XDocument form, FormEventType evt)
    {
        var result = new HashSet<FormEventHandler>();

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
            result.Add(new FormEventHandler(functionName, libraryName, guid, parameters));
        }

        return result;
    }

    public static IReadOnlySet<FormLibrary> GetLibraries(XDocument form)
    {
        var result = new HashSet<FormLibrary>();

        var formLibrariesElement = form.Root?.Element("formLibraries");
        if (formLibrariesElement is null)
            return result;

        foreach (var library in formLibrariesElement.Elements("Library"))
        {
            var name = library.Attribute("name")?.Value ?? "";
            var rawGuid = library.Attribute("libraryUniqueId")?.Value ?? "";
            if (!Guid.TryParse(rawGuid, out var guid))
                guid = Guid.Empty;
            result.Add(new FormLibrary(name, guid));
        }

        return result;
    }

    public static void SetHandlers(XDocument form, FormEventType evt, IReadOnlySet<FormEventHandler> desired)
    {
        var root = form.Root ?? throw new InvalidOperationException("Form XML has no root element.");

        var eventsElement = GetOrAdd(root, "events");

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

        var handlersElement = GetOrAdd(eventElement, "Handlers");

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

    public static void SetLibraries(XDocument form, IReadOnlySet<FormLibrary> desired)
    {
        var root = form.Root ?? throw new InvalidOperationException("Form XML has no root element.");

        // Confirmed live: Dataverse's formxml schema rejects an empty <formLibraries></formLibraries> —
        // "has incomplete content, expected 'Library'" — the element requires at least one Library child
        // whenever it's present at all. Remove it outright rather than leave an empty stub.
        if (desired.Count == 0)
        {
            root.Element("formLibraries")?.Remove();
            return;
        }

        var formLibrariesElement = GetOrAdd(root, "formLibraries");

        formLibrariesElement.Elements("Library").Remove();
        foreach (var library in desired)
        {
            formLibrariesElement.Add(new XElement("Library",
                new XAttribute("name", library.Name),
                new XAttribute("libraryUniqueId", $"{{{library.LibraryUniqueId}}}")));
        }
    }

    static XElement GetOrAdd(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is not null)
            return element;

        element = new XElement(name);
        parent.Add(element);
        return element;
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
