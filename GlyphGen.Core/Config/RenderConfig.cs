namespace GlyphGen.Core.Config;

/// <summary>Per-language rendering settings. Pixel size and margin still come from CanvasConfig.</summary>
public sealed class RenderConfig
{
    public FramingMode Framing { get; set; } = FramingMode.Grid;

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Foreground { get; set; } = "#000000";
    /// <summary>#RRGGBB or #AARRGGBB; the default is fully transparent.</summary>
    public string Background { get; set; } = "#00000000";

    public TypefaceConfig Typeface { get; set; } = new();

    /// <summary>The overview sheet(s) the CLI writes next to the glyph images.</summary>
    public ContactSheetConfig ContactSheet { get; set; } = new();

    /// <summary>Debug option. When this or DebugColorComponents is on, the CLI writes an extra
    /// "&lt;glyph&gt;.debug.png" next to each normal image, drawn with the enabled overlays (the
    /// normal images stay clean; with both options off no debug files are written).
    /// Overlay for checking generation output: the whole lattice as faint dots with
    /// coordinates labelled every 5 cells, plus a dot on every node of the glyph - red for a
    /// free-form sub-symbol, blue for a guaranteed motif, gold where a node is used more than
    /// once (a revisit, or a node shared by shapes).</summary>
    public bool DebugShowNodes { get; set; } = false;
    /// <summary>Debug option (see DebugShowNodes): colour each combination component differently in
    /// the extra ".debug.png" images.</summary>
    public bool DebugColorComponents { get; set; } = false;
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
