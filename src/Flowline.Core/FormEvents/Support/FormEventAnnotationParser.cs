using System.Text.RegularExpressions;
using Flowline.Core.Models;

namespace Flowline.Core.FormEvents.Support;

// The successfully parsed annotations, plus any line that clearly intends to be a flowline:on... comment
// (matches AnnotationIntentRegex) but fails the strict grammar — surfaced so a caller can warn instead of
// silently registering nothing.
public record FormEventAnnotationParseResult(IReadOnlyList<FormEventAnnotation> Annotations, IReadOnlyList<string> MalformedLines);

public static class FormEventAnnotationParser
{
    // Trailing bracket-modifier tail shared by every directive: zero or more [bulkEdit] / [order:N] tokens,
    // in any order, positioned after the mandatory tokens and before the optional function name — the same
    // slot the (params) tail already occupies relative to the function. Parsed permissively here (any
    // directive can syntactically carry either modifier); semantic rejection ([bulkEdit] on a non-onload
    // directive, duplicate [order:N] on a shared event/scope) is a planner concern, not the parser's.
    // order:N is capped at 9 digits (max 999,999,999, well within int range) so int.Parse in ExtractModifiers
    // can never overflow — an oversized value simply fails this regex and falls through to the existing
    // malformed-line path, the same way a non-numeric order value already does.
    const string ModifierFragment = """(?:\[(?<modifier>bulkEdit|order:\d{1,9})\]\s*)*""";

    // Matches "// flowline:onload/onsave <entity> <form> [modifiers] [Function[(params)]]", the "//!"
    // legal-comment variant, and the single-line block form "/*! ... */" — same three comment forms
    // WebResourceAnnotationParser recognizes for "flowline:depends". <form> is a bare token (no whitespace),
    // a double-quoted string, or a single-quoted string (R3: both quote styles are accepted — matches JS's
    // own string-literal convention; Dataverse form names routinely contain spaces).
    static readonly Regex OnLoadSaveAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:on(?<event>load|save)\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|\S+)(?:\s+""" + ModifierFragment + """(?<function>[A-Za-z_][\w.]*)?(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // Same shape as OnLoadSaveAnnotationRegex, with one extra mandatory token between <form> and the
    // optional [modifiers][Function[(params)]] tail: <attribute>, always a bare token (attribute logical
    // names never contain spaces, so no quoting rules apply to it). <form>'s bare-token fallback excludes a
    // leading quote character (unlike OnLoadSaveAnnotationRegex, where nothing is required after <form>) —
    // without that exclusion, a malformed quoted form with the mandatory <attribute> token missing lets the
    // regex backtrack into splitting the quoted form itself into bogus <form>/<attribute> pieces instead of
    // failing to match.
    static readonly Regex OnChangeAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:onchange\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|[^"'\s]\S*)\s+(?<attribute>\S+)(?:\s+""" + ModifierFragment + """(?<function>[A-Za-z_][\w.]*)?(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // Tab TabStateChange — <attribute> is the tab's FormXml `name`. Same shape as OnChangeAnnotationRegex,
    // one directive literal swapped in. Kept as its own regex (not merged into a shared alternation) per
    // the readability precedent set for OnLoadSaveAnnotationRegex vs OnChangeAnnotationRegex.
    static readonly Regex TabStateChangeAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:tabstatechange\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|[^"'\s]\S*)\s+(?<attribute>\S+)(?:\s+""" + ModifierFragment + """(?<function>[A-Za-z_][\w.]*)?(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // IFRAME OnReadyStateComplete — <attribute> is the IFRAME control's id. Maker Portal always renders
    // "IFRAME_" as a fixed, non-editable prefix in the control's Name field, so the token may be written
    // either with or without it (FormXmlEventSerializer.NormalizeIframeControlId resolves both to the same
    // control).
    static readonly Regex OnReadyStateCompleteAnnotationRegex = new(
        """^(?://!?|/\*!)\s*flowline:onreadystatecomplete\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|[^"'\s]\S*)\s+(?<attribute>\S+)(?:\s+""" + ModifierFragment + """(?<function>[A-Za-z_][\w.]*)?(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$""",
        RegexOptions.Compiled);

    // Prefix-only check: "does this line even intend to be a flowline annotation" — used to distinguish a
    // malformed annotation (warn) from an ordinary comment that just happens not to match (silently skip).
    static readonly Regex AnnotationIntentRegex = new(
        """^(?://!?|/\*!)\s*flowline:(?:on(?:load|save|change|readystatecomplete)|tabstatechange)\b""",
        RegexOptions.Compiled);

    // The three attribute-scoped directives share identical match-handling shape (entity, form, attribute,
    // optional modifiers/function/params) — only the regex and the resulting FormEventType differ, so
    // ParseAnnotations loops over this table instead of repeating the extraction logic per directive.
    static readonly (Regex Regex, FormEventType Event)[] AttributeScopedDirectives =
    [
        (OnChangeAnnotationRegex, FormEventType.OnChange),
        (TabStateChangeAnnotationRegex, FormEventType.TabStateChange),
        (OnReadyStateCompleteAnnotationRegex, FormEventType.OnReadyStateComplete)
    ];

    // Extracts BulkEdit/Order from a match's repeated "modifier" captures. Last [order:N] wins if repeated;
    // any [bulkEdit] occurrence sets BulkEdit true. Semantic validation of these values is the planner's job.
    static (bool BulkEdit, int? Order) ExtractModifiers(Match match)
    {
        var bulkEdit = false;
        int? order = null;
        foreach (Capture capture in match.Groups["modifier"].Captures)
        {
            if (capture.Value.Equals("bulkEdit", StringComparison.Ordinal))
                bulkEdit = true;
            else if (capture.Value.StartsWith("order:", StringComparison.Ordinal))
                order = int.Parse(capture.Value["order:".Length..]);
        }
        return (bulkEdit, order);
    }

    /// <summary>
    /// Reads flowline:onload/onsave/onchange/tabstatechange/onreadystatecomplete annotation lines from
    /// anywhere in a JS file — not just the leading comment block, since a bundler-injected banner can
    /// precede the annotation without being a "//" line comment itself, which would otherwise stop a
    /// leading-block-only scan before it ever reaches the annotation.
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
                var (bulkEdit, order) = ExtractModifiers(onLoadSaveMatch);

                (annotations ??= []).Add(new FormEventAnnotation(entity, form, evt, functionName, parameters, BulkEdit: bulkEdit, Order: order));
                continue;
            }

            var matchedAttributeScoped = false;
            foreach (var (regex, evtType) in AttributeScopedDirectives)
            {
                var match = regex.Match(trimmed);
                if (!match.Success)
                    continue;

                var entity = match.Groups["entity"].Value;
                var form = match.Groups["form"].Value.Trim('"', '\'');
                var attribute = match.Groups["attribute"].Value;
                var functionName = match.Groups["function"].Success ? match.Groups["function"].Value : null;
                var parameters = match.Groups["params"].Success
                    ? string.Join(",", match.Groups["params"].Value.Split(',').Select(p => p.Trim()))
                    : null;
                var (bulkEdit, order) = ExtractModifiers(match);

                (annotations ??= []).Add(new FormEventAnnotation(entity, form, evtType, functionName, parameters, attribute, bulkEdit, order));
                matchedAttributeScoped = true;
                break;
            }
            if (matchedAttributeScoped)
                continue;

            if (AnnotationIntentRegex.IsMatch(trimmed))
                (malformed ??= []).Add(trimmed);
        }
        return new FormEventAnnotationParseResult(
            annotations?.AsReadOnly() ?? (IReadOnlyList<FormEventAnnotation>)[],
            malformed?.AsReadOnly() ?? (IReadOnlyList<string>)[]);
    }
}
