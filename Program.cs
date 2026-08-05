using System;
using System.Diagnostics;
using System.Numerics;
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
    private const uint GlTexture2D = 0x0DE1;

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
    private static GpuKernel? _gpu;
    private static string? _gpuUnavailable;
    private static float _lastUpscale = 1f;

    /// <summary>
    /// Share of a display frame the GPU kernel may take. The rest goes on presenting, and the
    /// window is only smooth while the two together fit inside a vsync interval — so this is what
    /// keeps a deep view from freezing the animation, at the cost of a kernel frame taking several
    /// display frames to finish.
    ///
    /// Expressed as a fraction of the measured frame time rather than as a number of milliseconds
    /// because it has to hold at any refresh rate: a fixed 8 ms is two thirds of a 60 Hz frame and
    /// more than a whole 165 Hz one. As a fraction the duty cycle comes out the same either way.
    /// </summary>
    private const double GpuFrameShare = 0.7;

    /// <summary>Seconds between handing the idle backend a frame to see how long it takes.</summary>
    private const double ProbeSeconds = 3.0;

    /// <summary>
    /// How much faster the idle backend has to be before the kernel moves to it. The gap is what
    /// stops the two swapping back and forth over measurement noise.
    /// </summary>
    private const double SwitchMargin = 0.8;

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
    // Only CPU frames ever reach that last case — a card frame arrives already resolved.
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

    /// <summary>
    /// Magnification to stop the camera at, or 0 to keep descending. Freezing makes the kernel
    /// render one identical view over and over, which is the only way to compare two kernels or two
    /// machines honestly: the cost of a frame depends on where in the set it is, and a descent left
    /// running takes a different route every time.
    /// </summary>
    private static double _freezeAt;
    private static double _frozenGpuMs = -1;
    private static long _frozenFrames;

    /// <summary>Camera is driven by the mouse instead of by the director.</summary>
    private static bool _explore;

    private static Fractal _startFractal = Fractal.Mandelbrot;

    /// <summary>Matches a name from the command line against the list, on any unique prefix.</summary>
    private static Fractal? FindFractal(string name)
    {
        for (int i = 0; i < FractalKind.All.Length; i++)
        {
            if (FractalKind.All[i].Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return (Fractal)i;
        }
        return null;
    }

    private static StillRenderer _stills = null!;

    /// <summary>
    /// Longest edge of a saved still in pixels, or 0 to match the window. The other edge follows the
    /// window's aspect ratio, so a still is the composition on screen at a higher resolution rather
    /// than a differently framed picture of the same place.
    /// </summary>
    private static int _stillLongEdge = 3840;

    private static SKEncodedImageFormat _stillFormat = SKEncodedImageFormat.Png;

    /// <summary>How long the outcome of a still stays in the readout after it finishes.</summary>
    private const double StillNoticeSeconds = 12.0;
    private static double _stillNoticeUntil;

    /// <summary>
    /// Shortest gap between two stills. Held-down keys arrive as a stream of presses that are
    /// indistinguishable from real ones here, and without this a leant-on P writes a picture every
    /// time the previous one finishes — three files came out of one keystroke before this existed.
    /// </summary>
    private const double StillDebounceSeconds = 1.0;
    private static double _lastStillAt = double.NegativeInfinity;

    private static int _stillSamples = 3;
    private static double _stillIterations = 2.0;


    /// <summary>
    /// Zoom the wheel has asked for and the camera has not yet performed, as a factor. Applied over
    /// several frames rather than in one jump: the kernel is behind the display by design, and a
    /// camera that teleports leaves the re-projection stretching a frame rendered somewhere else.
    /// Easing it keeps every intermediate frame close to 1:1.
    /// </summary>
    private static double _pendingZoom = 1.0;

    private static Vector2 _cursor;
    private static bool _dragging;

    /// <summary>Zoom per wheel notch, and how quickly the camera catches up with the wheel.</summary>
    private const double ZoomPerNotch = 1.4;
    private const double ZoomEase = 9.0;
    private static Backend _backend = Backend.Auto;
    private static bool _useGpu = true;
    private static bool _loaded;

    private static double _nextProbe = ProbeSeconds;
    private static bool _probing;
    private static long _probeMark;
    private static double _probeMs;

    /// <summary>Which kernel the frames come from. Auto measures both and follows the faster one.</summary>
    private enum Backend { Auto, Gpu, Cpu }

    private static void Main(string[] args)
    {
        if (!ParseArgs(args)) return;

        // The kernel shaders iterate in double precision, which needs a 4.0 context; everything
        // else here is happy on 3.3. Ask for 4.0 first and drop back rather than refuse to start,
        // so a card too old for the GPU kernel still runs the CPU one exactly as before.
        if (!TryRun(new APIVersion(4, 0)))
        {
            _gpuUnavailable = "this driver would not give us an OpenGL 4.0 context";
            _useGpu = false;
            if (!TryRun(new APIVersion(3, 3)))
                throw new InvalidOperationException("Could not create an OpenGL window.");
        }
    }

    /// <summary>
    /// Opens the window at the given GL version and runs until it closes. False means the context
    /// could not be created at all — a failure any later than that is a real one and is rethrown.
    /// </summary>
    private static bool TryRun(APIVersion version)
    {
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
            version);

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += _ => RecreateSurface();
        _window.Closing += OnClosing;

        try
        {
            _window.Run();
        }
        catch (Exception) when (!_loaded)
        {
            TryDisposeWindow();
            return false;
        }

        _window.Dispose();
        return true;
    }

    private static void TryDisposeWindow()
    {
        try { _window.Dispose(); }
        catch (Exception) { /* the window never came up; nothing to clean up but the handle */ }
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
                case "--explore":
                    _explore = true;
                    break;
                case "--fractal" when Next() is { } which && FindFractal(which) is { } chosen:
                    _startFractal = chosen;
                    break;
                case "--still-size" when int.TryParse(Next(), out int sh):
                    _stillLongEdge = Math.Clamp(sh, 0, 16384);
                    break;
                case "--still-format" when Next() is { } fmt && fmt is "png" or "jpeg" or "jpg":
                    _stillFormat = fmt == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                    break;
                case "--freeze" when double.TryParse(Next(), out double f):
                    _freezeAt = f;
                    break;
                case "--renderer" when Next() is { } which && which is "auto" or "gpu" or "cpu":
                    _backend = which switch
                    {
                        "gpu" => Backend.Gpu,
                        "cpu" => Backend.Cpu,
                        _ => Backend.Auto,
                    };
                    _useGpu = _backend != Backend.Cpu;
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
                          --fractal NAME    mandelbrot (default), julia, burning, tricorn, multibrot
                                            and twenty more. F opens the list. Only the Mandelbrot has a
                                            perturbed form, so the others stop around 1e13x.
                          --explore         start on the whole set and steer by mouse: scroll to
                                            zoom at the pointer, drag to pan. E switches modes.
                          --still-size N    longest edge of a saved still, in pixels (default 3840,
                                            0 matches the window). The other edge follows the
                                            window's shape. P opens the dialog for it.
                          --still-format F  png (default) or jpeg
                          --renderer WHICH  where the kernel runs: auto (default) times both and
                                            follows the faster one — the card wins by several times
                                            until the view is deep enough that the CPU's iteration
                                            skipping wins instead. gpu or cpu pins it. G cycles.
                          --no-menu         skip the startup settings screen
                          --no-hud          hide the readout (for clean stills)
                          --palette N       fix the gradient: 0 Electric, 1 Ember, 2 Aurora,
                                            3 Abyss, 4 Copper (default: a new one per descent)
                          --freeze N        stop the camera once it reaches N magnification and
                                            keep re-rendering that one view, so kernel timings can
                                            be compared without the route varying between runs
                          --duration N      exit after N seconds (default: run forever)
                          --snapshot FILE   write the last frame to FILE as a PNG on exit

                        Keys: esc/tab settings  space pause  R new descent / reset view
                              E explore  F fractals  P save a still  G gpu/cpu  H readout
                              up/down speed  Q quit

                        A still is a fresh render of the current view at the chosen resolution,
                        supersampled, written to your pictures folder — not a capture of the window.
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
        _stills = new StillRenderer();
        SetUpGpu();

        var typeface = FindMonospace();
        if (_showMenu)
        {
            _menu = new SettingsScreen(typeface, _gpu is not null);
            _menu.Preselect(_quality, _startSpeed, _driftBudget, BackendCode, _explore,
                _stillLongEdge, _stillFormat == SKEncodedImageFormat.Jpeg);
        }
        _font = new SKFont(typeface, 13f);
        _textPaint = new SKPaint { Color = new SKColor(0xEA, 0xF2, 0xFF), IsAntialias = true };
        _shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };
        _fadePaint = new SKPaint { Color = SKColors.Black };

        var input = _window.CreateInput();
        foreach (var kb in input.Keyboards)
            kb.KeyDown += OnKeyDown;

        foreach (var mouse in input.Mice)
        {
            _cursor = mouse.Position;
            mouse.Scroll += (_, wheel) => OnScroll(wheel.Y);
            mouse.MouseMove += (_, position) => OnMouseMove(position);
            mouse.MouseDown += (_, button) => OnMouseButton(button, true);
            mouse.MouseUp += (_, button) => OnMouseButton(button, false);
        }

        ChooseFractal(_startFractal);
        _director.Interactive = _explore;
        if (_explore) _director.ResetToOverview();

        RecreateSurface();
        Clock.Restart();
        _loaded = true;
    }

    /// <summary>
    /// Builds the GPU kernel, or records why it could not be built. A card that cannot run it is
    /// not an error: the CPU kernel is the same renderer, only slower, so the run continues on it.
    /// </summary>
    private static void SetUpGpu()
    {
        if (_gpuUnavailable is not null) { Report(); return; }

        try
        {
            _gpu = new GpuKernel(_gl);
            Console.WriteLine($"Graphics card kernel ready on {_gpu.Renderer}.");
        }
        catch (Exception ex)
        {
            _gpu = null;
            _gpuUnavailable = ex.Message;
            Report();
        }

        void Report()
        {
            if (_useGpu) Console.Error.WriteLine($"Falling back to the CPU kernel: {_gpuUnavailable}");
            _useGpu = false;
        }
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
            var handled = _menu.HandleKey(key);

            // P and F reach their own pages from inside the panel too, not only from the view.
            if (handled is MenuAction.None && key == Key.P) OpenStillMenu();
            else if (handled is MenuAction.None && key == Key.F) OpenFractalMenu();
            else RunMenuAction(handled);
            return;
        }

        switch (key)
        {
            case Key.Escape: OpenMenu(); break;
            case Key.Q: _window.Close(); break;
            case Key.Space: _paused = !_paused; break;
            case Key.E: SetExplore(!_explore); break;
            case Key.F: OpenFractalMenu(); break;
            case Key.P: OpenStillMenu(); break;
            case Key.R:
                if (_explore) _director.ResetToOverview();
                else _director.NewDescent();
                break;
            case Key.H: _hud = !_hud; break;
            case Key.G:
                SetBackend(_backend switch
                {
                    Backend.Auto => Backend.Gpu,
                    Backend.Gpu => Backend.Cpu,
                    _ => Backend.Auto,
                });
                break;
            case Key.Tab: OpenMenu(); break;
            case Key.Up or Key.Equal: _director.Speed = Math.Min(2.5, _director.Speed * 1.25); break;
            case Key.Down or Key.Minus: _director.Speed = Math.Max(0.05, _director.Speed / 1.25); break;
        }
    }

    /// <summary>Opens the list of every fractal, which is what F does.</summary>
    private static void OpenFractalMenu()
    {
        EnsureMenu();
        _menu!.ShowFractals(_director.Kind);
    }

    /// <summary>
    /// Switches formula, and hands over the camera if the new one has no automatic descent to run:
    /// the director's steering is built on escape times, which the drawn and ray-marched fractals do
    /// not have, so those are explored rather than watched.
    /// </summary>
    private static void ChooseFractal(Fractal kind)
    {
        if (FractalKind.Of(kind).Style != RenderStyle.Field && !_explore) SetExplore(true);
        _director.SetKind(kind, _explore);
    }

    /// <summary>Carries out whatever the panel decided, whether a key or a click asked for it.</summary>
    private static void RunMenuAction(MenuAction action)
    {
        switch (action)
        {
            case MenuAction.Quit:
                _window.Close();
                break;
            case MenuAction.NewDescent:
                if (_explore) _director.ResetToOverview();
                else _director.NewDescent();
                break;
            case MenuAction.RenderStill:
                SaveStill();
                break;
            case MenuAction.PickFractal:
                ChooseFractal(_menu!.Fractal);
                break;
        }
    }

    /// <summary>
    /// Dimensions a still would come out at: the chosen longest edge mapped onto the window's shape,
    /// so the picture is framed exactly as it is on screen.
    /// </summary>
    private static (int Width, int Height) StillSize()
    {
        var fb = _window.FramebufferSize;
        if (fb.X <= 0 || fb.Y <= 0) return (0, 0);
        if (_stillLongEdge <= 0) return (fb.X, fb.Y);

        double aspect = (double)fb.X / fb.Y;
        return aspect >= 1.0
            ? (_stillLongEdge, Math.Max(16, (int)Math.Round(_stillLongEdge / aspect)))
            : (Math.Max(16, (int)Math.Round(_stillLongEdge * aspect)), _stillLongEdge);
    }

    /// <summary>
    /// Kicks off a high-quality render of exactly what is on screen and leaves it to finish in the
    /// background — the view stays live and steerable while it does.
    /// </summary>
    private static void SaveStill()
    {
        if (_palette is null) return;

        double now = Clock.Elapsed.TotalSeconds;
        if (now - _lastStillAt < StillDebounceSeconds) return;

        if (_stills.Busy)
        {
            _stillNoticeUntil = now + 3.0;
            return;
        }

        var (width, height) = StillSize();
        if (width <= 0 || height <= 0) return;

        _lastStillAt = now;

        // The view as it is now, not the one the kernel is working toward: a still should be the
        // picture that was on screen when it was asked for.
        var view = new FractalRenderer.View(
            _director.OffsetX, _director.OffsetY, _director.Scale,
            _director.MaxIterations, _paletteShift, _director.Generation, _director.Reference,
            _director.Kind);

        string stem = $"fractal-zoom-{DateTime.Now:yyyyMMdd-HHmmss}-" +
                      $"{_director.Magnification:0.0e+00}x".Replace("+", "");

        if (_stills.Start(view, _palette, width, height, _stillSamples, _stillIterations,
                _stillFormat, stem))
            _stillNoticeUntil = double.MaxValue;
    }

    /// <summary>Fills in the two lines the still page cannot work out for itself.</summary>
    private static void DescribeStill()
    {
        if (_menu is null) return;

        var (width, height) = StillSize();
        long pixels = (long)width * height;
        double samples = pixels * (double)_stillSamples * _stillSamples;
        double iterations = _director.MaxIterations * _stillIterations;

        _menu.StillSummary =
            $"{width} x {height} — {pixels / 1e6:0.#} megapixels, {samples / 1e6:0.#} million samples, " +
            $"{iterations:0} iterations{Estimate(samples * iterations)}";
        _menu.StillDestination = $"Saves to {StillRenderer.Folder}";
        _menu.StillStatus = _stills.Busy
            ? $"Rendering… {_stills.Progress * 100:0}%   (the view stays live meanwhile)"
            : _stills.LastFile.Length > 0
                ? $"Last saved: {_stills.LastFile}"
                : "";

        // Only once a still has been rendered on this machine is there anything honest to say about
        // how long the next one will take.
        static string Estimate(double work)
        {
            double rate = _stills.SamplesPerSecond;
            if (rate <= 0) return "";

            double seconds = work / rate;
            return seconds < 90
                ? $"   about {Math.Max(1, Math.Round(seconds)):0} s"
                : $"   about {seconds / 60:0.#} min";
        }
    }

    /// <summary>
    /// Performs a slice of whatever zoom the wheel has queued, about wherever the pointer is now.
    /// Re-reading the pointer every frame rather than remembering where the wheel was turned is
    /// deliberate: the zoom then follows the pointer if it moves mid-gesture, which is what the
    /// gesture feels like it should do.
    /// </summary>
    private static void EaseZoom(double dt)
    {
        double remaining = Math.Log(_pendingZoom);
        if (Math.Abs(remaining) < 1e-4)
        {
            _pendingZoom = 1.0;
            return;
        }

        double step = Math.Exp(remaining * Math.Clamp(dt * ZoomEase, 0.0, 1.0));
        var (ax, ay) = CursorOffset();
        _director.ZoomAbout(step, ax, ay);
        _pendingZoom /= step;
    }

    /// <summary>
    /// Complex-plane units per window point at the current view. Window points rather than
    /// framebuffer pixels because that is what the mouse reports, and on a scaled display the two
    /// are not the same thing.
    /// </summary>
    private static double UnitsPerPoint()
    {
        var fb = _window.FramebufferSize;
        if (fb.Y <= 0) return 0;
        double perPixel = 2.0 * _director.Scale / fb.Y;
        return perPixel * fb.Y / Math.Max(1, _window.Size.Y);
    }

    /// <summary>Where the cursor is, as an offset from the view centre in complex-plane units.</summary>
    private static (double X, double Y) CursorOffset()
    {
        double units = UnitsPerPoint();
        double halfW = _window.Size.X * 0.5, halfH = _window.Size.Y * 0.5;
        return ((_cursor.X - halfW) * units, -(_cursor.Y - halfH) * units);
    }

    /// <summary>Framebuffer coordinates of the cursor, which is what the panel's layout is in.</summary>
    private static (float X, float Y) CursorInPanel()
    {
        var fb = _window.FramebufferSize;
        float sx = fb.X / (float)Math.Max(1, _window.Size.X);
        float sy = fb.Y / (float)Math.Max(1, _window.Size.Y);
        return (_cursor.X * sx, _cursor.Y * sy);
    }

    private static void OnScroll(float notches)
    {
        if (notches == 0) return;

        if (_menu is { Open: true })
        {
            _menu.HandleScroll(notches);
            return;
        }

        if (!_explore) return;

        // Accumulated rather than applied, so a fast flick of the wheel is one smooth zoom instead
        // of a burst of jumps. Bounded so a spin cannot queue up a descent to the floor.
        _pendingZoom = Math.Clamp(_pendingZoom * Math.Pow(ZoomPerNotch, notches), 1e-4, 1e4);
    }

    private static void OnMouseMove(Vector2 position)
    {
        var previous = _cursor;
        _cursor = position;

        if (_menu is { Open: true })
        {
            var (mx, my) = CursorInPanel();
            _menu.HandleMouseMove(mx, my);
            return;
        }

        if (!_explore || !_dragging) return;

        double units = UnitsPerPoint();
        var moved = position - previous;

        // Dragging carries the image with the pointer, so the view centre travels the other way.
        _director.PanBy(-moved.X * units, moved.Y * units);
    }

    private static void OnMouseButton(MouseButton button, bool down)
    {
        if (button != MouseButton.Left) return;

        if (_menu is { Open: true })
        {
            _dragging = false;
            if (!down) return;

            var (mx, my) = CursorInPanel();
            RunMenuAction(_menu.HandleClick(mx, my));
            return;
        }

        _dragging = down && _explore;
    }

    /// <summary>
    /// Switches between the automatic descent and steering by hand. Either way the camera starts
    /// over — on the whole set for exploring, on a fresh descent for watching — because the two
    /// modes want to begin in opposite places, and a half-way handover would begin in neither.
    /// </summary>
    private static void SetExplore(bool explore)
    {
        if (explore == _explore) return;

        _explore = explore;
        _director.Interactive = explore;
        _pendingZoom = 1.0;
        _dragging = false;

        if (explore) _director.ResetToOverview();
        else _director.NewDescent();
    }

    /// <summary>
    /// Moves the kernel, abandoning any probe in flight. A pinned choice takes effect at once; auto
    /// keeps whichever is running until the next probe has something to say about it.
    /// </summary>
    private static void SetBackend(Backend backend)
    {
        if (_gpu is null || backend == _backend) return;

        _backend = backend;
        if (backend != Backend.Auto) _useGpu = backend == Backend.Gpu;
        _probing = false;
        _nextProbe = Clock.Elapsed.TotalSeconds + ProbeSeconds;
    }

    /// <summary>Code the settings screen uses for the current choice: -1 auto, 1 card, 0 processor.</summary>
    private static int BackendCode => _backend switch
    {
        Backend.Gpu => 1,
        Backend.Cpu => 0,
        _ => -1,
    };

    /// <summary>
    /// Brings the menu up mid-run, creating it if the startup screen was skipped, so escape always
    /// has somewhere to go.
    /// </summary>
    private static void OpenMenu()
    {
        if (_menu is null)
        {
            _menu = new SettingsScreen(FindMonospace(), _gpu is not null);
            _menu.Preselect(_quality, _director.Speed, _driftBudget, BackendCode, _explore,
                _stillLongEdge, _stillFormat == SKEncodedImageFormat.Jpeg);
            _menu.Close(); // not a first run: the Resume row should not read "Start"
        }

        _menu.Show(_hud, BackendCode, _explore, _director.Kind);
    }

    /// <summary>Opens the panel on its still page, creating it if the startup screen was skipped.</summary>
    private static void OpenStillMenu()
    {
        EnsureMenu();
        DescribeStill();
        _menu!.ShowStill();
    }

    private static void EnsureMenu()
    {
        if (_menu is not null) return;

        _menu = new SettingsScreen(FindMonospace(), _gpu is not null);
        _menu.Preselect(_quality, _director.Speed, _driftBudget, BackendCode, _explore,
            _stillLongEdge, _stillFormat == SKEncodedImageFormat.Jpeg);
        _menu.Close(); // not a first run: the Resume row should not read "Start"
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

        // Once the target depth is reached the camera stops and the same view is re-rendered, so
        // the kernel timings settle on one number instead of tracking whatever the descent wanders
        // into. Everything else keeps running, so the window still animates and the readout is live.
        //
        // Getting there is stepped at a fixed rate rather than in real time, because the depth alone
        // does not pin the view down: the route is re-aimed against the clock, so two runs that stop
        // at the same magnification otherwise stop in different places, and a frame's cost depends
        // far more on where it is than on how deep. Fixed steps make the route a function of the
        // seed, which is what makes two runs comparable.
        bool seeking = _freezeAt > 0 && _director.Magnification < _freezeAt;
        bool frozen = _freezeAt > 0 && !seeking;
        if (seeking)
        {
            dt = 1.0 / 60.0;
            _director.Throttle = 1.0;
        }
        else if (frozen && _frozenGpuMs < 0 && _gpu is not null)
        {
            // Mark where the frozen stretch begins, so the figure reported at exit is a mean over
            // frames of one identical view rather than a decaying average that is still carrying
            // the descent that led here.
            _frozenGpuMs = _gpu.TotalGpuMs;
            _frozenFrames = _gpu.CompletedFrames;
        }

        if (menuOpen)
        {
            _quality = _menu!.Quality;
            _driftBudget = _menu.Drift;
            _director.Speed = _menu.Speed;
            _hud = _menu.ShowHud;
            _stillLongEdge = _menu.StillLongEdge;
            _stillSamples = _menu.StillSamples;
            _stillIterations = _menu.StillIterations;
            _stillFormat = _menu.StillJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
            if (_menu.OnStillPage) DescribeStill();
            SetExplore(_menu.Explore);
            ChooseFractal(_menu.Fractal);
            if (_gpu is not null) SetBackend(_menu.Renderer switch
            {
                1 => Backend.Gpu,
                0 => Backend.Cpu,
                _ => Backend.Auto,
            });

            // Let time pass so the descent's fade-in completes and the preview is actually visible,
            // but with the zoom stopped: a zero throttle freezes both the scale and the pan.
            _director.Throttle = 0.0;
            _director.Advance(dt, aspect);
        }
        else if (!_paused)
        {
            if (frozen) _director.Throttle = 0.0;
            if (_explore) EaseZoom(dt);
            _director.Advance(dt, aspect);
            if (!frozen) _paletteShift += dt * 0.015; // slow shimmer through the gradient
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
            iterations, _paletteShift, _director.Generation, _director.Reference,
            _director.Kind);

        // Ask the worker for the current view at the current quality, then draw whatever it
        // last finished. The two are deliberately out of step; the transform below hides it.
        // Submit the camera as it will be once the kernel finishes, not as it is now, and with a
        // slightly wider field so the frame still covers the window as the view moves on.
        // Nothing to predict when the camera only moves on input: rendering ahead of a hand-steered
        // view would aim at a position it has no reason to reach.
        double lead = _explore ? 0.0 : Math.Clamp(_computeMs / 1000.0 * LeadFactor, 0.0, 4.0);
        var (px, py, ps) = _explore
            ? (_director.OffsetX, _director.OffsetY, _director.Scale)
            : _director.Predict(lead);
        var (kernelW, kernelH) = KernelSize(fb.X, fb.Y);
        var submit = now with { CenterX = px, CenterY = py, Scale = ps * Overscan };

        var style = FractalKind.Of(_director.Kind).Style;

        // Ray marching exists only on the card, so those fractals override the backend choice
        // rather than quietly rendering something else on the processor.
        bool marched = style == RenderStyle.Raymarched;
        bool onGpu = _gpu is not null && (_useGpu || marched) && style != RenderStyle.Drawn;

        // Ray marching has no CPU counterpart, so without the card these have nothing to draw. Left
        // blank with a word in the readout rather than handed to a kernel that would answer with the
        // wrong fractal.
        bool unavailable = marched && _gpu is null;
        double budget = GpuFrameShare * 1000.0 / Math.Clamp(_fps, 20.0, 250.0);

        if (style == RenderStyle.Drawn || unavailable)
        {
            // Nothing to compute: these are drawn from their own rule every frame, straight onto the
            // canvas, and cost almost nothing because each rule stops subdividing at a pixel.
            _computeMs += (1.0 - _computeMs) * 0.15;
        }
        else if (onGpu)
        {
            // Supersampled frames are averaged back down on the card, so what arrives is always at
            // roughly screen density and Skia only ever has to draw it at about 1:1.
            var (outW, outH) = PresentSize(kernelW, kernelH, fb.X);
            _gpu!.Submit(submit, _palette, kernelW, kernelH, outW, outH);

            _gpu.Step(budget);
            _computeMs += (_gpu.KernelMs - _computeMs) * 0.15;
        }
        else
        {
            _renderer.Submit(submit, _palette, kernelW, kernelH);
            _computeMs += (_renderer.KernelMs - _computeMs) * 0.15;
        }

        if (_backend == Backend.Auto && _gpu is not null && !menuOpen
            && style == RenderStyle.Field)
            Arbitrate(submit, kernelW, kernelH, budget);

        // The zoom controller has nothing to control while the camera is being steered by hand:
        // there is no rate to hold down, and no descent to give up on.
        if (!menuOpen && _freezeAt <= 0 && !_explore) AdaptZoom();

        _grContext.ResetContext();
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Black);

        // Must outlive the flush below. SKImage.FromPixels neither copies the pixels nor takes
        // ownership of them, so Skia may not have read them yet when the draw call returns —
        // releasing the image before flushing lets it read memory that the kernel buffer has since
        // freed or reallocated, which shows up as an intermittent hard crash with no managed stack.
        SKImage? image = null;
        GRBackendTexture? backing = null;

        var frame = default(FractalRenderer.View);
        int fw = 0, fh = 0;
        bool have;

        if (unavailable)
        {
            have = false;
        }
        else if (style == RenderStyle.Drawn)
        {
            have = false;
            DrawnFractals.Draw(canvas, _director.Kind,
                _director.OffsetX, _director.OffsetY, _director.Scale, fb.X, fb.Y, _palette);
        }
        else if (onGpu)
        {
            // The card already holds the frame as a texture, so there is nothing to copy: it is
            // wrapped where it lies and drawn from there. The kernel keeps owning it.
            have = _gpu!.TryGetReady(out uint texture, out frame, out fw, out fh);
            if (have)
            {
                backing = new GRBackendTexture(fw, fh, false,
                    new GRGlTextureInfo(GlTexture2D, texture, GlRgba8));
                image = SKImage.FromTexture(_grContext, backing, GRSurfaceOrigin.BottomLeft,
                    SKColorType.Rgba8888, SKAlphaType.Opaque);
                have = image is not null;
            }
        }
        else
        {
            have = _renderer.TryTakeLatest(ref _bitmap, out frame, out fw, out fh);

            // Wrapping without copying, with a fresh identity each frame so Skia re-uploads it
            // instead of reusing last frame's cached texture.
            if (have) image = SKImage.FromPixels(_bitmap!.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
        }

        if (have && image is not null && frame.Generation == now.Generation)
        {
            var (k, tx, ty) = FractalRenderer.Reproject(frame, fw, fh, now, fb.X, fb.Y);

            canvas.Save();
            canvas.Translate(tx, ty);
            canvas.Scale(k, k);
            // A card frame arrives already resolved to screen density, so it never needs the
            // mipmapped path — which Skia does not do correctly on a texture it does not own.
            var sampling = k > 1.05f ? Cubic : k >= 0.98f || onGpu ? Linear : Downsample;
            canvas.DrawImage(image, 0, 0, sampling);
            canvas.Restore();
            _lastUpscale = k;
        }

        if (_director.Fade < 1.0)
        {
            _fadePaint.Color = SKColors.Black.WithAlpha((byte)(255 * (1.0 - Math.Clamp(_director.Fade, 0, 1))));
            canvas.DrawRect(SKRect.Create(0, 0, fb.X, fb.Y), _fadePaint);
        }

        // Once it stops being busy, start the clock on how long its outcome stays on screen.
        if (!_stills.Busy && _stillNoticeUntil == double.MaxValue)
            _stillNoticeUntil = Clock.Elapsed.TotalSeconds + StillNoticeSeconds;

        if (_hud) DrawHud(canvas, fb.X, fb.Y);
        if (menuOpen) _menu!.Draw(canvas, fb.X, fb.Y, (float)(fb.Y / (double)Math.Max(1, _window.Size.Y)));

        _surface.Flush();
        _grContext.Flush();
        image?.Dispose();
        backing?.Dispose();

        _frames++;

        // A timed run can land in the middle of a cross-fade, where the frame is largely black. For a
        // snapshot that is useless, so wait for the fade to finish — but only briefly, so a run can
        // never hang waiting for a descent that is still fading in.
        bool timeUp = Clock.Elapsed.TotalSeconds >= _duration;
        bool presentable = _director.Fade >= 0.999 || _snapshotPath is null;
        if (timeUp && (presentable || Clock.Elapsed.TotalSeconds >= _duration + FadeGrace))
        {
            if (_snapshotPath is not null) SaveSnapshot(_snapshotPath);
            var (kw, kh) = onGpu ? _gpu!.LastSize : _renderer.LastSize;
            Console.WriteLine($"Rendered {_frames} frames in {Clock.Elapsed.TotalSeconds:0.0}s " +
                              $"({_frames / Clock.Elapsed.TotalSeconds:0.0} fps), " +
                              $"reached {_director.Magnification:0.00e+00}x on cycle {_director.Cycle + 1} " +
                              $"({(onGpu ? "gpu" : "cpu")} kernel, {kw}x{kh}, {_computeMs:0.0} ms" +
                              $"{(onGpu ? $", {_gpu!.GpuMs:0.0} ms of card time" : "")}).");

            if (_frozenGpuMs >= 0 && _gpu is not null)
            {
                long frames = _gpu.CompletedFrames - _frozenFrames;
                if (frames > 0)
                    Console.WriteLine($"Frozen at {_director.Magnification:0.00e+00}x: " +
                                      $"{(_gpu.TotalGpuMs - _frozenGpuMs) / frames:0.00} ms of card time " +
                                      $"per frame, mean of {frames}.");
            }
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
    /// Size a kernel frame should be presented at: the same field of view at one sample per screen
    /// pixel. Equal to the kernel size unless the detail setting is supersampling, in which case
    /// the extra samples are what the resolve pass averages away. Not quantised — the ratio has to
    /// stay the kernel's, or the frame would be drawn slightly stretched.
    ///
    /// The 5% margin is there because kernel dimensions are rounded up to a multiple of 16, so at
    /// native detail the buffer is already a percent or two larger than the window. Resolving that
    /// away would cost a pass and a little sharpness to correct for rounding.
    /// </summary>
    private static (int Width, int Height) PresentSize(int kernelW, int kernelH, int fbW)
    {
        double density = fbW * Overscan / kernelW;
        return density > 0.95
            ? (kernelW, kernelH)
            : ((int)Math.Round(kernelW * density), (int)Math.Round(kernelH * density));
    }

    /// <summary>
    /// Keeps the kernel on whichever backend is actually faster here, by occasionally handing the
    /// idle one a frame and timing it.
    ///
    /// Measured rather than assumed, because where the crossover falls is a property of the machine
    /// and not of the program. The card computes vastly more pixels at once but iterates in fp64,
    /// which consumer cards run at a small fraction of their fp32 rate; the CPU takes far fewer
    /// pixels at a time but skips whole runs of iterations through the approximation table. Which
    /// of those wins depends on the depth and on the two chips, and the ratio between a given pair
    /// of them spans more than an order of magnitude across machines.
    ///
    /// The probe costs one kernel frame every few seconds, and it leaves a fresh frame behind on
    /// the backend it timed, so a handover has something current to draw immediately.
    /// </summary>
    private static void Arbitrate(FractalRenderer.View submit, int kernelW, int kernelH, double budget)
    {
        double now = Clock.Elapsed.TotalSeconds;

        if (!_probing)
        {
            if (now < _nextProbe) return;
            _probing = true;

            if (_useGpu)
            {
                _probeMark = _renderer.Passes;
                _renderer.Submit(submit, _palette, kernelW, kernelH);
            }
            else
            {
                var (outW, outH) = PresentSize(kernelW, kernelH, _window.FramebufferSize.X);
                _gpu!.Submit(submit, _palette, kernelW, kernelH, outW, outH);
            }
            return;
        }

        if (_useGpu)
        {
            if (_renderer.Passes == _probeMark) return;
            _probeMs = _renderer.KernelMs;
        }
        else
        {
            _gpu!.Step(budget);
            if (_gpu.Busy) return;
            _probeMs = _gpu.KernelMs;
        }

        _probing = false;
        _nextProbe = now + ProbeSeconds;

        double active = _useGpu ? _gpu!.KernelMs : _renderer.KernelMs;
        if (_probeMs < active * SwitchMargin) _useGpu = !_useGpu;
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

        bool onGpu = _gpu is not null && _useGpu;
        var (kw, kh) = onGpu ? _gpu!.LastSize : _renderer.LastSize;
        int iterations = _director.MaxIterations;
        var style = FractalKind.Of(_director.Kind).Style;
        string mode = style == RenderStyle.Raymarched
            ? _gpu is null ? "needs an OpenGL 4.0 card — unavailable here" : "ray-marched"
            : style == RenderStyle.Drawn ? "drawn"
            : _director.Reference is { } r ? $"perturbed, {r.FracBits}-bit anchor" : "fp64";
        string where = (onGpu ? "gpu" : "cpu") + (_backend == Backend.Auto && _gpu is not null ? ", auto" : "");
        string still = Clock.Elapsed.TotalSeconds < _stillNoticeUntil && _stills.Status.Length > 0
            ? "   " + _stills.Status
            : "";

        string[] lines = _explore
            ?
            [
                $"{FractalKind.Of(_director.Kind).Name}   {_director.Magnification:0.000e+00}x   " +
                $"explore   {mode}",
                $"iter   {iterations}    fps {_fps,4:0}    kernel {_computeMs,5:0.0} ms on {where}",
                style == RenderStyle.Drawn
                    ? $"drew {DrawnFractals.LastPieces}   visited {DrawnFractals.LastVisits}   " +
                      $"depth {DrawnFractals.LastDepth}{(_paused ? "   [PAUSED]" : "")}{still}"
                    : $"render {kw}x{kh}   resample {_lastUpscale:0.00}x{(_paused ? "   [PAUSED]" : "")}{still}",
                "scroll zoom at pointer   drag pan   P save still   R whole set   E auto   esc menu",
            ]
            :
            [
                $"{FractalKind.Of(_director.Kind).Name}   {_director.Magnification:0.000e+00}x   " +
                $"(cycle {_director.Cycle + 1})   {mode}",
                $"iter   {iterations}    fps {_fps,4:0}    kernel {_computeMs,5:0.0} ms on {where}",
                $"render {kw}x{kh}   resample {_lastUpscale:0.00}x   speed {_director.Speed * _director.Throttle:0.000}/s{(_paused ? "   [PAUSED]" : "")}{still}",
                "space pause   R new descent   E explore   P save still   G gpu/cpu   esc quit",
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
        _gpu?.Dispose();
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


