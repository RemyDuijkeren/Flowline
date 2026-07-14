using System.Text.RegularExpressions;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

// The successfully parsed annotations, plus any line that clearly intends to be a flowline:on... comment
// (matches AnnotationIntentRegex) but fails the strict grammar — surfaced so a caller can warn instead of
// silently registering nothing.
public record FormEventAnnotationParseResult(IReadOnlyList<FormEventAnnotation> Annotations, IReadOnlyList<string> MalformedLines);

public static class FormEventAnnotationParser
{
    // Matches "// flowline:onload/onsave <entity> <form> [Function[(params)]]", the "//!" legal-comment
    // variant, and the single-line block form "/*! ... */" — same three comment forms
    // WebResourceAnnotationParser recognizes for "flowline:depends". <form> is a bare token (no whitespace),
    // a double-quoted string, or a single-quoted string (R3: both quote styles are accepted — matches JS's
    // own string-literal convention; Dataverse form names routinely contain spaces).
    static readonly Regex OnLoadSaveAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:on(?<event>load|save)\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|\S+)(?:\s+(?<function>[A-Za-z_][\w.]*)(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // Same shape as OnLoadSaveAnnotationRegex, with one extra mandatory token between <form> and the
    // optional [Function[(params)]] tail: <attribute>, always a bare token (attribute logical names never
    // contain spaces, so no quoting rules apply to it). <form>'s bare-token fallback excludes a leading
    // quote character (unlike OnLoadSaveAnnotationRegex, where nothing is required after <form>) — without
    // that exclusion, a malformed quoted form with the mandatory <attribute> token missing lets the regex
    // backtrack into splitting the quoted form itself into bogus <form>/<attribute> pieces instead of
    // failing to match.
    static readonly Regex OnChangeAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:onchange\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|[^"'\s]\S*)\s+(?<attribute>\S+)(?:\s+(?<function>[A-Za-z_][\w.]*)(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // Prefix-only check: "does this line even intend to be a flowline annotation" — used to distinguish a
    // malformed annotation (warn) from an ordinary comment that just happens not to match (silently skip).
    static readonly Regex AnnotationIntentRegex = new(
        """^(?://!?|/\*!)\s*flowline:on(?:load|save|change)\b""",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads flowline:onload/flowline:onsave/flowline:onchange annotation lines from anywhere in a JS file — not just
    /// the leading comment block, since a bundler-injected banner can precede the annotation without
    /// being a "//" line comment itself, which would otherwise stop a leading-block-only scan before
    /// it ever reaches the annotation.
    /// </summary>
    public static FormEventAnnotationParseResult ParseAnnotations(string filePath) =>
        ParseAnnotations(File.ReadLines(filePath));

    // Lines-based overload — lets a caller that already has the file's content in memory (e.g. the reader,
    // which decodes it for ResolvedFormEventAnnotation.Content) scan it without a second disk read.
    public static FormEventAnnotationParseResult ParseAnnotations(IEnumerable<string> lines)
    {
        List<FormEventAnnotation>? annotations = null;
        List<string>? malformed = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var onLoadSaveMatch = OnLoadSaveAnnotationRegex.Match(trimmed);
            if (onLoadSaveMatch.Success)
            {
                var evt = onLoadSaveMatch.Groups["event"].Value.Equals("load", StringComparison.OrdinalIgnoreCase)
                    ? FormEventType.OnLoad
                    : FormEventType.OnSave;
                var entity = onLoadSaveMatch.Groups["entity"].Value;
                var form = onLoadSaveMatch.Groups["form"].Value.Trim('"', '\'');
                var functionName = onLoadSaveMatch.Groups["function"].Success ? onLoadSaveMatch.Groups["function"].Value : null;
                var parameters = onLoadSaveMatch.Groups["params"].Success
                    ? string.Join(",", onLoadSaveMatch.Groups["params"].Value.Split(',').Select(p => p.Trim()))
                    : null;

                (annotations ??= []).Add(new FormEventAnnotation(entity, form, evt, functionName, parameters));
                continue;
            }

            var onChangeMatch = OnChangeAnnotationRegex.Match(trimmed);
            if (onChangeMatch.Success)
            {
                var entity = onChangeMatch.Groups["entity"].Value;
                var form = onChangeMatch.Groups["form"].Value.Trim('"', '\'');
                var attribute = onChangeMatch.Groups["attribute"].Value;
                var functionName = onChangeMatch.Groups["function"].Success ? onChangeMatch.Groups["function"].Value : null;
                var parameters = onChangeMatch.Groups["params"].Success
                    ? string.Join(",", onChangeMatch.Groups["params"].Value.Split(',').Select(p => p.Trim()))
                    : null;

                (annotations ??= []).Add(new FormEventAnnotation(entity, form, FormEventType.OnChange, functionName, parameters, attribute));
                continue;
            }

            if (AnnotationIntentRegex.IsMatch(trimmed))
                (malformed ??= []).Add(trimmed);
        }
        return new FormEventAnnotationParseResult(
            annotations?.AsReadOnly() ?? (IReadOnlyList<FormEventAnnotation>)[],
            malformed?.AsReadOnly() ?? (IReadOnlyList<string>)[]);
    }
}
