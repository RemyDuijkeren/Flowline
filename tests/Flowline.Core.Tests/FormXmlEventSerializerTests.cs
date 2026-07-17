using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.Services.FormEvents.Support;

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
        var desired = new List<FormEventHandler> { new("Example1.onLoad", "av_ns/example1.js", handlerId, "") };

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
        var desired = new List<FormEventHandler> { new("New.onLoad", "av_ns/new.js", Guid.NewGuid(), "") };

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
        var desired = new List<FormEventHandler>
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
        var desired = new List<FormEventHandler> { new("New.onLoad", "av_ns/new.js", Guid.NewGuid(), "") };

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
        var desired = new HashSet<FormLibrary> { new("av_ns/example1.js", libraryId) };

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
        var desired = new HashSet<FormLibrary> { new("av_ns/new.js", Guid.NewGuid()) };

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
            new List<FormEventHandler> { new("Example1.onLoad", "av_ns/example1.js", handlerId, "") });
        FormXmlEventSerializer.SetLibraries(doc,
            new HashSet<FormLibrary> { new("av_ns/example1.js", libraryId) });

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

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, new List<FormEventHandler>());

        var handlersElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers");
        Assert.NotNull(handlersElement);
        Assert.Empty(handlersElement!.Elements("Handler"));
    }

    [Fact]
    public void SetLibraries_EmptyDesiredSet_RemovesFormLibrariesElementEntirely()
    {
        // Regression: a live push failed schema validation — "The element 'formLibraries' has incomplete
        // content. List of possible elements expected: 'Library'." — an empty <formLibraries></formLibraries>
        // (unlike an empty <Handlers />, which is valid) is rejected by Dataverse whenever the element is
        // present at all, so it must be removed outright rather than left as an empty stub.
        var doc = XDocument.Parse(RealisticFormXml);

        FormXmlEventSerializer.SetLibraries(doc, new HashSet<FormLibrary>());

        Assert.Null(doc.Root!.Element("formLibraries"));
    }

    // --- onchange ---

    const string TwoOnChangeAttributesFormXml =
        """
        <form>
          <events>
            <event name="onchange" application="false" active="false" attribute="creditlimit">
              <Handlers>
                <Handler functionName="Example1.onCreditLimitChange" libraryName="av_ns/example1.js" handlerUniqueId="{99999999-9999-9999-9999-999999999999}" enabled="true" parameters="" passExecutionContext="true"/>
              </Handlers>
            </event>
            <event name="onchange" application="false" active="false" attribute="revenue">
              <Handlers>
                <Handler functionName="Example1.onRevenueChange" libraryName="av_ns/example1.js" handlerUniqueId="{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}" enabled="true" parameters="" passExecutionContext="true"/>
              </Handlers>
            </event>
          </events>
        </form>
        """;

    [Fact]
    public void GetHandlers_OnChangeNoEventsElement_ReturnsEmptySet()
    {
        var doc = XDocument.Parse(NoEventsFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnChange, "creditlimit");

        Assert.Empty(result);
    }

    [Fact]
    public void GetHandlers_OnChangeMatchingAttribute_ReturnsHandlerSet()
    {
        var doc = XDocument.Parse(TwoOnChangeAttributesFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnChange, "creditlimit");

        var handler = Assert.Single(result);
        Assert.Equal("Example1.onCreditLimitChange", handler.FunctionName);
    }

    [Fact]
    public void GetHandlers_OnChangeDifferentAttribute_DoesNotReturnOtherAttributesHandlers()
    {
        var doc = XDocument.Parse(TwoOnChangeAttributesFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnChange, "revenue");

        var handler = Assert.Single(result);
        Assert.Equal("Example1.onRevenueChange", handler.FunctionName);
    }

    [Fact]
    public void GetHandlers_OnLoadWithoutAttributeParameter_StillWorksUnchanged()
    {
        var doc = XDocument.Parse(InternalAndPublicHandlersFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnLoad);

        Assert.Single(result);
    }

    [Fact]
    public void SetHandlers_OnChangeNoEventsElement_CreatesEventWithFalseDefaultsAndAttribute()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var handlerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var desired = new List<FormEventHandler> { new("Example1.onCreditLimitChange", "av_ns/example1.js", handlerId, "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnChange, desired, "creditlimit");

        var eventElement = doc.Root!.Element("events")!.Element("event")!;
        Assert.Equal("onchange", eventElement.Attribute("name")?.Value);
        Assert.Equal("false", eventElement.Attribute("application")?.Value);
        Assert.Equal("false", eventElement.Attribute("active")?.Value);
        Assert.Equal("creditlimit", eventElement.Attribute("attribute")?.Value);
        var handlerElement = Assert.Single(eventElement.Element("Handlers")!.Elements("Handler"));
        Assert.Equal("Example1.onCreditLimitChange", handlerElement.Attribute("functionName")?.Value);
    }

    [Fact]
    public void SetHandlers_OnChangeExistingOtherAttributeEvent_OnlyModifiesTargetedAttributeEvent()
    {
        var doc = XDocument.Parse(TwoOnChangeAttributesFormXml);
        var revenueEventBefore = doc.Root!.Element("events")!.Elements("event")
            .First(e => e.Attribute("attribute")?.Value == "revenue").ToString();
        var desired = new List<FormEventHandler> { new("Example1.onCreditLimitChangeV2", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnChange, desired, "creditlimit");

        var revenueEventAfter = doc.Root!.Element("events")!.Elements("event")
            .First(e => e.Attribute("attribute")?.Value == "revenue").ToString();
        Assert.Equal(revenueEventBefore, revenueEventAfter);

        var creditLimitEvent = doc.Root!.Element("events")!.Elements("event")
            .First(e => e.Attribute("attribute")?.Value == "creditlimit");
        var handlerElement = Assert.Single(creditLimitEvent.Element("Handlers")!.Elements("Handler"));
        Assert.Equal("Example1.onCreditLimitChangeV2", handlerElement.Attribute("functionName")?.Value);
    }

    [Fact]
    public void SetHandlers_OnLoadUnaffectedByAttributeParameter_DefaultsStayTrueTrue()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler> { new("Example1.onLoad", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var eventElement = doc.Root!.Element("events")!.Element("event")!;
        Assert.Equal("true", eventElement.Attribute("application")?.Value);
        Assert.Equal("true", eventElement.Attribute("active")?.Value);
        Assert.Null(eventElement.Attribute("attribute"));
    }

    [Fact]
    public void GetOnChangeAttributes_TwoOnChangeEventElements_ReturnsBothAttributeNames()
    {
        var doc = XDocument.Parse(TwoOnChangeAttributesFormXml);

        var result = FormXmlEventSerializer.GetOnChangeAttributes(doc);

        Assert.True(result.SetEquals(new[] { "creditlimit", "revenue" }));
    }

    [Fact]
    public void GetOnChangeAttributes_NoOnChangeEvents_ReturnsEmptySet()
    {
        var doc = XDocument.Parse(RealisticFormXml);

        var result = FormXmlEventSerializer.GetOnChangeAttributes(doc);

        Assert.Empty(result);
    }

    [Fact]
    public void RoundTrip_SetHandlersOnChangeThenGetHandlers_ReturnsOriginalDesiredSet()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler>
        {
            new("Example1.onCreditLimitChange", "av_ns/example1.js", Guid.NewGuid(), "")
        };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnChange, desired, "creditlimit");
        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnChange, "creditlimit");

        Assert.Equal(desired, result);
    }

    [Fact]
    public void SetHandlers_OnChange_HandlerAttributeOrderMatchesEmpiricalShape()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var handlerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var desired = new List<FormEventHandler> { new("Example1.onCreditLimitChange", "av_ns/example1.js", handlerId, "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnChange, desired, "creditlimit");

        var handlerElement = doc.Root!.Element("events")!.Element("event")!.Element("Handlers")!.Element("Handler")!;
        Assert.Equal(
            """<Handler functionName="Example1.onCreditLimitChange" libraryName="av_ns/example1.js" handlerUniqueId="{cccccccc-cccc-cccc-cccc-cccccccccccc}" enabled="true" parameters="" passExecutionContext="true" />""",
            handlerElement.ToString(SaveOptions.DisableFormatting));
    }

    // --- tabstatechange (container-nested, live-verified shape) ---

    const string TwoTabsFormXml =
        """
        <form>
          <tabs>
            <tab name="Summary">
              <labels><label description="Summary" languagecode="1033" /></labels>
              <columns><column width="100%" /></columns>
            </tab>
            <tab name="Details">
              <labels><label description="Details" languagecode="1033" /></labels>
              <columns><column width="100%" /></columns>
            </tab>
          </tabs>
        </form>
        """;

    [Fact]
    public void SetHandlers_TabStateChangeNoExistingEvent_CreatesNestedEventsInsideNamedTab()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        var handlerId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var desired = new List<FormEventHandler> { new("onSummaryTabStateChange", "av_ns/example1.js", handlerId, "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "Summary");

        var summaryTab = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Summary");
        var eventElement = summaryTab.Element("events")!.Element("event")!;
        Assert.Equal("tabstatechange", eventElement.Attribute("name")?.Value);
        Assert.Equal("false", eventElement.Attribute("application")?.Value);
        Assert.Equal("false", eventElement.Attribute("active")?.Value);
        var handlerElement = Assert.Single(eventElement.Element("Handlers")!.Elements("Handler"));
        Assert.Equal("onSummaryTabStateChange", handlerElement.Attribute("functionName")?.Value);

        var detailsTab = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Details");
        Assert.Null(detailsTab.Element("events"));
    }

    [Fact]
    public void SetHandlers_TabStateChangeTwoTabsSameFunctionName_StayIsolatedPerTab()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        var desired = new List<FormEventHandler> { new("onTabStateChange", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "Summary");
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "Details");

        var summaryTab = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Summary");
        var detailsTab = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Details");
        Assert.NotNull(summaryTab.Element("events"));
        Assert.NotNull(detailsTab.Element("events"));
        Assert.NotSame(summaryTab.Element("events"), detailsTab.Element("events"));
    }

    [Fact]
    public void GetHandlers_TabStateChangeMatchingTab_ReturnsHandlerList()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange,
            [new("onSummaryTabStateChange", "av_ns/example1.js", Guid.NewGuid(), "")], "Summary");

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.TabStateChange, "Summary");

        var handler = Assert.Single(result);
        Assert.Equal("onSummaryTabStateChange", handler.FunctionName);
    }

    [Fact]
    public void GetHandlers_TabStateChangeNoMatchingTab_ReturnsEmptyList()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);

        var result = FormXmlEventSerializer.GetHandlers(doc, FormEventType.TabStateChange, "DoesNotExist");

        Assert.Empty(result);
    }

    [Fact]
    public void SetHandlers_TabStateChangeUnknownTabName_ThrowsInvalidOperationException()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        var desired = new List<FormEventHandler> { new("onX", "av_ns/example1.js", Guid.NewGuid(), "") };

        Assert.Throws<InvalidOperationException>(() =>
            FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "DoesNotExist"));
    }

    [Fact]
    public void GetTabNamesWithStateChangeHandlers_OneTabWithHandlers_ReturnsThatTabOnly()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange,
            [new("onSummaryTabStateChange", "av_ns/example1.js", Guid.NewGuid(), "")], "Summary");

        var result = FormXmlEventSerializer.GetTabNamesWithStateChangeHandlers(doc);

        Assert.True(result.SetEquals(new[] { "Summary" }));
    }

    // --- onreadystatecomplete (container-nested, live-verified shape) ---

    const string IframeCellNoEventsFormXml =
        """
        <form>
          <tabs>
            <tab name="Details">
              <columns><column width="100%">
                <sections><section name="s1">
                  <rows><row><cell id="cell1">
                    <control id="IFRAME_myFrame" classid="{FD2A7985-3187-444E-908D-6624B21F69C0}">
                      <parameters><Url>https://example.com</Url></parameters>
                    </control>
                  </cell></row></rows>
                </section></sections>
              </column></columns>
            </tab>
          </tabs>
        </form>
        """;

    const string IframeCellWithOnloadStubFormXml =
        """
        <form>
          <tabs>
            <tab name="Details">
              <columns><column width="100%">
                <sections><section name="s1">
                  <rows><row><cell id="cell1">
                    <control id="IFRAME_myFrame" classid="{FD2A7985-3187-444E-908D-6624B21F69C0}">
                      <parameters><Url>https://example.com</Url></parameters>
                    </control>
                    <events>
                      <event name="onload" application="false" active="false" />
                    </events>
                  </cell></row></rows>
                </section></sections>
              </column></columns>
            </tab>
          </tabs>
        </form>
        """;

    [Fact]
    public void SetHandlers_OnReadyStateCompleteNoExistingEvents_CreatesEventsSiblingOfControl()
    {
        var doc = XDocument.Parse(IframeCellNoEventsFormXml);
        var handlerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var desired = new List<FormEventHandler> { new("Test.onMyFrameReady", "av_ns/example1.js", handlerId, "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete, desired, "IFRAME_myFrame");

        var control = doc.Root!.Descendants("control").First(c => c.Attribute("id")?.Value == "IFRAME_myFrame");
        var cell = control.Parent!;
        var eventElement = cell.Element("events")!.Element("event")!;
        Assert.Equal("onreadystatecomplete", eventElement.Attribute("name")?.Value);
        Assert.Equal("false", eventElement.Attribute("application")?.Value);
        Assert.Equal("false", eventElement.Attribute("active")?.Value);
        var handlerElement = Assert.Single(eventElement.Element("Handlers")!.Elements("Handler"));
        Assert.Equal("Test.onMyFrameReady", handlerElement.Attribute("functionName")?.Value);
    }

    [Fact]
    public void SetHandlers_OnReadyStateCompleteCellHasOnloadStub_StubLeftUntouched()
    {
        // AE7 / R4: the Maker-Portal-auto-generated empty onload stub next to an IFRAME control must
        // survive a write to that same cell's onreadystatecomplete event untouched.
        var doc = XDocument.Parse(IframeCellWithOnloadStubFormXml);
        var stubBefore = doc.Root!.Descendants("event").First(e => e.Attribute("name")?.Value == "onload").ToString();
        var desired = new List<FormEventHandler> { new("Test.onMyFrameReady", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete, desired, "IFRAME_myFrame");

        var stubAfter = doc.Root!.Descendants("event").First(e => e.Attribute("name")?.Value == "onload").ToString();
        Assert.Equal(stubBefore, stubAfter);
        var readyStateEvent = doc.Root!.Descendants("event").First(e => e.Attribute("name")?.Value == "onreadystatecomplete");
        Assert.Single(readyStateEvent.Element("Handlers")!.Elements("Handler"));
    }

    [Fact]
    public void SetHandlers_OnReadyStateCompleteUnknownControlId_ThrowsInvalidOperationException()
    {
        var doc = XDocument.Parse(IframeCellNoEventsFormXml);
        var desired = new List<FormEventHandler> { new("onX", "av_ns/example1.js", Guid.NewGuid(), "") };

        Assert.Throws<InvalidOperationException>(() =>
            FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete, desired, "IFRAME_doesNotExist"));
    }

    [Fact]
    public void GetIframeControlIdsWithReadyStateHandlers_OneControlWithHandlers_ReturnsThatControlOnlyAsBareId()
    {
        var doc = XDocument.Parse(IframeCellNoEventsFormXml);
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete,
            [new("Test.onMyFrameReady", "av_ns/example1.js", Guid.NewGuid(), "")], "IFRAME_myFrame");

        var result = FormXmlEventSerializer.GetIframeControlIdsWithReadyStateHandlers(doc);

        // Bare form (prefix stripped) is the canonical scope-token representation — see
        // FormXmlEventSerializer.NormalizeIframeControlId.
        Assert.True(result.SetEquals(new[] { "myFrame" }));
    }

    [Theory]
    [InlineData("IFRAME_myFrame")]
    [InlineData("myFrame")]
    [InlineData("iframe_myFrame")]
    public void SetHandlers_OnReadyStateComplete_ResolvesSameControl_RegardlessOfPrefixCasing(string controlIdToken)
    {
        var doc = XDocument.Parse(IframeCellNoEventsFormXml);

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete,
            [new("Test.onMyFrameReady", "av_ns/example1.js", Guid.NewGuid(), "")], controlIdToken);

        var handlers = FormXmlEventSerializer.GetHandlers(doc, FormEventType.OnReadyStateComplete, "IFRAME_myFrame");
        Assert.Single(handlers);
        Assert.Equal("Test.onMyFrameReady", handlers[0].FunctionName);
    }

    [Fact]
    public void RoundTrip_TabStateChangeSetThenGet_Idempotent()
    {
        var doc = XDocument.Parse(TwoTabsFormXml);
        var desired = new List<FormEventHandler> { new("onSummaryTabStateChange", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "Summary");
        var firstWrite = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Summary").ToString();
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.TabStateChange, desired, "Summary");
        var secondWrite = doc.Root!.Descendants("tab").First(t => t.Attribute("name")?.Value == "Summary").ToString();

        Assert.Equal(firstWrite, secondWrite);
    }

    [Fact]
    public void RoundTrip_OnReadyStateCompleteSetThenGet_Idempotent()
    {
        var doc = XDocument.Parse(IframeCellNoEventsFormXml);
        var desired = new List<FormEventHandler> { new("Test.onMyFrameReady", "av_ns/example1.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete, desired, "IFRAME_myFrame");
        var firstWrite = doc.Root!.Descendants("cell").First().ToString();
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnReadyStateComplete, desired, "IFRAME_myFrame");
        var secondWrite = doc.Root!.Descendants("cell").First().ToString();

        Assert.Equal(firstWrite, secondWrite);
    }

    // --- ordered writes (KTD2) ---

    [Fact]
    public void SetHandlers_MultipleHandlers_WrittenInSuppliedListOrder()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler>
        {
            new("Third", "av_ns/lib.js", Guid.NewGuid(), ""),
            new("First", "av_ns/lib.js", Guid.NewGuid(), ""),
            new("Second", "av_ns/lib.js", Guid.NewGuid(), "")
        };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired);

        var handlerNames = doc.Root!.Element("events")!.Element("event")!.Element("Handlers")!
            .Elements("Handler").Select(h => h.Attribute("functionName")?.Value);
        Assert.Equal(["Third", "First", "Second"], handlerNames);
    }

    // --- BehaviorInBulkEditForm (KTD4) ---

    [Fact]
    public void SetHandlers_OnLoadBulkEditEnabledTrue_SetsBehaviorInBulkEditFormAttribute()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler> { new("onLoad", "av_ns/lib.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired, bulkEditEnabled: true);

        var eventElement = doc.Root!.Element("events")!.Element("event")!;
        Assert.Equal("Enabled", eventElement.Attribute("BehaviorInBulkEditForm")?.Value);
    }

    [Fact]
    public void SetHandlers_OnLoadBulkEditEnabledFalseAfterPreviouslyEnabled_RemovesAttributeEntirely()
    {
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler> { new("onLoad", "av_ns/lib.js", Guid.NewGuid(), "") };
        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired, bulkEditEnabled: true);

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnLoad, desired, bulkEditEnabled: false);

        var eventElement = doc.Root!.Element("events")!.Element("event")!;
        Assert.Null(eventElement.Attribute("BehaviorInBulkEditForm"));
    }

    [Fact]
    public void SetHandlers_OnSaveBulkEditEnabledTrue_AttributeNotWritten()
    {
        // BehaviorInBulkEditForm is OnLoad-only (Scope Boundaries) — passing bulkEditEnabled for any other
        // event type must have no effect, even though the parser/model permit the modifier syntactically.
        var doc = XDocument.Parse(NoEventsFormXml);
        var desired = new List<FormEventHandler> { new("onSave", "av_ns/lib.js", Guid.NewGuid(), "") };

        FormXmlEventSerializer.SetHandlers(doc, FormEventType.OnSave, desired, bulkEditEnabled: true);

        var eventElement = doc.Root!.Element("events")!.Element("event")!;
        Assert.Null(eventElement.Attribute("BehaviorInBulkEditForm"));
    }
}
