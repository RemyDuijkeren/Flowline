using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Tests;

public class FormXmlEventSerializerTests
{
    const string NoEventsFormXml =
        """<form><tabs><tab name="tab1"/></tabs></form>""";

    const string InternalAndPublicHandlersFormXml =
        """
        <form>
          <events>
            <event name="onload" application="true" active="true">
              <InternalHandlers>
                <Handler functionName="Internal.func" libraryName="internal/lib.js" handlerUniqueId="{11111111-1111-1111-1111-111111111111}" enabled="true"/>
              </InternalHandlers>
              <Handlers>
                <Handler functionName="Example1.onLoad" libraryName="av_ns/example1.js" handlerUniqueId="{22222222-2222-2222-2222-222222222222}" enabled="true" parameters="" passExecutionContext="true"/>
              </Handlers>
            </event>
          </events>
        </form>
        """;

    const string RealisticFormXml =
        """
        <form>
          <tabs>
            <tab name="tab1">
              <sections>
                <section name="section1">
                  <rows>
                    <row><cell><control id="control1"/></cell></row>
                  </rows>
                </section>
              </sections>
            </tab>
          </tabs>
          <formLibraries>
            <Library name="av_ns/existing.js" libraryUniqueId="{33333333-3333-3333-3333-333333333333}"/>
          </formLibraries>
          <events>
            <event name="onload" application="true" active="true">
              <InternalHandlers>
                <Handler functionName="Internal.func" libraryName="internal/lib.js" handlerUniqueId="{11111111-1111-1111-1111-111111111111}" enabled="true"/>
              </InternalHandlers>
              <Handlers>
                <Handler functionName="Old.onLoad" libraryName="av_ns/old.js" handlerUniqueId="{22222222-2222-2222-2222-222222222222}" enabled="true" parameters="" passExecutionContext="true"/>
              </Handlers>
            </event>
            <event name="onsave" application="true" active="true">
              <Handlers>
                <Handler functionName="Example1.onSave" libraryName="av_ns/example1.js" handlerUniqueId="{44444444-4444-4444-4444-444444444444}" enabled="true" parameters="ctx" passExecutionContext="true"/>
              </Handlers>
            </event>
          </events>
        </form>
        """;

    // --- GetHandlers ---

    [Fact]
    public void GetHandlers_NoEventsElement_ReturnsEmptySet()
    {
        var doc = XDocument.Parse(NoEventsFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnLoad);

        Assert.Empty(result);
    }

    [Fact]
    public void GetHandlers_ExcludesInternalHandlers_ReturnsOnlyHandlers()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnLoad);

        Assert.Single(result);
        var handler = result.Single();
        Assert.Equal("Example1.onLoad", handler.FunctionName);
        Assert.Equal("av_ns/example1.js", handler.LibraryName);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), handler.HandlerUniqueId);
    }

    [Fact]
    public void GetHandlers_RequestedEventMissingButOtherEventPresent_ReturnsEmptySet()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnSave);

        Assert.Empty(result);
    }

    // --- GetLibraries ---

    [Fact]
    public void GetLibraries_NoFormLibrariesElement_ReturnsEmptySet()
    {
        var doc = XDocument.Parse(NoEventsFormXml);

        var result = FormXmlEventSerializer.GetLibraries(doc);

        Assert.Empty(result);
    }

    [Fact]
    public void GetLibraries_ExistingLibrary_ReturnsEntry()
    {
        var doc = XDocument.Parse(RealisticFormXml);

        var result = FormXmlEventSerializer.GetLibraries(doc);

        Assert.Single(result);
        var lib = result.Single();
        Assert.Equal("av_ns/existing.js", lib.Name);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), lib.LibraryUniqueId);
    }

    // --- SetHandlers ---

    [Fact]
    public void SetHandlers_NoEventsElement_CreatesFullSubtree()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var handlerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var desired = new HashSet<FormHandler> { new("Example1.onLoad", "av_ns/example1.js", handlerId, "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var eventsElement = doc.Root!.Element("events");
        Assert.NotNull(eventsElement);
        var eventElement = eventsElement!.Element("event");
        Assert.NotNull(eventElement);
        Assert.Equal("onload", eventElement!.Attribute("name")?.Value);
        Assert.Equal("true", eventElement.Attribute("application")?.Value);
        Assert.Equal("true", eventElement.Attribute("active")?.Value);
        var handlersElement = eventElement.Element("Handlers");
        Assert.NotNull(handlersElement);
        var handlerElement = Assert.Single(handlersElement!.Elements("Handler"));
        Assert.Equal("Example1.onLoad", handlerElement.Attribute("functionName")?.Value);
        Assert.Equal("av_ns/example1.js", handlerElement.Attribute("libraryName")?.Value);
        Assert.Equal("{55555555-5555-5555-5555-555555555555}", handlerElement.Attribute("handlerUniqueId")?.Value);
        Assert.Equal("true", handlerElement.Attribute("enabled")?.Value);
        Assert.Equal("", handlerElement.Attribute("parameters")?.Value);
        Assert.Equal("true", handlerElement.Attribute("passExecutionContext")?.Value);
    }

    [Fact]
    public void SetHandlers_ExistingInternalHandlers_LeavesInternalHandlersUnchanged()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);
        var originalInternalHandlers = doc.Root!.Element("events")!.Element("event")!.Element("InternalHandlers")!.ToString();
        var desired = new HashSet<FormHandler> { new("New.onLoad", "av_ns/new.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var internalHandlersAfter = doc.Root!.Element("events")!.Element("event")!.Element("InternalHandlers")!.ToString();
        Assert.Equal(originalInternalHandlers, internalHandlersAfter);

        var handlersElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers");
        var handlerElement = Assert.Single(handlersElement!.Elements("Handler"));
        Assert.Equal("New.onLoad", handlerElement.Attribute("functionName")?.Value);
    }

    [Fact]
    public void SetHandlers_RemovesExistingHandlersBeforeAddingDesired()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);
        var desired = new HashSet<FormHandler>
        {
            new("A.onLoad", "av_ns/a.js", Guid.NewGuid(), ""),
            new("B.onLoad", "av_ns/b.js", Guid.NewGuid(), "")
        };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var handlersElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers")!;
        Assert.Equal(2, handlersElement.Elements("Handler").Count());
        Assert.DoesNotContain(handlersElement.Elements("Handler"), h => h.Attribute("functionName")?.Value == "Example1.onLoad");
    }

    [Fact]
    public void SetHandlers_OtherEventUntouched_AndRestOfFormUnchanged()
    {
        var doc = XDocument.Parse(RealisticFormXml);
        var onSaveEventBefore = doc.Root!.Element("events")!.Elements("event")
            .First(e => e.Attribute("name")?.Value == "onsave").ToString();
        var tabsBefore = doc.Root!.Element("tabs")!.ToString();
        var desired = new HashSet<FormHandler> { new("New.onLoad", "av_ns/new.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var onSaveEventAfter = doc.Root!.Element("events")!.Elements("event")
            .First(e => e.Attribute("name")?.Value == "onsave").ToString();
        var tabsAfter = doc.Root!.Element("tabs")!.ToString();
        Assert.Equal(onSaveEventBefore, onSaveEventAfter);
        Assert.Equal(tabsBefore, tabsAfter);
    }

    // --- SetLibraries ---

    [Fact]
    public void SetLibraries_NoFormLibrariesElement_CreatesFresh()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var libraryId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var desired = new HashSet<FormLibraryEntry> { new("av_ns/example1.js", libraryId) };

        FormXmlEventSerializer.SetLibraries(doc, desired);

        var formLibrariesElement = doc.Root!.Element("formLibraries");
        Assert.NotNull(formLibrariesElement);
        var libraryElement = Assert.Single(formLibrariesElement!.Elements("Library"));
        Assert.Equal("av_ns/example1.js", libraryElement.Attribute("name")?.Value);
        Assert.Equal("{66666666-6666-6666-6666-666666666666}", libraryElement.Attribute("libraryUniqueId")?.Value);
    }

    [Fact]
    public void SetLibraries_ReplacesExistingLibraries_RestOfFormUnchanged()
    {
        var doc = XDocument.Parse(RealisticFormXml);
        var tabsBefore = doc.Root!.Element("tabs")!.ToString();
        var eventsBefore = doc.Root!.Element("events")!.ToString();
        var desired = new HashSet<FormLibraryEntry> { new("av_ns/new.js", Guid.NewGuid()) };

        FormXmlEventSerializer.SetLibraries(doc, desired);

        var formLibrariesElement = doc.Root!.Element("formLibraries")!;
        var libraryElement = Assert.Single(formLibrariesElement.Elements("Library"));
        Assert.Equal("av_ns/new.js", libraryElement.Attribute("name")?.Value);
        Assert.Equal(tabsBefore, doc.Root!.Element("tabs")!.ToString());
        Assert.Equal(eventsBefore, doc.Root!.Element("events")!.ToString());
    }

    // --- Round trip ---

    [Fact]
    public void RoundTrip_SetHandlersAndLibraries_ProducesExactAttributeShape()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);
        var handlerId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var libraryId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad,
            new HashSet<FormHandler> { new("Example1.onLoad", "av_ns/example1.js", handlerId, "") });
        FormXmlEventSerializer.SetLibraries(doc,
            new HashSet<FormLibraryEntry> { new("av_ns/example1.js", libraryId) });

        var handlerElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers")!.Element("Handler")!;
        Assert.Equal("Example1.onLoad", handlerElement.Attribute("functionName")?.Value);
        Assert.Equal("av_ns/example1.js", handlerElement.Attribute("libraryName")?.Value);
        Assert.Equal("{77777777-7777-7777-7777-777777777777}", handlerElement.Attribute("handlerUniqueId")?.Value);
        Assert.Equal("true", handlerElement.Attribute("enabled")?.Value);
        Assert.Equal("", handlerElement.Attribute("parameters")?.Value);
        Assert.Equal("true", handlerElement.Attribute("passExecutionContext")?.Value);

        var libraryElement = doc.Root!.Element("formLibraries")!.Element("Library")!;
        Assert.Equal("av_ns/example1.js", libraryElement.Attribute("name")?.Value);
        Assert.Equal("{88888888-8888-8888-8888-888888888888}", libraryElement.Attribute("libraryUniqueId")?.Value);

        // Locks the exact attribute set/order XLinq renders for a written Handler/Library element.
        // Note: XLinq always renders empty elements with a space before "/>" (`<Handler .../>` with a
        // leading space) regardless of SaveOptions.DisableFormatting - this differs cosmetically from the
        // ground-truth sample in the plan doc, which is whitespace-insignificant XML either way. Any exact
        // byte-for-byte comparison against portal-exported formxml is a caller/serialization concern, not
        // this unit's.
        Assert.Equal(
            """<Handler functionName="Example1.onLoad" libraryName="av_ns/example1.js" handlerUniqueId="{77777777-7777-7777-7777-777777777777}" enabled="true" parameters="" passExecutionContext="true" />""",
            handlerElement.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(
            """<Library name="av_ns/example1.js" libraryUniqueId="{88888888-8888-8888-8888-888888888888}" />""",
            libraryElement.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void SetHandlers_EmptyDesiredSet_LeavesEmptyHandlersElement()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, new HashSet<FormHandler>());

        var handlersElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers");
        Assert.NotNull(handlersElement);
        Assert.Empty(handlersElement!.Elements("Handler"));
    }

    [Fact]
    public void SetLibraries_EmptyDesiredSet_LeavesEmptyFormLibrariesElement()
    {
        var doc = XDocument.Parse(RealisticFormXml);

        FormXmlEventSerializer.SetLibraries(doc, new HashSet<FormLibraryEntry>());

        var formLibrariesElement = doc.Root!.Element("formLibraries");
        Assert.NotNull(formLibrariesElement);
        Assert.Empty(formLibrariesElement!.Elements("Library"));
    }
}
