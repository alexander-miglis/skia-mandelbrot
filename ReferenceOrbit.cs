using System;

namespace FractalZoom;

/// <summary>
/// A high-precision orbit of one point, plus the perturbed iteration that rides on it.
///
/// The escape time of a pixel at c = C + dc can be found without ever iterating c itself: with
/// Z the orbit of C and dz = z - Z, the iteration becomes
///
///     dz' = 2*Z*dz + dz^2 + dc
///
/// dc and dz stay tiny, so their *relative* double precision still separates pixels that are
/// 1e-60 apart — which is the whole point. Only this one orbit needs big arithmetic; the millions
/// of pixels stay in hardware doubles. The orbit values themselves are kept as doubles because
/// their error is common to every pixel in the view, so it acts as a negligible shift of the whole
/// image rather than as per-pixel noise.
/// </summary>
internal sealed class ReferenceOrbit
{
    private const double Bailout2 = 1e10;

    private readonly double[] _zr;
    private readonly double[] _zi;

    private readonly BlaTable? _bla;

    private ReferenceOrbit(BigFixed cx, BigFixed cy, double[] zr, double[] zi, int count, BlaTable? bla)
    {
        CenterX = cx;
        CenterY = cy;
        _zr = zr;
        _zi = zi;
        Count = count;
        _bla = bla;
    }

    public BigFixed CenterX { get; }
    public BigFixed CenterY { get; }

    /// <summary>Number of usable orbit entries. Z[0] is 0, so rebasing to index 0 is exact.</summary>
    public int Count { get; }

    public int FracBits => CenterX.FracBits;

    /// <param name="dcMax">
    /// Largest offset from this orbit's centre that any pixel will be rendered at. Bounds the
    /// bilinear-approximation radii; pixels beyond it fall back to plain perturbation.
    /// </param>
    public static ReferenceOrbit Compute(BigFixed cx, BigFixed cy, int length, double dcMax)
    {
        length = Math.Max(2, length);
        var zr = new double[length];
        var zi = new double[length];

        var x = BigFixed.Zero(cx.FracBits);
        var y = BigFixed.Zero(cx.FracBits);
        int count = 0;

        for (int n = 0; n < length; n++)
        {
            double dr = x.ToDouble();
            double di = y.ToDouble();
            zr[n] = dr;
            zi[n] = di;
            count = n + 1;

            if (dr * dr + di * di > Bailout2) break; // reference escaped; this is all we get

            var x2 = x * x;
            var y2 = y * y;
            var xy = x * y;
            x = x2 - y2 + cx;
            y = xy + xy + cy;
        }

        return new ReferenceOrbit(cx, cy, zr, zi, count, BlaTable.Build(zr, zi, count, dcMax));
    }

    /// <summary>
    /// Escape time for the point at (reference centre + dc), in the same convention as
    /// <see cref="Mandelbrot.Escape"/>. Uses the bilinear-approximation table when the pixel is
    /// within the radius it was built for.
    /// </summary>
    public bool Escape(double dcr, double dci, int maxIter, out double smooth)
    {
        var bla = _bla;
        if (bla is not null && dcr * dcr + dci * dci > bla.DcMaxSquared) bla = null;
        return Iterate(dcr, dci, maxIter, bla, out smooth);
    }

    /// <summary>Same, with every iteration taken individually. Reference path for verification.</summary>
    public bool EscapeStepwise(double dcr, double dci, int maxIter, out double smooth) =>
        Iterate(dcr, dci, maxIter, null, out smooth);

    private bool Iterate(double dcr, double dci, int maxIter, BlaTable? bla, out double smooth)
    {
        smooth = 0;

        double[] zr = _zr, zi = _zi;
        int count = Count;
        double dzr = 0, dzi = 0;
        int n = 0;  // reference index: the current z is Z[n] + dz
        int m = 0;  // iterations completed

        while (m < maxIter)
        {
            if (bla is not null && n >= 1 &&
                bla.TryStep(n, dzr * dzr + dzi * dzi, maxIter - m, out var step))
            {
                // Whole run of steps applied as one linear map.
                double sr = step.Ar * dzr - step.Ai * dzi + step.Br * dcr - step.Bi * dci;
                double si = step.Ar * dzi + step.Ai * dzr + step.Br * dci + step.Bi * dcr;
                dzr = sr;
                dzi = si;
                n += step.Length;
                m += step.Length;
            }
            else
            {
                // The step below needs Z[n+1] to rebuild z, so rebase if the orbit is exhausted.
                if (n + 1 >= count)
                {
                    dzr += zr[n];
                    dzi += zi[n];
                    n = 0;
                }

                double zrn = zr[n], zin = zi[n];
                double nr = 2.0 * (zrn * dzr - zin * dzi) + (dzr * dzr - dzi * dzi) + dcr;
                double ni = 2.0 * (zrn * dzi + zin * dzr) + 2.0 * dzr * dzi + dci;
                dzr = nr;
                dzi = ni;
                n++;
                m++;
            }

            double fr = zr[n] + dzr;
            double fi = zi[n] + dzi;
            double mag2 = fr * fr + fi * fi;

            if (mag2 > Bailout2)
            {
                smooth = m - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }

            // Rebase whenever the orbit passes nearer the origin than the delta itself. Holding z
            // directly is then more accurate than holding it as an offset from Z[n], and since
            // Z[0] is exactly 0 the switch loses nothing. This is what removes the usual
            // perturbation "glitches" without needing a second reference orbit.
            if (mag2 < dzr * dzr + dzi * dzi)
            {
                dzr = fr;
                dzi = fi;
                n = 0;
            }
        }

        return false;
    }
}
