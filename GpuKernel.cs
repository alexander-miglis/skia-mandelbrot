using System;
using System.Diagnostics;
using Silk.NET.OpenGL;

namespace FractalZoom;

/// <summary>
/// The Mandelbrot kernel as an OpenGL program, so the graphics card computes the escape field
/// instead of the CPU. Same two passes as <see cref="Mandelbrot"/> — escape times, then a
/// band-limited colouring that reads the neighbouring escape times — and the same arithmetic,
/// including the perturbed iteration against a reference orbit, so the two backends agree pixel
/// for pixel to within rounding.
///
/// Two things about it are not obvious:
///
/// The iteration runs in fp64. Perturbation is what makes a GPU kernel viable at all here, but it
/// does not lower the precision needed per pixel — it lowers the precision needed for the *centre*.
/// The per-pixel deltas are still doubles, and at 1e-60 they are far below what fp32 can even
/// represent. Consumer cards run fp64 at a fraction of their fp32 rate, and that fraction is still
/// worth many CPU cores.
///
/// A frame is rendered in horizontal strips spread across display frames, rather than in one draw.
/// The whole design here rests on the display staying at vsync while the kernel runs behind it, and
/// a single draw call that takes 200 ms would stall the window for 200 ms — and, past a couple of
/// seconds, be killed outright by the Windows display watchdog. Strips keep each draw short and
/// let the latency show up where the rest of the program already knows how to absorb it: as a
/// frame that is a little out of date, which the display re-projects.
/// </summary>
internal sealed unsafe class GpuKernel : IDisposable
{
    private const int PaletteSize = 4096;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlRgba8 = 0x8058;

    /// <summary>Timer queries in flight. Results are collected whenever they happen to be ready.</summary>
    private const int QueryCount = 8;

    /// <summary>
    /// Strips are sized from a running cost per pixel-iteration. Starting pessimistic means the
    /// first frame of a run is split more finely than it needs to be, which costs a few frames of
    /// latency once; starting optimistic means a stall, which is what the strips exist to avoid.
    /// </summary>
    private const double InitialNsPerUnit = 2e-3;

    /// <summary>
    /// Smallest strip worth issuing, in pixels. Below roughly this the card is no longer saturated
    /// and the strip costs far more than its share of the frame — see <see cref="RowsForBudget"/>
    /// for the measurements. Set at the low end of the flat part of that curve rather than the
    /// middle: it measures the same, and a smaller floor leaves the pacing more room to keep the
    /// display at vsync when frames are expensive.
    /// </summary>
    private const int MinStripPixels = 128_000;

    private readonly GL _gl;

    private uint _vao;
    private uint _escapeProgram, _colourProgram, _resolveProgram, _marchProgram;

    private int _mHalf, _mHeight, _mKind, _mSteps, _mYaw, _mPitch, _mDistance, _mPalette, _mShift;

    /// <summary>True when the frame in flight is a ray-marched one rather than an escape-time field.</summary>
    private bool _marching;

    private uint _fieldTex, _fieldFbo;
    private int _fieldW, _fieldH;

    // Only used when supersampling, where the colouring lands here at kernel resolution and the
    // resolve pass reads it down into the presented texture.
    private uint _colourTex, _colourFbo;
    private int _colourW, _colourH;

    private readonly uint[] _frameTex = new uint[2];
    private readonly uint[] _frameFbo = new uint[2];
    private readonly int[] _texW = new int[2];
    private readonly int[] _texH = new int[2];
    private int _slot;          // the half currently being filled
    private int _readySlot = -1;

    private uint _paletteTex;
    private Mandelbrot.Palette? _uploadedPalette;

    private uint _orbitBuffer, _orbitTex;
    private ReferenceOrbit? _uploadedOrbit;
    private int _orbitCount;

    private readonly uint[] _queries = new uint[QueryCount];
    private readonly double[] _queryWork = new double[QueryCount];
    private int _queryHead;
    private int _queriesInFlight;
    private double _nsPerUnit = InitialNsPerUnit;
    private ulong _gpuNs, _gpuNsAtFinish;

    // Uniform locations, looked up once.
    private int _uCenter, _uPixel, _uHalf, _uHeight, _uMaxIter, _uOrbitCount, _uOrbit, _uKind;
    private int _cField, _cPalette, _cMean, _cShift, _cSize;
    private int _rSource, _rScale, _rSourceSize, _rTaps;

    private readonly Stopwatch _clock = new();

    private FractalRenderer.View _pending;
    private Mandelbrot.Palette? _pendingPalette;
    private int _pendingW, _pendingH, _pendingOutW, _pendingOutH;
    private bool _hasPending;

    private FractalRenderer.View _frame;
    private int _frameW, _frameH, _frameRow, _frameOutW, _frameOutH;
    private bool _busy;

    private FractalRenderer.View _readyView;
    private int _readyKernelW, _readyKernelH;

    public GpuKernel(GL gl)
    {
        _gl = gl;

        int major = _gl.GetInteger(GetPName.MajorVersion);
        if (major < 4)
            throw new NotSupportedException(
                $"needs OpenGL 4.0 for double-precision shaders, this context is {major}.x");

        _escapeProgram = Link(VertexSource, EscapeSource);
        _colourProgram = Link(VertexSource, ColourSource);
        _resolveProgram = Link(VertexSource, ResolveSource);
        _marchProgram = Link(VertexSource, MarchSource);

        _mHalf = _gl.GetUniformLocation(_marchProgram, "uHalf");
        _mHeight = _gl.GetUniformLocation(_marchProgram, "uHeight");
        _mKind = _gl.GetUniformLocation(_marchProgram, "uKind");
        _mSteps = _gl.GetUniformLocation(_marchProgram, "uSteps");
        _mYaw = _gl.GetUniformLocation(_marchProgram, "uYaw");
        _mPitch = _gl.GetUniformLocation(_marchProgram, "uPitch");
        _mDistance = _gl.GetUniformLocation(_marchProgram, "uDistance");
        _mPalette = _gl.GetUniformLocation(_marchProgram, "uPalette");
        _mShift = _gl.GetUniformLocation(_marchProgram, "uShift");

        _uCenter = _gl.GetUniformLocation(_escapeProgram, "uCenter");
        _uPixel = _gl.GetUniformLocation(_escapeProgram, "uPixel");
        _uHalf = _gl.GetUniformLocation(_escapeProgram, "uHalf");
        _uHeight = _gl.GetUniformLocation(_escapeProgram, "uHeight");
        _uMaxIter = _gl.GetUniformLocation(_escapeProgram, "uMaxIter");
        _uOrbitCount = _gl.GetUniformLocation(_escapeProgram, "uOrbitCount");
        _uOrbit = _gl.GetUniformLocation(_escapeProgram, "uOrbit");
        _uKind = _gl.GetUniformLocation(_escapeProgram, "uKind");

        _cField = _gl.GetUniformLocation(_colourProgram, "uField");
        _cPalette = _gl.GetUniformLocation(_colourProgram, "uPalette");
        _cMean = _gl.GetUniformLocation(_colourProgram, "uMean");
        _cShift = _gl.GetUniformLocation(_colourProgram, "uShift");
        _cSize = _gl.GetUniformLocation(_colourProgram, "uSize");

        _rSource = _gl.GetUniformLocation(_resolveProgram, "uSource");
        _rScale = _gl.GetUniformLocation(_resolveProgram, "uScale");
        _rSourceSize = _gl.GetUniformLocation(_resolveProgram, "uSourceSize");
        _rTaps = _gl.GetUniformLocation(_resolveProgram, "uTaps");

        // Nothing is fetched from it — the vertex shader builds the covering triangle from
        // gl_VertexID — but core profile still refuses to draw without one bound.
        _vao = _gl.GenVertexArray();

        _fieldFbo = _gl.GenFramebuffer();
        _colourFbo = _gl.GenFramebuffer();
        _frameFbo[0] = _gl.GenFramebuffer();
        _frameFbo[1] = _gl.GenFramebuffer();

        // Allocated with one dummy texel so the sampler has a complete backing store even before a
        // descent goes deep enough to need a reference orbit.
        _orbitBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _orbitBuffer);
        var seed = stackalloc uint[4];
        _gl.BufferData(BufferTargetARB.TextureBuffer, 4 * sizeof(uint), seed, BufferUsageARB.StaticDraw);
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);

        _orbitTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureBuffer, _orbitTex);
        _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba32ui, _orbitBuffer);
        _gl.BindTexture(TextureTarget.TextureBuffer, 0);

        _paletteTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        for (int i = 0; i < QueryCount; i++) _queries[i] = _gl.GenQuery();

        Renderer = _gl.GetStringS(StringName.Renderer) ?? "unknown";
    }

    /// <summary>The card's own name for itself, for the readout.</summary>
    public string Renderer { get; }

    /// <summary>Wall time the last completed frame took end to end, in milliseconds.</summary>
    public double KernelMs { get; private set; } = 8.0;

    /// <summary>
    /// Card time the strips of a frame add up to, in milliseconds — what the kernel would take if
    /// it were allowed to run in one go. <see cref="KernelMs"/> above it is time spent waiting for
    /// the next display frame, which is the price of not stalling the window.
    /// </summary>
    public double GpuMs { get; private set; }

    /// <summary>Card time spent on kernel strips since startup, in milliseconds.</summary>
    public double TotalGpuMs => _gpuNs / 1e6;

    /// <summary>Kernel frames finished since startup. With the above, gives a mean free of the EWMA's lag.</summary>
    public long CompletedFrames { get; private set; }

    /// <summary>True while a frame is part-rendered, so <see cref="Step"/> still has work to do.</summary>
    public bool Busy => _busy;

    /// <summary>
    /// Dimensions the frame currently presentable was computed at, which is larger than the texture
    /// it ended up in whenever the detail setting is supersampling.
    /// </summary>
    public (int Width, int Height) LastSize => (_readyKernelW, _readyKernelH);

    /// <summary>
    /// Tells the kernel what to render next. The newest submission wins; nothing queues.
    /// </summary>
    /// <param name="outWidth">
    /// Width to present at. Below <paramref name="width"/> the kernel is supersampling, and the
    /// extra samples are averaged down to this size before the frame is handed over.
    /// </param>
    public void Submit(FractalRenderer.View view, Mandelbrot.Palette palette,
        int width, int height, int outWidth, int outHeight)
    {
        _pending = view;
        _pendingPalette = palette;
        _pendingW = width;
        _pendingH = height;
        _pendingOutW = Math.Clamp(outWidth, 16, width);
        _pendingOutH = Math.Clamp(outHeight, 16, height);
        _hasPending = true;
    }

    /// <summary>
    /// Pushes the current frame forward by about <paramref name="budgetMs"/> of card time, starting
    /// a new one if the last has finished. Called once per display frame.
    /// </summary>
    public void Step(double budgetMs)
    {
        CollectQueries();

        if (!_busy && _hasPending) BeginFrame();
        if (!_busy) return;

        SetupState();

        bool resolving = _frameOutW < _frameW;
        _gl.UseProgram(_marching ? _marchProgram : _escapeProgram);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer,
            _marching ? (resolving ? _colourFbo : _frameFbo[_slot]) : _fieldFbo);
        _gl.Viewport(0, 0, (uint)_frameW, (uint)_frameH);

        // Rebound every strip rather than once per frame: Skia draws between strips and leaves the
        // texture units wherever its own last draw did.
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (_marching)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
            _gl.Uniform1(_mPalette, 0);
        }
        else
        {
            _gl.BindTexture(TextureTarget.TextureBuffer, _orbitTex);
            _gl.Uniform1(_uOrbit, 0);
        }

        int rows = RowsForBudget(budgetMs);
        rows = Math.Min(rows, _frameH - _frameRow);

        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(0, _frameRow, (uint)_frameW, (uint)rows);

        double work = (double)rows * _frameW * _frame.MaxIterations;
        int query = BeginQuery(work);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        if (query >= 0) _gl.EndQuery(QueryTarget.TimeElapsed);

        _frameRow += rows;
        if (_frameRow >= _frameH) Finish();

        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    /// <summary>
    /// The most recently finished frame: its texture, the view it was rendered for, and its size.
    /// The texture stays owned by the kernel and stays valid until the frame after next.
    /// </summary>
    public bool TryGetReady(out uint texture, out FractalRenderer.View view, out int width, out int height)
    {
        if (_readySlot < 0)
        {
            texture = 0;
            view = default;
            width = height = 0;
            return false;
        }

        texture = _frameTex[_readySlot];
        view = _readyView;
        width = _texW[_readySlot];
        height = _texH[_readySlot];
        return true;
    }

    private void BeginFrame()
    {
        _frame = _pending;
        _frameW = _pendingW;
        _frameH = _pendingH;
        _frameOutW = _pendingOutW;
        _frameOutH = _pendingOutH;
        _frameRow = 0;
        _hasPending = false;
        _busy = true;

        _marching = FractalKind.Of(_frame.Kind).Style == RenderStyle.Raymarched;

        _slot = _readySlot == 0 ? 1 : 0;
        if (!_marching) EnsureField(_frameW, _frameH);
        EnsureTexture(ref _frameTex[_slot], ref _texW[_slot], ref _texH[_slot], _frameFbo[_slot],
            _frameOutW, _frameOutH, "frame");
        if (_frameOutW < _frameW)
            EnsureTexture(ref _colourTex, ref _colourW, ref _colourH, _colourFbo,
                _frameW, _frameH, "colour");
        UploadPalette(_pendingPalette!);
        UploadOrbit(_frame.Reference);

        SetupState();

        if (_marching)
        {
            // The flat view's pan and zoom read as an orbit and a distance, so one set of controls
            // works for both kinds of fractal.
            _gl.UseProgram(_marchProgram);
            _gl.Uniform2(_mHalf, _frameW * 0.5f, _frameH * 0.5f);
            _gl.Uniform1(_mHeight, (float)_frameH);
            _gl.Uniform1(_mKind, (int)_frame.Kind);
            _gl.Uniform1(_mSteps, Math.Clamp(_frame.MaxIterations / 3, 60, 220));
            _gl.Uniform1(_mYaw, (float)_frame.CenterX);
            _gl.Uniform1(_mPitch, (float)Math.Clamp(_frame.CenterY, -1.4, 1.4));
            // The near limit is how far into a 3D fractal you can get. It has to stay well above
            // where single precision gives out — the marcher works in fp32, and its hit tolerance
            // scales with distance — but 0.02 was stopping the camera less than a couple of hundred
            // times in, which is nowhere.
            _gl.Uniform1(_mDistance, (float)Math.Clamp(_frame.Scale * 2.2, 0.0008, 40.0));
            _gl.Uniform1(_mShift, (float)_frame.PaletteShift);
            _clock.Restart();
            return;
        }

        _gl.UseProgram(_escapeProgram);

        // The centre is an offset from the reference orbit when there is one, exactly as the CPU
        // kernel reads it, so nothing here has to know which mode the director is in.
        Uniform2d(_uCenter, _frame.CenterX, _frame.CenterY);
        Uniform1d(_uPixel, 2.0 * _frame.Scale / _frameH);
        _gl.Uniform2(_uHalf, _frameW * 0.5f, _frameH * 0.5f);
        _gl.Uniform1(_uHeight, (float)_frameH);
        _gl.Uniform1(_uMaxIter, _frame.MaxIterations);
        _gl.Uniform1(_uOrbitCount, _frame.Reference is null ? 0 : _orbitCount);
        _gl.Uniform1(_uKind, (int)_frame.Kind);

        _clock.Restart();
    }

    private void Finish()
    {
        bool resolving = _frameOutW < _frameW;

        // A marched frame is already colour, so there is nothing to colourise — only, if it was
        // supersampled, the same resolve the others get.
        if (_marching)
        {
            if (resolving) Resolve();
            FinishTiming();
            return;
        }

        _gl.UseProgram(_colourProgram);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, resolving ? _colourFbo : _frameFbo[_slot]);
        _gl.Viewport(0, 0, (uint)_frameW, (uint)_frameH);
        _gl.Disable(EnableCap.ScissorTest);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _fieldTex);
        _gl.Uniform1(_cField, 0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
        _gl.Uniform1(_cPalette, 1);
        _gl.ActiveTexture(TextureUnit.Texture0);

        var palette = _uploadedPalette!;
        _gl.Uniform3(_cMean, palette.MeanR / 255f, palette.MeanG / 255f, palette.MeanB / 255f);
        _gl.Uniform1(_cShift, (float)_frame.PaletteShift);
        _gl.Uniform2(_cSize, _frameW, _frameH);

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        if (resolving) Resolve();

        FinishTiming();
    }

    /// <summary>Closes the books on a frame: its timing, and handing it over as the one to draw.</summary>
    private void FinishTiming()
    {
        _clock.Stop();

        // Strips whose timer has not reported yet land in the next frame's tally instead of this
        // one's, so this is smoothed rather than read as an exact per-frame figure.
        double elapsed = (_gpuNs - _gpuNsAtFinish) / 1e6;
        _gpuNsAtFinish = _gpuNs;
        GpuMs += (elapsed - GpuMs) * 0.3;

        // Drawing commands return long before the card has run them, so when a whole frame is
        // issued in one strip the wall clock above reads near zero — which would tell the zoom
        // controller the kernel is free and let it race ahead of frames that have not been drawn
        // yet. The card's own timing is the floor under that.
        KernelMs = Math.Max(_clock.Elapsed.TotalMilliseconds, GpuMs);

        _readySlot = _slot;
        _readyView = _frame;
        _readyKernelW = _frameW;
        _readyKernelH = _frameH;
        CompletedFrames++;
        _busy = false;
    }

    /// <summary>
    /// Averages the supersampled colour buffer down to the size the frame will be drawn at.
    ///
    /// Skia can minify while drawing, but only well with mipmaps, and its mipmapped sampling of a
    /// texture it does not own came out as a flat wash of the image's mean colour — the mip chain is
    /// generated and complete, and it still does not read from it. Resolving here sidesteps that
    /// entirely and is the better filter anyway: a box over exactly the samples that belong to the
    /// output pixel, rather than a blend of two power-of-two levels that straddle it.
    /// </summary>
    private void Resolve()
    {
        _gl.UseProgram(_resolveProgram);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frameFbo[_slot]);
        _gl.Viewport(0, 0, (uint)_frameOutW, (uint)_frameOutH);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _colourTex);
        _gl.Uniform1(_rSource, 0);

        float scaleX = (float)_frameW / _frameOutW;
        float scaleY = (float)_frameH / _frameOutH;
        _gl.Uniform2(_rScale, scaleX, scaleY);
        _gl.Uniform2(_rSourceSize, _frameW, _frameH);
        _gl.Uniform1(_rTaps, Math.Clamp((int)Math.Ceiling(Math.Max(scaleX, scaleY)), 1, 4));

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    /// <summary>
    /// How many rows the card can be expected to get through in the time available. The cost model
    /// is one number — nanoseconds per pixel-iteration — which is crude (most pixels escape long
    /// before the iteration limit) but self-correcting, since it is measured from the strips that
    /// have actually run at this depth.
    /// </summary>
    private int RowsForBudget(double budgetMs)
    {
        double perRow = _nsPerUnit * _frameW * Math.Max(1, _frame.MaxIterations);
        int rows = (int)(budgetMs * 1e6 / Math.Max(1e-9, perRow));

        // Never below what it takes to keep the card busy. A strip is a rectangle of pixels and
        // the card wants thousands of threads in flight to hide the latency of a dependent fp64
        // chain; a thin one leaves most of it idle, so the work does not get cheaper anywhere near
        // in proportion to its size. Measured on one frozen view at 1e17x, cost per whole frame
        // against strip height:
        //
        //     224 rows  65.4 ms      112 rows   78.1 ms      32 rows  224.2 ms
        //     168 rows  66.0 ms       64 rows  149.0 ms      16 rows  321.6 ms
        //
        // Worse, it compounds: the cost model below measures the collapse, concludes strips must be
        // smaller still to fit the budget, and drives itself into the corner. The floor is what
        // stops that, at the price of a strip sometimes overrunning a vsync interval.
        int left = _frameH - _frameRow;
        rows = Math.Max(rows, MinStripPixels / Math.Max(1, _frameW));

        // Splitting a frame costs a whole display frame of latency, so a frame that only just
        // overruns the budget is better finished than split: overshooting one vsync interval by a
        // third beats handing the display a frame that is twice as stale.
        if (left <= rows * 3 / 2) return left;

        return Math.Clamp(rows, 1, _frameH);
    }

    private int BeginQuery(double work)
    {
        if (_queriesInFlight >= QueryCount) return -1;

        int slot = _queryHead;
        _queryHead = (_queryHead + 1) % QueryCount;
        _queriesInFlight++;
        _queryWork[slot] = work;
        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[slot]);
        return slot;
    }

    /// <summary>
    /// Folds whichever timer results have landed into the cost estimate, without ever waiting on
    /// one: a blocking read here would undo the point of measuring at all.
    /// </summary>
    private void CollectQueries()
    {
        while (_queriesInFlight > 0)
        {
            int slot = (_queryHead - _queriesInFlight + QueryCount) % QueryCount;
            _gl.GetQueryObject(_queries[slot], QueryObjectParameterName.ResultAvailable, out uint ready);
            if (ready == 0) return;

            _gl.GetQueryObject(_queries[slot], QueryObjectParameterName.Result, out ulong ns);
            _queriesInFlight--;

            _gpuNs += ns;
            double work = _queryWork[slot];
            if (work > 0 && ns > 0)
            {
                double sample = ns / work;
                _nsPerUnit += (sample - _nsPerUnit) * 0.25;
            }
        }
    }

    private void SetupState()
    {
        _gl.BindVertexArray(_vao);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.StencilTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.ColorMask(true, true, true, true);
    }

    private void EnsureField(int w, int h)
    {
        if (_fieldTex != 0 && _fieldW == w && _fieldH == h) return;

        if (_fieldTex != 0) _gl.DeleteTexture(_fieldTex);
        _fieldTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fieldTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f, (uint)w, (uint)h, 0,
            PixelFormat.Red, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _fieldW = w;
        _fieldH = h;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fieldFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fieldTex, 0);
        Check("escape field");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Reallocates an RGBA8 target and its framebuffer if the size has changed. Sizes are tracked
    /// per target rather than globally: the frame being presented keeps its own until the one being
    /// filled has finished at the new size, so a resize does not blank the window.
    /// </summary>
    private void EnsureTexture(ref uint texture, ref int width, ref int height, uint fbo,
        int w, int h, string what)
    {
        if (texture != 0 && width == w && height == h) return;

        if (texture != 0) _gl.DeleteTexture(texture);
        texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        width = w;
        height = h;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        Check(what);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Check(string what)
    {
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"{what} framebuffer incomplete: {status}");
    }

    private void UploadPalette(Mandelbrot.Palette palette)
    {
        if (ReferenceEquals(palette, _uploadedPalette)) return;
        _uploadedPalette = palette;

        var bytes = new byte[PaletteSize * 4];
        for (int i = 0; i < PaletteSize; i++)
        {
            uint c = palette.Colors[i];
            bytes[i * 4 + 0] = (byte)(c >> 16);
            bytes[i * 4 + 1] = (byte)(c >> 8);
            bytes[i * 4 + 2] = (byte)c;
            bytes[i * 4 + 3] = 0xFF;
        }

        _gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
        fixed (byte* p = bytes)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, PaletteSize, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Hands the reference orbit to the card as raw doubles — two 32-bit halves per component,
    /// reassembled in the shader — because there is no double-precision texture format. Rounding
    /// them to fp32 instead would be far cheaper and completely wrong: the orbit is the one thing
    /// in the perturbed iteration that every pixel shares, so an error in it is an error in every
    /// pixel at once, and at depth it shows up as bands of nonsense rather than as slight noise.
    /// </summary>
    private void UploadOrbit(ReferenceOrbit? orbit)
    {
        if (orbit is null || ReferenceEquals(orbit, _uploadedOrbit)) return;
        _uploadedOrbit = orbit;
        _orbitCount = orbit.Count;

        var zr = orbit.Zr;
        var zi = orbit.Zi;
        var packed = new uint[Math.Max(4, _orbitCount * 4)];
        for (int i = 0; i < _orbitCount; i++)
        {
            ulong r = BitConverter.DoubleToUInt64Bits(zr[i]);
            ulong m = BitConverter.DoubleToUInt64Bits(zi[i]);
            packed[i * 4 + 0] = (uint)r;
            packed[i * 4 + 1] = (uint)(r >> 32);
            packed[i * 4 + 2] = (uint)m;
            packed[i * 4 + 3] = (uint)(m >> 32);
        }

        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _orbitBuffer);
        fixed (uint* p = packed)
            _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)packed.Length * sizeof(uint), p,
                BufferUsageARB.StaticDraw);
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
    }

    private uint Link(string vertex, string fragment)
    {
        uint vs = Compile(ShaderType.VertexShader, vertex);
        uint fs = Compile(ShaderType.FragmentShader, fragment);

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(program);
            _gl.DeleteProgram(program);
            throw new NotSupportedException($"shader link failed: {log}");
        }

        _gl.DetachShader(program, vs);
        _gl.DetachShader(program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new NotSupportedException($"{type} failed to compile: {log}");
        }
        return shader;
    }

    private void Uniform1d(int location, double value) => _gl.Uniform1(location, value);

    private void Uniform2d(int location, double x, double y) => _gl.Uniform2(location, x, y);

    public void Dispose()
    {
        if (_escapeProgram != 0) _gl.DeleteProgram(_escapeProgram);
        if (_colourProgram != 0) _gl.DeleteProgram(_colourProgram);
        if (_resolveProgram != 0) _gl.DeleteProgram(_resolveProgram);
        if (_fieldTex != 0) _gl.DeleteTexture(_fieldTex);
        if (_colourTex != 0) _gl.DeleteTexture(_colourTex);
        if (_colourFbo != 0) _gl.DeleteFramebuffer(_colourFbo);
        if (_paletteTex != 0) _gl.DeleteTexture(_paletteTex);
        if (_orbitTex != 0) _gl.DeleteTexture(_orbitTex);
        if (_orbitBuffer != 0) _gl.DeleteBuffer(_orbitBuffer);
        if (_fieldFbo != 0) _gl.DeleteFramebuffer(_fieldFbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        for (int i = 0; i < 2; i++)
        {
            if (_frameTex[i] != 0) _gl.DeleteTexture(_frameTex[i]);
            if (_frameFbo[i] != 0) _gl.DeleteFramebuffer(_frameFbo[i]);
        }
        for (int i = 0; i < QueryCount; i++)
            if (_queries[i] != 0) _gl.DeleteQuery(_queries[i]);

        _escapeProgram = _colourProgram = _resolveProgram = 0;
        _fieldTex = _colourTex = _paletteTex = _orbitTex = _orbitBuffer = 0;
        _fieldFbo = _colourFbo = _vao = 0;
    }

    /// <summary>One triangle covering the target, built without a vertex buffer.</summary>
    private const string VertexSource = """
        #version 400 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Escape-time pass. A transcription of <see cref="Mandelbrot.Escape"/> and
    /// <see cref="ReferenceOrbit"/>'s iteration, in the same order of operations, so the two
    /// backends do not disagree about where the set's edge is. The one deliberate omission is the
    /// bilinear-approximation table: skipping runs of iterations pays for itself on a CPU, but on a
    /// card the divergence between neighbouring pixels costs more than the skips save.
    /// </summary>
    private const string EscapeSource = """
        #version 400 core

        out float outEscape;

        uniform dvec2  uCenter;
        uniform double uPixel;
        uniform vec2   uHalf;
        uniform float  uHeight;
        uniform int    uMaxIter;
        uniform int    uOrbitCount;
        uniform int    uKind;
        uniform usamplerBuffer uOrbit;

        const double kBailout2 = 1e10lf;
        const double kBailout = 1e5lf;
        const double kJuliaCr = -0.7269lf;
        const double kJuliaCi = 0.1889lf;
        const double kPhoenixP1 = 0.56667lf;
        const double kPhoenixP2 = -0.5lf;
        const double kSettled = 1e-12lf;

        dvec2 orbitAt(int n)
        {
            uvec4 t = texelFetch(uOrbit, n);
            return dvec2(packDouble2x32(t.xy), packDouble2x32(t.zw));
        }

        float plain(double cr, double ci)
        {
            double dx = cr - 0.25lf;
            double q = dx * dx + ci * ci;
            if (q * (q + dx) <= 0.25lf * ci * ci) return -1.0;
            double px = cr + 1.0lf;
            if (px * px + ci * ci <= 0.0625lf) return -1.0;

            double zr = 0.0lf, zi = 0.0lf, zr2 = 0.0lf, zi2 = 0.0lf;
            double oldR = 0.0lf, oldI = 0.0lf;
            int since = 0, checkAt = 8;

            for (int n = 0; n < uMaxIter; n++)
            {
                zi = 2.0lf * zr * zi + ci;
                zr = zr2 - zi2 + cr;
                zr2 = zr * zr;
                zi2 = zi * zi;

                double mag2 = zr2 + zi2;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));

                if (abs(zr - oldR) < 1e-16lf && abs(zi - oldI) < 1e-16lf) return -1.0;

                if (++since > checkAt) { since = 0; checkAt <<= 1; oldR = zr; oldI = zi; }
            }

            return -1.0;
        }

        float perturbed(double dcr, double dci)
        {
            // A Julia's offset is where the pixel starts and is never added again; a Mandelbrot's is
            // a difference in the parameter, so it returns every iteration.
            bool julia = uKind == 1;
            double dzr = julia ? dcr : 0.0lf;
            double dzi = julia ? dci : 0.0lf;
            if (julia) { dcr = 0.0lf; dci = 0.0lf; }

            int n = 0, m = 0;
            dvec2 z = orbitAt(0);

            while (m < uMaxIter)
            {
                if (n + 1 >= uOrbitCount)
                {
                    dzr += z.x;
                    dzi += z.y;
                    n = 0;
                    z = orbitAt(0);
                }

                double nr = 2.0lf * (z.x * dzr - z.y * dzi) + (dzr * dzr - dzi * dzi) + dcr;
                double ni = 2.0lf * (z.x * dzi + z.y * dzr) + 2.0lf * dzr * dzi + dci;
                dzr = nr;
                dzi = ni;
                n++;
                m++;

                z = orbitAt(n);
                double fr = z.x + dzr;
                double fi = z.y + dzi;

                // Both tests below are on magnitudes, and squaring to get them costs four of the
                // eleven fp64 multiplies in this loop — the single largest saving available in
                // it. The larger component is within a factor of root two of the true magnitude,
                // and neither test needs better than that: the escape threshold is arbitrary (the
                // smooth count below is computed from the actual magnitude, and is insensitive to
                // where exactly a large bailout falls), and rebasing is exact wherever it happens,
                // so triggering it a fraction of an iteration early or late changes nothing.
                double mz = max(abs(fr), abs(fi));

                if (mz > kBailout)
                {
                    double mag2 = fr * fr + fi * fi;
                    return max(0.0, float(m) - log2(0.5 * log(float(mag2))));
                }

                // Same rebasing rule as the CPU path: once the orbit passes nearer the origin than
                // the delta, holding z directly beats holding it as an offset.
                if (mz < max(abs(dzr), abs(dzi)))
                {
                    dzr = fr;
                    dzi = fi;
                    n = 0;
                    z = orbitAt(0);
                }
            }

            return -1.0;
        }

        // The other formulas, each a transcription of its counterpart in Mandelbrot.cs. Separate
        // loops rather than one loop with a switch inside it: the choice is the same for every pixel
        // in the frame, so branching once outside costs nothing and branching per iteration would.
        float quadratic(double zr, double zi, double kr, double ki)
        {
            double oldR = 0.0lf, oldI = 0.0lf;
            int since = 0, checkAt = 8;

            for (int n = 0; n < uMaxIter; n++)
            {
                double next = zr * zr - zi * zi + kr;
                zi = 2.0lf * zr * zi + ki;
                zr = next;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));

                if (abs(zr - oldR) < 1e-16lf && abs(zi - oldI) < 1e-16lf) return -1.0;
                if (++since > checkAt) { since = 0; checkAt <<= 1; oldR = zr; oldI = zi; }
            }

            return -1.0;
        }

        float burningShip(double cr, double ci)
        {
            ci = -ci;   // hull down, masts up: the orientation it is always shown in
            double zr = 0.0lf, zi = 0.0lf;
            for (int n = 0; n < uMaxIter; n++)
            {
                double next = zr * zr - zi * zi + cr;
                zi = 2.0lf * abs(zr * zi) + ci;
                zr = next;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));
            }
            return -1.0;
        }

        float tricorn(double cr, double ci)
        {
            double zr = 0.0lf, zi = 0.0lf;
            for (int n = 0; n < uMaxIter; n++)
            {
                double next = zr * zr - zi * zi + cr;
                zi = -2.0lf * zr * zi + ci;
                zr = next;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));
            }
            return -1.0;
        }

        float multibrot(double cr, double ci)
        {
            double zr = 0.0lf, zi = 0.0lf;
            for (int n = 0; n < uMaxIter; n++)
            {
                double r2 = zr * zr, i2 = zi * zi;
                double next = zr * (r2 - 3.0lf * i2) + cr;
                zi = zi * (3.0lf * r2 - i2) + ci;
                zr = next;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log(0.5 * log(float(mag2))) / 1.0986123);
            }
            return -1.0;
        }

        float phoenix(double cr, double ci)
        {
            // Iterated from the pixel, with the axes swapped, as it is conventionally drawn.
            double zr = ci, zi = cr;
            double prevR = 0.0lf, prevI = 0.0lf;

            for (int n = 0; n < uMaxIter; n++)
            {
                double nr = zr * zr - zi * zi + kPhoenixP1 + kPhoenixP2 * prevR;
                double ni = 2.0lf * zr * zi + kPhoenixP2 * prevI;
                prevR = zr;
                prevI = zi;
                zr = nr;
                zi = ni;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));
            }
            return -1.0;
        }

        // Newton on z^3 - 1, and with a non-zero add, the Nova variant. Both converge instead of
        // escaping, so the count is of steps to settle and the root reached shifts the palette.
        float newton(double zr, double zi, double addR, double addI)
        {
            bool nova = addR != 0.0lf || addI != 0.0lf;
            if (nova) { zr = 1.0lf; zi = 0.0lf; }

            for (int n = 0; n < uMaxIter; n++)
            {
                double r2 = zr * zr, i2 = zi * zi;
                double sqR = r2 - i2, sqI = 2.0lf * zr * zi;
                double cuR = zr * sqR - zi * sqI, cuI = zr * sqI + zi * sqR;
                double numR = cuR - 1.0lf, numI = cuI;
                double denR = 3.0lf * sqR, denI = 3.0lf * sqI;

                double d = denR * denR + denI * denI;
                if (d < 1e-300lf) return -1.0;

                double qR = (numR * denR + numI * denI) / d;
                double qI = (numI * denR - numR * denI) / d;

                double nextR = zr - qR + addR;
                double nextI = zi - qI + addI;
                double stepR = nextR - zr, stepI = nextI - zi;
                zr = nextR;
                zi = nextI;

                if (stepR * stepR + stepI * stepI < kSettled)
                {
                    float angle = atan(float(zi), float(zr));
                    int root = int(floor(angle / 2.0943951 + 0.5));
                    return max(0.0, float(n) + 0.34 * float((root + 3) % 3));
                }

                if (zr * zr + zi * zi > 1e12lf) return -1.0;
            }
            return -1.0;
        }

        float magnet(double cr, double ci)
        {
            double zr = 0.0lf, zi = 0.0lf;
            for (int n = 0; n < uMaxIter; n++)
            {
                double numR = zr * zr - zi * zi + cr - 1.0lf;
                double numI = 2.0lf * zr * zi + ci;
                double denR = 2.0lf * zr + cr - 2.0lf;
                double denI = 2.0lf * zi + ci;

                double d = denR * denR + denI * denI;
                if (d < 1e-300lf) return -1.0;

                double qR = (numR * denR + numI * denI) / d;
                double qI = (numI * denR - numR * denI) / d;

                double nextR = qR * qR - qI * qI;
                double nextI = 2.0lf * qR * qI;
                double stepR = nextR - zr, stepI = nextI - zi;
                zr = nextR;
                zi = nextI;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > kBailout2)
                    return max(0.0, float(n) + 1.0 - log2(0.5 * log(float(mag2))));
                if (stepR * stepR + stepI * stepI < kSettled) return float(n);
            }
            return -1.0;
        }

        // Lyapunov exponent of the logistic map, its growth rate alternating between the two
        // coordinates on a fixed schedule. A rate of divergence rather than an escape time.
        float lyapunov(double a, double b)
        {
            int seq[5] = int[5](0, 0, 1, 0, 1);   // AABAB
            int budget = clamp(uMaxIter, 80, 3000);

            double x = 0.5lf;
            for (int n = 0; n < 24; n++)
            {
                double r = seq[n % 5] == 0 ? a : b;
                x = r * x * (1.0lf - x);
            }

            float sum = 0.0;
            for (int n = 0; n < budget; n++)
            {
                double r = seq[n % 5] == 0 ? a : b;
                x = r * x * (1.0lf - x);

                float slope = abs(float(r * (1.0lf - 2.0lf * x)));
                sum += slope < 1e-30 ? -69.0 : log(slope);
            }

            float exponent = sum / float(budget);
            if (exponent >= 0.0) return -1.0;
            return min(60.0, -exponent * 24.0);
        }

        // Orbit traps: what is recorded is the orbit's closest approach to the axes (Pickover's
        // stalks) or to a circle, rather than when it left.
        float trap(double cr, double ci, bool stalks)
        {
            double zr = stalks ? cr : 0.0lf;
            double zi = stalks ? ci : 0.0lf;
            double kr = stalks ? kJuliaCr : cr;
            double ki = stalks ? kJuliaCi : ci;

            float nearest = 1e30;
            int budget = min(uMaxIter, 400);

            for (int n = 0; n < budget; n++)
            {
                double next = zr * zr - zi * zi + kr;
                zi = 2.0lf * zr * zi + ki;
                zr = next;

                double mag2 = zr * zr + zi * zi;
                if (mag2 > 1e6lf) break;

                float distance = stalks
                    ? min(abs(float(zr)), abs(float(zi)))
                    : abs(sqrt(float(mag2)) - 0.5);
                nearest = min(nearest, distance);
            }

            if (nearest > 1e29) return -1.0;
            return min(40.0, -log(max(1e-12, nearest)) * 3.0);
        }

        float sierpinskiTriangle(double x, double y)
        {
            if (x < 0.0lf || x > 1.0lf || y < 0.0lf || y > 1.0lf) return -1.0;

            int budget = min(uMaxIter, 45);
            for (int n = 0; n < budget; n++)
            {
                if (x >= 0.5lf && y >= 0.5lf) return float(n);

                if (y >= 0.5lf) { y = 2.0lf * y - 1.0lf; x = 2.0lf * x; }
                else if (x >= 0.5lf) { x = 2.0lf * x - 1.0lf; y = 2.0lf * y; }
                else { x = 2.0lf * x; y = 2.0lf * y; }
            }
            return -1.0;
        }

        float sierpinskiCarpet(double x, double y)
        {
            if (x < 0.0lf || x > 1.0lf || y < 0.0lf || y > 1.0lf) return -1.0;

            int budget = min(uMaxIter, 28);
            for (int n = 0; n < budget; n++)
            {
                x *= 3.0lf;
                y *= 3.0lf;
                int dx = int(x), dy = int(y);
                if (dx == 1 && dy == 1) return float(n);
                x -= double(dx);
                y -= double(dy);
            }
            return -1.0;
        }

        void main()
        {
            // The target is written bottom-up, so the row order is flipped against the CPU kernel's
            // and the image is handed to Skia with a bottom-left origin.
            double px = double(gl_FragCoord.x - uHalf.x);
            double py = double(uHeight - gl_FragCoord.y - uHalf.y);
            double cr = uCenter.x + px * uPixel;
            double ci = uCenter.y - py * uPixel;

            // Perturbation exists only for the Mandelbrot, so an orbit being present settles it.
            if (uOrbitCount > 0) { outEscape = perturbed(cr, ci); return; }

            if (uKind == 1) outEscape = quadratic(cr, ci, kJuliaCr, kJuliaCi);
            else if (uKind == 2) outEscape = burningShip(cr, ci);
            else if (uKind == 3) outEscape = tricorn(cr, ci);
            else if (uKind == 4) outEscape = multibrot(cr, ci);
            else if (uKind == 5) outEscape = phoenix(cr, ci);
            else if (uKind == 6) outEscape = newton(cr, ci, 0.0lf, 0.0lf);
            else if (uKind == 7) outEscape = newton(cr, ci, cr, ci);
            else if (uKind == 8) outEscape = magnet(cr, ci);
            else if (uKind == 9) outEscape = lyapunov(cr, ci);
            else if (uKind == 10) outEscape = trap(cr, ci, true);
            else if (uKind == 11) outEscape = trap(cr, ci, false);
            else if (uKind == 12) outEscape = sierpinskiTriangle(cr, ci);
            else if (uKind == 13) outEscape = sierpinskiCarpet(cr, ci);
            else outEscape = plain(cr, ci);
        }
        """;

    /// <summary>
    /// The three-dimensional fractals, which are surfaces rather than fields: a ray per pixel is
    /// marched against a distance estimator until it lands, and shaded by the surface's own gradient.
    /// Nothing of the escape-time pipeline applies, so this pass writes colour directly.
    ///
    /// Distance estimators are the standard ones for each shape. All of them are the same trick: a
    /// cheap lower bound on how far the surface can be, which is what makes marching converge in tens
    /// of steps instead of thousands.
    /// </summary>
    private const string MarchSource = """
        #version 400 core

        out vec4 outColour;

        uniform vec2  uHalf;        // half the target, in pixels
        uniform float uHeight;
        uniform int   uKind;
        uniform int   uSteps;
        uniform float uYaw;
        uniform float uPitch;
        uniform float uDistance;
        uniform sampler2D uPalette;
        uniform float uShift;

        const float kFov = 0.55;

        float boxDist(vec3 p, vec3 b)
        {
            vec3 d = abs(p) - b;
            return length(max(d, 0.0)) + min(max(d.x, max(d.y, d.z)), 0.0);
        }

        float deBulb(vec3 p)
        {
            vec3 z = p;
            float dr = 1.0, r = 0.0;
            for (int i = 0; i < 9; i++)
            {
                r = length(z);
                if (r > 2.0) break;

                float theta = acos(clamp(z.z / r, -1.0, 1.0));
                float phi = atan(z.y, z.x);
                dr = pow(r, 7.0) * 8.0 * dr + 1.0;

                float zr = pow(r, 8.0);
                theta *= 8.0;
                phi *= 8.0;
                z = zr * vec3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta)) + p;
            }
            return 0.5 * log(max(r, 1e-6)) * r / dr;
        }

        float deBox(vec3 p)
        {
            const float scale = 2.0;
            vec3 z = p;
            float dr = 1.0;
            for (int i = 0; i < 12; i++)
            {
                z = clamp(z, -1.0, 1.0) * 2.0 - z;          // fold the box

                float r2 = dot(z, z);
                if (r2 < 0.25) { z *= 4.0; dr *= 4.0; }      // and the sphere
                else if (r2 < 1.0) { z /= r2; dr /= r2; }

                z = z * scale + p;
                dr = dr * abs(scale) + 1.0;
            }
            return length(z) / abs(dr);
        }

        float deMenger(vec3 p)
        {
            float d = boxDist(p, vec3(1.0));
            float s = 1.0;
            for (int i = 0; i < 6; i++)
            {
                vec3 a = mod(p * s, 2.0) - 1.0;
                s *= 3.0;
                vec3 r = abs(1.0 - 3.0 * abs(a));

                float da = max(r.x, r.y);
                float db = max(r.y, r.z);
                float dc = max(r.z, r.x);
                d = max(d, (min(da, min(db, dc)) - 1.0) / s);
            }
            return d;
        }

        float deTetra(vec3 z)
        {
            const float scale = 2.0;
            vec3 a1 = vec3(1, 1, 1), a2 = vec3(-1, -1, 1);
            vec3 a3 = vec3(1, -1, -1), a4 = vec3(-1, 1, -1);

            for (int i = 0; i < 12; i++)
            {
                vec3 c = a1;
                float dist = length(z - a1), d;
                d = length(z - a2); if (d < dist) { c = a2; dist = d; }
                d = length(z - a3); if (d < dist) { c = a3; dist = d; }
                d = length(z - a4); if (d < dist) { c = a4; dist = d; }
                z = scale * z - c * (scale - 1.0);
            }
            return length(z) * pow(scale, -12.0);
        }

        float deQuaternion(vec3 p)
        {
            // c fixed, z from the ray point: a Julia, so the derivative carries no +1 the way a
            // Mandelbrot's would.
            vec4 c = vec4(-1.0, 0.2, 0.0, 0.0);
            vec4 z = vec4(p, 0.0);
            float dz = 1.0, r2 = dot(z, z);

            for (int i = 0; i < 12; i++)
            {
                if (r2 > 8.0) break;

                dz = 2.0 * sqrt(r2) * dz;
                z = vec4(z.x * z.x - dot(z.yzw, z.yzw), 2.0 * z.x * z.yzw) + c;
                r2 = dot(z, z);
            }

            float r = sqrt(max(r2, 1e-8));
            return 0.5 * log(max(r, 1.0001)) * r / max(dz, 1e-6);
        }

        float deKleinian(vec3 p)
        {
            vec3 size = vec3(0.92436, 0.90948, 1.0);
            float factor = 1.0;
            for (int i = 0; i < 10; i++)
            {
                p = 2.0 * clamp(p, -size, size) - p;
                float k = max(0.70968 / dot(p, p), 1.0);
                p *= k;
                factor *= k;
            }
            float rxy = length(p.xy);
            return max(rxy - 0.92784, abs(rxy * p.z) / length(p)) / factor;
        }

        float distanceTo(vec3 p)
        {
            if (uKind == 20) return deBulb(p);
            if (uKind == 21) return deBox(p);
            if (uKind == 22) return deMenger(p);
            if (uKind == 23) return deTetra(p);
            if (uKind == 24) return deQuaternion(p);
            return deKleinian(p);
        }

        vec3 normalAt(vec3 p, float epsilon)
        {
            vec2 e = vec2(epsilon, 0.0);
            return normalize(vec3(
                distanceTo(p + e.xyy) - distanceTo(p - e.xyy),
                distanceTo(p + e.yxy) - distanceTo(p - e.yxy),
                distanceTo(p + e.yyx) - distanceTo(p - e.yyx)));
        }

        void main()
        {
            // Same bottom-up target as the other passes.
            vec2 uv = vec2(gl_FragCoord.x - uHalf.x, uHalf.y - (uHeight - gl_FragCoord.y)) / uHalf.y;

            // Orbit: the pan offsets became angles and the zoom became a distance, so the same
            // dragging and scrolling that moves a flat view moves this one around the object.
            float cy = cos(uYaw), sy = sin(uYaw);
            float cp = cos(uPitch), sp = sin(uPitch);
            vec3 forward = vec3(cp * sy, sp, cp * cy);
            vec3 origin = -forward * uDistance;
            vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), forward));
            vec3 up = cross(forward, right);
            vec3 dir = normalize(forward + kFov * (uv.x * right + uv.y * up));

            float travelled = 0.0;
            int steps = 0;
            bool hit = false;

            for (steps = 0; steps < uSteps; steps++)
            {
                vec3 at = origin + dir * travelled;
                float d = distanceTo(at);

                // Tolerance grows with distance, so the far side of the object does not cost more
                // steps than the near side for detail no larger than a pixel there.
                float tolerance = 0.0007 * travelled + 0.00002;
                if (d < tolerance) { hit = true; break; }

                travelled += d;
                if (travelled > 12.0) break;
            }

            if (!hit) { outColour = vec4(0.0, 0.0, 0.0, 1.0); return; }

            vec3 at = origin + dir * travelled;
            vec3 normal = normalAt(at, 0.0005 * travelled + 0.00002);

            // Lit by one lamp over the shoulder plus a little from the opposite side, and darkened by
            // how many steps it took to arrive, which stands in for how enclosed the point is.
            vec3 lamp = normalize(vec3(0.6, 0.7, -0.4));
            float diffuse = max(dot(normal, lamp), 0.0);
            float fill = 0.35 * max(dot(normal, -lamp), 0.0) + 0.15;
            float occlusion = 1.0 - float(steps) / float(uSteps);

            // Tinted from the same gradient as everything else, indexed by depth into the scene.
            float t = fract(travelled * 0.16 + uShift);
            vec3 tint = texelFetch(uPalette, ivec2(int(t * 4096.0) & 4095, 0), 0).rgb;

            vec3 colour = tint * (diffuse + fill) * (0.35 + 0.65 * occlusion);
            outColour = vec4(clamp(colour, 0.0, 1.0), 1.0);
        }
        """;

    /// <summary>
    /// Resolve pass — averages a supersampled frame down to the size it will be drawn at, with a
    /// box over exactly the samples belonging to each output pixel. Taps are placed inside that
    /// footprint and read bilinearly, so a whole-number ratio (the 2× preset) lands on sample
    /// centres and a fractional one (1.4×) still weights the straddling samples correctly.
    /// </summary>
    private const string ResolveSource = """
        #version 400 core

        out vec4 outColour;

        uniform sampler2D uSource;
        uniform vec2  uScale;       // source pixels per output pixel
        uniform ivec2 uSourceSize;
        uniform int   uTaps;        // per axis

        void main()
        {
            vec2 centre = gl_FragCoord.xy * uScale;
            float step = 1.0 / float(uTaps);

            vec3 sum = vec3(0.0);
            for (int j = 0; j < uTaps; j++)
            {
                for (int i = 0; i < uTaps; i++)
                {
                    vec2 offset = ((vec2(i, j) + 0.5) * step - 0.5) * uScale;
                    sum += texture(uSource, (centre + offset) / vec2(uSourceSize)).rgb;
                }
            }

            outColour = vec4(sum / float(uTaps * uTaps), 1.0);
        }
        """;

    /// <summary>
    /// Colouring pass — the band-limiting from <see cref="Mandelbrot"/>, which needs the escape
    /// times of the neighbours and so cannot be folded into the pass above.
    /// </summary>
    private const string ColourSource = """
        #version 400 core

        out vec4 outColour;

        uniform sampler2D uField;
        uniform sampler2D uPalette;
        uniform vec3  uMean;
        uniform float uShift;
        uniform ivec2 uSize;

        const float kBandDensity = 2.1;

        float delta(float a, float b, float centre)
        {
            if (a < 0.0) a = centre;
            if (b < 0.0) b = centre;
            return abs(a - b) * 0.5;
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            float f = texelFetch(uField, p, 0).r;
            if (f < 0.0) { outColour = vec4(0.0, 0.0, 0.0, 1.0); return; }

            int xl = max(p.x - 1, 0), xr = min(p.x + 1, uSize.x - 1);
            int yd = max(p.y - 1, 0), yu = min(p.y + 1, uSize.y - 1);

            float gx = delta(texelFetch(uField, ivec2(xr, p.y), 0).r,
                             texelFetch(uField, ivec2(xl, p.y), 0).r, f);
            float gy = delta(texelFetch(uField, ivec2(p.x, yu), 0).r,
                             texelFetch(uField, ivec2(p.x, yd), 0).r, f);
            float g = max(gx, gy);

            float v = log(1.0 + f) * kBandDensity + uShift;
            float dv = kBandDensity * g / (1.0 + f);
            float atten = 1.0 / (1.0 + 3.0 * dv * dv);

            v = fract(v);
            vec3 c = texelFetch(uPalette, ivec2(int(v * 4096.0) & 4095, 0), 0).rgb;

            outColour = vec4(uMean + (c - uMean) * atten, 1.0);
        }
        """;
}
