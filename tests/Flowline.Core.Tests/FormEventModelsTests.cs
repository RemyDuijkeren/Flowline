using Flowline.Core.Models;

namespace Flowline.Core.Tests;

public class FormEventModelsTests
{
    // --- FormEventDeterministicId.ForHandler ---

    [Fact]
    public void ForHandler_SameInputs_ReturnsSameGuid()
    {
        var first = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var second = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForHandler_DifferentFunctionName_ReturnsDifferentGuid()
    {
        var first = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var second = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoadOther", "av_/lib.js");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForHandler_CaseDifference_ReturnsSameGuid()
    {
        var first = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var second = FormEventDeterministicId.ForHandler("ACCOUNT", "MAIN", FormEventType.OnLoad, "ONLOAD", "AV_/LIB.JS");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForHandler_FieldBoundaryShift_ReturnsDifferentGuid()
    {
        // Guards the length-prefixed key format (R17): a bare delimiter-less join would let
        // characters shift across field boundaries and collide (e.g. "account"+"main" vs
        // "accountmain"+""). Length-prefixing each part closes that ambiguity.
        var first = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "f", "l");
        var second = FormEventDeterministicId.ForHandler("accountmain", "", FormEventType.OnLoad, "f", "l");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForHandler_OnChangeWithAttribute_ReturnsDifferentGuidThanOnLoad()
    {
        var onLoad = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var onChange = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onLoad", "av_/lib.js", "creditlimit");

        Assert.NotEqual(onLoad, onChange);
    }

    [Fact]
    public void ForHandler_OnChangeDifferentAttribute_ReturnsDifferentGuid()
    {
        var creditLimit = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onChange", "av_/lib.js", "creditlimit");
        var revenue = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onChange", "av_/lib.js", "revenue");

        Assert.NotEqual(creditLimit, revenue);
    }

    [Fact]
    public void ForHandler_OnChangeAttributeCaseDifference_ReturnsSameGuid()
    {
        var lower = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onChange", "av_/lib.js", "creditlimit");
        var upper = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onChange", "av_/lib.js", "Creditlimit");

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void ForHandler_OnLoadWithNullAttribute_MatchesPreChangeOutput()
    {
        var withNullAttribute = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js", attribute: null);
        var withoutAttributeParam = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnLoad, "onLoad", "av_/lib.js");

        Assert.Equal(withoutAttributeParam, withNullAttribute);
    }

    [Fact]
    public void ForHandler_SameScopeStringDifferentEventType_ReturnsDifferentGuid()
    {
        // "Summary" as a tab name, an IFRAME control id, and an onchange attribute must never collide —
        // FormEventType is already part of the hash key, so distinct enum members guarantee distinctness
        // even though all three now share the same `attribute` hash dimension.
        var tab = FormEventDeterministicId.ForHandler("account", "main", FormEventType.TabStateChange, "onSummaryTabStateChange", "av_/lib.js", "Summary");
        var iframe = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnReadyStateComplete, "onSummaryTabStateChange", "av_/lib.js", "Summary");
        var onChange = FormEventDeterministicId.ForHandler("account", "main", FormEventType.OnChange, "onSummaryTabStateChange", "av_/lib.js", "Summary");

        Assert.NotEqual(tab, iframe);
        Assert.NotEqual(tab, onChange);
        Assert.NotEqual(iframe, onChange);
    }

    // --- FormEventDeterministicId.ForLibrary ---

    [Fact]
    public void ForLibrary_SameInputs_ReturnsSameGuid()
    {
        var first = FormEventDeterministicId.ForLibrary("av_/lib.js");
        var second = FormEventDeterministicId.ForLibrary("av_/lib.js");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForLibrary_CaseDifference_ReturnsSameGuid()
    {
        var first = FormEventDeterministicId.ForLibrary("av_/lib.js");
        var second = FormEventDeterministicId.ForLibrary("AV_/LIB.JS");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForLibrary_IndependentOfEntityOrForm_ReturnsSameGuid()
    {
        // ForLibrary only takes the library name — identity must not depend on where it's referenced from.
        var fromAccountMain = FormEventDeterministicId.ForLibrary("av_/lib.js");
        var fromContactOther = FormEventDeterministicId.ForLibrary("av_/lib.js");

        Assert.Equal(fromAccountMain, fromContactOther);
    }

    // --- FormEventHandler equality / hashing ---

    [Fact]
    public void FormEventHandler_SameIdentityKey_TreatedAsEqualInHashSet()
    {
        var set = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/lib.js", Guid.NewGuid(), "context")
        };

        var added = set.Add(new FormEventHandler("ONLOAD", "AV_/LIB.JS", Guid.NewGuid(), "differentParams"));

        Assert.False(added);
        Assert.Single(set);
    }

    // --- FormLibrary equality / hashing ---

    [Fact]
    public void FormLibrary_SameIdentityKey_TreatedAsEqualInHashSet()
    {
        var set = new HashSet<FormLibrary>
        {
            new("av_/lib.js", Guid.NewGuid())
        };

        var added = set.Add(new FormLibrary("AV_/LIB.JS", Guid.NewGuid()));

        Assert.False(added);
        Assert.Single(set);
    }

    // --- FormEventType ---

    [Fact]
    public void FormEventType_ContainsFiveMembersIncludingTabAndIframe()
    {
        var members = Enum.GetValues<FormEventType>();

        Assert.Equal(5, members.Length);
        Assert.Contains(FormEventType.TabStateChange, members);
        Assert.Contains(FormEventType.OnReadyStateComplete, members);
    }

    // --- FormEventAnnotation modifiers ---

    [Fact]
    public void FormEventAnnotation_DefaultConstructed_HasFalseBulkEditAndNullOrder()
    {
        var annotation = new FormEventAnnotation("account", "main", FormEventType.OnLoad, "onLoad", null);

        Assert.False(annotation.BulkEdit);
        Assert.Null(annotation.Order);
    }

    [Fact]
    public void FormEventAnnotation_WithBulkEditAndOrder_RoundTripsThroughDeconstruction()
    {
        var annotation = new FormEventAnnotation("account", "main", FormEventType.OnLoad, "onLoad", null, BulkEdit: true, Order: 5);

        Assert.True(annotation.BulkEdit);
        Assert.Equal(5, annotation.Order);
    }

    // --- FormEventFormPlan.DesiredHandlers ordering ---

    [Fact]
    public void FormEventFormPlan_DesiredHandlers_PreservesConstructedOrder()
    {
        IReadOnlyList<FormEventHandler> ordered =
        [
            new("First", "av_/lib.js", Guid.NewGuid(), ""),
            new("Second", "av_/lib.js", Guid.NewGuid(), ""),
            new("Third", "av_/lib.js", Guid.NewGuid(), "")
        ];

        var plan = new FormEventFormPlan(
            Guid.NewGuid(), "account", "main", FormEventType.OnLoad,
            ordered, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        Assert.Equal(["First", "Second", "Third"], plan.DesiredHandlers.Select(h => h.FunctionName));
    }
}
