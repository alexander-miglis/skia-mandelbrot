using System;
using System.Threading.Tasks;

namespace FractalZoom;

/// <summary>
/// CPU Mandelbrot renderer. Runs in two passes: first an escape-time field, then a colouring
/// pass that band-limits the palette using the local gradient of that field. The second pass is
/// what keeps deep views from turning into salt-and-pepper noise — where the structure is finer
/// than a pixel, the colour converges to the palette's mean instead of sampling it at random.
/// Pixels are written as BGRA into caller-owned memory (an SKBitmap), so there is no copy.
/// </summary>
internal static unsafe class Mandelbrot
{
    /// <summary>Escape radius squared. A large bailout makes the smooth-iteration term accurate.</summary>
    private const double Bailout2 = 1e10;

    /// <summary>Colour cycles per e-fold of the smooth iteration count.</summary>
    private const double BandDensity = 2.1;

    /// <summary>Marks a pixel that never escaped.</summary>
    private const float Interior = -1f;

    /// <summary>Leave a core for the display thread so the window keeps hitting vsync.</summary>
    private static readonly ParallelOptions Options =
        new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

    /// <summary>
    /// Iterates z -> z^2 + c. Returns false for points that never escape (set interior),
    /// otherwise true with <paramref name="smooth"/> set to the fractional escape time.
    /// </summary>
    public static bool Escape(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;

        // Cheap interior tests: main cardioid and the period-2 bulb.
        double dx = cr - 0.25;
        double q = dx * dx + ci * ci;
        if (q * (q + dx) <= 0.25 * ci * ci) return false;
        double px = cr + 1.0;
        if (px * px + ci * ci <= 0.0625) return false;

        double zr = 0, zi = 0, zr2 = 0, zi2 = 0;
        // Periodicity detection: interior orbits settle into a cycle, so once the orbit revisits
        // a remembered point we can stop instead of burning the whole iteration budget.
        double oldR = 0, oldI = 0;
        int since = 0, checkAt = 8;

        for (int n = 0; n < maxIter; n++)
        {
            zi = 2.0 * zr * zi + ci;
            zr = zr2 - zi2 + cr;
            zr2 = zr * zr;
            zi2 = zi * zi;

            double mag2 = zr2 + zi2;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }

            if (Math.Abs(zr - oldR) < 1e-16 && Math.Abs(zi - oldI) < 1e-16)
                return false;

            if (++since > checkAt)
            {
                since = 0;
                checkAt <<= 1;
                oldR = zr;
                oldI = zi;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders the view into <paramref name="pixels"/> (BGRA8888, <paramref name="stride"/> bytes
    /// per row). <paramref name="field"/> must hold at least width*height floats and is used as
    /// scratch for the escape-time pass.
    /// </summary>
    /// <param name="reference">
    /// When non-null, the view is rendered by perturbation against this orbit and
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) are read as an offset from the
    /// orbit's centre rather than as absolute coordinates. This is what lets the zoom continue
    /// past the ~1e13 magnification where plain doubles stop resolving neighbouring pixels.
    /// </param>
    public static void Render(
        IntPtr pixels, int stride, int width, int height, float[] field,
        double centerX, double centerY, double scale,
        int maxIter, Palette palette, double paletteShift, ReferenceOrbit? reference)
    {
        EscapeField(field, width, height, centerX, centerY, scale, maxIter, reference);
        Colourise((byte*)pixels, stride, width, height, field, palette, paletteShift);
    }

    private static void EscapeField(
        float[] field, int width, int height,
        double centerX, double centerY, double scale, int maxIter, ReferenceOrbit? reference)
    {
        double pixelSize = 2.0 * scale / height;
        double halfW = width * 0.5;
        double halfH = height * 0.5;

        Parallel.For(0, height, Options, y =>
        {
            int row = y * width;
            double ci = centerY - (y - halfH + 0.5) * pixelSize;

            if (reference is null)
            {
                for (int x = 0; x < width; x++)
                {
                    double cr = centerX + (x - halfW + 0.5) * pixelSize;
                    field[row + x] = Escape(cr, ci, maxIter, out double smooth)
                        ? (float)Math.Max(0.0, smooth)
                        : Interior;
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    double cr = centerX + (x - halfW + 0.5) * pixelSize;
                    field[row + x] = reference.Escape(cr, ci, maxIter, out double smooth)
                        ? (float)Math.Max(0.0, smooth)
                        : Interior;
                }
            }
        });
    }

    /// <summary>
    /// Turns the escape field into colours. The palette is a cosine gradient, so a pixel spanning
    /// dv colour cycles has its oscillating component attenuated toward the gradient's mean —
    /// analytic antialiasing, at the cost of one extra pass over the pixels.
    /// </summary>
    private static void Colourise(
        byte* pixels, int stride, int width, int height, float[] field,
        Palette palette, double shift)
    {
        uint[] colors = palette.Colors;
        int mask = colors.Length - 1;
        int meanR = palette.MeanR, meanG = palette.MeanG, meanB = palette.MeanB;

        Parallel.For(0, height, Options, y =>
        {
            uint* dst = (uint*)(pixels + (long)y * stride);
            int row = y * width;
            int up = y > 0 ? row - width : row;
            int down = y < height - 1 ? row + width : row;

            for (int x = 0; x < width; x++)
            {
                float f = field[row + x];
                if (f < 0f)
                {
                    dst[x] = 0xFF000000u;
                    continue;
                }

                // Central differences, falling back to this pixel wherever a neighbour is
                // interior so the set's edge doesn't register as an infinite gradient.
                int xl = x > 0 ? x - 1 : x;
                int xr = x < width - 1 ? x + 1 : x;
                float gx = Delta(field[row + xr], field[row + xl], f);
                float gy = Delta(field[down + x], field[up + x], f);
                float g = Math.Max(gx, gy);

                // v is the palette coordinate; dv is how much of it this pixel covers.
                double v = Math.Log(1.0 + f) * BandDensity + shift;
                double dv = BandDensity * g / (1.0 + f);
                double atten = 1.0 / (1.0 + 3.0 * dv * dv);

                v -= Math.Floor(v);
                uint c = colors[(int)(v * colors.Length) & mask];

                int r = meanR + (int)((((c >> 16) & 0xFF) - meanR) * atten);
                int gg = meanG + (int)((((c >> 8) & 0xFF) - meanG) * atten);
                int b = meanB + (int)(((c & 0xFF) - meanB) * atten);

                dst[x] = 0xFF000000u | ((uint)r << 16) | ((uint)gg << 8) | (uint)b;
            }
        });
    }

    /// <summary>Half the absolute central difference, ignoring interior neighbours.</summary>
    private static float Delta(float a, float b, float centre)
    {
        if (a < 0f) a = centre;
        if (b < 0f) b = centre;
        return Math.Abs(a - b) * 0.5f;
    }

    /// <summary>A cyclic colour ramp plus the mean colour that its bands average to.</summary>
    internal sealed class Palette
    {
        public required uint[] Colors { get; init; }
        public required int MeanR { get; init; }
        public required int MeanG { get; init; }
        public required int MeanB { get; init; }
    }

    /// <summary>Builds a 4096-entry cyclic palette from a cosine gradient.</summary>
    public static Palette Build(Gradient grad)
    {
        const int n = 4096;
        var colors = new uint[n];
        long sr = 0, sg = 0, sb = 0;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;
            byte r = Channel(grad.AR, grad.BR, grad.CR, grad.DR, t);
            byte g = Channel(grad.AG, grad.BG, grad.CG, grad.DG, t);
            byte b = Channel(grad.AB, grad.BB, grad.CB, grad.DB, t);
            colors[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            sr += r;
            sg += g;
            sb += b;
        }

        return new Palette
        {
            Colors = colors,
            MeanR = (int)(sr / n),
            MeanG = (int)(sg / n),
            MeanB = (int)(sb / n),
        };
    }

    private static byte Channel(double a, double b, double c, double d, double t)
    {
        double v = a + b * Math.Cos(2.0 * Math.PI * (c * t + d));
        v = Math.Clamp(v, 0.0, 1.0);
        return (byte)(Math.Pow(v, 1.0 / 1.6) * 255.0 + 0.5); // gamma, so mid tones aren't muddy
    }

    /// <summary>Coefficients of a cosine gradient: colour(t) = a + b * cos(2*pi*(c*t + d)).</summary>
    internal readonly record struct Gradient(
        double AR, double AG, double AB,
        double BR, double BG, double BB,
        double CR, double CG, double CB,
        double DR, double DG, double DB)
    {
        public static readonly Gradient[] All =
        [
            // Electric — blue through gold
            new(0.50, 0.50, 0.50, 0.50, 0.50, 0.50, 1, 1, 1, 0.00, 0.10, 0.20),
            // Ember — black, red, amber, white
            new(0.50, 0.36, 0.22, 0.50, 0.38, 0.26, 1, 1, 1, 0.00, 0.12, 0.26),
            // Aurora — teal and magenta
            new(0.44, 0.50, 0.48, 0.38, 0.46, 0.42, 1, 1, 1, 0.36, 0.20, 0.06),
            // Abyss — deep indigo to ice
            new(0.30, 0.34, 0.52, 0.34, 0.36, 0.46, 1, 1, 1, 0.62, 0.55, 0.42),
            // Copper — sepia with cyan highlights
            new(0.48, 0.42, 0.34, 0.44, 0.40, 0.44, 1, 1, 1, 0.10, 0.06, 0.55),
        ];
    }
}
