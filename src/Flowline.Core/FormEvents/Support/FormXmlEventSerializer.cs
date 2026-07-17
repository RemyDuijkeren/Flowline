using System.Xml.Linq;
using Flowline.Core.Models;

namespace Flowline.Core.FormEvents.Support;

// Assumes formxml has no XML namespace (matches the verified ground truth - Dataverse formxml is
// namespace-free). Element("...") lookups here are namespace-sensitive and would silently miss
// matches if that ever changed.
public static class FormXmlEventSerializer
{
    // Live-verified (AutomateValue Dev): the classid Dataverse assigns to every IFRAME control, regardless
    // of control name. Used to disambiguate an IFRAME <control> from any other control type sharing the
    // same parent <cell> when locating OnReadyStateComplete's scope container.
    const string IframeClassId = "{FD2A7985-3187-444E-908D-6624B21F69C0}";

    // Maker Portal always renders "IFRAME_" as a fixed, non-editable prefix in the control's Name field —
    // the maker only ever types the suffix (confirmed live: Maker Portal screenshot showed "IFRAME_" greyed
    // out with only "myiFrame" editable). The stored FormXml `id` carries the full prefixed value, but
    // requiring the annotation to repeat that system-assigned prefix verbatim is unnecessary friction, so
    // the bare suffix is the canonical scope-token form used everywhere control ids are compared, hashed,
    // or derived into a default function name — an annotation may still spell out the full prefixed id and
    // it resolves identically.
    const string IframeControlIdPrefix = "IFRAME_";

    public static string NormalizeIframeControlId(string controlId) =>
        controlId.StartsWith(IframeControlIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? controlId[IframeControlIdPrefix.Length..]
            : controlId;

    // attribute is non-null for OnChange (attribute logical name), TabStateChange (tab name), and
    // OnReadyStateComplete (IFRAME control id) — selects the specific scoped <event> element.
    public static IReadOnlyList<FormEventHandler> GetHandlers(XDocument form, FormEventType evt, string? attribute = null)
    {
        var result = new List<FormEventHandler>();

        var handlersElement = FindEvent(form, evt, attribute)?.Element("Handlers");
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

    // Enumerates every attribute currently wired to an onchange event, regardless of whether a current
    // annotation still targets it — needed by the planner for orphan detection (KTD4), since onchange's
    // "current" set can't be derived from a fixed enum the way onload/onsave's can.
    public static IReadOnlySet<string> GetOnChangeAttributes(XDocument form)
    {
        var eventName = EventName(FormEventType.OnChange);
        var eventsElement = form.Root?.Element("events");
        if (eventsElement is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return eventsElement.Elements("event")
            .Where(e => string.Equals(e.Attribute("name")?.Value, eventName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("attribute")?.Value)
            .OfType<string>()
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Enumerates every <tab name="..."> currently carrying a tabstatechange event with at least one
    // Handler, regardless of whether a current annotation still targets it — same orphan-detection role
    // GetOnChangeAttributes plays for onchange (KTD4/R13, extended to Tab/IFRAME).
    public static IReadOnlySet<string> GetTabNamesWithStateChangeHandlers(XDocument form) =>
        (form.Root?.Descendants("tab") ?? [])
            .Where(tab => tab.Element("events")?.Elements("event")
                .Any(e => string.Equals(e.Attribute("name")?.Value, EventName(FormEventType.TabStateChange), StringComparison.OrdinalIgnoreCase)
                          && e.Element("Handlers")?.Elements("Handler").Any() == true) == true)
            .Select(tab => tab.Attribute("name")?.Value)
            .OfType<string>()
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Enumerates every IFRAME <control id="..."> currently carrying an onreadystatecomplete event with at
    // least one Handler. Same orphan-detection role as GetOnChangeAttributes/GetTabNamesWithStateChangeHandlers.
    // Returns the canonical bare (prefix-stripped) form — callers union this against annotation-supplied
    // scope tokens, which may or may not carry the "IFRAME_" prefix themselves, so both sides must agree on
    // one representation or the same control could be double-counted as two distinct scopes.
    public static IReadOnlySet<string> GetIframeControlIdsWithReadyStateHandlers(XDocument form) =>
        (form.Root?.Descendants("control") ?? [])
            .Where(control => string.Equals(control.Attribute("classid")?.Value, IframeClassId, StringComparison.OrdinalIgnoreCase)
                              && control.Parent?.Element("events")?.Elements("event")
                                  .Any(e => string.Equals(e.Attribute("name")?.Value, EventName(FormEventType.OnReadyStateComplete), StringComparison.OrdinalIgnoreCase)
                                            && e.Element("Handlers")?.Elements("Handler").Any() == true) == true)
            .Select(control => control.Attribute("id")?.Value)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Select(NormalizeIframeControlId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // R8: lets the planner detect a bulk-edit-only change (no handler/order change on the onload event
    // itself) by comparing the freshly-computed union against what's already live — without this, a push
    // that only adds/removes [bulkEdit] with no other onload change would never emit a plan entry, since
    // the handlersChanged gate has nothing else to trigger on.
    public static bool IsBulkEditEnabled(XDocument form) =>
        FindEvent(form, FormEventType.OnLoad)?.Attribute("BehaviorInBulkEditForm")?.Value == "Enabled";

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

    // attribute carries the scope token for OnChange/TabStateChange/OnReadyStateComplete — see GetHandlers.
    // desired is an ordered list (KTD2): callers must de-duplicate by identity without discarding position
    // (the write order here is exactly the order handlers are enumerated in, no internal re-sorting).
    // bulkEditEnabled only applies to OnLoad (KTD4) — sets/clears BehaviorInBulkEditForm on the located
    // onload <event> element; ignored for every other event type.
    public static void SetHandlers(XDocument form, FormEventType evt, IReadOnlyList<FormEventHandler> desired, string? attribute = null, bool bulkEditEnabled = false)
    {
        var root = form.Root ?? throw new InvalidOperationException("Form XML has no root element.");

        var eventElement = FindEvent(form, evt, attribute);
        if (eventElement is null)
        {
            eventElement = CreateEventElement(evt, attribute);
            var eventsElement = GetOrAddEventsContainer(root, evt, attribute);
            eventsElement.Add(eventElement);
        }

        // SetAttributeValue with a null value removes the attribute — this is how a previously-set
        // BehaviorInBulkEditForm gets cleared when no onload annotation specifies [bulkEdit] anymore (R8).
        if (evt == FormEventType.OnLoad)
            eventElement.SetAttributeValue("BehaviorInBulkEditForm", bulkEditEnabled ? "Enabled" : null);

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

    // Locates (or creates) the <events> container this evt/attribute pair writes into: the form root for
    // OnLoad/OnSave/OnChange (unchanged), or the Tab/IFRAME container's own nested <events> child (KTD1).
    // Throws if a Tab/IFRAME scope token doesn't resolve to any live container — planning enumerates scopes
    // from existing FormXml/annotations, so an unresolvable scope here means the annotation references a
    // tab/control the current form genuinely doesn't have; synthesizing one is out of scope for this plan
    // (Scope Boundaries: rename/not-found resilience for Tab/IFRAME is deferred).
    static XElement GetOrAddEventsContainer(XElement root, FormEventType evt, string? attribute)
    {
        var container = evt switch
        {
            FormEventType.TabStateChange => FindTabElement(root, attribute!)
                ?? throw new InvalidOperationException($"No tab named '{attribute}' found in form XML."),
            FormEventType.OnReadyStateComplete => FindIframeCell(root, attribute!)
                ?? throw new InvalidOperationException($"No IFRAME control '{attribute}' found in form XML."),
            _ => root
        };
        return GetOrAdd(container, "events");
    }

    static XElement CreateEventElement(FormEventType evt, string? attribute)
    {
        // Empirically verified defaults: onload/onsave use application="true" active="true"; onchange,
        // tabstatechange, and onreadystatecomplete use application="false" active="false" — onchange also
        // carries the attribute="<logicalname>" it's scoped to (Tab/IFRAME's scope lives in which container
        // holds the <events> element, not in an attribute on <event> itself).
        return evt switch
        {
            FormEventType.OnLoad or FormEventType.OnSave => new XElement("event",
                new XAttribute("name", EventName(evt)),
                new XAttribute("application", "true"),
                new XAttribute("active", "true")),
            FormEventType.OnChange => new XElement("event",
                new XAttribute("name", EventName(evt)),
                new XAttribute("application", "false"),
                new XAttribute("active", "false"),
                new XAttribute("attribute", attribute!)),
            _ => new XElement("event",
                new XAttribute("name", EventName(evt)),
                new XAttribute("application", "false"),
                new XAttribute("active", "false"))
        };
    }

    // Tab name match is case-insensitive, consistent with every other scope-token comparison in this file.
    static XElement? FindTabElement(XElement root, string tabName) =>
        root.Descendants("tab").FirstOrDefault(tab => string.Equals(tab.Attribute("name")?.Value, tabName, StringComparison.OrdinalIgnoreCase));

    // Live-verified shape: the IFRAME <control id="..." classid="{FD2A7985-...}"> and its sibling <events>
    // element are both direct children of the same <cell> — <events> is a sibling of <control>, not nested
    // inside it. controlId match is prefix-insensitive (NormalizeIframeControlId on both sides): accepts
    // either the bare maker-typed suffix or the fully-prefixed FormXml id. controlId is null-tolerant
    // (returns no match, same as the other Find* helpers' string.Equals-against-null behavior) since
    // GetHandlers/FindEvent are called with attribute: null for every FormEventType during generic,
    // event-agnostic sweeps (e.g. library-tracking inference), not just the scope-token-bearing ones.
    static XElement? FindIframeCell(XElement root, string? controlId)
    {
        if (controlId is null) return null;
        var normalized = NormalizeIframeControlId(controlId);
        return root.Descendants("control")
            .FirstOrDefault(c => string.Equals(c.Attribute("classid")?.Value, IframeClassId, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(NormalizeIframeControlId(c.Attribute("id")?.Value ?? ""), normalized, StringComparison.OrdinalIgnoreCase))
            ?.Parent;
    }

    // Existence checks the planner uses to validate a Tab/IFRAME scope *before* SetHandlers ever runs —
    // GetOrAddEventsContainer throws when a scope doesn't resolve, so a plan-time check lets the planner
    // report a clean per-annotation FlowlineException instead of an unhandled exception surfacing deep
    // inside the executor and aborting the entire push.
    public static bool TabExists(XDocument form, string tabName) =>
        form.Root is not null && FindTabElement(form.Root, tabName) is not null;

    public static bool IframeControlExists(XDocument form, string controlId) =>
        form.Root is not null && FindIframeCell(form.Root, controlId) is not null;

    // attribute carries the scope token for OnChange/TabStateChange/OnReadyStateComplete (KTD1): OnChange
    // additionally matches the "attribute" XML attribute on a form-root <event> element; TabStateChange and
    // OnReadyStateComplete instead resolve their container first (FindTabElement/FindIframeCell) and match
    // by name only within that container's own nested <events> — the container itself is the scope, so no
    // attribute-token match is needed on the <event> element there. Null (OnLoad/OnSave) preserves the
    // original form-root, name-only match.
    static XElement? FindEvent(XDocument form, FormEventType evt, string? attribute = null)
    {
        var eventName = EventName(evt);

        XElement? container = evt switch
        {
            FormEventType.TabStateChange => form.Root is null ? null : FindTabElement(form.Root, attribute!),
            FormEventType.OnReadyStateComplete => form.Root is null ? null : FindIframeCell(form.Root, attribute!),
            _ => form.Root
        };

        return container?
            .Element("events")?
            .Elements("event")
            .FirstOrDefault(e =>
                string.Equals(e.Attribute("name")?.Value, eventName, StringComparison.OrdinalIgnoreCase)
                && (evt != FormEventType.OnChange || attribute is null || string.Equals(e.Attribute("attribute")?.Value, attribute, StringComparison.OrdinalIgnoreCase)));
    }

    static string EventName(FormEventType evt) => evt.ToString().ToLowerInvariant();
}
