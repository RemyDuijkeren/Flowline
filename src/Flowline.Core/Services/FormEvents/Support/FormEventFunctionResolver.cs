using Acornima;
using Acornima.Ast;

namespace Flowline.Core.Services.FormEvents.Support;

// Replaces regex-based function-existence checking (the old FormXmlEventSerializer.ResolveFunction)
// with a real structural JS parse (Acornima, no execution) - see KTD13/KTD4. A regex can be fooled by
// a comment or string literal that merely looks like an export ("// exports.onLoad = oldThing"); a
// structural parse never tokenizes comment/string content as executable statements.
public static class FormEventFunctionResolver
{
    // R7/R7a: resolves a form event handler's function name against a built web resource's actual
    // export surface.
    //   Namespace: the file's real global namespace is read structurally from its own IIFE invocation
    //   (Rollup's `})(this.Foo.Bar = this.Foo.Bar || {});` closing call) via ActualNamespace, not
    //   guessed from the filename - a project can set Rollup's namespacePrefix (or build with any other
    //   tool) and produce a multi-segment or otherwise-unpredictable global path; the built file itself
    //   is the only reliable source of truth for what path its exports actually land on. autoNamespace
    //   (filename-derived) is used only as a fallback for verbatim-mode files with no such wrapper.
    //   isExplicit=false (R4 default, requestedFunctionName is the auto-derived onLoad/onSave guess):
    //     try "<namespace>.<requestedFunctionName>" (Rollup-style exports.X= shape), then bare
    //     "<requestedFunctionName>" (verbatim shape). Found:false drives R7's hard fail; Confident is
    //     not meaningful for this branch.
    //   isExplicit=true (R7a, the user wrote the name): a dotted name's prefix must match THIS file's
    //     own namespace - a Handler's libraryName is always the file the annotation lives in, so a
    //     function it references can only ever live in that same file's own export surface, never a
    //     different file's. A mismatched prefix (wrong namespace, typo, or a reference into an
    //     unrelated file) is a hard fail even when the tail happens to match a same-named export in
    //     this file. Once the prefix matches, only the final segment is verified against the parsed
    //     export surface; a bare name is matched the same namespaced-then-bare way as the defaulted
    //     branch. Confident reports whether Acornima fully traced a *known* export shape for this file
    //     (an "exports.X =" assignment anywhere in the tree, or a verbatim top-level function/const-
    //     arrow declaration) - i.e. whether a reported absence is a positive determination rather than
    //     a shape the walk couldn't fully enumerate (e.g. a namespace object assembled across multiple
    //     statements). Found:true always implies Confident:true, since a match can only come from one
    //     of those two known, fully-enumerated shapes.
    public static (string? FunctionName, bool Found, bool Confident) Resolve(
        string builtJsContent, string requestedFunctionName, string autoNamespace, bool isExplicit)
    {
        Script script;
        try
        {
            script = new Parser().ParseScript(builtJsContent);
        }
        catch (ParseErrorException)
        {
            // Malformed/non-JS content should not occur in practice (it's build output) - fail closed
            // to Found:false instead of letting a parse exception surface as an unhandled stack trace.
            return (null, false, false);
        }

        var exportAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var topLevelDeclarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectExportAssignments(script, exportAssignments);
        CollectTopLevelDeclarations(script, topLevelDeclarations);

        // A match can only ever come from one of these two dictionaries, so whenever one of them is
        // non-empty the walk has fully traced at least one known shape for this file - that's exactly
        // what "Confident" needs to report, whether or not the requested name itself was among them.
        var confident = exportAssignments.Count > 0 || topLevelDeclarations.Count > 0;

        var namespaceName = FindActualNamespace(script) ?? autoNamespace;

        if (isExplicit)
        {
            var dotIndex = requestedFunctionName.LastIndexOf('.');
            if (dotIndex >= 0)
            {
                var namespacePrefix = requestedFunctionName[..dotIndex];
                var tail = requestedFunctionName[(dotIndex + 1)..];

                // A prefix mismatch is a positive, parser-independent determination (pure string
                // comparison) - it's always confidently confirmed absent, regardless of whether the
                // rest of the file's export shape was fully traceable.
                if (!string.Equals(namespacePrefix, namespaceName, StringComparison.OrdinalIgnoreCase))
                    return (null, false, true);

                if (exportAssignments.TryGetValue(tail, out var realTail) ||
                    topLevelDeclarations.TryGetValue(tail, out realTail))
                    return ($"{namespaceName}.{realTail}", true, confident);

                return (null, false, confident);
            }
        }

        if (exportAssignments.TryGetValue(requestedFunctionName, out var realNamespaced))
            return ($"{namespaceName}.{realNamespaced}", true, confident);

        if (topLevelDeclarations.TryGetValue(requestedFunctionName, out var realBare))
            return (realBare, true, confident);

        return (null, false, confident);
    }

    // Reads the file's real global namespace off its own closing IIFE invocation - Rollup (and any
    // other bundler using the same UMD-ish pattern) always assigns the shared namespace object as the
    // sole call argument: `})(this.Foo.Bar = this.Foo.Bar || {});`. Walking the assignment's left-hand
    // member-expression chain back to `this` yields the exact dotted path the exports were attached to,
    // however many segments a project's bundler configuration adds - no guessing from the filename, and
    // no reading the project's build config. Returns null when no such top-level pattern is found (e.g.
    // verbatim-mode files with no IIFE wrapper at all), letting the caller fall back to autoNamespace.
    static string? FindActualNamespace(Program program)
    {
        foreach (var statement in program.Body)
        {
            if (statement is not ExpressionStatement { Expression: CallExpression { Arguments.Count: > 0 } call })
                continue;

            if (call.Arguments[^1] is not AssignmentExpression { Left: MemberExpression } assignment)
                continue;

            var segments = new List<string>();
            Node current = assignment.Left;
            while (current is MemberExpression { Computed: false, Property: Identifier property } member)
            {
                segments.Insert(0, property.Name);
                current = member.Object;
            }

            if (current is ThisExpression && segments.Count > 0)
                return string.Join(".", segments);
        }

        return null;
    }

    // Shape A (KTD4): `exports.<Name> = ...` assignment nodes, found anywhere in the tree - this is the
    // shape Rollup's IIFE output produces, nested inside the closure's function body, not at Program
    // top level. Walked recursively via ChildNodes since nesting depth is not fixed.
    static void CollectExportAssignments(Node root, Dictionary<string, string> result)
    {
        if (root is ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment } assignment } &&
            assignment.Left is MemberExpression { Computed: false, Object: Identifier { Name: "exports" }, Property: Identifier property })
        {
            result.TryAdd(property.Name, property.Name);
        }

        foreach (var child in root.ChildNodes)
            CollectExportAssignments(child, result);
    }

    // Shape B: verbatim bare-global declarations - a `function <Name>(...)` or `const <Name> = (...) =>`
    // sitting directly at the top level (no `exports.` wrapper at all). Restricted to Program.Body
    // (not recursed into) and to declarators whose initializer is actually a function, so an unrelated
    // top-level object/namespace assembly (e.g. `var ns = {};`) is never mistaken for a known shape.
    static void CollectTopLevelDeclarations(Program program, Dictionary<string, string> result)
    {
        foreach (var statement in program.Body)
        {
            switch (statement)
            {
                case FunctionDeclaration { Id: { } id }:
                    result.TryAdd(id.Name, id.Name);
                    break;
                case VariableDeclaration variableDeclaration:
                    foreach (var declarator in variableDeclaration.Declarations)
                    {
                        if (declarator.Id is Identifier declaratorId &&
                            declarator.Init is FunctionExpression or ArrowFunctionExpression)
                            result.TryAdd(declaratorId.Name, declaratorId.Name);
                    }
                    break;
            }
        }
    }
}
