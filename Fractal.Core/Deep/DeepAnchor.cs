namespace Fractal.Deep;

/// <summary>
/// Full-precision grid anchor for deep zoom. The reference orbit is computed here, and every
/// pixel's offset from it is just (global pixel index) × scale, which doubles hold exactly
/// enough at any depth. Value equality, so views can be compared for pan reuse.
/// </summary>
public sealed class DeepAnchor(BigFixed x, BigFixed y) : IEquatable<DeepAnchor>
{
    public BigFixed X { get; } = x;
    public BigFixed Y { get; } = y;

    public bool Equals(DeepAnchor? o) =>
        o != null && X.Bits == o.X.Bits && Y.Bits == o.Y.Bits && X.Raw == o.X.Raw && Y.Raw == o.Y.Raw;

    public override bool Equals(object? obj) => Equals(obj as DeepAnchor);
    public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw, X.Bits);
}
