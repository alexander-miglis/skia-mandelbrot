using System;
using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;

namespace FractalZoom;

/// <summary>
/// Hosts a SkiaSharp GPU surface on a GLFW/OpenGL window and blits a continuously
/// deepening Mandelbrot zoom into it, forever.
/// </summary>
internal static class Program
{
    private const uint GlRgba8 = 0x8058;

    /// <summary>
    /// How far the re-projection may drift from 1:1 over a frame's life. This is the sharpness
    /// setting: it is what bounds how much a stale frame is ever magnified by.
    ///
    /// Deliberately independent of the zoom speed. Scaling it with speed (as an earlier version did)
    /// couples them so that the sustainable kernel latency works out to a constant whatever the
    /// speed, which makes a slower zoom buy nothing. Held fixed, it does what you would expect:
    /// halving the speed doubles how long a descent can keep going before the kernel outgrows it.
    /// </summary>
    private static double _driftBudget = 1.30;

    /// <summary>
    /// Floor on the zoom throttle. The rate is allowed to vary between this and the requested speed,
    /// which reads as steady; below it the descent would visibly grind to a crawl instead, so the
    /// location is handed over to a fresh descent rather than slowed down further.
    /// </summary>
    private const double MinThrottle = 0.6;

    /// <summary>Frames the rate must be unsustainable before giving up, so a spike cannot end a descent.</summary>
    private const int GiveUpFrames = 150;

    /// <summary>Extra seconds a snapshot run may wait for a cross-fade to complete.</summary>
    private const double FadeGrace = 4.0;

    /// <summary>
    /// Fraction of the estimated kernel latency to render ahead by. Deliberately under 1: a frame
    /// only covers the screen if its view is at least as wide as the view at the moment it lands,
    /// so under-shooting the lead is safe and over-shooting clips the edges.
    /// </summary>
    private const double LeadFactor = 0.8;

    /// <summary>
    /// Extra field of view rendered beyond the window (with matching extra pixels, so density is
    /// unchanged), absorbing the error in predicting where the camera will be when a frame lands.
    ///
    /// Scaled by the drift budget rather than fixed, because that is what the error scales with: the
    /// faster the zoom, the further the camera travels during a frame's life, and the more a jittery
    /// latency estimate can miss by. A fixed 6% left visible gaps of ~160px at the highest speed.
    /// </summary>
    private static double Overscan => 1.0 + 0.35 * (_driftBudget - 1.0);

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static GRGlInterface? _glInterface;
    private static GRContext? _grContext;
    private static GRBackendRenderTarget? _renderTarget;
    private static SKSurface? _surface;

    private static SKBitmap? _bitmap;
    private static FractalRenderer _renderer = null!;
    private static float _lastUpscale = 1f;

    private static ZoomDirector _director = null!;
    private static Mandelbrot.Palette _palette = null!;
    private static double _paletteShift;

    private static SKFont _font = null!;
    private static SKPaint _textPaint = null!;
    private static SKPaint _shadowPaint = null!;
    private static SKPaint _fadePaint = null!;
    // Bilinear is fine at 1:1; a Mitchell cubic keeps filament edges from turning to mush when the
    // kernel buffer is smaller than the framebuffer; and mipmaps are needed when it is larger, since
    // plain bilinear only reads a 2x2 neighbourhood and would alias away most of the extra samples.
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.Mitchell);
    private static readonly SKSamplingOptions Downsample = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Ceiling on kernel buffer pixels. Supersampling squares with the factor, so on a large display
    /// an uncapped 2x would ask for hundreds of millions of pixels and gigabytes of buffers.
    /// </summary>
    private const long MaxKernelPixels = 24_000_000;

    private static double _quality = 1.0;
    private static double _computeMs = 12.0;
    private static int _unsustainable;
    private static int _judgedGeneration = -1;
    private static SettingsScreen? _menu;
    private static int _paletteChoice = int.MinValue;
    private static double _fps = 60.0;

    private static bool _paused;
    private static bool _hud = true;
    private static long _frames;

    private static readonly Stopwatch Clock = new();

    private static double _duration = double.PositiveInfinity;
    private static int _seed = Environment.TickCount;
    private static double _startSpeed = 0.25;
    private static int _winW = 1280, _winH = 800;
    private static string? _snapshotPath;
    private static bool _showMenu = true;
    private static int _paletteOverride = -1;

    private static void Main(string[] args)
    {
        if (!ParseArgs(args)) return;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_winW, _winH);
        options.Title = "Fractal Zoom — SkiaSharp";
        options.VSync = true;
        options.PreferredDepthBufferBits = 0;
        options.PreferredStencilBufferBits = 8;
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.ForwardCompatible,
            new APIVersion(3, 3));

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += _ => RecreateSurface();
        _window.Closing += OnClosing;

        _window.Run();
        _window.Dispose();
    }

    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a)
            {
                case "--duration" when double.TryParse(Next(), out double d):
                    _duration = d;
                    break;
                case "--speed" when double.TryParse(Next(), out double s):
                    _startSpeed = Math.Clamp(s, 0.05, 2.5);
                    break;
                case "--seed" when int.TryParse(Next(), out int n):
                    _seed = n;
                    break;
                case "--quality" when double.TryParse(Next(), out double q):
                    _quality = Math.Clamp(q, 0.2, 2.0);
                    break;
                case "--no-menu":
                    _showMenu = false;
                    break;
                case "--no-hud":
                    _hud = false;
                    break;
                case "--palette" when int.TryParse(Next(), out int p):
                    _paletteOverride = p;
                    break;
                case "--snapshot" when Next() is { Length: > 0 } path:
                    _snapshotPath = path;
                    break;
                case "--size" when Next() is { } size && size.Split('x') is [var w, var h]
                                   && int.TryParse(w, out int wi) && int.TryParse(h, out int hi):
                    _winW = Math.Max(320, wi);
                    _winH = Math.Max(240, hi);
                    break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Fractal Zoom — an endless Mandelbrot descent rendered with SkiaSharp.

                          --size WxH        window size (default 1280x800)
                          --speed N         zoom e-folds per second (default 0.25). The rate is
                                            held steady, so this also sets how deep a descent gets
                                            before the kernel outgrows it — slower goes deeper.
                          --seed N          RNG seed for the route it takes
                          --quality N       kernel resolution vs the window, 0.2-2.0 (default 1).
                                            Above 1 supersamples for extra crispness; below 1 is
                                            softer but each descent reaches far deeper.
                          --no-menu         skip the startup settings screen
                          --no-hud          hide the readout (for clean stills)
                          --palette N       fix the gradient: 0 Electric, 1 Ember, 2 Aurora,
                                            3 Abyss, 4 Copper (default: a new one per descent)
                          --duration N      exit after N seconds (default: run forever)
                          --snapshot FILE   write the last frame to FILE as a PNG on exit

                        Keys: esc/tab settings  space pause  R new descent  up/down speed
                              H readout  Q quit
                        """);
                    return false;
                default:
                    Console.Error.WriteLine($"Unknown or malformed option: {a}. Try --help.");
                    return false;
            }
        }
        return true;
    }

    private static void OnLoad()
    {
        _gl = GL.GetApi(_window);

        _glInterface = GRGlInterface.Create(name =>
            _window.GLContext!.TryGetProcAddress(name, out nint addr) ? addr : IntPtr.Zero);
        _grContext = GRContext.CreateGl(_glInterface)
            ?? throw new InvalidOperationException("Could not create a Skia GPU context.");

        _director = new ZoomDirector(_seed) { Speed = _startSpeed };
        _renderer = new FractalRenderer();

        var typeface = FindMonospace();
        if (_showMenu)
        {
            _menu = new SettingsScreen(typeface);
            _menu.Preselect(_quality, _startSpeed, _driftBudget);
        }
        _font = new SKFont(typeface, 13f);
        _textPaint = new SKPaint { Color = new SKColor(0xEA, 0xF2, 0xFF), IsAntialias = true };
        _shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };
        _fadePaint = new SKPaint { Color = SKColors.Black };

        var input = _window.CreateInput();
        foreach (var kb in input.Keyboards)
            kb.KeyDown += OnKeyDown;

        RecreateSurface();
        Clock.Restart();
    }

    /// <summary>
    /// First monospace family that actually resolves. FromFamilyName substitutes a default rather
    /// than returning null for a missing family, so the name has to be checked on the result.
    /// </summary>
    private static SKTypeface FindMonospace()
    {
        string[] candidates =
        [
            "Menlo", "SF Mono",                          // macOS
            "Consolas", "Cascadia Mono", "Courier New",   // Windows
            "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono", // Linux
            "monospace",
        ];

        foreach (string name in candidates)
        {
            var typeface = SKTypeface.FromFamilyName(name);
            if (typeface is not null &&
                typeface.FamilyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return typeface;
        }

        return SKTypeface.Default;
    }

    private static void OnKeyDown(IKeyboard _, Key key, int __)
    {
        if (_menu is { Open: true })
        {
            switch (_menu.HandleKey(key))
            {
                case MenuAction.Quit: _window.Close(); break;
                case MenuAction.NewDescent: _director.NewDescent(); break;
            }
            return;
        }

        switch (key)
        {
            case Key.Escape: OpenMenu(); break;
            case Key.Q: _window.Close(); break;
            case Key.Space: _paused = !_paused; break;
            case Key.R: _director.NewDescent(); break;
            case Key.H: _hud = !_hud; break;
            case Key.Tab: OpenMenu(); break;
            case Key.Up or Key.Equal: _director.Speed = Math.Min(2.5, _director.Speed * 1.25); break;
            case Key.Down or Key.Minus: _director.Speed = Math.Max(0.05, _director.Speed / 1.25); break;
        }
    }

    /// <summary>
    /// Brings the menu up mid-run, creating it if the startup screen was skipped, so escape always
    /// has somewhere to go.
    /// </summary>
    private static void OpenMenu()
    {
        if (_menu is null)
        {
            _menu = new SettingsScreen(FindMonospace());
            _menu.Preselect(_quality, _director.Speed, _driftBudget);
            _menu.Close(); // not a first run: the Resume row should not read "Start"
        }

        _menu.Show(_hud);
    }

    private static void RecreateSurface()
    {
        var fb = _window.FramebufferSize;
        if (fb.X <= 0 || fb.Y <= 0) return;

        _surface?.Dispose();
        _renderTarget?.Dispose();

        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);

        var fbInfo = new GRGlFramebufferInfo(0, GlRgba8);
        _renderTarget = new GRBackendRenderTarget(fb.X, fb.Y, 0, 8, fbInfo);
        _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Could not create the Skia render target surface.");
    }

    private static void OnRender(double delta)
    {
        if (_surface is null || _grContext is null) return;

        var fb = _window.FramebufferSize;
        if (fb.X <= 0 || fb.Y <= 0) return;

        double dt = Math.Clamp(delta, 0.0, 0.05);
        _fps += ((delta > 0 ? 1.0 / delta : 0) - _fps) * 0.08;

        double aspect = (double)fb.X / fb.Y;
        bool menuOpen = _menu is { Open: true };

        if (menuOpen)
        {
            _quality = _menu!.Quality;
            _driftBudget = _menu.Drift;
            _director.Speed = _menu.Speed;
            _hud = _menu.ShowHud;

            // Let time pass so the descent's fade-in completes and the preview is actually visible,
            // but with the zoom stopped: a zero throttle freezes both the scale and the pan.
            _director.Throttle = 0.0;
            _director.Advance(dt, aspect);
        }
        else if (!_paused)
        {
            _director.Advance(dt, aspect);
            _paletteShift += dt * 0.015; // slow shimmer through the gradient
        }

        var gradients = Mandelbrot.Gradient.All;
        int chosen = _menu?.Palette ?? _paletteOverride;
        int wanted = chosen >= 0 ? chosen % gradients.Length : _director.Cycle % gradients.Length;
        if (wanted != _paletteChoice)
        {
            _paletteChoice = wanted;
            _palette = Mandelbrot.Build(gradients[wanted]);
        }

        int iterations = _director.MaxIterations;
        var now = new FractalRenderer.View(
            _director.OffsetX, _director.OffsetY, _director.Scale,
            iterations, _paletteShift, _director.Generation, _director.Reference);

        // Ask the worker for the current view at the current quality, then draw whatever it
        // last finished. The two are deliberately out of step; the transform below hides it.
        // Submit the camera as it will be once the kernel finishes, not as it is now, and with a
        // slightly wider field so the frame still covers the window as the view moves on.
        double lead = Math.Clamp(_computeMs / 1000.0 * LeadFactor, 0.0, 4.0);
        var (px, py, ps) = _director.Predict(lead);
        var (kernelW, kernelH) = KernelSize(fb.X, fb.Y);
        _renderer.Submit(
            now with { CenterX = px, CenterY = py, Scale = ps * Overscan },
            _palette, kernelW, kernelH);

        _computeMs += (_renderer.KernelMs - _computeMs) * 0.15;
        if (!menuOpen) AdaptZoom();

        _grContext.ResetContext();
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Black);

        // Must outlive the flush below. SKImage.FromPixels neither copies the pixels nor takes
        // ownership of them, so Skia may not have read them yet when the draw call returns —
        // releasing the image before flushing lets it read memory that the kernel buffer has since
        // freed or reallocated, which shows up as an intermittent hard crash with no managed stack.
        SKImage? image = null;

        if (_renderer.TryTakeLatest(ref _bitmap, out var frame, out int fw, out int fh)
            && frame.Generation == now.Generation)
        {
            var (k, tx, ty) = FractalRenderer.Reproject(frame, fw, fh, now, fb.X, fb.Y);

            // Wrapping without copying, with a fresh identity each frame so Skia re-uploads it
            // instead of reusing last frame's cached texture.
            image = SKImage.FromPixels(_bitmap!.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
            canvas.Save();
            canvas.Translate(tx, ty);
            canvas.Scale(k, k);
            canvas.DrawImage(image, 0, 0, k > 1.05f ? Cubic : k < 0.98f ? Downsample : Linear);
            canvas.Restore();
            _lastUpscale = k;
        }

        if (_director.Fade < 1.0)
        {
            _fadePaint.Color = SKColors.Black.WithAlpha((byte)(255 * (1.0 - Math.Clamp(_director.Fade, 0, 1))));
            canvas.DrawRect(SKRect.Create(0, 0, fb.X, fb.Y), _fadePaint);
        }

        if (_hud) DrawHud(canvas, fb.X, fb.Y);
        if (menuOpen) _menu!.Draw(canvas, fb.X, fb.Y, (float)(fb.Y / (double)Math.Max(1, _window.Size.Y)));

        _surface.Flush();
        _grContext.Flush();
        image?.Dispose();

        _frames++;

        // A timed run can land in the middle of a cross-fade, where the frame is largely black. For a
        // snapshot that is useless, so wait for the fade to finish — but only briefly, so a run can
        // never hang waiting for a descent that is still fading in.
        bool timeUp = Clock.Elapsed.TotalSeconds >= _duration;
        bool presentable = _director.Fade >= 0.999 || _snapshotPath is null;
        if (timeUp && (presentable || Clock.Elapsed.TotalSeconds >= _duration + FadeGrace))
        {
            if (_snapshotPath is not null) SaveSnapshot(_snapshotPath);
            Console.WriteLine($"Rendered {_frames} frames in {Clock.Elapsed.TotalSeconds:0.0}s " +
                              $"({_frames / Clock.Elapsed.TotalSeconds:0.0} fps), " +
                              $"reached {_director.Magnification:0.00e+00}x on cycle {_director.Cycle + 1}.");
            _window.Close();
        }
    }

    private static void SaveSnapshot(string path)
    {
        using var image = _surface!.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = System.IO.File.OpenWrite(path);
        data.SaveTo(stream);
        Console.WriteLine($"Wrote {path}");
    }

    /// <summary>Multiples of 16 so gradual quality changes don't reallocate on every frame.</summary>
    private static int Quantise(double v) => Math.Max(64, ((int)v + 15) & ~15);

    /// <summary>
    /// Kernel buffer dimensions: the window scaled by the detail setting and the overscan, then held
    /// under the pixel ceiling. Above 1.0 the detail setting is supersampling — the buffer is larger
    /// than the window and Skia downsamples it, which resolves structure finer than a pixel instead
    /// of letting the band-limited colouring average it away.
    /// </summary>
    private static (int Width, int Height) KernelSize(int fbW, int fbH)
    {
        double scale = _quality * Overscan;
        double w = fbW * scale, h = fbH * scale;

        double excess = w * h / MaxKernelPixels;
        if (excess > 1.0)
        {
            double shrink = Math.Sqrt(excess);
            w /= shrink;
            h /= shrink;
        }

        return (Quantise(w), Quantise(h));
    }

    /// <summary>
    /// The only quality controller left. Perturbation removes the precision wall but not the cost
    /// wall — iteration counts keep climbing with depth — so something has to give. Resolution and
    /// iteration count are held fixed and the zoom speed absorbs the cost instead: a view that
    /// takes a second to compute is explored slowly, at full sharpness, rather than quickly and
    /// blurrily. Sharpness is resolution / drift, and this pins both.
    ///
    /// Solved directly rather than as a feedback loop: a control loop running at vsync against a
    /// value that resets on every kernel pass oscillates badly and collapses the speed.
    /// </summary>
    private static void AdaptZoom()
    {
        double window = Math.Max(0.001, _computeMs / 1000.0);
        double sustainable = Math.Log(_driftBudget) / (Math.Max(1e-9, _director.Speed) * window);
        _director.Throttle = Math.Clamp(sustainable, MinThrottle, 1.0);

        // Below the floor the frame would be magnified past the drift budget however much the zoom
        // slowed, so there is nothing left to trade: end the descent instead of crawling.
        //
        // Only judged in the steady state. A new descent starts while the latency estimate still
        // holds the previous one's deep, slow value, and counting that would end the fresh descent
        // seconds after it began.
        if (_director.Generation != _judgedGeneration)
        {
            _judgedGeneration = _director.Generation;
            _unsustainable = 0;
        }
        else if (_director.Fade >= 1.0)
        {
            _unsustainable = sustainable < MinThrottle ? _unsustainable + 1 : 0;
            if (_unsustainable > GiveUpFrames)
            {
                _unsustainable = 0;
                _director.EndDescent();
            }
        }
    }

    private static void DrawHud(SKCanvas canvas, int fbW, int fbH)
    {
        double dpi = fbH / (double)Math.Max(1, _window.Size.Y);
        float size = (float)(13 * dpi);
        if (Math.Abs(_font.Size - size) > 0.5f) _font.Size = size;

        var (kw, kh) = _renderer.LastSize;
        int iterations = _director.MaxIterations;
        string mode = _director.Reference is { } r ? $"perturbed, {r.FracBits}-bit anchor" : "fp64";
        string[] lines =
        [
            $"zoom   {_director.Magnification:0.000e+00}x   (cycle {_director.Cycle + 1})   {mode}",
            $"iter   {iterations}    fps {_fps,4:0}    kernel {_computeMs,5:0.0} ms",
            $"render {kw}x{kh}   resample {_lastUpscale:0.00}x   speed {_director.Speed * _director.Throttle:0.000}/s{(_paused ? "   [PAUSED]" : "")}",
            "space pause   R new descent   up/down speed   H hud   esc quit",
        ];

        float x = 14 * (float)dpi;
        float y = fbH - (float)(10 * dpi) - (lines.Length - 1) * size * 1.45f;
        foreach (var line in lines)
        {
            canvas.DrawText(line, x + 1, y + 1, _font, _shadowPaint);
            canvas.DrawText(line, x, y, _font, _textPaint);
            y += size * 1.45f;
        }
    }

    private static void OnClosing()
    {
        _menu?.Dispose();
        _renderer?.Dispose();
        _bitmap?.Dispose();
        _surface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
        _glInterface?.Dispose();
        _font?.Dispose();
        _textPaint?.Dispose();
        _shadowPaint?.Dispose();
        _fadePaint?.Dispose();
    }
}
