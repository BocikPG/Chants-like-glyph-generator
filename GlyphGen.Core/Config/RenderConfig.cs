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

    /// <summary>Draw a dot on every grid node (useful for checking generation output).</summary>
    public bool DebugShowNodes { get; set; } = false;
    /// <summary>Colour each combination component differently.</summary>
    public bool DebugColorComponents { get; set; } = false;
}
