using System;

namespace FractalZoom;

/// <summary>
/// A number held as an unevaluated sum of two doubles, which gives about 32 significant digits
/// instead of 16.
///
/// This is the middle rung of the program's three kinds of arithmetic. Plain doubles resolve a view
/// down to about 1e-13 of the whole set and are what the kernels run on. <see cref="BigFixed"/> is
/// arbitrarily precise but allocates and is only affordable for the one reference orbit a whole frame
/// shares. In between sit the things that need more than a double per value but are evaluated tens of
/// thousands of times a frame: the camera anchor, and the coordinates of the fractals that are drawn
/// as geometry rather than sampled.
///
/// The representation is the usual one — <see cref="Hi"/> is the nearest double to the value and
/// <see cref="Lo"/> is the remainder, with |Lo| at most half an ulp of Hi — and the operations are
/// the standard error-free transformations, which recover the bits an ordinary add or multiply throws
/// away and carry them in the second component.
/// </summary>
internal readonly struct Dd : IEquatable<Dd>
{
    public readonly double Hi;
    public readonly double Lo;

    public Dd(double hi, double lo)
    {
        Hi = hi;
        Lo = lo;
    }

    public Dd(double value)
    {
        Hi = value;
        Lo = 0.0;
    }

    public static readonly Dd Zero = new(0.0, 0.0);

    public double ToDouble() => Hi + Lo;

    public bool IsZero => Hi == 0.0 && Lo == 0.0;

    public static implicit operator Dd(double value) => new(value);

    // ---- Error-free transformations. Every operation below is built out of these three. ----

    /// <summary>Exact sum of two doubles, given that |a| is at least |b|.</summary>
    private static Dd QuickSum(double a, double b)
    {
        double s = a + b;
        return new Dd(s, b - (s - a));
    }

    /// <summary>Exact sum of two doubles, with no assumption about their sizes.</summary>
    private static Dd Sum(double a, double b)
    {
        double s = a + b;
        double bb = s - a;
        return new Dd(s, (a - (s - bb)) + (b - bb));
    }

    /// <summary>
    /// Exact product of two doubles. The fused multiply-add computes a*b to full width and rounds
    /// once, so subtracting the rounded product from it leaves exactly the bits that were lost —
    /// which is the whole trick, and why this needs no splitting of the operands.
    /// </summary>
    private static Dd Product(double a, double b)
    {
        double p = a * b;
        return new Dd(p, Math.FusedMultiplyAdd(a, b, -p));
    }

    // ---- Arithmetic. ----

    public static Dd operator +(Dd a, double b)
    {
        var s = Sum(a.Hi, b);
        return QuickSum(s.Hi, s.Lo + a.Lo);
    }

    public static Dd operator +(double a, Dd b) => b + a;

    public static Dd operator +(Dd a, Dd b)
    {
        var s = Sum(a.Hi, b.Hi);
        var t = Sum(a.Lo, b.Lo);
        s = QuickSum(s.Hi, s.Lo + t.Hi);
        return QuickSum(s.Hi, s.Lo + t.Lo);
    }

    public static Dd operator -(Dd a) => new(-a.Hi, -a.Lo);

    public static Dd operator -(Dd a, Dd b) => a + -b;

    public static Dd operator -(Dd a, double b) => a + -b;

    public static Dd operator -(double a, Dd b) => -b + a;

    public static Dd operator *(Dd a, double b)
    {
        var p = Product(a.Hi, b);
        return QuickSum(p.Hi, p.Lo + a.Lo * b);
    }

    public static Dd operator *(double a, Dd b) => b * a;

    public static Dd operator *(Dd a, Dd b)
    {
        var p = Product(a.Hi, b.Hi);
        return QuickSum(p.Hi, p.Lo + (a.Hi * b.Lo + a.Lo * b.Hi));
    }

    public static Dd operator /(Dd a, double b) => a / new Dd(b);

    public static Dd operator /(Dd a, Dd b)
    {
        // Long division, three digits at a time: take a quotient estimate from the leading doubles,
        // subtract off what it accounts for exactly, and repeat on the remainder.
        double q1 = a.Hi / b.Hi;
        var r = a - b * q1;

        double q2 = r.Hi / b.Hi;
        r -= b * q2;

        double q3 = r.Hi / b.Hi;

        var q = QuickSum(q1, q2);
        return q + q3;
    }

    public static Dd Abs(Dd a) => a.Hi < 0.0 ? -a : a;

    public static Dd Sqrt(Dd a)
    {
        if (a.Hi <= 0.0) return Zero;

        // One Newton step on the double root, which doubles its correct digits — enough, because a
        // double root is already accurate to half of what a Dd holds.
        double x = Math.Sqrt(a.Hi);
        var d = (a - Product(x, x)) * (0.5 / x);
        return new Dd(x) + d;
    }

    /// <summary>Largest integer at or below the value, as a double.</summary>
    public double Floor()
    {
        double f = Math.Floor(Hi);
        if (f != Hi) return f;
        return f + Math.Floor(Lo);   // Hi was whole, so the fraction is all in Lo
    }

    // ---- Comparison. Lexicographic on the two components, which is the order of the values. ----

    public static bool operator <(Dd a, Dd b) => a.Hi < b.Hi || (a.Hi == b.Hi && a.Lo < b.Lo);

    public static bool operator >(Dd a, Dd b) => a.Hi > b.Hi || (a.Hi == b.Hi && a.Lo > b.Lo);

    public static bool operator <=(Dd a, Dd b) => !(a > b);

    public static bool operator >=(Dd a, Dd b) => !(a < b);

    public static bool operator ==(Dd a, Dd b) => a.Hi == b.Hi && a.Lo == b.Lo;

    public static bool operator !=(Dd a, Dd b) => !(a == b);

    public bool Equals(Dd other) => this == other;

    public override bool Equals(object? obj) => obj is Dd other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Hi, Lo);

    public override string ToString() => ToDouble().ToString("G17");
}
