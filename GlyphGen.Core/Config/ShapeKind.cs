namespace GlyphGen.Core.Config;

/// <summary>Whether a shape's geometry is fixed (Literal) or generated (Generative).</summary>
public enum ShapeKind
{
    /// <summary>Fixed relative-coordinate shape, identical every time it appears.</summary>
    Literal,

    /// <summary>Generated via the chain engine with its own constrained rule set.</summary>
    Generative,
}