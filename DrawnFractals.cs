using System;
using SkiaSharp;

namespace FractalZoom;

/// <summary>
/// The fractals that are constructions rather than fields: curves built by replacing a segment with
/// a smaller copy of a rule, and point sets thrown by an iterated function system. There is no escape
/// time to colour here, so none of the kernel applies — these are drawn straight onto the canvas.
///
/// Two things make them zoom as deep as the rest of the program rather than to a fixed recursion
/// depth. Every rule recurses until a piece is smaller than a pixel and stops, so detail appears as
/// you go in and the work stays bounded; and everything is transformed to screen coordinates in
/// double precision here rather than by loading a scale into the canvas matrix, because that matrix
/// is single precision and would fall apart a few thousand times in.
/// </summary>
internal static class DrawnFractals
{
    /// <summary>Below this many pixels a piece is drawn as a line instead of being subdivided.</summary>
    private const double SmallestPiece = 2.0;

    /// <summary>
    /// Safety valve, in case a rule's culling ever fails to bound the work — deliberately far beyond
    /// what any view needs, because for the rules that shrink slowly it is a zoom limit as well as a
    /// safety valve. The tree shrinks by 0.72 a level and the dragon by 0.71, so a cap of forty-two
    /// meant their finest pieces were only about a millionth of the whole: zoom past that and there
    /// was nothing left to draw. What actually bounds the work is the piece budget and the rule that
    /// stops subdividing below a pixel.
    /// </summary>
    private const int MaxDepth = 140;

    /// <summary>
    /// Ceiling on how many pieces a single frame may consider.
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
    /// Pieces left in the current frame. Static because drawing only ever happens on the render
    /// thread, and reset at the top of every <see cref="Draw"/>.
    /// </summary>
    private static int _budget;

    /// <summary>
    /// Nodes a frame may *examine*, as opposed to draw. The two need separate limits: reaching a
    /// deeply zoomed view means descending past a great many candidates that are culled the moment
    /// they are tested, and charging those to the drawing budget meant it could be spent entirely on
    /// rejected nodes before the first circle worth drawing was reached — which looks exactly like
    /// the fractal refusing to draw past a certain zoom.
    /// </summary>
    private const int VisitBudget = 600_000;

    private static int _visits;

    /// <summary>
    /// What the last frame actually did, for the readout. When one of these fractals stops drawing,
    /// these three numbers say why without having to guess: pieces at its ceiling means the drawing
    /// budget ran out, visits at its ceiling means the search did, depth at 140 means the recursion
    /// limit, and all three small means the view is simply somewhere the fractal is not.
    /// </summary>
    public static int LastPieces { get; private set; }

    public static int LastVisits { get; private set; }

    public static int LastDepth { get; private set; }

    private static void Reached(int depth)
    {
        if (depth > LastDepth) LastDepth = depth;
    }

    /// <summary>
    /// Collects segments into one path per recursion depth, so a rule that emits thousands of little
    /// lines costs a handful of draw calls instead of thousands.
    ///
    /// This is what the tree needed. Drawing each branch as it was found meant sixteen thousand
    /// DrawLine calls a frame, each preceded by a change of colour and width, which Skia cannot batch
    /// across — eleven frames a second at the opening view, before any zooming at all. Depth is the
    /// right thing to group by because it is exactly what the colour and the width are derived from.
    /// </summary>
    private sealed class Batch : IDisposable
    {
        private readonly SKPath?[] _paths = new SKPath?[MaxDepth + 2];

        public void Line(int depth, SKPoint from, SKPoint to)
        {
            var path = _paths[Math.Clamp(depth, 0, _paths.Length - 1)] ??= new SKPath();
            path.MoveTo(from);
            path.LineTo(to);
        }

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

    /// <summary>Plane-to-screen transform, the visible rectangle, and how big a pixel is.</summary>
    private readonly struct Frame
    {
        public readonly double CenterX, CenterY, Pixel, HalfW, HalfH;
        public readonly double Left, Right, Bottom, Top;

        public Frame(double centerX, double centerY, double scale, int width, int height)
        {
            CenterX = centerX;
            CenterY = centerY;
            Pixel = 2.0 * scale / height;
            HalfW = width * 0.5;
            HalfH = height * 0.5;

            double halfWidth = Pixel * HalfW;
            Left = centerX - halfWidth;
            Right = centerX + halfWidth;
            Bottom = centerY - scale;
            Top = centerY + scale;
        }

        public SKPoint To(double x, double y) => new(
            (float)((x - CenterX) / Pixel + HalfW),
            (float)(HalfH - (y - CenterY) / Pixel));

        public double Pixels(double planeLength) => planeLength / Pixel;

        /// <summary>Whether a box, grown by a margin for whatever the rule adds outside it, is in view.</summary>
        public bool Visible(double x0, double y0, double x1, double y1, double margin)
        {
            double lo = Math.Min(x0, x1) - margin, hi = Math.Max(x0, x1) + margin;
            if (hi < Left || lo > Right) return false;
            lo = Math.Min(y0, y1) - margin;
            hi = Math.Max(y0, y1) + margin;
            return hi >= Bottom && lo <= Top;
        }
    }

    public static void Draw(
        SKCanvas canvas, Fractal kind, double centerX, double centerY, double scale,
        int width, int height, Mandelbrot.Palette palette)
    {
        var frame = new Frame(centerX, centerY, scale, width, height);
        _budget = PieceBudget;
        _visits = VisitBudget;
        LastDepth = 0;

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
            case Fractal.BarnsleyFern: Fern(canvas, paint, palette, frame); break;
            case Fractal.ApollonianGasket: Apollonian(canvas, paint, palette, frame); break;
            case Fractal.FractalTree: Tree(canvas, paint, palette, frame); break;
            default: Hilbert(canvas, paint, palette, frame); break;
        }

        LastPieces = PieceBudget - _budget;
        LastVisits = VisitBudget - _visits;
    }

    private static SKColor Shade(Mandelbrot.Palette palette, int depth)
    {
        uint c = palette.Colors[(depth * 137) & (palette.Colors.Length - 1)];
        return new SKColor((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }

    // ---- Koch snowflake: each segment becomes four, with an equilateral spike in the middle. ----

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

        using var batch = new Batch();
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            KochEdge(batch, frame, xs[i], ys[i], xs[j], ys[j], 0);
        }

        batch.Flush(canvas, paint, palette, null);
    }

    private static void KochEdge(
        Batch batch, Frame frame, double ax, double ay, double bx, double by, int depth)
    {
        if (_budget-- <= 0) return;

        double dx = bx - ax, dy = by - ay;
        double length = Math.Sqrt(dx * dx + dy * dy);

        // The spike reaches about 0.29 of the segment's length outside its own box.
        if (!frame.Visible(ax, ay, bx, by, length * 0.3)) return;

        if (depth >= MaxDepth || frame.Pixels(length) < SmallestPiece)
        {
            batch.Line(depth, frame.To(ax, ay), frame.To(bx, by));
            return;
        }

        double x1 = ax + dx / 3.0, y1 = ay + dy / 3.0;
        double x3 = ax + 2.0 * dx / 3.0, y3 = ay + 2.0 * dy / 3.0;

        // Apex of the equilateral triangle standing on the middle third. The right-hand normal, so
        // that the spikes on a counter-clockwise triangle point outward and make a snowflake rather
        // than an inward-folded star.
        double mx = (x1 + x3) * 0.5, my = (y1 + y3) * 0.5;
        double h = Math.Sqrt(3.0) / 6.0;
        double x2 = mx + dy * h, y2 = my - dx * h;

        KochEdge(batch, frame, ax, ay, x1, y1, depth + 1);
        KochEdge(batch, frame, x1, y1, x2, y2, depth + 1);
        KochEdge(batch, frame, x2, y2, x3, y3, depth + 1);
        KochEdge(batch, frame, x3, y3, bx, by, depth + 1);
    }

    // ---- Heighway dragon: each segment becomes two, meeting over a right angle. ----

    private static void Dragon(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        using var batch = new Batch();
        DragonEdge(batch, frame, -0.35, 0.0, 0.95, 0.0, 1, 0);
        batch.Flush(canvas, paint, palette, null);
    }

    private static void DragonEdge(
        Batch batch, Frame frame,
        double ax, double ay, double bx, double by, int hand, int depth)
    {
        if (_budget-- <= 0) return;

        double dx = bx - ax, dy = by - ay;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!frame.Visible(ax, ay, bx, by, length * 0.75)) return;

        if (depth >= MaxDepth || frame.Pixels(length) < SmallestPiece)
        {
            batch.Line(depth, frame.To(ax, ay), frame.To(bx, by));
            return;
        }

        // The corner sits half a segment away, square to it; which side alternates.
        double mx = (ax + bx) * 0.5 + hand * dy * 0.5;
        double my = (ay + by) * 0.5 - hand * dx * 0.5;

        DragonEdge(batch, frame, ax, ay, mx, my, 1, depth + 1);
        DragonEdge(batch, frame, mx, my, bx, by, -1, depth + 1);
    }

    // ---- Barnsley fern: an iterated function system, thrown as points. ----

    private static void Fern(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        // Fixed seed every frame, so the fern is the same fern each time rather than shimmering.
        var rng = new Random(12345);
        int points = 240_000;
        var buffer = new SKPoint[points];
        int kept = 0;

        double x = 0, y = 0;
        for (int i = 0; i < points; i++)
        {
            double p = rng.NextDouble();
            double nx, ny;

            if (p < 0.01) { nx = 0.0; ny = 0.16 * y; }
            else if (p < 0.08) { nx = 0.2 * x - 0.26 * y; ny = 0.23 * x + 0.22 * y + 1.6; }
            else if (p < 0.15) { nx = -0.15 * x + 0.28 * y; ny = 0.26 * x + 0.24 * y + 0.44; }
            else { nx = 0.85 * x + 0.04 * y; ny = -0.04 * x + 0.85 * y + 1.6; }

            x = nx;
            y = ny;

            if (frame.Visible(x, y, x, y, 0)) buffer[kept++] = frame.To(x, y);
        }

        paint.Style = SKPaintStyle.Fill;
        paint.StrokeWidth = 1.0f;
        paint.Color = Shade(palette, 3);
        canvas.DrawPoints(SKPointMode.Points, buffer.AsSpan(0, kept).ToArray(), paint);
        paint.Style = SKPaintStyle.Stroke;
    }

    // ---- Apollonian gasket: fill each gap between three tangent circles, forever. ----

    /// <summary>
    /// A circle as Descartes' theorem likes them: curvature, and curvature times centre. In this form
    /// the reflection that generates the next circle of a packing is linear — no square roots, and so
    /// no choosing between their branches, which is what an earlier attempt at this got wrong.
    /// </summary>
    private readonly record struct Circ(double K, double Kx, double Ky)
    {
        public double Radius => 1.0 / Math.Abs(K);
        public double X => Kx / K;
        public double Y => Ky / K;

        public static Circ Reflect(Circ a, Circ b, Circ c, Circ d) => new(
            2.0 * (a.K + b.K + c.K) - d.K,
            2.0 * (a.Kx + b.Kx + c.Kx) - d.Kx,
            2.0 * (a.Ky + b.Ky + c.Ky) - d.Ky);
    }

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

        Circle(canvas, paint, palette, frame, outer, 0);
        foreach (var c in inner) Circle(canvas, paint, palette, frame, c, 1);

        // The four gaps of the starting quadruple: replacing the outer circle fills the middle, and
        // replacing each inner one fills the gap opposite it.
        Gap(canvas, paint, palette, frame, inner[0], inner[1], inner[2], outer, 2);
        Gap(canvas, paint, palette, frame, outer, inner[1], inner[2], inner[0], 2);
        Gap(canvas, paint, palette, frame, outer, inner[0], inner[2], inner[1], 2);
        Gap(canvas, paint, palette, frame, outer, inner[0], inner[1], inner[2], 2);
    }

    /// <summary>
    /// Replaces <paramref name="d"/> in the quadruple with the other circle tangent to the same
    /// three, which is the one filling the gap, then recurses into the three gaps that opens.
    /// </summary>
    private static void Gap(
        SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame,
        Circ a, Circ b, Circ c, Circ d, int depth)
    {
        // The depth limit has to be generous, not tidy. Curvature roughly triples per level, so the
        // circles you are looking at after zooming in a few thousand times are fifty or more levels
        // down — a cap of twenty-four meant the gasket simply stopped being drawn once you went past
        // what it could reach. What bounds the work is the pixel test below, not the depth.
        // Charged against the visit budget, not the drawing one: most of these are rejected two lines
        // below and cost nothing to draw.
        if (depth >= 140 || _visits-- <= 0) return;
        Reached(depth);

        var next = Circ.Reflect(a, b, c, d);
        if (next.K <= 0 || double.IsNaN(next.K)) return;

        double radius = next.Radius;
        if (frame.Pixels(radius) < 0.6) return;

        // Culled generously: the three gaps this circle opens lie beside it, so a tight box around
        // the circle alone would prune subtrees whose own circles are still on screen.
        if (!frame.Visible(next.X, next.Y, next.X, next.Y, radius * 3.0)) return;

        if (_budget-- > 0) Circle(canvas, paint, palette, frame, next, depth);

        Gap(canvas, paint, palette, frame, b, c, next, a, depth + 1);
        Gap(canvas, paint, palette, frame, a, c, next, b, depth + 1);
        Gap(canvas, paint, palette, frame, a, b, next, c, depth + 1);
    }

    private static void Circle(
        SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame, Circ c, int depth)
    {
        paint.Color = Shade(palette, depth);
        canvas.DrawCircle(frame.To(c.X, c.Y), (float)frame.Pixels(c.Radius), paint);
    }

    // ---- Fractal tree: a trunk, then two of itself, smaller and turned. ----

    private const double TrunkLength = 0.42;
    private const double TreeShrink = 0.72;

    private static void Tree(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        using var batch = new Batch();
        Branch(batch, frame, 0.0, -0.6, Math.PI / 2, TrunkLength, 0);

        // Width follows from the depth, because the length does.
        batch.Flush(canvas, paint, palette, depth =>
            (float)Math.Clamp(frame.Pixels(TrunkLength * Math.Pow(TreeShrink, depth)) * 0.06, 0.8, 6.0));
    }

    private static void Branch(
        Batch batch, Frame frame, double x, double y, double angle, double length, int depth)
    {
        if (_budget-- <= 0) return;

        double ex = x + Math.Cos(angle) * length;
        double ey = y + Math.Sin(angle) * length;

        // A branch's descendants stay within about two and a half of its own lengths of its tip:
        // shrink / (1 - shrink), for a shrink of 0.72.
        if (!frame.Visible(x, y, ex, ey, length * 2.6)) return;
        if (depth >= MaxDepth) return;

        if (frame.Pixels(length) >= 0.6) batch.Line(depth, frame.To(x, y), frame.To(ex, ey));
        if (frame.Pixels(length) < SmallestPiece) return;

        const double spread = 0.42;
        Branch(batch, frame, ex, ey, angle - spread, length * TreeShrink, depth + 1);
        Branch(batch, frame, ex, ey, angle + spread, length * TreeShrink, depth + 1);
    }

    // ---- Hilbert curve: the L-system entry, a curve that fills its square. ----

    private static void Hilbert(SKCanvas canvas, SKPaint paint, Mandelbrot.Palette palette, Frame frame)
    {
        // Deep enough that the finest cell is about two pixels, and no deeper: the number of points
        // is four to the depth, so one step too far is four times the work for nothing visible.
        int depth = 1;
        while (depth < 9 && frame.Pixels(1.0 / (1 << depth)) > SmallestPiece * 2) depth++;

        using var path = new SKPath();
        bool started = false;
        HilbertStep(path, frame, ref started, -0.5, -0.5, 1.0, 0.0, 0.0, 1.0, depth);

        paint.Color = Shade(palette, depth);
        paint.StrokeWidth = 1.3f;
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// The usual recursive formulation: a cell given by its corner and its two edge vectors, which
    /// halve and swap as it descends. The leaf emits the centre of its cell, and those points in
    /// order are the curve.
    /// </summary>
    private static void HilbertStep(
        SKPath path, Frame frame, ref bool started,
        double x, double y, double xi, double xj, double yi, double yj, int depth)
    {
        if (depth <= 0)
        {
            var point = frame.To(x + (xi + yi) * 0.5, y + (xj + yj) * 0.5);
            if (started) path.LineTo(point);
            else { path.MoveTo(point); started = true; }
            return;
        }

        HilbertStep(path, frame, ref started, x, y,
            yi / 2, yj / 2, xi / 2, xj / 2, depth - 1);
        HilbertStep(path, frame, ref started, x + xi / 2, y + xj / 2,
            xi / 2, xj / 2, yi / 2, yj / 2, depth - 1);
        HilbertStep(path, frame, ref started, x + xi / 2 + yi / 2, y + xj / 2 + yj / 2,
            xi / 2, xj / 2, yi / 2, yj / 2, depth - 1);
        HilbertStep(path, frame, ref started, x + xi / 2 + yi, y + xj / 2 + yj,
            -yi / 2, -yj / 2, -xi / 2, -xj / 2, depth - 1);
    }
}
