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
    /// Iterates whichever formula <paramref name="kind"/> names. Returns false for points that never
    /// escape (set interior), otherwise true with <paramref name="smooth"/> set to the fractional
    /// escape time.
    /// </summary>
    public static bool Escape(double cr, double ci, int maxIter, Fractal kind, out double smooth)
    {
        smooth = 0;
        return kind switch
        {
            Fractal.Mandelbrot => Escape(cr, ci, maxIter, out smooth),
            Fractal.Julia => Quadratic(cr, ci, FractalKind.JuliaCr, FractalKind.JuliaCi, maxIter, out smooth),
            Fractal.BurningShip => BurningShip(cr, ci, maxIter, out smooth),
            Fractal.Tricorn => Tricorn(cr, ci, maxIter, out smooth),
            Fractal.Multibrot => Multibrot(cr, ci, maxIter, out smooth),
            Fractal.Phoenix => Phoenix(cr, ci, maxIter, out smooth),
            Fractal.Newton => Newton(cr, ci, maxIter, 0.0, 0.0, out smooth),
            Fractal.Nova => Newton(cr, ci, maxIter, cr, ci, out smooth),
            Fractal.Magnet => Magnet(cr, ci, maxIter, out smooth),
            Fractal.Lyapunov => Lyapunov(cr, ci, maxIter, out smooth),
            Fractal.PickoverStalks => Trap(cr, ci, maxIter, stalks: true, out smooth),
            Fractal.OrbitTrap => Trap(cr, ci, maxIter, stalks: false, out smooth),
            Fractal.SierpinskiTriangle => SierpinskiTriangle(cr, ci, maxIter, out smooth),
            Fractal.SierpinskiCarpet => SierpinskiCarpet(cr, ci, maxIter, out smooth),

            // Anything else is not a field at all — it is drawn, or ray-marched on the card — and has
            // no escape time to give. Falling through to a formula here would render something that
            // is not what was asked for, which is worse than rendering nothing.
            _ => false,
        };
    }

    /// <summary>
    /// The same formulas, iterated in <see cref="Dd"/> instead of doubles.
    ///
    /// Only the arithmetic differs — every recurrence, every bailout and every colouring below is the
    /// double version's, line for line — so this exists purely to carry the formulas without a
    /// perturbed form deeper than a double reaches. Perturbation would be faster and go far deeper
    /// still, but it has to be derived per formula, and for some of these (the logistic map's
    /// Lyapunov exponent, say) there is nothing to derive: the iteration is not analytic in the
    /// parameter. Wider arithmetic needs no derivation and works for all of them.
    ///
    /// It costs about fifteen times a double's iteration, which the host absorbs by rendering fewer
    /// pixels: deep views on these formulas go soft rather than stopping.
    /// </summary>
    public static bool Escape(Dd cr, Dd ci, int maxIter, Fractal kind, out double smooth)
    {
        smooth = 0;
        return kind switch
        {
            Fractal.Mandelbrot => QuadraticWide(Dd.Zero, Dd.Zero, cr, ci, maxIter, 1.0, out smooth),
            Fractal.Julia => QuadraticWide(cr, ci, FractalKind.JuliaCr, FractalKind.JuliaCi, maxIter,
                1.0, out smooth),
            Fractal.BurningShip => BurningShipWide(cr, ci, maxIter, out smooth),
            Fractal.Tricorn => QuadraticWide(Dd.Zero, Dd.Zero, cr, ci, maxIter, -1.0, out smooth),
            Fractal.Multibrot => MultibrotWide(cr, ci, maxIter, out smooth),
            Fractal.Phoenix => PhoenixWide(cr, ci, maxIter, out smooth),
            Fractal.Newton => NewtonWide(cr, ci, maxIter, false, out smooth),
            Fractal.Nova => NewtonWide(cr, ci, maxIter, true, out smooth),
            Fractal.Magnet => MagnetWide(cr, ci, maxIter, out smooth),
            Fractal.Lyapunov => LyapunovWide(cr, ci, maxIter, out smooth),
            Fractal.PickoverStalks => TrapWide(cr, ci, maxIter, stalks: true, out smooth),
            Fractal.OrbitTrap => TrapWide(cr, ci, maxIter, stalks: false, out smooth),
            Fractal.SierpinskiTriangle => SierpinskiTriangleWide(cr, ci, maxIter, out smooth),
            Fractal.SierpinskiCarpet => SierpinskiCarpetWide(cr, ci, maxIter, out smooth),
            _ => false,
        };
    }

    /// <summary>
    /// z -> z^2 + k, or with <paramref name="conjugate"/> negative the Tricorn's z -> conj(z)^2 + k.
    /// Covers three of the entries: the Mandelbrot and Tricorn start at zero and take the pixel as k,
    /// a Julia starts at the pixel and takes k fixed.
    /// </summary>
    private static bool QuadraticWide(
        Dd zr, Dd zi, Dd kr, Dd ki, int maxIter, double conjugate, out double smooth)
    {
        smooth = 0;

        for (int n = 0; n < maxIter; n++)
        {
            var next = zr * zr - zi * zi + kr;
            zi = zr * zi * (2.0 * conjugate) + ki;
            zr = next;

            double mag2 = (zr * zr + zi * zi).ToDouble();
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }
        }

        return false;
    }

    private static bool BurningShipWide(Dd cr, Dd ci, int maxIter, out double smooth)
    {
        smooth = 0;
        ci = -ci;
        Dd zr = Dd.Zero, zi = Dd.Zero;

        for (int n = 0; n < maxIter; n++)
        {
            var next = zr * zr - zi * zi + cr;
            zi = Dd.Abs(zr * zi) * 2.0 + ci;
            zr = next;

            double mag2 = (zr * zr + zi * zi).ToDouble();
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }
        }

        return false;
    }

    private static bool MultibrotWide(Dd cr, Dd ci, int maxIter, out double smooth)
    {
        smooth = 0;
        Dd zr = Dd.Zero, zi = Dd.Zero;
        const double logDegree = 1.0986122886681098; // log 3

        for (int n = 0; n < maxIter; n++)
        {
            var r2 = zr * zr;
            var i2 = zi * zi;
            var next = zr * (r2 - i2 * 3.0) + cr;
            zi = zi * (r2 * 3.0 - i2) + ci;
            zr = next;

            double mag2 = (zr * zr + zi * zi).ToDouble();
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log(0.5 * Math.Log(mag2)) / logDegree;
                return true;
            }
        }

        return false;
    }

    private static bool PhoenixWide(Dd cr, Dd ci, int maxIter, out double smooth)
    {
        smooth = 0;
        Dd zr = ci, zi = cr;
        Dd prevR = Dd.Zero, prevI = Dd.Zero;

        for (int n = 0; n < maxIter; n++)
        {
            var nextR = zr * zr - zi * zi + FractalKind.PhoenixP1 + prevR * FractalKind.PhoenixP2;
            var nextI = zr * zi * 2.0 + prevI * FractalKind.PhoenixP2;
            prevR = zr;
            prevI = zi;
            zr = nextR;
            zi = nextI;

            double mag2 = (zr * zr + zi * zi).ToDouble();
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }
        }

        return false;
    }

    private static bool NewtonWide(Dd zr, Dd zi, int maxIter, bool nova, out double smooth)
    {
        smooth = 0;
        Dd addR = nova ? zr : Dd.Zero, addI = nova ? zi : Dd.Zero;
        if (nova) { zr = 1.0; zi = Dd.Zero; }

        for (int n = 0; n < maxIter; n++)
        {
            var r2 = zr * zr;
            var i2 = zi * zi;
            var sqR = r2 - i2;
            var sqI = zr * zi * 2.0;
            var cuR = zr * sqR - zi * sqI;
            var cuI = zr * sqI + zi * sqR;
            var numR = cuR - 1.0;
            var numI = cuI;
            var denR = sqR * 3.0;
            var denI = sqI * 3.0;

            var d = denR * denR + denI * denI;
            if (d.Hi < 1e-300) return false;

            var qR = (numR * denR + numI * denI) / d;
            var qI = (numI * denR - numR * denI) / d;

            var nextR = zr - qR + addR;
            var nextI = zi - qI + addI;
            double stepR = (nextR - zr).ToDouble(), stepI = (nextI - zi).ToDouble();
            zr = nextR;
            zi = nextI;

            if (stepR * stepR + stepI * stepI < Settled)
            {
                double angle = Math.Atan2(zi.ToDouble(), zr.ToDouble());
                int root = (int)Math.Round(angle / (2.0 * Math.PI / 3.0));
                smooth = n + 0.34 * ((root + 3) % 3);
                return true;
            }

            if ((zr * zr + zi * zi).ToDouble() > 1e12) return false;
        }

        return false;
    }

    private static bool MagnetWide(Dd cr, Dd ci, int maxIter, out double smooth)
    {
        smooth = 0;
        Dd zr = Dd.Zero, zi = Dd.Zero;

        for (int n = 0; n < maxIter; n++)
        {
            var numR = zr * zr - zi * zi + cr - 1.0;
            var numI = zr * zi * 2.0 + ci;
            var denR = zr * 2.0 + cr - 2.0;
            var denI = zi * 2.0 + ci;

            var d = denR * denR + denI * denI;
            if (d.Hi < 1e-300) return false;

            var qR = (numR * denR + numI * denI) / d;
            var qI = (numI * denR - numR * denI) / d;

            var nextR = qR * qR - qI * qI;
            var nextI = qR * qI * 2.0;
            double stepR = (nextR - zr).ToDouble(), stepI = (nextI - zi).ToDouble();
            zr = nextR;
            zi = nextI;

            double mag2 = (zr * zr + zi * zi).ToDouble();
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }

            if (stepR * stepR + stepI * stepI < Settled)
            {
                smooth = n;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The logistic map's Lyapunov exponent, iterated wide. The exponent itself is a sum of a few
    /// thousand logarithms, each accurate to a double's sixteen digits and then divided by their
    /// count, so it stays a double — what needs the width is the map, whose sensitivity to its growth
    /// rate is the whole reason the picture has structure at every scale.
    /// </summary>
    private static bool LyapunovWide(Dd a, Dd b, int maxIter, out double smooth)
    {
        smooth = 0;

        const string sequence = "AABAB";
        int budget = Math.Clamp(maxIter, 80, 3000);

        Dd x = 0.5;
        for (int n = 0; n < 24; n++)
        {
            var r = sequence[n % sequence.Length] == 'A' ? a : b;
            x = r * x * (1.0 - x);
        }

        double sum = 0;
        for (int n = 0; n < budget; n++)
        {
            var r = sequence[n % sequence.Length] == 'A' ? a : b;
            x = r * x * (1.0 - x);

            double slope = Math.Abs((r * (1.0 - x * 2.0)).ToDouble());
            if (slope < 1e-300) { sum -= 690.0; continue; }
            sum += Math.Log(slope);
        }

        double exponent = sum / budget;
        if (exponent >= 0) return false;

        smooth = Math.Min(60.0, -exponent * 24.0);
        return true;
    }

    private static bool TrapWide(Dd cr, Dd ci, int maxIter, bool stalks, out double smooth)
    {
        smooth = 0;

        Dd zr = stalks ? cr : Dd.Zero;
        Dd zi = stalks ? ci : Dd.Zero;
        Dd kr = stalks ? -0.7269 : cr;
        Dd ki = stalks ? 0.1889 : ci;

        double nearest = double.MaxValue;
        int budget = Math.Min(maxIter, 400);

        for (int n = 0; n < budget; n++)
        {
            var next = zr * zr - zi * zi + kr;
            zi = zr * zi * 2.0 + ki;
            zr = next;

            double r = zr.ToDouble(), i = zi.ToDouble();
            double mag2 = r * r + i * i;
            if (mag2 > 1e6) break;

            double distance = stalks
                ? Math.Min(Math.Abs(r), Math.Abs(i))
                : Math.Abs(Math.Sqrt(mag2) - 0.5);
            if (distance < nearest) nearest = distance;
        }

        if (nearest == double.MaxValue) return false;

        smooth = Math.Min(40.0, -Math.Log(Math.Max(1e-12, nearest)) * 3.0);
        return true;
    }

    /// <summary>
    /// The Sierpinski tests are the two that gain most from the width, because what they consume is
    /// the coordinate's bits themselves — one per doubling, or log2(3) per tripling. A double runs out
    /// after forty-five levels; this holds twice as many, and the pictures are that much deeper.
    /// </summary>
    private static bool SierpinskiTriangleWide(Dd x, Dd y, int maxIter, out double smooth)
    {
        smooth = 0;
        if (x.Hi < 0 || x.Hi > 1 || y.Hi < 0 || y.Hi > 1) return false;

        int budget = Math.Min(maxIter, 100);
        for (int n = 0; n < budget; n++)
        {
            bool right = x >= new Dd(0.5), top = y >= new Dd(0.5);
            if (right && top) { smooth = n; return true; }

            if (top) { y = y * 2.0 - 1.0; x *= 2.0; }
            else if (right) { x = x * 2.0 - 1.0; y *= 2.0; }
            else { x *= 2.0; y *= 2.0; }
        }

        return false;
    }

    private static bool SierpinskiCarpetWide(Dd x, Dd y, int maxIter, out double smooth)
    {
        smooth = 0;
        if (x.Hi < 0 || x.Hi > 1 || y.Hi < 0 || y.Hi > 1) return false;

        int budget = Math.Min(maxIter, 62);
        for (int n = 0; n < budget; n++)
        {
            x *= 3.0;
            y *= 3.0;
            double dx = x.Floor(), dy = y.Floor();
            if (dx == 1.0 && dy == 1.0) { smooth = n; return true; }

            x -= dx;
            y -= dy;
        }

        return false;
    }

    /// <summary>Relative step below which a converging iteration is called settled.</summary>
    private const double Settled = 1e-12;

    /// <summary>
    /// Newton's method on z^3 - 1, and with <paramref name="addR"/> set, the Nova variant that adds
    /// the pixel back in each step. Both converge rather than escape, so what is coloured is how long
    /// they took and which of the three roots they landed on — the basins are the picture.
    /// </summary>
    private static bool Newton(double zr, double zi, int maxIter, double addR, double addI, out double smooth)
    {
        smooth = 0;

        // Nova starts from a fixed point and reads the pixel as a shift; plain Newton starts from
        // the pixel itself, which is what makes the pixel's basin the thing on show.
        bool nova = addR != 0.0 || addI != 0.0;
        if (nova) { zr = 1.0; zi = 0.0; }

        for (int n = 0; n < maxIter; n++)
        {
            // z - (z^3 - 1) / (3 z^2)
            double r2 = zr * zr, i2 = zi * zi;
            double sqR = r2 - i2, sqI = 2.0 * zr * zi;                 // z^2
            double cuR = zr * sqR - zi * sqI, cuI = zr * sqI + zi * sqR; // z^3
            double numR = cuR - 1.0, numI = cuI;
            double denR = 3.0 * sqR, denI = 3.0 * sqI;

            double d = denR * denR + denI * denI;
            if (d < 1e-300) return false;

            double qR = (numR * denR + numI * denI) / d;
            double qI = (numI * denR - numR * denI) / d;

            double nextR = zr - qR + addR;
            double nextI = zi - qI + addI;
            double stepR = nextR - zr, stepI = nextI - zi;
            zr = nextR;
            zi = nextI;

            if (stepR * stepR + stepI * stepI < Settled)
            {
                // Which root it settled on shifts the palette, so the three basins read as three
                // families of colour rather than one.
                double angle = Math.Atan2(zi, zr);
                int root = (int)Math.Round(angle / (2.0 * Math.PI / 3.0));
                smooth = n + 0.34 * ((root + 3) % 3);
                return true;
            }

            if (zr * zr + zi * zi > 1e12) return false;
        }

        return false;
    }

    /// <summary>
    /// The magnet-1 map, ((z^2 + c - 1) / (2z + c - 2))^2, which comes from a physical model of phase
    /// transitions. It both escapes and converges — to one rather than to zero — so both endings have
    /// to be tested for.
    /// </summary>
    private static bool Magnet(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;
        double zr = 0, zi = 0;

        for (int n = 0; n < maxIter; n++)
        {
            double numR = zr * zr - zi * zi + cr - 1.0;
            double numI = 2.0 * zr * zi + ci;
            double denR = 2.0 * zr + cr - 2.0;
            double denI = 2.0 * zi + ci;

            double d = denR * denR + denI * denI;
            if (d < 1e-300) return false;

            double qR = (numR * denR + numI * denI) / d;
            double qI = (numI * denR - numR * denI) / d;

            double nextR = qR * qR - qI * qI;
            double nextI = 2.0 * qR * qI;
            double stepR = nextR - zr, stepI = nextI - zi;
            zr = nextR;
            zi = nextI;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }

            if (stepR * stepR + stepI * stepI < Settled)
            {
                smooth = n;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lyapunov exponent of the logistic map with its growth rate alternating between the two
    /// coordinates on a fixed schedule. Negative means the orbit settles and is what the structure
    /// is made of; positive means chaos. Not an escape time at all — a rate of divergence — which is
    /// why the whole plane gets a value and none of it is interior.
    /// </summary>
    private static bool Lyapunov(double a, double b, int maxIter, out double smooth)
    {
        smooth = 0;

        // "AABAB" gives the familiar swallow shapes; the sequence is what picks the pattern.
        const string sequence = "AABAB";
        int budget = Math.Clamp(maxIter, 80, 3000);

        double x = 0.5;
        for (int n = 0; n < 24; n++)  // settle first, so the exponent is of the attractor
        {
            double r = sequence[n % sequence.Length] == 'A' ? a : b;
            x = r * x * (1.0 - x);
        }

        double sum = 0;
        for (int n = 0; n < budget; n++)
        {
            double r = sequence[n % sequence.Length] == 'A' ? a : b;
            x = r * x * (1.0 - x);

            double slope = Math.Abs(r * (1.0 - 2.0 * x));
            if (slope < 1e-300) { sum -= 690.0; continue; }
            sum += Math.Log(slope);
        }

        double exponent = sum / budget;
        if (exponent >= 0) return false;              // chaotic: left as interior, so it reads as black

        smooth = Math.Min(60.0, -exponent * 24.0);   // stable: deeper negatives get more colour
        return true;
    }

    /// <summary>
    /// Orbit traps. The iteration is the Mandelbrot's or a Julia's, but what is recorded is how close
    /// the orbit ever came to something — the axes for Pickover's stalks, a circle otherwise —
    /// rather than when it escaped. The same dynamics, a completely different picture.
    /// </summary>
    private static bool Trap(double cr, double ci, int maxIter, bool stalks, out double smooth)
    {
        smooth = 0;

        // Stalks are conventionally drawn on a Julia set, where the orbit lingers near the axes;
        // the circle trap goes on the Mandelbrot.
        double zr = stalks ? cr : 0.0;
        double zi = stalks ? ci : 0.0;
        double kr = stalks ? -0.7269 : cr;
        double ki = stalks ? 0.1889 : ci;

        double nearest = double.MaxValue;
        int budget = Math.Min(maxIter, 400);   // the trap settles long before an escape count would

        for (int n = 0; n < budget; n++)
        {
            double next = zr * zr - zi * zi + kr;
            zi = 2.0 * zr * zi + ki;
            zr = next;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > 1e6) break;

            double distance = stalks
                ? Math.Min(Math.Abs(zr), Math.Abs(zi))
                : Math.Abs(Math.Sqrt(mag2) - 0.5);
            if (distance < nearest) nearest = distance;
        }

        if (nearest == double.MaxValue) return false;

        smooth = Math.Min(40.0, -Math.Log(Math.Max(1e-12, nearest)) * 3.0);
        return true;
    }

    /// <summary>
    /// The Sierpinski triangle as a per-pixel test: double the coordinate repeatedly and see how many
    /// doublings it survives before landing in the quadrant that was cut out. Counting the survivals
    /// gives the same graded colouring the escape-time sets get, and it keeps working as far down as
    /// the arithmetic does, which drawing the triangle would not.
    /// </summary>
    private static bool SierpinskiTriangle(double x, double y, int maxIter, out double smooth)
    {
        smooth = 0;
        if (x < 0 || x > 1 || y < 0 || y > 1) return false;

        int budget = Math.Min(maxIter, 45);   // doubling exhausts a double's mantissa by here
        for (int n = 0; n < budget; n++)
        {
            if (x >= 0.5 && y >= 0.5) { smooth = n; return true; }

            if (y >= 0.5) { y = 2.0 * y - 1.0; x = 2.0 * x; }
            else if (x >= 0.5) { x = 2.0 * x - 1.0; y = 2.0 * y; }
            else { x = 2.0 * x; y = 2.0 * y; }
        }

        return false;
    }

    /// <summary>
    /// The Sierpinski carpet, tested the same way but in base three: the middle ninth is what gets
    /// cut, so a coordinate is excluded when both of its digits are the middle one.
    /// </summary>
    private static bool SierpinskiCarpet(double x, double y, int maxIter, out double smooth)
    {
        smooth = 0;
        if (x < 0 || x > 1 || y < 0 || y > 1) return false;

        int budget = Math.Min(maxIter, 28);
        for (int n = 0; n < budget; n++)
        {
            x *= 3.0;
            y *= 3.0;
            int dx = (int)x, dy = (int)y;
            if (dx == 1 && dy == 1) { smooth = n; return true; }

            x -= dx;
            y -= dy;
        }

        return false;
    }

    /// <summary>
    /// Iterates z -> z^2 + c. Returns false for points that never escape (set interior),
    /// otherwise true with <paramref name="smooth"/> set to the fractional escape time.
    /// </summary>
    public static bool Escape(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;

        // Cheap interior tests: main cardioid and the period-2 bulb. Specific to this formula —
        // the others have differently shaped interiors and get only periodicity detection.
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
    /// z -> z^2 + k, starting from the pixel rather than from zero. With k fixed this is a Julia set;
    /// the Mandelbrot is the same iteration with k taken from the pixel and z starting at zero, which
    /// is why the two look like each other everywhere.
    /// </summary>
    private static bool Quadratic(double zr, double zi, double kr, double ki, int maxIter, out double smooth)
    {
        smooth = 0;
        double oldR = 0, oldI = 0;
        int since = 0, checkAt = 8;

        for (int n = 0; n < maxIter; n++)
        {
            double next = zr * zr - zi * zi + kr;
            zi = 2.0 * zr * zi + ki;
            zr = next;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }

            if (Math.Abs(zr - oldR) < 1e-16 && Math.Abs(zi - oldI) < 1e-16) return false;
            if (++since > checkAt) { since = 0; checkAt <<= 1; oldR = zr; oldI = zi; }
        }

        return false;
    }

    /// <summary>
    /// z -> (|Re z| + i|Im z|)^2 + c. The absolute values break the conjugate symmetry, which is
    /// what turns the familiar bulbs into the masts and hulls it is named for.
    /// </summary>
    private static bool BurningShip(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;

        // Drawn with the imaginary axis inverted, which is the orientation it is always shown in —
        // hull down, masts up. Only the formula's reading of the coordinate is flipped, so the
        // mapping from pixels to the plane is untouched and zooming at the pointer still holds.
        ci = -ci;
        double zr = 0, zi = 0;

        for (int n = 0; n < maxIter; n++)
        {
            double next = zr * zr - zi * zi + cr;
            zi = 2.0 * Math.Abs(zr * zi) + ci;
            zr = next;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }
        }

        return false;
    }

    /// <summary>z -> conj(z)^2 + c, which reflects the iteration and folds the set into three lobes.</summary>
    private static bool Tricorn(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;
        double zr = 0, zi = 0;

        for (int n = 0; n < maxIter; n++)
        {
            double next = zr * zr - zi * zi + cr;
            zi = -2.0 * zr * zi + ci;
            zr = next;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// z -> z^3 + c. The smooth count is logarithmic in the degree, so the base of that logarithm
    /// changes with it — at degree two it reduces to the expression the others use.
    /// </summary>
    private static bool Multibrot(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;
        double zr = 0, zi = 0;
        const double logDegree = 1.0986122886681098; // log 3

        for (int n = 0; n < maxIter; n++)
        {
            double r2 = zr * zr, i2 = zi * zi;
            double next = zr * (r2 - 3.0 * i2) + cr;
            zi = zi * (3.0 * r2 - i2) + ci;
            zr = next;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log(0.5 * Math.Log(mag2)) / logDegree;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// z -> z^2 + c + p*z_previous. Carrying one iterate of history makes it the only formula here
    /// that is not a plain function of the current point, and the reason it grows curling tendrils
    /// rather than bulbs.
    /// </summary>
    private static bool Phoenix(double cr, double ci, int maxIter, out double smooth)
    {
        smooth = 0;

        // Iterated from the pixel, like a Julia set, and with the axes swapped, which is what stands
        // the flames upright.
        double zr = ci, zi = cr;
        double prevR = 0, prevI = 0;

        for (int n = 0; n < maxIter; n++)
        {
            double nextR = zr * zr - zi * zi + FractalKind.PhoenixP1 + FractalKind.PhoenixP2 * prevR;
            double nextI = 2.0 * zr * zi + FractalKind.PhoenixP2 * prevI;
            prevR = zr;
            prevI = zi;
            zr = nextR;
            zi = nextI;

            double mag2 = zr * zr + zi * zi;
            if (mag2 > Bailout2)
            {
                smooth = n + 1.0 - Math.Log2(0.5 * Math.Log(mag2));
                return true;
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
    /// <param name="originX">
    /// High-precision part of the centre, for a view too narrow for a double to hold its position.
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) are then read as an offset from it and
    /// the formulas are iterated in <see cref="Dd"/>. Zero, and inert, for any view a double covers.
    /// </param>
    public static void Render(
        IntPtr pixels, int stride, int width, int height, float[] field,
        double centerX, double centerY, double scale,
        int maxIter, Palette palette, double paletteShift, ReferenceOrbit? reference,
        Fractal kind = Fractal.Mandelbrot, Dd originX = default, Dd originY = default)
    {
        EscapeField(field, width, height, centerX, centerY, scale, maxIter, reference, kind,
            originX, originY);
        Colourise((byte*)pixels, stride, width, height, field, palette, paletteShift);
    }

    private static void EscapeField(
        float[] field, int width, int height,
        double centerX, double centerY, double scale, int maxIter, ReferenceOrbit? reference,
        Fractal kind, Dd originX, Dd originY)
    {
        double pixelSize = 2.0 * scale / height;
        double halfW = width * 0.5;
        double halfH = height * 0.5;

        // Perturbation first where the formula has it, then the wider arithmetic, then plain doubles.
        bool wide = reference is null && scale < FractalKind.DoubleFloor;

        Parallel.For(0, height, Options, y =>
        {
            int row = y * width;
            double ci = centerY - (y - halfH + 0.5) * pixelSize;

            if (wide)
            {
                var ciWide = originY + ci;
                for (int x = 0; x < width; x++)
                {
                    var crWide = originX + (centerX + (x - halfW + 0.5) * pixelSize);
                    field[row + x] = Escape(crWide, ciWide, maxIter, kind, out double smooth)
                        ? (float)Math.Max(0.0, smooth)
                        : Interior;
                }
            }
            else if (reference is null)
            {
                for (int x = 0; x < width; x++)
                {
                    double cr = centerX + (x - halfW + 0.5) * pixelSize;
                    field[row + x] = Escape(cr, ci, maxIter, kind, out double smooth)
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

    /// <summary>
    /// Coefficients of a cosine gradient: colour(t) = a + b * cos(2*pi*(c*t + d)).
    ///
    /// Four numbers a channel, which is a very small thing to hold a colour scheme in, and the reason
    /// every one here is smooth and cyclic — it has to be, since <see cref="Colourise"/> reads the
    /// palette at a wrapped coordinate and attenuates toward its mean where a pixel spans more than one
    /// cycle of it. A hand-picked list of stops would need its own interpolation and would have to be
    /// made to meet itself at both ends; a cosine already does.
    ///
    /// <see cref="CR"/> and its siblings are how many times a channel goes round per cycle. Leaving
    /// them at one keeps the three channels in step and gives the smooth two- or three-colour ramps;
    /// setting them to different whole numbers beats them against each other, which is where the
    /// banded schemes at the end of the list come from.
    /// </summary>
    internal readonly record struct Gradient(
        string Name,
        double AR, double AG, double AB,
        double BR, double BG, double BB,
        double CR, double CG, double CB,
        double DR, double DG, double DB)
    {
        public static readonly Gradient[] All =
        [
            // Blue through gold.
            new("Electric", 0.50, 0.50, 0.50, 0.50, 0.50, 0.50, 1, 1, 1, 0.00, 0.10, 0.20),
            // Black, red, amber, white.
            new("Ember", 0.50, 0.36, 0.22, 0.50, 0.38, 0.26, 1, 1, 1, 0.00, 0.12, 0.26),
            // Teal and magenta.
            new("Aurora", 0.44, 0.50, 0.48, 0.38, 0.46, 0.42, 1, 1, 1, 0.36, 0.20, 0.06),
            // Deep indigo to ice.
            new("Abyss", 0.30, 0.34, 0.52, 0.34, 0.36, 0.46, 1, 1, 1, 0.62, 0.55, 0.42),
            // Sepia with cyan highlights.
            new("Copper", 0.48, 0.42, 0.34, 0.44, 0.40, 0.44, 1, 1, 1, 0.10, 0.06, 0.55),

            // The three channels a third of a cycle apart, which is the whole spectrum in order.
            new("Spectrum", 0.50, 0.50, 0.50, 0.50, 0.50, 0.50, 1, 1, 1, 0.00, 0.33, 0.67),
            // Moss and bark: leaf green, olive, near-black, then a dark green.
            new("Fern", 0.36, 0.44, 0.18, 0.34, 0.40, 0.16, 1, 1, 1, 0.92, 0.00, 0.10),
            // Ink and rust. All three channels dip together, so most of the range is dark.
            new("Foundry", 0.32, 0.22, 0.18, 0.34, 0.24, 0.20, 1, 1, 1, 0.00, 0.05, 0.12),
            // Black up to an icy blue-white, blue leading.
            new("Glacier", 0.34, 0.42, 0.52, 0.32, 0.36, 0.40, 1, 1, 1, 0.62, 0.58, 0.52),
            // Salmon, violet, deep blue, gold.
            new("Nebula", 0.48, 0.34, 0.46, 0.44, 0.30, 0.42, 1, 1, 1, 0.00, 0.15, 0.75),
            // Red twice a cycle against blue once, which is the one scheme here whose bands do not all
            // look alike: it comes round red, then teal, then white.
            new("Bloom", 0.50, 0.48, 0.50, 0.48, 0.44, 0.48, 2, 1, 1, 0.50, 0.20, 0.25),
            // Black to white with a cool cast, and no hue to speak of. The odd one out on purpose:
            // structure is easiest to read when nothing but brightness is carrying it, and every
            // coloured scheme hides some of the boundary in a hue the eye separates poorly.
            new("Graphite", 0.50, 0.51, 0.55, 0.50, 0.50, 0.48, 1, 1, 1, 0.00, 0.01, 0.03),
        ];

        /// <summary>The names, for the menu, so the two cannot fall out of step.</summary>
        public static string[] Names()
        {
            var names = new string[All.Length];
            for (int i = 0; i < names.Length; i++) names[i] = All[i].Name;
            return names;
        }
    }
}
