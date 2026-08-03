using System.Text.RegularExpressions;

namespace Flowline.Core.Services;

/// <summary>
/// Validates the names <c>flowline init</c> sends to Dataverse — solution unique/display name,
/// publisher prefix, publisher unique name — before any create call (R14, R19).
/// </summary>
/// <remarks>
/// Pure and static: every method here either returns an error string naming the violated rule
/// (or <c>null</c> when valid), or throws <see cref="FlowlineException"/> with
/// <see cref="ExitCode.ValidationFailed"/> for callers that want the throw-on-invalid shape.
/// No Dataverse call, no console — <c>Flowline.Core</c> has neither dependency, and this class
/// doesn't need either.
/// </remarks>
public static class SolutionNameValidator
{
    static readonly Regex s_uniqueNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    static readonly Regex s_publisherPrefixPattern = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    /// <summary>The C# reserved keywords, which cannot appear unescaped in a namespace declaration.</summary>
    static readonly HashSet<string> s_csharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    /// <summary>
    /// Validates the solution unique name (the <c>init</c> positional): <c>[A-Za-z0-9_]</c> only, starts
    /// with a letter or underscore, at most 65 characters, and not a C# keyword (it becomes the plugin
    /// namespace). Returns the violated rule, or <c>null</c> when valid.
    /// </summary>
    public static string? ValidateSolutionUniqueName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Solution unique name is required.";
        if (name.Length > 65)
            return $"Solution unique name must be at most 65 characters — '{name}' is {name.Length}.";
        if (!s_uniqueNamePattern.IsMatch(name))
            return $"Solution unique name must contain only letters, digits, and underscores, and start with a letter or underscore — '{name}' doesn't.";
        if (IsCSharpKeyword(name))
            return $"Solution unique name '{name}' is a C# keyword, so the plugin namespace '{name}.Plugins' won't compile. Choose a different name.";
        return null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a C# reserved keyword, and so can't become a plugin
    /// namespace unescaped. Case-sensitive on purpose — <c>Event</c> is a perfectly good namespace.
    /// </summary>
    /// <remarks>
    /// Public because clone needs the keyword rule alone, without the rest of
    /// <see cref="ValidateSolutionUniqueName"/>: a cloned name comes from Dataverse and may
    /// legitimately break rules init would refuse (over 65 characters, say).
    /// </remarks>
    public static bool IsCSharpKeyword(string? name) => name is not null && s_csharpKeywords.Contains(name);

    /// <summary>Throws <see cref="FlowlineException"/> (<see cref="ExitCode.ValidationFailed"/>) when <see cref="ValidateSolutionUniqueName"/> rejects <paramref name="name"/>.</summary>
    public static void EnsureSolutionUniqueName(string? name) => Ensure(ValidateSolutionUniqueName(name));

    /// <summary>
    /// Validates the solution display name (<c>--display-name</c>): free text, at most 256 characters.
    /// Returns the violated rule, or <c>null</c> when valid.
    /// </summary>
    public static string? ValidateSolutionDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Solution display name is required.";
        if (displayName.Length > 256)
            return $"Solution display name must be at most 256 characters — got {displayName.Length}.";
        return null;
    }

    /// <summary>Throws <see cref="FlowlineException"/> (<see cref="ExitCode.ValidationFailed"/>) when <see cref="ValidateSolutionDisplayName"/> rejects <paramref name="displayName"/>.</summary>
    public static void EnsureSolutionDisplayName(string? displayName) => Ensure(ValidateSolutionDisplayName(displayName));

    /// <summary>
    /// Validates the publisher prefix (<c>--publisher-prefix</c>): 2-8 characters, alphanumeric, starts
    /// with a letter, must not start with <c>mscrm</c> (reserved by Dataverse). Returns the violated rule,
    /// or <c>null</c> when valid.
    /// </summary>
    public static string? ValidatePublisherPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "Publisher prefix is required.";
        if (prefix.Length is < 2 or > 8)
            return $"Publisher prefix must be 2-8 characters — '{prefix}' is {prefix.Length}.";
        if (!s_publisherPrefixPattern.IsMatch(prefix))
            return $"Publisher prefix must be alphanumeric and start with a letter — '{prefix}' doesn't.";
        if (prefix.StartsWith("mscrm", StringComparison.OrdinalIgnoreCase))
            return $"Publisher prefix must not start with 'mscrm' — that prefix is reserved by Dataverse. Choose a different prefix.";
        return null;
    }

    /// <summary>Throws <see cref="FlowlineException"/> (<see cref="ExitCode.ValidationFailed"/>) when <see cref="ValidatePublisherPrefix"/> rejects <paramref name="prefix"/>.</summary>
    public static void EnsurePublisherPrefix(string? prefix) => Ensure(ValidatePublisherPrefix(prefix));

    static void Ensure(string? error)
    {
        if (error is not null)
            throw new FlowlineException(ExitCode.ValidationFailed, error);
    }
}
