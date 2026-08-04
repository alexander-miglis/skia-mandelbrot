using System;
using System.Numerics;

namespace FractalZoom;

/// <summary>
/// Binary fixed-point number: value = Raw / 2^FracBits. Arithmetic requires matching FracBits.
///
/// This is only ever used for the reference orbit — a few thousand iterations per rebuild, a few
/// times per descent — never per pixel. BigInteger's allocation cost is therefore irrelevant, and
/// in exchange the precision is whatever the depth demands.
/// </summary>
internal readonly struct BigFixed
{
    public BigInteger Raw { get; }
    public int FracBits { get; }

    public BigFixed(BigInteger raw, int fracBits)
    {
        Raw = raw;
        FracBits = fracBits;
    }

    public static BigFixed Zero(int fracBits) => new(BigInteger.Zero, fracBits);

    /// <summary>Exact: every finite double is a dyadic rational, so nothing is lost here.</summary>
    public static BigFixed FromDouble(double v, int fracBits)
    {
        if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return Zero(fracBits);

        long bits = BitConverter.DoubleToInt64Bits(v);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;

        if (exponent == 0) exponent = 1;              // subnormal
        else mantissa |= 1L << 52;                    // restore the implicit bit
        exponent -= 1075;                             // v = mantissa * 2^exponent

        BigInteger raw = mantissa;
        int shift = exponent + fracBits;
        raw = shift >= 0 ? raw << shift : raw >> -shift;
        return new BigFixed(negative ? -raw : raw, fracBits);
    }

    public double ToDouble()
    {
        if (Raw.IsZero) return 0;

        BigInteger magnitude = BigInteger.Abs(Raw);
        int length = (int)magnitude.GetBitLength();
        int shift = FracBits;

        if (length > 53)
        {
            int excess = length - 53;
            magnitude >>= excess;
            shift -= excess;
        }

        double d = Math.ScaleB((double)magnitude, -shift);
        return Raw.Sign < 0 ? -d : d;
    }

    public BigFixed WithFracBits(int fracBits)
    {
        if (fracBits == FracBits) return this;
        int delta = fracBits - FracBits;
        return new BigFixed(delta >= 0 ? Raw << delta : Raw >> -delta, fracBits);
    }

    public BigFixed AddDouble(double v) => this + FromDouble(v, FracBits);

    public static BigFixed operator +(BigFixed a, BigFixed b) => new(a.Raw + b.Raw, a.FracBits);
    public static BigFixed operator -(BigFixed a, BigFixed b) => new(a.Raw - b.Raw, a.FracBits);
    public static BigFixed operator *(BigFixed a, BigFixed b) => new((a.Raw * b.Raw) >> a.FracBits, a.FracBits);
}
