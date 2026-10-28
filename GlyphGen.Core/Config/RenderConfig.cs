using System.Text.Json.Serialization;
namespace GlyphGen.Core.Config;

/// <summary>One weighted range of turns for render.transform.rotateOptions.</summary>
public sealed class RotateOption
{
    /// <summary>Smallest angle in degrees (clockwise; negative turns the other way).</summary>
    public double Min { get; set; }

    /// <summary>Largest angle in degrees.</summary>
    public double Max { get; set; }

    /// <summary>Relative chance of this option (default 1; 0 = never).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>Per-language rendering settings: image size, framing, turn, colours, stroke style, joins and the debug overlays.</summary>
public sealed class RenderConfig
{
    /// <summary>How a glyph is framed in its image: the whole Grid (default), the Glyph's own box, the whole Language, or GlyphCentered.</summary>
    public FramingMode Framing { get; set; } = FramingMode.Grid;

    /// <summary>Size of each rendered glyph image and its margin.</summary>
    public CanvasConfig Canvas { get; set; } = new();

    /// <summary>Turns the glyphs: a fixed angle for every glyph and/or a random one per glyph.</summary>
    public RenderTransformConfig Transform { get; set; } = new();

    /// <summary>Shortcut to render.transform.rotateDegrees.</summary>
    [JsonIgnore] public double RotateDegrees { get => Transform.RotateDegrees; set => Transform.RotateDegrees = value; }
    /// <summary>Shortcut to render.transform.rotateOptions.</summary>
    [JsonIgnore] public List<RotateOption> RotateOptions { get => Transform.RotateOptions; set => Transform.RotateOptions = value; }

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Foreground { get; set; } = "#000000";
    /// <summary>#RRGGBB or #AARRGGBB; the default is fully transparent.</summary>
    public string Background { get; set; } = "#00000000";

    /// <summary>The stroke style: kind (Rectangular, ThickEnds or Brush) and that kind's own block.</summary>
    public TypefaceConfig Typeface { get; set; } = new();

    /// <summary>How strokes are joined where they bend and where they meet, for every typeface.</summary>
    public JoinsConfig Joins { get; set; } = new();

    /// <summary>The debug overlays. When any is on, the CLI writes an extra "&lt;glyph&gt;.debug.png" next to each normal image, drawn with the
    /// enabled overlays (the normal images stay clean; with all of them off no debug files are written).</summary>
    public RenderDebugConfig Debug { get; set; } = new();

    /// <summary>Shortcut to render.debug.showNodes.</summary>
    [JsonIgnore] public bool DebugShowNodes { get => Debug.ShowNodes; set => Debug.ShowNodes = value; }
    /// <summary>Shortcut to render.debug.colorComponents.</summary>
    [JsonIgnore] public bool DebugColorComponents { get => Debug.ColorComponents; set => Debug.ColorComponents = value; }
    /// <summary>Shortcut to render.debug.colorParts.</summary>
    [JsonIgnore] public bool DebugColorParts { get => Debug.ColorParts; set => Debug.ColorParts = value; }
    /// <summary>Shortcut to render.debug.colorShapes.</summary>
    [JsonIgnore] public bool DebugColorShapes { get => Debug.ColorShapes; set => Debug.ColorShapes = value; }
    /// <summary>Shortcut to render.debug.showUndrawn.</summary>
    [JsonIgnore] public bool DebugShowUndrawn { get => Debug.ShowUndrawn; set => Debug.ShowUndrawn = value; }
}

/// <summary>render.transform: how the glyphs are turned.</summary>
public sealed class RenderTransformConfig
{
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
}

/// <summary>render.debug: overlays for checking generation output. Any of them being on makes the CLI write an extra ".debug.png" per glyph.</summary>
public sealed class RenderDebugConfig
{
    /// <summary>The whole lattice as faint dots with coordinates labelled every 5 cells, plus a dot on every node of the glyph (gold where a node
    /// is used more than once: a revisit, or a node shared by shapes).</summary>
    public bool ShowNodes { get; set; } = false;

    /// <summary>Colour each combination component differently.</summary>
    public bool ColorComponents { get; set; } = false;

    /// <summary>Colour the ink each kind of part ADDS on top of the stroke bodies - grey for caps (and Ball / Ink terminals), violet for
    /// corner joins, pink for junction joins (including straightening). The strokes themselves keep the foreground colour (or their
    /// component colour). Parts are painted over the strokes in the order they are built: corner joins, then junction joins, then caps.</summary>
    public bool ColorParts { get; set; } = false;

    /// <summary>Give every shape its own colour, in the order of the shapes list. Instances of one shape (a shape with a count, or a copy of
    /// each instance) share its hue and differ in lightness, so they can be told apart. Joins between strokes keep the foreground colour.
    /// Takes the place of colorComponents where both are on.</summary>
    public bool ColorShapes { get; set; } = false;

    /// <summary>Also draw the shapes that have isDrawn = false, in one translucent orange. They are never in the normal images.</summary>
    public bool ShowUndrawn { get; set; } = false;
}

/// <summary>run.contactSheet: one PNG laying a language's glyphs out in a grid.</summary>
public sealed class ContactSheetConfig
{
    /// <summary>Glyphs per row.</summary>
    public int Columns { get; set; } = 6;

    /// <summary>Also write a sheet of the language's base glyphs, as "_bases/_contact-sheet.png"
    /// (the main sheet never contains them). Only has an effect when baseGlyphs is used.</summary>
    public bool BaseGlyphsSheet { get; set; } = true;
}
