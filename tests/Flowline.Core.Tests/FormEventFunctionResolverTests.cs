using Flowline.Core.Services;
using Flowline.Core.Services.FormEvents.Support;

namespace Flowline.Core.Tests;

public class FormEventFunctionResolverTests
{
    [Fact]
    public void Resolve_NamespacedRollupStyle_DifferentCaseDefaulted_FoundWithRealCasingConfident()
    {
        const string builtJs = "function onLoad(executionContext) {} exports.onLoad = onLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onload", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Example1.onLoad", functionName);
    }

    [Fact]
    public void Resolve_RealRollupIifeShape_DefaultedGuess_FoundViaNestedExportsWalk()
    {
        // KTD4's actual empirical build shape: the exports.onLoad assignment sits nested inside the
        // IIFE's function body, not at Program top level, so topLevelDeclarations is empty here - a
        // pass proves resolution happens through the recursive exports walk, not the bare-declaration
        // fallback.
        const string builtJs =
            """
            (function (exports) {
                'use strict';
                function onLoad(executionContext) { }
                exports.onLoad = onLoad;
            })(this.Example1 = this.Example1 || {});
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onload", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Example1.onLoad", functionName);
    }

    [Fact]
    public void Resolve_IifeNamespaceHasConfiguredPrefix_DefaultedGuess_UsesRealMultiSegmentNamespaceNotFilenameGuess()
    {
        // Live bug: Rollup's namespacePrefix setting (rollup.config.mjs) makes the built IIFE assign to
        // a multi-segment global path (e.g. "this.Test.Example2 = ..."), but autoNamespace is always
        // just ToPascalCase(filename) - a single segment ("Example2"), never "Test.Example2". Resolve
        // must read the REAL namespace off the file's own IIFE invocation, not the filename guess, or
        // every defaulted handler from this file gets registered under a global path that doesn't
        // exist at runtime ("Example2.onLoad" instead of the real "Test.Example2.onLoad").
        const string builtJs =
            """
            this.Test = this.Test || {};
            (function (exports) {
                'use strict';
                function onLoad(executionContext) { }
                exports.onLoad = onLoad;
            })(this.Test.Example2 = this.Test.Example2 || {});
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example2", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Test.Example2.onLoad", functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_MatchesRealMultiSegmentIifeNamespace_FoundConfident()
    {
        const string builtJs =
            """
            this.Test = this.Test || {};
            (function (exports) {
                'use strict';
                function onLoad(executionContext) { }
                exports.onLoad = onLoad;
            })(this.Test.Example2 = this.Test.Example2 || {});
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Test.Example2.onLoad", "Example2", isExplicit: true);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Test.Example2.onLoad", functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_FilenameGuessAloneNoLongerMatchesConfiguredPrefixNamespace_HardFails()
    {
        // Guards against overcorrecting: once the real namespace is "Test.Example2", the bare filename
        // guess "Example2.onLoad" (missing the "Test." prefix a configured namespacePrefix adds) must
        // fail just as surely as an unrelated wrong prefix does - the real IIFE namespace is now the
        // sole source of truth, not a partial/fallback match against autoNamespace.
        const string builtJs =
            """
            this.Test = this.Test || {};
            (function (exports) {
                'use strict';
                function onLoad(executionContext) { }
                exports.onLoad = onLoad;
            })(this.Test.Example2 = this.Test.Example2 || {});
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example2.onLoad", "Example2", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_PascalCaseExports_DefaultedLowercaseGuess_FoundWithRealCasingFromFile()
    {
        const string builtJs = "function OnLoad(executionContext) {} exports.OnLoad = OnLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Example1.OnLoad", functionName);
    }

    [Fact]
    public void Resolve_VerbatimBareFunctionDeclaration_DefaultedNoDot_FoundViaBareFallbackConfident()
    {
        const string builtJs = "function onLoad(executionContext) { console.log('hi'); }";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("onLoad", functionName);
    }

    [Fact]
    public void Resolve_NeitherPatternPresent_Defaulted_NotFound()
    {
        const string builtJs = "function somethingElse() {}";

        var (functionName, found, _) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.False(found);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_VerbatimArrowConstDeclaration_DefaultedNoDot_Found()
    {
        const string builtJs = "const onLoad = (ctx) => { doStuff(ctx); };";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("onLoad", functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_PrefixMatchesThisFilesNamespace_FoundConfident()
    {
        const string builtJs = "function OnLoad(executionContext) {} exports.OnLoad = OnLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example1.OnLoad", "Example1", isExplicit: true);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Example1.OnLoad", functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_PrefixDoesNotMatchThisFilesNamespace_HardFailOutcome()
    {
        // A dotted prefix is not an arbitrary/organizational qualifier (e.g. "MyCompany.Example1") -
        // it must match this file's own auto-derived namespace, since a Handler's libraryName is
        // always the file the annotation lives in.
        const string builtJs = "function OnLoad(executionContext) {} exports.OnLoad = OnLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "MyCompany.Example1.OnLoad", "Example1", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_PrefixReferencesDifferentFilesNamespace_HardFailInsteadOfCrossFileMatch()
    {
        // Regression: "Example1.onload1" written inside example2.js (auto-derived namespace
        // "Example2") must not silently match this file's own same-named "onLoad1" export just
        // because the tail happens to line up - a wrong/typo'd prefix always fails, even when a
        // same-named export exists in this file.
        const string builtJs = "function onLoad1(executionContext) {} exports.onLoad1 = onLoad1;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example1.onload1", "Example2", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_PrefixMismatchOnUntraceableFile_AlwaysConfidentNeverInconclusive()
    {
        // Regression: a namespace-prefix mismatch is a positive, parser-independent determination
        // (pure string comparison) - it must always be confidently confirmed absent, even when the
        // rest of the file's export shape can't be traced (neither exports.X= nor a bare top-level
        // function/const - e.g. this verbatim IIFE with no known shape at all). Otherwise a mismatch
        // on an untraceable file gets misclassified as R7a's "inconclusive" outcome, which warns and
        // silently registers the wrong/typo'd dotted name instead of hard-failing.
        const string builtJs = "(function () { window.SomethingElse = {}; })();";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example1.onLoad", "Example2", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_ProvablyAbsentFromFullyTracedShape_HardFailOutcome()
    {
        const string builtJs = "function onLoad(executionContext) {} exports.onLoad = onLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example1.NonExistentFunction", "Example1", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_ExplicitBareName_ProvablyAbsentFromFullyTracedShape_HardFailOutcome()
    {
        const string builtJs = "function onLoad(executionContext) {} exports.onLoad = onLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "NonExistentFunction", "Example1", isExplicit: true);

        Assert.False(found);
        Assert.True(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_NamespaceObjectAssembledAcrossStatements_InconclusiveWarnOutcome()
    {
        const string builtJs =
            """
            var Example1 = Example1 || {};
            Example1.OnLoad = function(executionContext) { };
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "Example1.OnLoad", "Example1", isExplicit: true);

        Assert.False(found);
        Assert.False(confident);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_LookalikeCommentText_NotFound_ProvesRealParserIgnoresCommentContent()
    {
        const string builtJs =
            """
            // exports.onLoad = oldThing
            function somethingElse() {}
            """;

        var (functionName, found, _) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.False(found);
        Assert.Null(functionName);
    }

    [Fact]
    public void Resolve_HostGlobalsReferencedAtModuleScope_FoundNormallyNoError()
    {
        const string builtJs =
            """
            if (typeof window !== 'undefined') { console.log('browser'); }
            function onLoad(executionContext) {}
            exports.onLoad = onLoad;
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("Example1.onLoad", functionName);
    }

    [Fact]
    public void Resolve_NotValidJs_ResolvesToNotFoundInsteadOfThrowing()
    {
        const string builtJs = "function onLoad( { { this is not valid javascript at all !!!";

        var exception = Record.Exception(() => FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false));

        Assert.Null(exception);
        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(builtJs, "onLoad", "Example1", isExplicit: false);
        Assert.False(found);
        Assert.False(confident);
        Assert.Null(functionName);
    }
}
