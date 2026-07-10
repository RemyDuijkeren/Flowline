using System.Text.RegularExpressions;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public static class FormEventAnnotationParser
{
    // Matches "// flowline:onload/onsave <entity> <form> [Function[(params)]]", the "//!" legal-comment
    // variant, and the single-line block form "/*! ... */" — same three comment forms
    // WebResourceAnnotationParser recognizes for "flowline:depends". <form> is either a bare token
    // (no whitespace) or a double-quoted string (Dataverse form names routinely contain spaces).
    static readonly Regex AnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:on(?<event>load|save)\s+(?<entity>\S+)\s+(?<form>"[^"]+"|\S+)(?:\s+(?<function>[A-Za-z_][\w.]*)(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads flowline:onload/flowline:onsave annotation lines from anywhere in a JS file — not just
    /// the leading comment block, since a bundler-injected banner can precede the annotation without
    /// being a "//" line comment itself, which would otherwise stop a leading-block-only scan before
    /// it ever reaches the annotation.
    /// </summary>
    public static IReadOnlyList<FormEventAnnotation> ParseAnnotations(string filePath) =>
        ParseAnnotations(File.ReadLines(filePath));

    // Lines-based overload — lets a caller that already has the file's content in memory (e.g. the reader,
    // which decodes it for ResolvedFormEventAnnotation.Content) scan it without a second disk read.
    public static IReadOnlyList<FormEventAnnotation> ParseAnnotations(IEnumerable<string> lines)
    {
        List<FormEventAnnotation>? result = null;
        foreach (var line in lines)
        {
            var match = AnnotationRegex.Match(line.Trim());
            if (!match.Success) continue;

            var evt = match.Groups["event"].Value.Equals("load", StringComparison.OrdinalIgnoreCase)
                ? FormEventType.OnLoad
                : FormEventType.OnSave;
            var entity = match.Groups["entity"].Value;
            var form = match.Groups["form"].Value.Trim('"');
            var functionName = match.Groups["function"].Success ? match.Groups["function"].Value : null;
            var parameters = match.Groups["params"].Success
                ? string.Join(",", match.Groups["params"].Value.Split(',').Select(p => p.Trim()))
                : null;

            (result ??= []).Add(new FormEventAnnotation(entity, form, evt, functionName, parameters));
        }
        return result?.AsReadOnly() ?? (IReadOnlyList<FormEventAnnotation>)[];
    }
}
