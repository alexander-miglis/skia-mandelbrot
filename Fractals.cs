using System;

namespace FractalZoom;

/// <summary>
/// Which formula the kernels iterate. Every one of these is an escape-time set in the complex plane,
/// which is what lets them share everything else in this program: the same two-pass kernel, the same
/// band-limited colouring, the same boundary-seeking director, the same re-projection.
///
/// That is also the boundary of what belongs here. Fractals built by iterated function systems
/// (Sierpiński, Koch, the Barnsley fern, L-systems) are drawn rather than sampled — there is no
/// escape time to colour by — and the 3D ones (Mandelbulb, Mandelbox, Menger, quaternion Julia,
/// Kleinian) need a camera, a ray marcher and a lighting model. Both are separate renderers, not
/// entries in this list.
/// </summary>
internal enum Fractal
{
    // Escape-time and convergence-time fields in the complex plane.
    Mandelbrot,
    Julia,
    BurningShip,
    Tricorn,
    Multibrot,
    Phoenix,
    Newton,
    Nova,
    Magnet,
    Lyapunov,
    PickoverStalks,
    OrbitTrap,
    SierpinskiTriangle,
    SierpinskiCarpet,

    // Drawn as geometry rather than sampled per pixel.
    KochSnowflake,
    DragonCurve,
    BarnsleyFern,
    ApollonianGasket,
    FractalTree,
    LSystem,

    // Ray-marched against a distance estimator, in three dimensions.
    Mandelbulb,
    Mandelbox,
    MengerSponge,
    SierpinskiTetrahedron,
    QuaternionJulia,
    Kleinian,
}

/// <summary>
/// How a fractal gets onto the screen. Three genuinely different renderers, because these fractals
/// are three genuinely different kinds of object: a scalar field over the plane, a curve or point set
/// built by repeated substitution, and a surface in space.
/// </summary>
internal enum RenderStyle
{
    /// <summary>A number per pixel, coloured by <see cref="Mandelbrot"/>'s second pass.</summary>
    Field,

    /// <summary>Lines and shapes drawn with Skia, subdivided as far as the zoom warrants.</summary>
    Drawn,

    /// <summary>A distance estimator marched on the card, lit by its own gradient.</summary>
    Raymarched,
}

/// <summary>What the rest of the program needs to know about a formula.</summary>
/// <param name="Name">As shown in the menu and the readout.</param>
/// <param name="CenterX">Where a fresh view of it starts, and how wide.</param>
/// <param name="Perturbable">
/// Whether the deep-zoom machinery applies. Perturbation is not a general technique — the reference
/// orbit in <see cref="ReferenceOrbit"/> implements dz' = 2*Z*dz + dz^2 + dc, which is this
/// program's Mandelbrot recurrence and nothing else's. Each other formula has its own perturbed
/// form, none of which are written, so the others iterate directly in fp64 and stop resolving at
/// around 1e13x rather than 1e290x.
/// </param>
/// <param name="Degree">
/// Power the iteration raises z to, which the smooth escape count is logarithmic in.
/// </param>
internal readonly record struct FractalKind(
    string Name, double CenterX, double CenterY, double Scale, bool Perturbable, double Degree,
    RenderStyle Style = RenderStyle.Field)
{
    /// <summary>Indexed by <see cref="Fractal"/>.</summary>
    public static readonly FractalKind[] All =
    [
        new("Mandelbrot", -0.6, 0.0, 1.4, true, 2.0),
        new("Julia", 0.0, 0.0, 1.5, true, 2.0),
        new("Burning Ship", -0.45, 0.5, 1.3, false, 2.0),
        new("Tricorn / Mandelbar", -0.2, 0.0, 1.6, false, 2.0),
        new("Multibrot (cubic)", 0.0, 0.0, 1.5, false, 3.0),
        new("Phoenix", 0.0, 0.0, 1.5, false, 2.0),
        new("Newton", 0.0, 0.0, 1.6, false, 3.0),
        new("Nova", 0.0, 0.0, 1.6, false, 3.0),
        new("Magnet", 1.4, 0.0, 2.2, false, 2.0),
        new("Lyapunov", 3.2, 3.2, 0.9, false, 2.0),
        new("Pickover Stalks", 0.0, 0.0, 1.5, false, 2.0),
        new("Orbit Trap", -0.6, 0.0, 1.4, false, 2.0),
        new("Sierpinski Triangle", 0.5, 0.5, 0.62, false, 2.0),
        new("Sierpinski Carpet", 0.5, 0.5, 0.62, false, 2.0),

        new("Koch Snowflake", 0.0, 0.0, 0.8, false, 2.0, RenderStyle.Drawn),
        new("Dragon Curve", 0.15, -0.15, 1.05, false, 2.0, RenderStyle.Drawn),
        new("Barnsley Fern", 0.0, 5.0, 5.6, false, 2.0, RenderStyle.Drawn),
        new("Apollonian Gasket", 0.0, 0.0, 1.15, false, 2.0, RenderStyle.Drawn),
        new("Fractal Tree", 0.0, 0.45, 0.7, false, 2.0, RenderStyle.Drawn),
        new("L-System (Hilbert)", 0.0, 0.0, 0.62, false, 2.0, RenderStyle.Drawn),

        // For these the scale is a camera distance rather than a half-height, and the centre is a
        // pair of orbit angles: the same pan and zoom, read as a way of moving around an object.
        new("Mandelbulb", 0.6, 0.35, 1.6, false, 8.0, RenderStyle.Raymarched),
        new("Mandelbox", 0.0, 0.0, 3.2, false, 2.0, RenderStyle.Raymarched),
        new("Menger Sponge", 0.0, 0.0, 3.4, false, 3.0, RenderStyle.Raymarched),
        new("Sierpinski Tetrahedron", 0.0, 0.0, 1.7, false, 2.0, RenderStyle.Raymarched),
        new("Quaternion Julia", 0.0, 0.0, 1.7, false, 2.0, RenderStyle.Raymarched),
        new("Kleinian", 0.0, 0.0, 1.6, false, 2.0, RenderStyle.Raymarched),
    ];

    public static FractalKind Of(Fractal fractal) => All[(int)fractal];

    /// <summary>
    /// The constant a Julia set is drawn for. One value has to be picked because the set is a
    /// different shape for every c; this one sits just outside the Mandelbrot boundary, which is
    /// where the spiralling filaments come from.
    /// </summary>
    public const double JuliaCr = -0.7269;

    public const double JuliaCi = 0.1889;

    /// <summary>
    /// The two constants of the Phoenix recurrence: z' = z^2 + p1 + p2*z_previous, iterated from the
    /// pixel. Both have to be constants — feeding the pixel in as c instead leaves the imaginary part
    /// unseeded, the whole orbit stays on the real axis, and the picture comes out as flat bands.
    /// </summary>
    public const double PhoenixP1 = 0.56667;

    public const double PhoenixP2 = -0.5;

    /// <summary>
    /// Scale below which a plain double can no longer place a pixel: the coordinates of a view this
    /// small differ in bits a double does not have. Above it everything runs in doubles, because they
    /// are what the hardware and the card are fast at; below it the renderers switch to the wider
    /// arithmetic in <see cref="Dd"/>.
    /// </summary>
    public const double DoubleFloor = 1e-12;

    /// <summary>
    /// Scale at which a formula stops resolving neighbouring pixels.
    ///
    /// Three different answers, because there are three different limits. Perturbation carries the
    /// Mandelbrot and its Julia sets down to where the deltas themselves stop being representable.
    /// Everything else in the plane — the other formulas, and the constructions that are drawn rather
    /// than sampled — runs on <see cref="Dd"/> below <see cref="DoubleFloor"/>, which holds about
    /// thirty-two digits and so places a pixel accurately to about here. The ray-marched ones read
    /// this as a camera distance rather than a width and never approach it.
    /// </summary>
    public double Floor => Perturbable ? 1e-290 : Style == RenderStyle.Raymarched ? 2e-13 : 1e-25;
}
