using System;

namespace FractalZoom;

/// <summary>
/// Drives the camera: zooms in continuously and keeps steering toward a point on the set boundary
/// so there is always fresh detail in the middle of the screen.
///
/// The centre is held relative to a <see cref="ReferenceOrbit"/> rather than as an absolute pair of
/// doubles. Above <see cref="PerturbBelow"/> plain doubles resolve the view fine and are cheaper,
/// so the reference is null and the offsets are simply absolute coordinates; below it the director
/// maintains a reference orbit and the offsets become deltas from its centre. That is what carries
/// the zoom from ~1e13 magnification out to ~1e290.
/// </summary>
internal sealed class ZoomDirector
{
    /// <summary>
    /// Where a view stops resolving: for the Mandelbrot, where the pixel deltas themselves stop
    /// being representable as doubles, which is the real end of the road for this arithmetic; for
    /// the formulas without a perturbed form, where plain doubles give out instead.
    /// </summary>
    private double ScaleFloor => FractalKind.Of(Kind).Floor;

    /// <summary>View scale below which plain doubles stop resolving neighbouring pixels.</summary>
    private const double PerturbBelow = 1e-11;

    private const double FadeSeconds = 0.9;

    /// <summary>Which formula is being explored. Changing it starts a fresh view of the new one.</summary>
    public Fractal Kind { get; private set; } = Fractal.Mandelbrot;

    private double StartScale => FractalKind.Of(Kind).Scale;
    private double StartX => FractalKind.Of(Kind).CenterX;
    private double StartY => FractalKind.Of(Kind).CenterY;

    /// <summary>
    /// Switches formula and starts over on it. Each one has its own opening view, because the
    /// interesting part of a Burning Ship is nowhere near the interesting part of a Mandelbrot.
    /// </summary>
    public void SetKind(Fractal kind, bool interactive)
    {
        if (kind == Kind) return;

        Kind = kind;
        if (interactive) ResetToOverview();
        else { Cycle++; Reset(); }
    }

    private readonly Random _rng;

    /// <summary>
    /// How many 4x steps to fast-forward through when a descent begins. The wide view of the whole
    /// set is mostly smooth exterior — a soft potential field with detail only along the boundary —
    /// which reads as blurry however many pixels it is rendered at. Skipping ahead means the first
    /// visible frame already sits among filaments.
    /// </summary>
    private const int OpeningSteps = 5;

    /// <summary>Fraction of the view searched for the next target on a normal re-aim.</summary>
    private const double SearchSpan = 0.42;

    /// <summary>Fallback sweep when the usual region turns up nothing.</summary>
    private const double WideSearchSpan = 0.95;

    /// <summary>Rounds of hill-climbing used to pin the target onto the boundary.</summary>
    private const int RefineRounds = 16;

    /// <summary>
    /// Starting radius, as a fraction of the view, for re-pinning the current course.
    /// </summary>
    private const double HoldRadius = 0.15;

    /// <summary>
    /// Hard cap on how far off centre a target may sit, as a fraction of the view.
    ///
    /// This is what makes frame coverage a guarantee rather than a coincidence. A rendered frame only
    /// covers the window while the view's sideways drift stays inside the margin that zooming opens
    /// up, which holds when <see cref="PanPerEFold"/> times this stays below 1. Leaving it to the
    /// targeting heuristic does not work: refinement adds displacement on top of whatever offset is
    /// still outstanding, so without a cap the target can walk away from the centre indefinitely.
    /// </summary>
    private const double MaxTargetOffset = 0.40;

    /// <summary>
    /// Re-aim once per this much magnification. Frequent and cheap (a few hundred escape-time
    /// samples against the kernel's billions of iterations), which both tracks the boundary closely
    /// and notices a view going featureless within a second rather than after a doubling.
    /// </summary>
    private const double RetargetStep = 0.8;

    /// <summary>
    /// Re-anchor the reference orbit once per this much magnification. Much rarer than re-aiming,
    /// because rebuilding means a high-precision orbit plus its approximation table.
    /// </summary>
    private const double RebuildStep = 0.5;

    /// <summary>
    /// Fraction of the iteration budget the refined target must reach for the view to count as
    /// having structure. Escape time grows without limit toward the boundary, so a target that
    /// cannot climb past this is nowhere near it, and zooming in would only magnify a smooth
    /// gradient. A view containing any interior at all is exempt: the set is right there.
    /// </summary>
    private const double DetailFraction = 0.25;

    private double _tdx, _tdy;      // target, as a delta from the current centre
    private double _retargetAt;
    private double _rebuildAt;
    private Phase _phase;
    private bool _needsOpening;
    private bool _exhausted;

    public ZoomDirector(int seed)
    {
        _rng = new Random(seed);
        Reset();
    }

    /// <summary>
    /// Hands the camera to the host. The director stops steering — no zooming, no re-aiming, no
    /// ending the descent — and keeps only the two jobs that cannot move outside it: fading a fresh
    /// view in, and keeping the reference orbit fit for wherever the camera has been taken.
    /// </summary>
    public bool Interactive { get; set; }

    /// <summary>
    /// How far out the camera may be pulled interactively. A little wider than the opening view, so
    /// the whole set can sit inside the window with a margin, and no further: past that there is
    /// nothing to look at but a receding dot.
    /// </summary>
    private double MaxInteractiveScale => StartScale * 3.0;

    /// <summary>Non-null once the view is too deep for plain doubles.</summary>
    public ReferenceOrbit? Reference { get; private set; }

    /// <summary>
    /// Absolute centre while <see cref="Reference"/> is null, otherwise the centre's offset from
    /// the reference orbit's centre.
    /// </summary>
    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    /// <summary>Half-height of the view in complex-plane units.</summary>
    public double Scale { get; private set; }

    /// <summary>e-folds per second. Higher zooms faster.</summary>
    public double Speed { get; set; } = 0.42;

    /// <summary>
    /// Multiplier the host lowers when the kernel can no longer keep up, so that deep views glide
    /// instead of lurching. Zooming slows down rather than tearing.
    /// </summary>
    public double Throttle { get; set; } = 1.0;

    /// <summary>
    /// How fast the centre eases toward its target, as a multiple of the zoom rate. Tying the two
    /// together is not cosmetic: a rendered frame only covers the screen while the view's sideways
    /// drift stays inside the margin that zooming opens up, and that holds exactly when this
    /// constant stays below ~2.4 (the reciprocal of the target search span). It also makes the
    /// camera move the same way regardless of zoom speed.
    /// </summary>
    private const double PanPerEFold = 1.2;

    private double PanRate => PanPerEFold * Speed * Throttle;

    /// <summary>0 = black, 1 = fully visible. Used for the cross-fade between descents.</summary>
    public double Fade { get; private set; }

    /// <summary>How many descents have been completed. Also selects the palette.</summary>
    public int Cycle { get; private set; }

    /// <summary>
    /// Bumped whenever the camera teleports. Frames rendered for an older generation cannot be
    /// re-projected onto the current view, so the display discards them.
    /// </summary>
    public int Generation { get; private set; }

    public double Magnification => StartScale / Scale;

    /// <summary>Iteration budget wanted at this depth. The host clamps it to what it can afford.</summary>
    public int MaxIterations
    {
        get
        {
            double decades = Math.Log10(StartScale / Scale) + 1.0;
            return (int)Math.Clamp(120 + 55 * Math.Pow(decades, 1.35), 150, 400_000);
        }
    }

    private enum Phase { FadeIn, Zoom, FadeOut }

    public void Reset()
    {
        Reference = null;
        OffsetX = StartX;
        OffsetY = StartY;
        Scale = StartScale;
        _tdx = 0;
        _tdy = 0;
        _retargetAt = Scale; // retarget immediately
        _rebuildAt = Scale;
        _phase = Phase.FadeIn;
        Fade = 0;
        _needsOpening = true;
        _exhausted = false;
        Generation++;
    }

    /// <summary>
    /// Puts the camera back on the whole set — the view a Mandelbrot picture usually opens on, which
    /// the automatic descent deliberately skips past. No opening fast-forward: that exists because a
    /// descent should not spend its first seconds on the featureless outer gradient, and someone
    /// steering by hand wants to start from the map.
    /// </summary>
    public void ResetToOverview()
    {
        Reference = null;
        OffsetX = StartX;
        OffsetY = StartY;
        Scale = StartScale;
        _tdx = 0;
        _tdy = 0;
        _retargetAt = 0;
        _rebuildAt = 0;
        _phase = Phase.FadeIn;
        Fade = 0;
        _needsOpening = false;
        _exhausted = false;
        Generation++;
    }

    /// <summary>
    /// Scales the view by <paramref name="factor"/> (above 1 zooms in) while holding one point
    /// still: the one at (<paramref name="anchorX"/>, <paramref name="anchorY"/>), given as an
    /// offset from the view centre in complex-plane units. That is what makes a wheel zoom land
    /// where the pointer is rather than wherever the middle of the window happens to be.
    /// </summary>
    public void ZoomAbout(double factor, double anchorX, double anchorY)
    {
        if (!(factor > 0) || double.IsNaN(factor)) return;

        double wanted = Math.Clamp(Scale / factor, ScaleFloor, MaxInteractiveScale);
        double applied = Scale / wanted;
        if (applied == 1.0) return;

        // The anchor keeps its complex coordinate, so the centre has to travel the fraction of the
        // way to it that the zoom removed.
        double pull = 1.0 - 1.0 / applied;
        OffsetX += anchorX * pull;
        OffsetY += anchorY * pull;
        Scale = wanted;
    }

    /// <summary>Slides the view by a complex-plane offset. Both coordinate modes take it unchanged.</summary>
    public void PanBy(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    public void Advance(double dt, double aspect)
    {
        if (Interactive)
        {
            MaintainReference(aspect);
            if (Fade < 1.0) Fade = Math.Min(1.0, Fade + dt / FadeSeconds);
            _phase = Fade >= 1.0 ? Phase.Zoom : Phase.FadeIn;
            return;
        }

        if (_needsOpening)
        {
            _needsOpening = false;
            Open(aspect);
        }

        Scale *= Math.Exp(-Speed * Throttle * dt);

        if (Scale <= _retargetAt)
        {
            Retarget(aspect);
            _retargetAt = Scale * RetargetStep;
        }

        // Ease the centre toward the target. Frame-rate independent, and slow enough that the
        // drift reads as a glide rather than a pan. Works unchanged in either coordinate mode,
        // since both the offset and the target are expressed in the same frame.
        double k = 1.0 - Math.Exp(-PanRate * dt);
        double mx = _tdx * k, my = _tdy * k;
        OffsetX += mx;
        OffsetY += my;
        _tdx -= mx;
        _tdy -= my;

        switch (_phase)
        {
            case Phase.FadeIn:
                Fade = Math.Min(1.0, Fade + dt / FadeSeconds);
                if (Fade >= 1.0) _phase = Phase.Zoom;
                break;

            case Phase.Zoom:
                if (Scale < ScaleFloor || _exhausted) _phase = Phase.FadeOut;
                break;

            case Phase.FadeOut:
                Fade -= dt / FadeSeconds;
                if (Fade <= 0)
                {
                    Cycle++;
                    Reset();
                }
                break;
        }
    }

    /// <summary>
    /// Keeps the reference orbit matched to a camera the director is not driving.
    ///
    /// Rebuilt on the way down at the same intervals the automatic descent uses, and anchored on the
    /// view centre rather than on a hill-climbed interior point — there is no target to climb toward
    /// here. Dropped again on the way back up, because a reference anchored decades deeper would be
    /// asked for deltas far larger than it was built for, and above the threshold plain doubles
    /// resolve the view anyway.
    /// </summary>
    private void MaintainReference(double aspect)
    {
        if (!FractalKind.Of(Kind).Perturbable) return;

        if (Scale < PerturbBelow)
        {
            if (Reference is null || Scale <= _rebuildAt)
            {
                RebuildReference(0, 0, aspect);
                _rebuildAt = Scale * RebuildStep;
            }
        }
        else if (Reference is { } reference)
        {
            // Back to absolute coordinates. Lossless enough by definition: this only happens above
            // the scale where doubles stop separating neighbouring pixels.
            OffsetX += reference.CenterX.ToDouble();
            OffsetY += reference.CenterY.ToDouble();
            Reference = null;
            _rebuildAt = 0;
        }
    }

    /// <summary>
    /// Runs the first few levels of the descent instantly, so a new run opens on structure rather
    /// than on the featureless outer gradient. Each step re-aims and snaps, exactly as the visible
    /// descent does, just without the travel time.
    /// </summary>
    private void Open(double aspect)
    {
        for (int step = 0; step < OpeningSteps; step++)
        {
            Retarget(aspect);
            OffsetX += _tdx;
            OffsetY += _tdy;
            _tdx = 0;
            _tdy = 0;
            Scale *= 0.25;
        }

        Retarget(aspect);
        _retargetAt = Scale * RetargetStep;
    }

    /// <summary>
    /// Where the camera will be in <paramref name="seconds"/>, assuming no re-aim in between.
    /// The host renders for this position rather than the current one, so that a frame is roughly
    /// current by the time it finishes and the re-projection lands near 1:1 instead of always
    /// having to magnify.
    /// </summary>
    public (double OffsetX, double OffsetY, double Scale) Predict(double seconds)
    {
        double decay = Math.Exp(-PanRate * seconds);
        return (OffsetX + _tdx * (1.0 - decay),
                OffsetY + _tdy * (1.0 - decay),
                Scale * Math.Exp(-Speed * Throttle * seconds));
    }

    /// <summary>
    /// Ends this descent through the normal cross-fade. Used when a location has become too
    /// expensive to keep exploring at a steady rate.
    /// </summary>
    public void EndDescent() => _exhausted = true;

    /// <summary>Skip to a fresh descent immediately (bound to the R key).</summary>
    public void NewDescent()
    {
        Cycle++;
        Reset();
    }

    /// <summary>Escape time of a point given as a delta from the current view centre.</summary>
    private bool SampleEscape(double dx, double dy, int maxIter, out double smooth) =>
        Reference is not null
            ? Reference.Escape(OffsetX + dx, OffsetY + dy, maxIter, out smooth)
            : Mandelbrot.Escape(OffsetX + dx, OffsetY + dy, maxIter, Kind, out smooth);

    /// <summary>
    /// Chooses the next point to fly toward, and notes an interior sample to anchor the next
    /// reference orbit on.
    ///
    /// A coarse grid alone is not enough. Escape time rises without bound as the set boundary is
    /// approached, so the slowest-escaping *grid sample* only locates the boundary to within the
    /// grid spacing — and after zooming 4x that error is four times larger relative to the view.
    /// Repeat that and the camera drifts off the boundary into featureless exterior and zooms into
    /// nothing. So the coarse pick is refined by hill-climbing on escape time, which converges to a
    /// point just outside the boundary however deep the view is.
    /// </summary>
    private void Retarget(double aspect)
    {
        if (TryHoldCourse(aspect)) return;

        if (!TryRetarget(aspect, SearchSpan) && !TryRetarget(aspect, WideSearchSpan))
        {
            // Nothing left to look at even across the whole view. Bail out of this descent rather
            // than spend the next several minutes magnifying empty space.
            _exhausted = true;
            _tdx = 0;
            _tdy = 0;
        }
    }

    /// <summary>
    /// Re-pins the existing course instead of choosing a fresh target.
    ///
    /// A point exactly on the boundary can be zoomed into forever without running out of detail, so
    /// panning is only needed because the target is an approximation. Picking a brand-new target on
    /// every re-aim makes that worse, not better: the camera never converges on anything, it just
    /// chases. So the normal path is to hill-climb from where the camera is already heading, which
    /// keeps the target pinned to the boundary as the iteration budget grows while leaving the
    /// offset small enough to actually reach.
    /// </summary>
    private bool TryHoldCourse(double aspect)
    {
        int maxIter = MaxIterations;
        double radiusY = Scale * HoldRadius;
        double radiusX = Scale * aspect * HoldRadius;

        var (x, y, escape) = Refine(_tdx, _tdy, radiusX, radiusY, maxIter);
        if (escape < DetailFraction * maxIter) return false;

        _tdx = x;
        _tdy = y;
        ClampTarget(aspect);

        if (FractalKind.Of(Kind).Perturbable && Scale < PerturbBelow && (Reference is null || Scale <= _rebuildAt))
        {
            RebuildReference(_tdx, _tdy, aspect);
            _rebuildAt = Scale * RebuildStep;
        }

        return true;
    }

    /// <summary>Pulls the target inside <see cref="MaxTargetOffset"/> of the view centre.</summary>
    private void ClampTarget(double aspect)
    {
        double u = _tdx / (Scale * aspect);
        double v = _tdy / Scale;
        double radius = Math.Sqrt(u * u + v * v);
        if (radius <= MaxTargetOffset || radius == 0) return;

        double shrink = MaxTargetOffset / radius;
        _tdx *= shrink;
        _tdy *= shrink;
    }

    /// <summary>
    /// Searches the given fraction of the view. Returns false if the region holds no structure
    /// worth flying into — either entirely inside the set, or a featureless exterior wash.
    /// </summary>
    private bool TryRetarget(double aspect, double span)
    {
        const int grid = 15;
        int maxIter = MaxIterations;

        double bestScore = double.NegativeInfinity;
        double bestX = 0, bestY = 0;
        bool haveInterior = false;
        double interiorScore = double.NegativeInfinity;
        double interiorX = 0, interiorY = 0;
        int escaped = 0;

        double halfH = Scale * span;
        double halfW = Scale * aspect * span;

        for (int gy = 0; gy < grid; gy++)
        {
            for (int gx = 0; gx < grid; gx++)
            {
                double u = (gx + _rng.NextDouble()) / grid * 2.0 - 1.0;
                double v = (gy + _rng.NextDouble()) / grid * 2.0 - 1.0;
                double dx = u * halfW;
                double dy = v * halfH;
                double radius = Math.Sqrt(u * u + v * v);

                if (!SampleEscape(dx, dy, maxIter, out double smooth))
                {
                    // Interior: a terrible place to fly to, but the ideal reference anchor — its
                    // orbit stays bounded for the full iteration budget, so pixels never have to
                    // rebase off the end of it. Prefer one near the centre.
                    if (-radius > interiorScore)
                    {
                        interiorScore = -radius;
                        interiorX = dx;
                        interiorY = dy;
                        haveInterior = true;
                    }
                    continue;
                }

                escaped++;

                // Prefer slow escapes (deep detail), nudged toward the centre so the camera does
                // not lurch sideways on every re-aim.
                double score = smooth * (1.0 - 0.25 * radius) * (0.85 + 0.3 * _rng.NextDouble());
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        if (escaped == 0) return false; // nothing but interior: flying here means flying into black

        double refined;
        (bestX, bestY, refined) = Refine(bestX, bestY, halfW / grid, halfH / grid, maxIter);

        // Featureless: no set in view, and the best the search could find is far from the boundary.
        if (!haveInterior && refined < DetailFraction * maxIter) return false;

        _tdx = bestX;
        _tdy = bestY;
        ClampTarget(aspect);

        if (FractalKind.Of(Kind).Perturbable && Scale < PerturbBelow && (Reference is null || Scale <= _rebuildAt))
        {
            RebuildReference(haveInterior ? interiorX : _tdx, haveInterior ? interiorY : _tdy, aspect);
            _rebuildAt = Scale * RebuildStep;
        }

        return true;
    }

    /// <summary>
    /// Pattern search on escape time with a shrinking radius. Escape time grows without limit
    /// toward the boundary, so climbing it walks the target onto the boundary; interior samples are
    /// rejected, which parks it just outside where there is both structure and colour.
    /// </summary>
    private (double X, double Y, double Escape) Refine(double x, double y, double rx, double ry, int maxIter)
    {
        double best = SampleEscape(x, y, maxIter, out double s) ? s : double.NegativeInfinity;

        for (int round = 0; round < RefineRounds; round++)
        {
            double atX = x, atY = y;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    double cx = x + dx * rx, cy = y + dy * ry;
                    if (SampleEscape(cx, cy, maxIter, out double candidate) && candidate > best)
                    {
                        best = candidate;
                        atX = cx;
                        atY = cy;
                    }
                }
            }

            x = atX;
            y = atY;
            rx *= 0.55;
            ry *= 0.55;
        }

        return (x, y, best);
    }

    /// <summary>
    /// Re-anchors the reference orbit at (current centre + the given delta), keeping the view
    /// itself exactly where it is. Called on re-aim, so a few times per descent.
    /// </summary>
    private void RebuildReference(double dx, double dy, double aspect)
    {
        // Enough fractional bits to place a pixel, plus generous headroom.
        int fracBits = 96 + (int)Math.Ceiling(-Math.Log2(Scale));

        BigFixed cx, cy;
        if (Reference is null)
        {
            cx = BigFixed.FromDouble(OffsetX + dx, fracBits);
            cy = BigFixed.FromDouble(OffsetY + dy, fracBits);
        }
        else
        {
            cx = Reference.CenterX.WithFracBits(fracBits).AddDouble(OffsetX + dx);
            cy = Reference.CenterY.WithFracBits(fracBits).AddDouble(OffsetY + dy);
        }

        // The view has not moved, so its offset from the new anchor is just -delta.
        OffsetX = -dx;
        OffsetY = -dy;

        // Bound on how far any pixel can sit from the new anchor before the next re-aim: the view
        // only shrinks in between, and its centre stays within roughly one view of the anchor.
        double dcMax = 2.0 * Scale * Math.Sqrt(aspect * aspect + 1.0);

        // Margin so the orbit still covers the iteration budget at the next re-aim, twice as deep.
        Reference = ReferenceOrbit.Compute(cx, cy, (int)(MaxIterations * 1.6) + 64, dcMax, Kind);
    }
}
