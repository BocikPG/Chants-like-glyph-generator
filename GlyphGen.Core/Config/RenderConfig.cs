namespace GlyphGen.Core.Config;

/// <summary>One weighted range of turns for render.rotateOptions.</summary>
public sealed class RotateOption
{
    /// <summary>Smallest angle in degrees (clockwise; negative turns the other way).</summary>
    public double Min { get; set; }

    /// <summary>Largest angle in degrees.</summary>
    public double Max { get; set; }

    /// <summary>Relative chance of this option (default 1; 0 = never).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>Per-language rendering settings. Pixel size and margin still come from CanvasConfig.</summary>
public sealed class RenderConfig
{
    public FramingMode Framing { get; set; } = FramingMode.Grid;

    /// <summary>Turns every glyph by this many degrees, clockwise, around the middle of its grid; the canvas itself stays
    /// as it is. Any angle works (curves turn exactly). The framing then fits the turned glyphs, so with Grid framing the
    /// grid's square is what is rotated and its corners may reach past the canvas margin. Applies to the normal and the debug
    /// images alike. Default 0.</summary>
    public double RotateDegrees { get; set; } = 0.0;

    /// <summary>Per-glyph random turn, added to rotateDegrees: each glyph draws one option by weight, then an angle
    /// uniformly from that option's range (degrees, clockwise). The draw comes from the glyph's own seed, so a glyph
    /// always turns the same way. Empty (default) = no random turn. Example: [ { "min": -8, "max": 8, "weight": 4 },
    /// { "min": 85, "max": 95, "weight": 1 } ] mostly tilts a little and now and then lays a glyph on its side.</summary>
    public List<RotateOption> RotateOptions { get; set; } = new();

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Foreground { get; set; } = "#000000";
    /// <summary>#RRGGBB or #AARRGGBB; the default is fully transparent.</summary>
    public string Background { get; set; } = "#00000000";

    public TypefaceConfig Typeface { get; set; } = new();

    /// <summary>How strokes are joined where they bend and where they meet, for every typeface.</summary>
    public JoinsConfig Joins { get; set; } = new();

    /// <summary>The overview sheet(s) the CLI writes next to the glyph images.</summary>
    public ContactSheetConfig ContactSheet { get; set; } = new();

    /// <summary>Debug option. When any of the debug options is on, the CLI writes an extra
    /// "&lt;glyph&gt;.debug.png" next to each normal image, drawn with the enabled overlays (the
    /// normal images stay clean; with all of them off no debug files are written).
    /// Overlay for checking generation output: the whole lattice as faint dots with
    /// coordinates labelled every 5 cells, plus a dot on every node of the glyph - red for a
    /// free-form sub-symbol, blue for a guaranteed motif, gold where a node is used more than
    /// once (a revisit, or a node shared by shapes).</summary>
    public bool DebugShowNodes { get; set; } = false;
    /// <summary>Debug option (see DebugShowNodes): colour each combination component differently in
    /// the extra ".debug.png" images.</summary>
    public bool DebugColorComponents { get; set; } = false;

    /// <summary>Debug option (see DebugShowNodes): in the extra ".debug.png" images, colour the ink each
    /// kind of part ADDS on top of the stroke bodies - grey for caps (and Ball / Ink terminals), violet for
    /// corner joins, pink for junction joins (including straightening). The strokes themselves keep the
    /// foreground colour (or their component colour). Parts are painted over the strokes in the order they
    /// are built: corner joins, then junction joins, then caps.</summary>
    public bool DebugColorParts { get; set; } = false;

    /// <summary>Debug option (see DebugShowNodes): in the extra ".debug.png" images, give every shape its own colour, in the
    /// order of the shapes list. Instances of one shape (a shape with a count, or a copy of each instance) share its hue and
    /// differ in lightness, so they can be told apart. Joins between strokes keep the foreground colour. Takes the place of
    /// debugColorComponents where both are on.</summary>
    public bool DebugColorShapes { get; set; } = false;

    /// <summary>Debug option (see DebugShowNodes): in the extra ".debug.png" images, also draw the shapes that have isDrawn = false,
    /// in one translucent orange. They are never in the normal images.</summary>
    public bool DebugShowUndrawn { get; set; } = false;
}

/// <summary>Settings for the contact sheet(s): one PNG laying a language's glyphs out in a grid.</summary>
public sealed class ContactSheetConfig
{
    /// <summary>Glyphs per row.</summary>
    public int Columns { get; set; } = 6;

    /// <summary>Also write a sheet of the language's base glyphs, as "_bases/_contact-sheet.png"
    /// (the main sheet never contains them). Only has an effect when baseGlyphs is used.</summary>
    public bool BaseGlyphsSheet { get; set; } = true;
}
