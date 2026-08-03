using Spectre.Console;

namespace Flowline.Core.Console;

/// <summary>
/// Flowline's presentation tokens — brand colors, the logo, and the status glyphs
/// <see cref="FlowlineConsoleExtensions"/> prefixes its lines with.
/// </summary>
/// <remarks>
/// The status <em>colors</em> deliberately aren't here: green/yellow/red/dim/cyan are written inline as
/// Spectre markup at the ten call sites in <see cref="FlowlineConsoleExtensions"/>, where a literal tag
/// reads better than an indirection. Add them here if a reskin or a no-color mode ever needs one switch.
/// </remarks>
public static class FlowlineTheme
{
    // Candidates auditioned: Turquoise2, HotPink2, Magenta1 (purple glow), MediumOrchid1, Orchid, Plum3
    /// <summary>Brand color — logo, help header, version line.</summary>
    public static readonly Color PrimaryColor = Color.Orchid; // RGB(215, 95, 215)

    /// <summary>
    /// Supporting brand color for secondary text alongside <see cref="PrimaryColor"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately another purple rather than a cyan/teal: green, yellow, red, dim and cyan are all
    /// spoken for by the status helpers (cyan in particular is <c>Question</c>'s), so a teal accent
    /// would read as a status rather than as branding. MediumPurple RGB(135, 135, 215) is far enough
    /// from Orchid to build real hierarchy while staying unmistakably in the brand family.
    /// </remarks>
    public static readonly Color SecondaryColor = Color.MediumPurple; // Plum3

    public static readonly string TextLogo = // Future Smooth
        """
        ╭─╴╷  ╭─╮╷ ╷╷  ╷╭╮╷╭─╴
        ├╴ │  │ ││╷││  ││╰┤├╴
        ╵  ╰─╴╰─╯╰┴╯╰─╴╵╵ ╵╰─╴
        """;

    internal const string OkPrefix       = "✓";
    internal const string DonePrefix     = "🚀";
    internal const string InfoPrefix     = "·";
    internal const string SkipPrefix     = "↷";
    internal const string WarningPrefix  = "!";
    internal const string ErrorPrefix    = "✗";
    internal const string QuestionPrefix = "?";
    internal const string StopPrefix     = "→";
}
