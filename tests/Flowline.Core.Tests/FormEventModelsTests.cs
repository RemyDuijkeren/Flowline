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
}
