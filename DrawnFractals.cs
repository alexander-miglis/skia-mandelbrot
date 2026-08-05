using System;
using System.Diagnostics;
using SkiaSharp;

namespace FractalZoom;

/// <summary>
/// The fractals that are constructions rather than fields: curves built by replacing a segment with
/// a smaller copy of a rule, and point sets thrown by an iterated function system. There is no escape
/// time to colour here, so none of the kernel applies — these are drawn straight onto the canvas.
///
/// Three things make them zoom as deep as the rest of the program rather than to a fixed recursion
/// depth. Every rule stops subdividing where a piece is smaller than a pixel, so detail appears as
/// you go in and the work stays bounded. Coordinates are <see cref="Dd"/> rather than doubles, which
/// is what carries a construction past about 1e13 times in — see <see cref="Frame"/>. And the rules
/// are walked a level at a time rather than depth first, which is what makes a frame that runs out of
/// budget come out evenly detailed instead of lopsided.
/// </summary>
internal static class DrawnFractals
{
    /// <summary>Below this many pixels a piece is drawn as it is instead of being subdivided.</summary>
    private const double SmallestPiece = 2.0;

    /// <summary>
    /// Safety valve, in case a rule's culling ever fails to bound the work — deliberately far beyond
    /// what any view needs, because for the rules that shrink slowly it is a zoom limit as well as a
    /// safety valve. The dragon's segments shrink by only 1/sqrt(2) a level, so reaching the deepest
    /// view the arithmetic supports takes upwards of a hundred and sixty of them. What actually
    /// bounds the work is the frontier limit and the rule that stops subdividing below a pixel.
    /// </summary>
    private const int MaxDepth = 400;

    /// <summary>
    /// Ceiling on how many pieces a single frame may draw, and on how many nodes it may carry from
    /// one level of a rule to the next.
    ///
    /// Culling bounds the work for the rules whose children barely overlap — Koch's spike reaches
    /// only a third of a segment beyond it, so a child that is off screen stays off. The tree is the
    /// opposite case: a branch's descendants sprawl about two and a half times its own length past
    /// its tip, so while the branches are still longer than the window *both* children overlap the
    /// view, nothing can be rejected, and the count doubles every level. Zoomed a million times in
    /// that is twenty-odd such levels before culling bites, which is millions of branches a frame.
    ///
    /// So the traversal is bounded instead. It costs detail at extreme zoom rather than time, which
    /// is the right way round for something drawn sixty times a second.
    /// </summary>
    /// <remarks>
    /// Sized by what can be drawn in a frame, not by what looks complete. At 120,000 the dragon spent
    /// the whole budget every frame once it could recurse deep enough to do so, and fell to twelve
    /// frames a second; the segments themselves are the cost, not finding them.
    /// </remarks>
    private const int PieceBudget = 45_000;

    /// <summary>
    /// Points the fern and the Hilbert curve may plot. Far higher than <see cref="PieceBudget"/>
    /// because a point costs a coordinate pair in one batched call rather than a stroke of its own —
    /// and it needs to be, since covering a window with a shape that nearly fills its own outline
    /// takes points in proportion to the window's *area*: a fern the height of a tall screen wants
    /// several hundred thousand of them before it stops looking stippled.
    /// </summary>
    private const int PointBudget = 420_000;

    /// <summary>
    /// What the last frame actually did, for the readout. When one of these fractals stops drawing,
    /// these numbers say why without having to guess: pieces at its ceiling means the frame ran out of
    /// budget, depth at <see cref="MaxDepth"/> means the recursion limit, and both small means the
    /// view is simply somewhere the fractal is not.
    /// </summary>
    public static int LastPieces { get; private set; }

    public static int LastVisits { get; private set; }

    public static int LastDepth { get; private set; }

    /// <summary>
    /// How long the last frame's rule took, in milliseconds. Reported in place of the kernel time the
    /// sampled fractals show, since for these the rule *is* the kernel.
    /// </summary>
    public static double LastMs { get; private set; }

    private static int _visits;

    /// <summary>
    /// Collects pieces into one path per recursion depth, so a rule that emits thousands of little
    /// lines or circles costs a handful of draw calls instead of thousands.
    ///
    /// This is what the tree needed. Drawing each branch as it was found meant sixteen thousand
    /// DrawLine calls a frame, each preceded by a change of colour and width, which Skia cannot batch
    /// across — eleven frames a second at the opening view, before any zooming at all. Depth is the
    /// right thing to group by because it is exactly what the colour and the width are derived from.
    /// </summary>
    private sealed class Batch : IDisposable
    {
        private readonly SKPath?[] _paths = new SKPath?[MaxDepth + 2];

        private SKPath At(int depth) =>
            _paths[Math.Clamp(depth, 0, _paths.Length - 1)] ??= new SKPath();

        /// <param name="joins">
        /// Whether this piece continues from where the last one ended, which for the curve rules it
        /// usually does: their levels come out in the curve's own order, so a run of segments can go
        /// into the path as one polyline instead of a move and a line each. Halves the points in the
        /// path, and lets the stroke join at the corners rather than butting.
        /// </param>
        public void Line(int depth, SKPoint from, SKPoint to, bool joins = false)
        {
            var path = At(depth);
            if (!joins) path.MoveTo(from);
            path.LineTo(to);
        }

        public void Circle(int depth, SKPoint centre, float radius) =>
            At(depth).AddCircle(centre.X, centre.Y, radius);

        /// <param name="width">Stroke width for a depth, or null to leave the paint's own.</param>
        public void Flush(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Func<int, float>? width)
        {
            for (int depth = 0; depth < _paths.Length; depth++)
            {
                var path = _paths[depth];
                if (path is null || path.IsEmpty) continue;

                paint.Color = Shade(palette, depth);
                if (width is not null) paint.StrokeWidth = width(depth);
                canvas.DrawPath(path, paint);
            }
        }

        public void Dispose()
        {
            foreach (var path in _paths) path?.Dispose();
        }
    }

    /// <summary>
    /// Plane-to-screen transform, the visible rectangle, and how big a pixel is.
    ///
    /// The centre is a <see cref="Dd"/> and so are the coordinates handed to it, because that is what
    /// the depth of the zoom is limited by. A construction is built by repeatedly adding a shrinking
    /// offset to a coordinate of order one; once the offset falls below the last bit of that
    /// coordinate, a double simply loses it, and every piece of the rule lands in the same place. That
    /// happens at around 1e-16, which is why these used to stop at a few thousand billion times in.
    /// Held as a pair of doubles the coordinate keeps about thirty-two digits instead of sixteen, and
    /// the constructions run to roughly 1e25 times in.
    ///
    /// Everything downstream of a coordinate, though, is an ordinary double: a rule's own dimensions,
    /// and a point's offset from the view centre. Those only ever need to be accurate relative to
    /// their own size, and the wider arithmetic would only make them slower. So the traversals take
    /// each node's offsets once and work from those.
    /// </summary>
    private readonly struct Frame
    {
        public readonly Dd CenterX, CenterY;
        public readonly double Pixel, HalfW, HalfH;
        private readonly double _spanX, _spanY;   // half the window, in plane units

        public Frame(Dd centerX, Dd centerY, double scale, int width, int height)
        {
            CenterX = centerX;
            CenterY = centerY;
            Pixel = 2.0 * scale / height;
            HalfW = width * 0.5;
            HalfH = height * 0.5;
            _spanX = Pixel * HalfW;
            _spanY = scale;
        }

        /// <summary>A coordinate as an offset from the view centre, which is small enough for a double.</summary>
        public double OffsetX(Dd x) => (x - CenterX).ToDouble();

        public double OffsetY(Dd y) => (y - CenterY).ToDouble();

        public SKPoint To(Dd x, Dd y) => At(OffsetX(x), OffsetY(y));

        /// <summary>Screen position of a point already reduced to offsets from the centre.</summary>
        public SKPoint At(double ox, double oy) => new(
            (float)(ox / Pixel + HalfW),
            (float)(HalfH - oy / Pixel));

        public double Pixels(double planeLength) => planeLength / Pixel;

        /// <summary>
        /// Whether a box given as offsets from the centre, grown by a margin for whatever the rule
        /// reaches outside it, is in view.
        /// </summary>
        public bool Visible(double x0, double y0, double x1, double y1, double margin)
        {
            if (Math.Max(x0, x1) + margin < -_spanX || Math.Min(x0, x1) - margin > _spanX) return false;
            return Math.Max(y0, y1) + margin >= -_spanY && Math.Min(y0, y1) - margin <= _spanY;
        }

        public bool VisibleBox(double ox, double oy, double hx, double hy) =>
            ox + hx >= -_spanX && ox - hx <= _spanX && oy + hy >= -_spanY && oy - hy <= _spanY;

        /// <summary>Whether a rectangle, given as offsets from the centre, is in view.</summary>
        public bool VisibleRect(double minX, double maxX, double minY, double maxY) =>
            maxX >= -_spanX && minX <= _spanX && maxY >= -_spanY && minY <= _spanY;

        /// <summary>Half the window in plane units, for a rule that has to size itself to it.</summary>
        public double SpanX => _spanX;

        public double SpanY => _spanY;
    }

    /// <param name="phase">
    /// Seconds of animation elapsed. Only the fern uses it, to lean � see <see cref="Maps"/>.
    /// </param>
    public static void Draw(
        SKCanvas canvas, Fractal kind, Dd centerX, Dd centerY, double scale,
        int width, int height, Mandelbrot.Palette palette, double phase = 0.0)
    {
        var frame = new Frame(centerX, centerY, scale, width, height);
        _visits = 0;
        LastDepth = 0;
        LastPieces = 0;
        long started = Stopwatch.GetTimestamp();

        // Colour comes from the same gradient the fields use, sampled by recursion depth, so a
        // switch of fractal does not also mean a switch of palette.
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            StrokeCap = SKStrokeCap.Round,
        };

        switch (kind)
        {
            case Fractal.KochSnowflake: Koch(canvas, paint, palette, frame); break;
            case Fractal.DragonCurve: Dragon(canvas, paint, palette, frame); break;
            case Fractal.BarnsleyFern: Fern(canvas, paint, palette, frame, phase); break;
            case Fractal.ApollonianGasket: Apollonian(canvas, paint, palette, frame); break;
            case Fractal.FractalTree: Tree(canvas, paint, palette, frame); break;
            default: Hilbert(canvas, paint, palette, frame); break;
        }

        LastVisits = _visits;
        LastMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static SKColor Shade(Mandelbrot.Palette palette, int depth)
    {
        uint c = palette.Colors[(depth * 137) & (palette.Colors.Length - 1)];
        return new SKColor((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }

    /// <summary>
    /// Whether a level of a rule may be expanded, or whether the frame has to stop and draw what it
    /// has. Deciding this per level rather than per node is the point: a running budget spent depth
    /// first gives the first subtree everything and its siblings nothing, which on the tree drew one
    /// side of it in full detail and left the other side empty.
    /// </summary>
    private static bool CanExpand(int depth, int drawn, int children) =>
        depth < MaxDepth && children <= PieceBudget && drawn + children <= PieceBudget;

    // ---- Koch snowflake and the dragon: rules that replace a segment with smaller segments. ----

    private struct Seg
    {
        public Dd Ax, Ay, Bx, By;
        public double Hand;   // dragon only: which side of the segment the corner goes
    }

    private static Seg[] _segs = new Seg[PieceBudget + 8];
    private static Seg[] _segSpare = new Seg[PieceBudget + 8];

    private static void Koch(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        // Three sides of an equilateral triangle, centred on the origin.
        const double r = 0.62;
        double[] xs = new double[3], ys = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double a = Math.PI / 2 + i * 2.0 * Math.PI / 3.0;
            xs[i] = r * Math.Cos(a);
            ys[i] = r * Math.Sin(a) - 0.1;
        }

        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            _segs[i] = new Seg { Ax = xs[i], Ay = ys[i], Bx = xs[j], By = ys[j] };
        }

        Curve(canvas, paint, palette, frame, 3, koch: true);
    }

    private static void Dragon(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        _segs[0] = new Seg { Ax = -0.35, Ay = 0.0, Bx = 0.95, By = 0.0, Hand = 1.0 };
        Curve(canvas, paint, palette, frame, 1, koch: false);
    }

    /// <summary>
    /// Walks a segment rule level by level, drawing a segment where it has become too small to
    /// subdivide and stopping altogether when the next level would not fit in the frame's budget.
    /// </summary>
    private static void Curve(
        SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame, int count, bool koch)
    {
        // How far outside its own box a rule reaches: Koch's spike stands about 0.29 of the segment
        // beyond it, the dragon's corner half a segment out and its descendants a little further.
        double margin = koch ? 0.3 : 0.75;
        int branch = koch ? 4 : 2;
        const double apex = 0.28867513459481287;   // sqrt(3)/6, the height of Koch's spike

        using var batch = new Batch();
        int drawn = 0;

        for (int depth = 0; count > 0; depth++)
        {
            LastDepth = depth;
            _visits += count;

            // Cull, and draw whatever has become small enough to be a piece rather than a rule.
            // Survivors are compacted to the front of the array.
            int keep = 0;
            int ran = -2;    // index of the last segment drawn, to spot a continuous run
            for (int i = 0; i < count; i++)
            {
                ref var s = ref _segs[i];

                double ax = frame.OffsetX(s.Ax), ay = frame.OffsetY(s.Ay);
                double bx = frame.OffsetX(s.Bx), by = frame.OffsetY(s.By);
                double dx = bx - ax, dy = by - ay;
                double length = Math.Sqrt(dx * dx + dy * dy);

                if (!frame.Visible(ax, ay, bx, by, length * margin)) continue;

                if (frame.Pixels(length) < SmallestPiece)
                {
                    if (drawn < PieceBudget)
                    {
                        batch.Line(depth, frame.At(ax, ay), frame.At(bx, by), ran == i - 1);
                        drawn++;
                        ran = i;
                    }
                    continue;
                }

                _segs[keep++] = s;
            }

            count = keep;
            if (count == 0) break;

            if (!CanExpand(depth, drawn, count * branch))
            {
                // Out of budget: draw this level as it stands. Every piece on screen is the same
                // size, so what is lost is detail everywhere rather than whole limbs.
                ran = -2;
                for (int i = 0; i < count && drawn < PieceBudget; i++)
                {
                    ref var s = ref _segs[i];
                    batch.Line(depth,
                        frame.To(s.Ax, s.Ay), frame.To(s.Bx, s.By), ran == i - 1);
                    drawn++;
                    ran = i;
                }
                break;
            }

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                ref var s = ref _segs[i];
                double dx = (s.Bx - s.Ax).ToDouble(), dy = (s.By - s.Ay).ToDouble();

                if (koch)
                {
                    Dd x1 = s.Ax + dx / 3.0, y1 = s.Ay + dy / 3.0;
                    Dd x3 = s.Ax + dx * (2.0 / 3.0), y3 = s.Ay + dy * (2.0 / 3.0);

                    // Apex of the equilateral triangle standing on the middle third. The right-hand
                    // normal, so that the spikes on a counter-clockwise triangle point outward and
                    // make a snowflake rather than an inward-folded star.
                    Dd x2 = s.Ax + (dx * 0.5 + dy * apex);
                    Dd y2 = s.Ay + (dy * 0.5 - dx * apex);

                    _segSpare[made++] = new Seg { Ax = s.Ax, Ay = s.Ay, Bx = x1, By = y1 };
                    _segSpare[made++] = new Seg { Ax = x1, Ay = y1, Bx = x2, By = y2 };
                    _segSpare[made++] = new Seg { Ax = x2, Ay = y2, Bx = x3, By = y3 };
                    _segSpare[made++] = new Seg { Ax = x3, Ay = y3, Bx = s.Bx, By = s.By };
                }
                else
                {
                    // The corner sits half a segment away, square to it; which side alternates.
                    Dd mx = s.Ax + (dx * 0.5 + s.Hand * dy * 0.5);
                    Dd my = s.Ay + (dy * 0.5 - s.Hand * dx * 0.5);

                    _segSpare[made++] = new Seg { Ax = s.Ax, Ay = s.Ay, Bx = mx, By = my, Hand = 1.0 };
                    _segSpare[made++] = new Seg { Ax = mx, Ay = my, Bx = s.Bx, By = s.By, Hand = -1.0 };
                }
            }

            (_segs, _segSpare) = (_segSpare, _segs);
            count = made;
        }

        batch.Flush(canvas, paint, palette, null);
        LastPieces = drawn;
    }

    // ---- Barnsley fern: an iterated function system, walked as boxes rather than thrown as points. ----

    /// <summary>One of the fern's affine maps: a two-by-two matrix and a translation.</summary>
    private readonly record struct Affine(double M00, double M01, double M10, double M11, double Tx, double Ty);

    /// <summary>
    /// The four maps. Thrown at random these draw the fern by the density of the points that land;
    /// that is the usual way to do it and it cannot be zoomed, since the chance of a random point
    /// landing inside a window a billionth of the fern across is a billionth. So the maps are applied
    /// to a *box* instead and the box is followed down: children that miss the view are dropped, and
    /// one that has shrunk below a pixel is plotted. Which is the same picture at the opening view,
    /// and still a picture however far in the camera goes.
    /// </summary>
    private static readonly Affine[] FernMaps =
    [
        new(0.00, 0.00, 0.00, 0.16, 0.00, 0.00),
        new(0.20, -0.26, 0.23, 0.22, 0.00, 1.60),
        new(-0.15, 0.28, 0.26, 0.24, 0.00, 0.44),
        new(0.85, 0.04, -0.04, 0.85, 0.00, 1.60),
    ];

    /// <summary>
    /// How far the fern may lean before the tip of its frond leaves the window, given where the edge
    /// of the view is.
    ///
    /// Exact rather than a guess, because the tip is the fixed point of the stem map: solving
    /// (I − M)·p = t for the sheared M puts it at 1.6·(0.04 + s) / (0.0241 + 0.04·s), and setting that
    /// equal to the edge and solving back for s gives the most the fern can lean and stay in frame.
    /// In a tall narrow window that is hardly at all — the upright fern already nearly fills the width,
    /// so a lean has nowhere to go — and in a wide one it is as much as the map will take while still
    /// contracting.
    /// </summary>
    private static double LeanRoom(double edge)
    {
        if (edge > 20.0) return 0.09;   // past where the formula below changes sign, and plenty of room
        return Math.Clamp((0.0241 * edge - 0.064) / (1.6 - 0.04 * edge), 0.0, 0.09);
    }

    /// <summary>The same maps, with the stem's own map sheared. Rebuilt when the shear changes.</summary>
    private static readonly Affine[] FernSheared = [.. FernMaps];

    private static double _fernSkew = double.NaN;

    /// <summary>
    /// How far the fern leans, and how tightly its fronds curl.
    ///
    /// The whole shape comes out of the one map that carries the stem: it is applied to the fern over
    /// and over, so a small change to it compounds all the way up. Shearing it — adding to the term
    /// that mixes height into sideways displacement — bends every frond by a little more than the one
    /// below, which is what makes a fern lean and curl rather than simply tilt. It is a far better
    /// thing to vary than the magnification, because this fractal has no interesting depth: zoom into
    /// it and you find another fern, the same fern, forever.
    /// </summary>
    private static Affine[] Maps(double skew)
    {
        // Bounded so the map stays a contraction — at 0.85 across the diagonal there is not much room
        // before the fern stops converging and grows without limit.
        skew = Math.Clamp(skew, -0.09, 0.09);
        if (skew == _fernSkew) return FernSheared;

        _fernSkew = skew;
        var stem = FernMaps[3];
        FernSheared[3] = stem with { M01 = stem.M01 + skew };
        return FernSheared;
    }

    private static SKPoint[] _points = new SKPoint[PointBudget];

    /// <summary>
    /// Walked depth first, unlike the rules above, because what stops it is the size of a box and not
    /// a budget. Every branch runs down to sub-pixel boxes and stops there, so the leaves cover the
    /// visible attractor once and the count is bounded by the window rather than by the depth — about
    /// thirty thousand points for a fern the height of the screen, at any magnification.
    ///
    /// Breadth first was wrong here and looked it: the boxes still larger than a pixel all had to be
    /// held at once, that frontier outgrew the point budget partway down, and what got plotted was the
    /// centres of half-finished boxes — a fern-shaped scatter of blobs with no fronds in it.
    /// </summary>
    private static void Fern(
        SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame, double phase)
    {
        // A slow sway either side of upright, so the shape is the thing that changes over time here
        // rather than the depth.
        var maps = Maps(LeanRoom(0.92 * frame.SpanX) * Math.Sin(phase * 0.22));

        // Thrown as points, which is the way a fern is normally drawn and much the cheapest: one
        // multiply-add per point, no recursion, and the density comes out as the shading, thin at the
        // frond tips and solid down the stem. Nothing else here looks as much like a fern.
        //
        // What it cannot do is zoom, and it fails in a way that measures itself: a random point lands
        // in a window a billionth of the fern across about a billionth of the time, so it simply stops
        // producing any. So it runs first and the count decides. Sizing the detail by hand instead —
        // trying to have the box walk cover every view — cost fourteen million boxes a frame at the
        // opening view and drew the fern as a solid silhouette.
        int thrown = Scatter(frame, maps);
        double width = 1.0;

        if (thrown < ScatterFloor)
        {
            // Too far in for that. Walked as boxes: an exact cover of whatever part of the fern is on
            // screen, at a couple of pixels' detail, with each point drawn the size of the box it
            // stands for so the cover reads evenly however the branches happened to divide.
            // Two and a half pixels rather than one: the window is as full of fern at a deep view as at
            // a shallow one — whatever part of it is on screen fills the screen the same way — so this
            // is what the frame can afford, and each point being drawn the size of its own box means
            // what is lost is sharpness rather than evenness.
            const double finest = 2.5;

            if (Sprinkle(frame, maps, finest) >= PointBudget) Sprinkle(frame, maps, finest * 2.0);
            else width = finest;
        }

        paint.Style = SKPaintStyle.Fill;
        paint.StrokeWidth = (float)width;
        paint.StrokeCap = SKStrokeCap.Square;

        // Not antialiased, unlike everything else here. A few hundred thousand smoothed points is most
        // of a frame's time in Skia — half a second, once — and it buys nothing on a cloud this dense:
        // the points are each other's edges.
        paint.IsAntialias = false;

        paint.Color = Frond;
        canvas.DrawPoints(SKPointMode.Points, _points.AsSpan(0, _drawnPoints).ToArray(), paint);

        // Stems last, so a midrib reads through the leaves it carries rather than under them.
        paint.Color = Stem;
        canvas.DrawPoints(SKPointMode.Points, _stems.AsSpan(0, _stemPoints).ToArray(), paint);

        paint.IsAntialias = true;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.Style = SKPaintStyle.Stroke;
        LastPieces = _drawnPoints + _stemPoints;
    }

    /// <summary>
    /// The one fractal here that is a picture of something, so it is coloured like the thing rather
    /// than from the shared gradient the rest share.
    ///
    /// Which part is which comes straight out of the rule. One of the four maps flattens the whole fern
    /// onto a vertical segment, and that segment is the stem — so a point is on a stem if its recent
    /// history includes that map, and how recently says which one: applied last it gives the main stem,
    /// one map ago the midribs of the three fronds, two ago their sub-fronds. A few generations of that
    /// is brown and everything else is leaf, which is how a fern is actually built.
    /// </summary>
    private static readonly SKColor Stem = new(0x8A, 0x5A, 0x2B);

    private static readonly SKColor Frond = new(0x4F, 0xA8, 0x3D);

    private const int StemGenerations = 3;

    private static readonly SKPoint[] _stems = new SKPoint[PointBudget / 4];

    private static int _stemPoints;

    /// <summary>Files a plotted point as stem or leaf, by how long ago the flattening map ran.</summary>
    private static void Plot(SKPoint at, int sinceStem)
    {
        if (sinceStem < StemGenerations)
        {
            if (_stemPoints < _stems.Length) _stems[_stemPoints++] = at;
        }
        else if (_drawnPoints < PointBudget)
        {
            _points[_drawnPoints++] = at;
        }
    }

    /// <summary>How many points to throw, and how few landing on screen means the camera has gone in
    /// too far for throwing them to work.</summary>
    private const int ScatterPoints = 240_000;

    private const int ScatterFloor = 30_000;

    /// <summary>
    /// The iterated function system played as a game of chance: pick a map by its weight, apply it,
    /// plot where you land. The weights are what make the picture — most of them go to the map that
    /// carries the stem, which is why the stem is solid and the tips are a scatter.
    /// </summary>
    private static int Scatter(Frame frame, Affine[] maps)
    {
        // Fixed seed every frame, so it is the same fern each time rather than a shimmering one.
        var rng = new Random(12345);
        double x = 0, y = 0;
        int sinceStem = StemGenerations;
        _drawnPoints = 0;
        _stemPoints = 0;

        for (int i = 0; i < ScatterPoints; i++)
        {
            double p = rng.NextDouble();
            int pick = p < 0.01 ? 0 : p < 0.08 ? 1 : p < 0.15 ? 2 : 3;
            ref readonly var m = ref maps[pick];

            double nx = x * m.M00 + y * m.M01 + m.Tx;
            y = x * m.M10 + y * m.M11 + m.Ty;
            x = nx;
            sinceStem = pick == 0 ? 0 : sinceStem + 1;

            double ox = x - frame.CenterX.Hi, oy = y - frame.CenterY.Hi;
            if (frame.VisibleBox(ox, oy, 0, 0)) Plot(frame.At(ox, oy), sinceStem);
        }

        _visits += ScatterPoints;
        return _drawnPoints + _stemPoints;
    }

    /// <summary>
    /// Runs one pass of the fern at the given detail, and returns how many points it drew. Visits
    /// accumulate across the passes, since what the readout should say is the work the frame did.
    /// </summary>
    private static int Sprinkle(Frame frame, Affine[] maps, double finest)
    {
        _drawnPoints = 0;
        _stemPoints = 0;

        // The stopping size in plane units rather than pixels, so the walk compares two lengths
        // instead of dividing by the pixel size a great many times.
        FernPiece(frame, maps, Piece.Whole, 0, finest * 0.5 * frame.Pixel, false);

        return _drawnPoints + _stemPoints;
    }

    /// <summary>
    /// The transform from the whole fern to one piece of it: a two-by-two matrix and a translation, the
    /// translation being a <see cref="Dd"/> because it is a position and a deep view needs more digits
    /// of one than a double holds. The matrix is not — it is only ever a ratio of sizes.
    /// </summary>
    private readonly record struct Piece(double A, double B, double C, double D, Dd Tx, Dd Ty)
    {
        public static readonly Piece Whole = new(1.0, 0.0, 0.0, 1.0, Dd.Zero, Dd.Zero);

        /// <summary>
        /// This piece's own copy of the given map — the map applied *inside* this transform rather than
        /// after it, which is what keeps the piece it describes inside this one.
        /// </summary>
        public Piece Then(in Affine m) => new(
            A * m.M00 + B * m.M10,
            A * m.M01 + B * m.M11,
            C * m.M00 + D * m.M10,
            C * m.M01 + D * m.M11,
            Tx + (A * m.Tx + B * m.Ty),
            Ty + (C * m.Tx + D * m.Ty));

        public Dd CentreX => Tx + (A * BoxX + B * BoxY);

        public Dd CentreY => Ty + (C * BoxX + D * BoxY);

        /// <summary>
        /// Half-extents of an upright box around this piece. The image of a box under a map that turns
        /// it is not a box, so the bound takes the absolute value of the matrix, which is the smallest
        /// upright box that certainly contains it.
        /// </summary>
        public double HalfW => Math.Abs(A) * BoxW + Math.Abs(B) * BoxH;

        public double HalfH => Math.Abs(C) * BoxW + Math.Abs(D) * BoxH;
    }

    /// <summary>A box around the whole fern, which every map takes into itself.</summary>
    private const double BoxX = 0.2, BoxY = 5.0, BoxW = 3.2, BoxH = 5.4;

    /// <summary>
    /// Walks the fern as nested boxes, for a view too far in for throwing points at it to land any.
    ///
    /// What descends is the composed transform from the whole fern down to this piece, each new map
    /// applied on the *inside*. That is the only arrangement in which a child's box lies within its
    /// parent's, and without that, culling is not sound. Applying the new map on the outside instead —
    /// which is the order the iteration itself runs in — sends a child's box somewhere else entirely, so
    /// a node does not stand for its descendants and rejecting one throws away pieces that were on
    /// screen. The symptom was a deep view of the fern's own tip drawing a single point: the nested
    /// pieces around the tip all lived in sibling subtrees, every one of which had been culled at the
    /// first level for being nowhere near it.
    /// </summary>
    private static void FernPiece(
        Frame frame, Affine[] maps, in Piece piece, int depth, double stop, bool stem)
    {
        _visits++;
        if (depth > LastDepth) LastDepth = depth;

        double hx = piece.HalfW, hy = piece.HalfH;
        double ox = frame.OffsetX(piece.CentreX), oy = frame.OffsetY(piece.CentreY);
        if (!frame.VisibleBox(ox, oy, hx, hy)) return;

        if (depth >= MaxDepth || Math.Max(hx, hy) < stop)
        {
            Plot(frame.At(ox, oy), stem ? 0 : StemGenerations);
            return;
        }

        for (int i = 0; i < maps.Length; i++)
        {
            // The maps chosen nearest the root are the ones applied last, so they are the ones that
            // decide whether this is stem or leaf — the same few generations the thrown points use.
            FernPiece(frame, maps, piece.Then(maps[i]), depth + 1, stop,
                stem || (i == 0 && depth < StemGenerations));
        }
    }

    // ---- Apollonian gasket: fill each gap between three tangent circles, forever. ----

    /// <summary>
    /// A circle as Descartes' theorem likes them: curvature, and curvature times centre. In this form
    /// the reflection that generates the next circle of a packing is linear — no square roots, and so
    /// no choosing between their branches, which is what an earlier attempt at this got wrong.
    /// </summary>
    private readonly record struct Circ(Dd K, Dd Kx, Dd Ky)
    {
        public double Radius => 1.0 / Math.Abs(K.ToDouble());
        public Dd X => Kx / K;
        public Dd Y => Ky / K;

        public static Circ Reflect(Circ a, Circ b, Circ c, Circ d) => new(
            (a.K + b.K + c.K) * 2.0 - d.K,
            (a.Kx + b.Kx + c.Kx) * 2.0 - d.Kx,
            (a.Ky + b.Ky + c.Ky) * 2.0 - d.Ky);
    }

    /// <summary>A quadruple of mutually tangent circles, with <see cref="D"/> the one to replace.</summary>
    private struct Quad
    {
        public Circ A, B, C, D;
    }

    private static Quad[] _quads = new Quad[PieceBudget * 3 + 8];
    private static Quad[] _quadSpare = new Quad[PieceBudget * 3 + 8];

    private static void Apollonian(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        // Three equal circles inside the unit circle, all four mutually tangent. The outer one's
        // curvature is negative because it contains the rest.
        double r = 2.0 * Math.Sqrt(3.0) - 3.0;
        double d = 1.0 - r;

        var outer = new Circ(-1.0, 0.0, 0.0);
        var inner = new Circ[3];
        for (int i = 0; i < 3; i++)
        {
            double a = Math.PI / 2 + i * 2.0 * Math.PI / 3.0;
            double x = d * Math.Cos(a), y = d * Math.Sin(a);
            inner[i] = new Circ(1.0 / r, x / r, y / r);
        }

        using var batch = new Batch();
        int drawn = 0;

        Ring(batch, frame, outer, 0, ref drawn);
        foreach (var c in inner) Ring(batch, frame, c, 1, ref drawn);

        // The four gaps of the starting quadruple: replacing the outer circle fills the middle, and
        // replacing each inner one fills the gap opposite it.
        _quads[0] = new Quad { A = inner[0], B = inner[1], C = inner[2], D = outer };
        _quads[1] = new Quad { A = outer, B = inner[1], C = inner[2], D = inner[0] };
        _quads[2] = new Quad { A = outer, B = inner[0], C = inner[2], D = inner[1] };
        _quads[3] = new Quad { A = outer, B = inner[0], C = inner[1], D = inner[2] };
        int count = 4;

        for (int depth = 2; count > 0; depth++)
        {
            LastDepth = depth;
            _visits += count;

            int keep = 0;
            for (int i = 0; i < count; i++)
            {
                var q = _quads[i];

                // Everything below this node lives in the gap between A, B and C, so that gap is
                // what decides whether to follow it. Bounding the subtree by the circle *inscribed*
                // in the gap instead is what made the gasket stop drawing partway into a zoom: the
                // circles that pile up towards a tangency point march away from the inscribed one,
                // and their distance from it grows without limit relative to its radius, so past a
                // handful of levels every subtree containing them was rejected as off screen.
                if (!GapVisible(frame, q.A, q.B, q.C)) continue;

                var next = Circ.Reflect(q.A, q.B, q.C, q.D);
                if (!(next.K.Hi > 0.0)) continue;          // also rejects NaN

                double radius = next.Radius;
                if (frame.Pixels(radius) < 0.5) continue;  // and every circle below it is smaller

                double ox = frame.OffsetX(next.Kx / next.K);
                double oy = frame.OffsetY(next.Ky / next.K);

                if (drawn < PieceBudget && Stroke(batch, frame, depth, ox, oy, radius)) drawn++;

                q.D = next;    // carry the new circle; the children below each drop one of A, B, C
                _quads[keep++] = q;
            }

            count = keep;
            if (count == 0) break;

            if (!CanExpand(depth, drawn, count * 3)) break;

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                ref var q = ref _quads[i];
                _quadSpare[made++] = new Quad { A = q.B, B = q.C, C = q.D, D = q.A };
                _quadSpare[made++] = new Quad { A = q.A, B = q.C, C = q.D, D = q.B };
                _quadSpare[made++] = new Quad { A = q.A, B = q.B, C = q.D, D = q.C };
            }

            (_quads, _quadSpare) = (_quadSpare, _quads);
            count = made;
        }

        batch.Flush(canvas, paint, palette, null);
        LastPieces = drawn;
    }

    /// <summary>
    /// Whether the gap between three mutually tangent circles reaches the window.
    ///
    /// The gap is the curvilinear triangle with the three tangency points as its corners and an arc of
    /// each circle as its sides. Each of those arcs stays within its own chord's sagitta of the
    /// straight triangle, so growing the corners' box by the largest of the three contains the gap —
    /// and therefore every circle the recursion will ever put inside it.
    /// </summary>
    private static bool GapVisible(Frame frame, Circ a, Circ b, Circ c)
    {
        if (!Touch(frame, a, b, out double abx, out double aby) ||
            !Touch(frame, b, c, out double bcx, out double bcy) ||
            !Touch(frame, c, a, out double cax, out double cay))
            return true;   // degenerate: do not cull on a number that means nothing

        // Each circle's arc runs between the two corners that involve it.
        double sag = Math.Max(Sagitta(a.Radius, abx - cax, aby - cay),
            Math.Max(Sagitta(b.Radius, abx - bcx, aby - bcy),
                Sagitta(c.Radius, bcx - cax, bcy - cay)));

        return frame.VisibleRect(
            Math.Min(abx, Math.Min(bcx, cax)) - sag, Math.Max(abx, Math.Max(bcx, cax)) + sag,
            Math.Min(aby, Math.Min(bcy, cay)) - sag, Math.Max(aby, Math.Max(bcy, cay)) + sag);
    }

    /// <summary>
    /// Where two tangent circles touch, as an offset from the view centre. In the curvature
    /// representation this is only a weighted mean, (k1*c1 + k2*c2) / (k1 + k2) — which is the other
    /// reason the circles are carried that way, and it holds for the containing circle's negative
    /// curvature as readily as for the rest.
    /// </summary>
    private static bool Touch(Frame frame, Circ p, Circ q, out double ox, out double oy)
    {
        var k = p.K + q.K;
        double scale = Math.Max(Math.Abs(p.K.Hi), Math.Abs(q.K.Hi));
        if (!(Math.Abs(k.Hi) > 1e-9 * scale))
        {
            ox = 0;
            oy = 0;
            return false;
        }

        ox = frame.OffsetX((p.Kx + q.Kx) / k);
        oy = frame.OffsetY((p.Ky + q.Ky) / k);
        return true;
    }

    /// <summary>How far an arc of the given radius bows away from a chord of the given vector.</summary>
    private static double Sagitta(double radius, double dx, double dy)
    {
        double half = 0.5 * Math.Sqrt(dx * dx + dy * dy);
        if (half >= radius) return radius;
        return radius - Math.Sqrt(radius * radius - half * half);
    }

    private static void Ring(Batch batch, Frame frame, Circ c, int depth, ref int drawn)
    {
        if (frame.Pixels(c.Radius) < 0.5) return;
        if (Stroke(batch, frame, depth, frame.OffsetX(c.X), frame.OffsetY(c.Y), c.Radius)) drawn++;
    }

    /// <summary>
    /// Draws one circle of the packing, given its centre as an offset from the view. Returns whether
    /// anything was drawn.
    ///
    /// What is on screen is the circle's *edge*, not its disk, and at depth those are very different
    /// tests: a view inside a circle a trillion times wider than itself sees one nearly straight arc of
    /// it, or nothing at all if the edge is elsewhere. Handing that circle to Skia as a radius of a
    /// hundred million million pixels draws nothing — which was the other half of the gasket going
    /// blank as it was zoomed, on top of the culling. Past the width where a circle can be rasterised
    /// at all, its arc across a window this small is a straight line to far better than a pixel, so
    /// that is what gets drawn.
    /// </summary>
    private static bool Stroke(Batch batch, Frame frame, int depth, double ox, double oy, double radius)
    {
        double reach = Math.Sqrt(frame.SpanX * frame.SpanX + frame.SpanY * frame.SpanY);
        double distance = Math.Sqrt(ox * ox + oy * oy);
        if (Math.Abs(distance - radius) > reach) return false;   // the edge is nowhere near the window

        double pixels = frame.Pixels(radius);
        if (pixels < 1e5)
        {
            batch.Circle(depth, frame.At(ox, oy), (float)pixels);
            return true;
        }

        if (distance <= 0.0) return false;

        // Nearest point of the edge to the view centre, and the tangent there.
        double nx = ox / distance, ny = oy / distance;
        double t = distance - radius;
        double bx = nx * t, by = ny * t;
        double span = 3.0 * (frame.SpanX + frame.SpanY);

        batch.Line(depth,
            frame.At(bx - ny * span, by + nx * span),
            frame.At(bx + ny * span, by - nx * span));
        return true;
    }

    // ---- Fractal tree: a trunk, then two of itself, smaller and turned. ----

    private const double TrunkLength = 0.42;
    private const double TreeShrink = 0.72;
    private const double TreeSpread = 0.42;

    private struct Twig
    {
        public Dd X, Y;
        public double Angle, Length;
    }

    private static Twig[] _twigs = new Twig[PieceBudget + 8];
    private static Twig[] _twigSpare = new Twig[PieceBudget + 8];

    private static void Tree(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        _twigs[0] = new Twig { X = 0.0, Y = -0.6, Angle = Math.PI / 2, Length = TrunkLength };
        int count = 1;

        using var batch = new Batch();
        int drawn = 0;

        for (int depth = 0; count > 0; depth++)
        {
            LastDepth = depth;
            _visits += count;

            int keep = 0;
            for (int i = 0; i < count; i++)
            {
                var t = _twigs[i];

                double dx = Math.Cos(t.Angle) * t.Length, dy = Math.Sin(t.Angle) * t.Length;
                double ox = frame.OffsetX(t.X), oy = frame.OffsetY(t.Y);

                // A branch's descendants stay within about two and a half of its own lengths of its
                // tip: shrink / (1 - shrink), for a shrink of 0.72.
                if (!frame.Visible(ox, oy, ox + dx, oy + dy, t.Length * 2.6)) continue;

                double pixels = frame.Pixels(t.Length);
                if (pixels >= 0.6 && drawn < PieceBudget)
                {
                    batch.Line(depth, frame.At(ox, oy), frame.At(ox + dx, oy + dy));
                    drawn++;
                }

                if (pixels < SmallestPiece) continue;   // too small to be worth splitting further

                t.X += dx;                              // the tip, where its children start
                t.Y += dy;
                _twigs[keep++] = t;
            }

            count = keep;
            if (count == 0) break;

            if (!CanExpand(depth, drawn, count * 2)) break;

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                ref var t = ref _twigs[i];
                double length = t.Length * TreeShrink;
                _twigSpare[made++] = new Twig
                {
                    X = t.X, Y = t.Y, Angle = t.Angle - TreeSpread, Length = length,
                };
                _twigSpare[made++] = new Twig
                {
                    X = t.X, Y = t.Y, Angle = t.Angle + TreeSpread, Length = length,
                };
            }

            (_twigs, _twigSpare) = (_twigSpare, _twigs);
            count = made;
        }

        // Width follows from the depth, because the length does.
        batch.Flush(canvas, paint, palette, depth =>
            (float)Math.Clamp(frame.Pixels(TrunkLength * Math.Pow(TreeShrink, depth)) * 0.06, 0.8, 6.0));
        LastPieces = drawn;
    }

    // ---- Hilbert curve: the L-system entry, a curve that fills its square. ----

    /// <summary>
    /// Unlike the other rules this one has to be walked in order — it is a single curve, and the order
    /// of the leaves *is* the curve — so it stays a depth-first recursion. A culled subtree breaks the
    /// path rather than being drawn across, which is what <see cref="_broke"/> carries.
    /// </summary>
    private static int _drawnPoints;

    private static bool _broke;

    /// <summary>
    /// Smallest cell the Hilbert curve is drawn down to, in pixels. Two was too fine to read: the
    /// curve passes through every cell, so at two pixels with a stroke over one the picture is a flat
    /// weave of solid colour rather than a line you can follow.
    /// </summary>
    private const double MinCell = 6.0;

    private static void Hilbert(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        // Deep enough that the finest cell is a handful of pixels, and no deeper: the number of
        // points is four to the depth, so one step too far is four times the work for nothing
        // visible. Culling is what makes a deep view affordable — only the cells over the window are
        // followed, so the depth can be ninety without the count ever being four to the ninety.
        //
        // The count of *visible* cells has to be part of that choice and not only their size. Left to
        // the size alone, a window wider than the curve's own square asks for more cells than can be
        // drawn, and since this rule is walked in the curve's order rather than by level, running out
        // partway leaves a contiguous first stretch of it drawn and the rest missing — which reads as
        // a solid block with a bite out of it, not as a curve.
        int depth = 1;
        while (depth < 120)
        {
            double side = Math.Pow(0.5, depth + 1);
            if (frame.Pixels(side) < MinCell) break;

            double across = Math.Min(1.0, 2.0 * frame.SpanX) / side + 1.0;
            double down = Math.Min(1.0, 2.0 * frame.SpanY) / side + 1.0;
            if (across * down > PointBudget) break;

            depth++;
        }

        using var path = new SKPath();
        _drawnPoints = 0;
        _broke = true;
        LastDepth = depth;

        HilbertStep(path, frame, -0.5, -0.5, 1.0, 0.0, 0.0, 1.0, depth);

        paint.Color = Shade(palette, depth);
        paint.StrokeWidth = 1.3f;
        canvas.DrawPath(path, paint);
        LastPieces = _drawnPoints;
    }

    /// <summary>
    /// The usual recursive formulation: a cell given by its corner and its two edge vectors, which
    /// halve and swap as it descends. The leaf emits the centre of its cell, and those points in
    /// order are the curve.
    /// </summary>
    private static void HilbertStep(
        SKPath path, Frame frame, Dd x, Dd y,
        double xi, double xj, double yi, double yj, int depth)
    {
        _visits++;

        // The cell spans its corner and the points its two edge vectors reach. They stay axis-aligned
        // all the way down, so their sum is the diagonal and half of it is the box.
        double ox = frame.OffsetX(x), oy = frame.OffsetY(y);
        double ex = xi + yi, ey = xj + yj;

        // A margin of a cell either side, so a neighbour just off screen is still followed: the line
        // into the first visible cell comes from it, and without it the curve would start late.
        if (!frame.VisibleBox(ox + ex * 0.5, oy + ey * 0.5, Math.Abs(ex) * 1.5, Math.Abs(ey) * 1.5))
        {
            _broke = true;
            return;
        }

        if (depth <= 0)
        {
            if (_drawnPoints >= PointBudget)
            {
                _broke = true;
                return;
            }

            var point = frame.At(ox + ex * 0.5, oy + ey * 0.5);
            if (_broke) path.MoveTo(point);
            else path.LineTo(point);

            _broke = false;
            _drawnPoints++;
            return;
        }

        HilbertStep(path, frame, x, y,
            yi / 2, yj / 2, xi / 2, xj / 2, depth - 1);
        HilbertStep(path, frame, x + xi / 2, y + xj / 2,
            xi / 2, xj / 2, yi / 2, yj / 2, depth - 1);
        HilbertStep(path, frame, x + (xi / 2 + yi / 2), y + (xj / 2 + yj / 2),
            xi / 2, xj / 2, yi / 2, yj / 2, depth - 1);
        HilbertStep(path, frame, x + (xi / 2 + yi), y + (xj / 2 + yj),
            -yi / 2, -yj / 2, -xi / 2, -xj / 2, depth - 1);
    }
}
