using Flowline.Core.Services;

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
    public void Resolve_ExplicitDottedName_StructureConfirmsNestedPathExists_FoundConfidentNoWarning()
    {
        const string builtJs = "function OnLoad(executionContext) {} exports.OnLoad = OnLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "MyCompany.Example1.OnLoad", "Example1", isExplicit: true);

        Assert.True(found);
        Assert.True(confident);
        Assert.Equal("MyCompany.Example1.OnLoad", functionName);
    }

    [Fact]
    public void Resolve_ExplicitDottedName_ProvablyAbsentFromFullyTracedShape_HardFailOutcome()
    {
        const string builtJs = "function onLoad(executionContext) {} exports.onLoad = onLoad;";

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "MyCompany.Example1.NonExistentFunction", "Example1", isExplicit: true);

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
            var MyCompany = MyCompany || {};
            MyCompany.Example1 = MyCompany.Example1 || {};
            MyCompany.Example1.OnLoad = function(executionContext) { };
            """;

        var (functionName, found, confident) = FormEventFunctionResolver.Resolve(
            builtJs, "MyCompany.Example1.OnLoad", "Example1", isExplicit: true);

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
