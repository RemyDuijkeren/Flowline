using Acornima;
using Acornima.Ast;

namespace Flowline.Core.Services;

// Replaces regex-based function-existence checking (the old FormXmlEventSerializer.ResolveFunction)
// with a real structural JS parse (Acornima, no execution) - see KTD13/KTD4. A regex can be fooled by
// a comment or string literal that merely looks like an export ("// exports.onLoad = oldThing"); a
// structural parse never tokenizes comment/string content as executable statements.
public static class FormEventFunctionResolver
{
    // R6/R6a/R7/R7a: resolves a form event handler's function name against a built web resource's
    // actual export surface.
    //   isExplicit=false (R4 default, requestedFunctionName is the auto-derived onLoad/onSave guess):
    //     try "<autoNamespace>.<requestedFunctionName>" (Rollup-style exports.X= shape), then bare
    //     "<requestedFunctionName>" (verbatim shape). Found:false drives R7's hard fail; Confident is
    //     not meaningful for this branch.
    //   isExplicit=true (R7a, the user wrote the name): a dotted name is already fully qualified, so
    //     auto-namespace derivation is skipped entirely (R6a) and only the final segment is verified
    //     against the parsed export surface; a bare name is matched the same namespaced-then-bare way
    //     as the defaulted branch. Confident reports whether Acornima fully traced a *known* export
    //     shape for this file (an "exports.X =" assignment anywhere in the tree, or a verbatim
    //     top-level function/const-arrow declaration) - i.e. whether a reported absence is a positive
    //     determination rather than a shape the walk couldn't fully enumerate (e.g. a namespace object
    //     assembled across multiple statements). Found:true always implies Confident:true, since a match
    //     can only come from one of those two known, fully-enumerated shapes.
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

        if (isExplicit)
        {
            var dotIndex = requestedFunctionName.LastIndexOf('.');
            if (dotIndex >= 0)
            {
                var namespacePrefix = requestedFunctionName[..dotIndex];
                var tail = requestedFunctionName[(dotIndex + 1)..];
                if (exportAssignments.TryGetValue(tail, out var realTail) ||
                    topLevelDeclarations.TryGetValue(tail, out realTail))
                    return ($"{namespacePrefix}.{realTail}", true, confident);

                return (null, false, confident);
            }
        }

        if (exportAssignments.TryGetValue(requestedFunctionName, out var realNamespaced))
            return ($"{autoNamespace}.{realNamespaced}", true, confident);

        if (topLevelDeclarations.TryGetValue(requestedFunctionName, out var realBare))
            return (realBare, true, confident);

        return (null, false, confident);
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
