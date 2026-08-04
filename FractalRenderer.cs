using System;
using System.Diagnostics;
using System.Threading;
using SkiaSharp;

namespace FractalZoom;

/// <summary>
/// Runs the Mandelbrot kernel on a worker thread so the display loop never blocks on it.
/// The display re-projects the most recent finished frame onto the current camera, which
/// lets the kernel take 100 ms at full resolution while the window still animates at vsync.
/// </summary>
internal sealed class FractalRenderer : IDisposable
{
    /// <summary>
    /// Everything the kernel needs to know about where the camera is. CenterX/CenterY are absolute
    /// when <see cref="Reference"/> is null, otherwise offsets from the reference orbit's centre.
    /// </summary>
    internal readonly record struct View(
        double CenterX, double CenterY, double Scale,
        int MaxIterations, double PaletteShift, int Generation,
        ReferenceOrbit? Reference)
    {
        /// <summary>
        /// Where <paramref name="a"/>'s centre sits relative to <paramref name="b"/>'s, in
        /// complex-plane units. Cheap in the common case that both share a reference orbit; when
        /// they don't — the brief window after a re-anchor — it falls back to comparing the two
        /// anchors in full precision.
        /// </summary>
        public static (double X, double Y) CenterDelta(View a, View b)
        {
            if (ReferenceEquals(a.Reference, b.Reference))
                return (a.CenterX - b.CenterX, a.CenterY - b.CenterY);

            int bits = Math.Max(a.Reference?.FracBits ?? 128, b.Reference?.FracBits ?? 128);
            var ax = Anchor(a.Reference?.CenterX, a.CenterX, bits);
            var ay = Anchor(a.Reference?.CenterY, a.CenterY, bits);
            var bx = Anchor(b.Reference?.CenterX, b.CenterX, bits);
            var by = Anchor(b.Reference?.CenterY, b.CenterY, bits);
            return ((ax - bx).ToDouble(), (ay - by).ToDouble());
        }

        private static BigFixed Anchor(BigFixed? anchor, double offset, int bits) =>
            anchor is { } a
                ? a.WithFracBits(bits).AddDouble(offset)
                : BigFixed.FromDouble(offset, bits);
    }

    private sealed class Frame
    {
        public SKBitmap? Bitmap;
        public float[] Field = [];
        public int Width, Height;
        public View View;
        public bool HasContent;

        public SKBitmap Resize(int w, int h)
        {
            if (Bitmap is null || Width != w || Height != h)
            {
                Bitmap?.Dispose();
                Bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
                Width = w;
                Height = h;
                HasContent = false;
            }
            if (Field.Length < w * h) Field = new float[w * h];
            return Bitmap;
        }
    }

    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _running = true;

    private Frame _ready = new();   // last completed frame, safe to copy out
    private Frame _working = new(); // currently being filled by the worker

    private View _job;
    private Mandelbrot.Palette? _jobPalette;
    private int _jobW = 64, _jobH = 64;
    private bool _jobDirty;

    private long _kernelUs;

    public FractalRenderer()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "mandelbrot-kernel",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Wall time of the most recent kernel pass, in milliseconds.</summary>
    public double KernelMs => Volatile.Read(ref _kernelUs) / 1000.0;

    /// <summary>Dimensions of the most recent kernel pass.</summary>
    public (int Width, int Height) LastSize
    {
        get { lock (_gate) return (_ready.Width, _ready.Height); }
    }

    /// <summary>Tells the worker what to render next. Cheap; call every display frame.</summary>
    public void Submit(View view, Mandelbrot.Palette palette, int width, int height)
    {
        lock (_gate)
        {
            _job = view;
            _jobPalette = palette;
            _jobW = width;
            _jobH = height;
            _jobDirty = true;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Copies the newest finished frame into <paramref name="display"/> (reallocating it if the
    /// size changed) so the caller can draw and flush without holding the worker's lock.
    /// </summary>
    public bool TryTakeLatest(ref SKBitmap? display, out View view, out int width, out int height)
    {
        lock (_gate)
        {
            view = _ready.View;
            width = _ready.Width;
            height = _ready.Height;

            if (!_ready.HasContent || _ready.Bitmap is null) return false;

            var src = _ready.Bitmap;
            if (display is null || display.Width != src.Width || display.Height != src.Height)
            {
                display?.Dispose();
                display = new SKBitmap(src.Info);
            }

            unsafe
            {
                byte* from = (byte*)src.GetPixels();
                byte* to = (byte*)display.GetPixels();
                if (src.RowBytes == display.RowBytes)
                {
                    Buffer.MemoryCopy(from, to, (long)display.RowBytes * height, (long)src.RowBytes * height);
                }
                else
                {
                    long row = Math.Min(src.RowBytes, display.RowBytes);
                    for (int y = 0; y < height; y++)
                        Buffer.MemoryCopy(from + (long)y * src.RowBytes, to + (long)y * display.RowBytes, row, row);
                }
            }

            return true;
        }
    }

    private void WorkerLoop()
    {
        try
        {
            Render();
        }
        catch (Exception ex)
        {
            // An exception here used to kill the process with nothing on stdout, which made a
            // failure indistinguishable from a clean exit. Say what happened.
            Console.Error.WriteLine($"fractal kernel thread failed: {ex}");
            Failure = ex;
        }
    }

    /// <summary>Set if the kernel thread died, so the host can report it rather than fall silent.</summary>
    public Exception? Failure { get; private set; }

    private void Render()
    {
        var sw = new Stopwatch();

        while (_running)
        {
            View job;
            Mandelbrot.Palette? palette;
            int w, h;
            SKBitmap target;
            float[] field;

            lock (_gate)
            {
                while (_running && !_jobDirty)
                    Monitor.Wait(_gate, 20);
                if (!_running) return;

                job = _job;
                palette = _jobPalette;
                w = _jobW;
                h = _jobH;
                _jobDirty = false;
                target = _working.Resize(w, h);
                field = _working.Field;
            }

            if (palette is null) continue;

            sw.Restart();
            Mandelbrot.Render(
                target.GetPixels(), target.RowBytes, w, h, field,
                job.CenterX, job.CenterY, job.Scale,
                job.MaxIterations, palette, job.PaletteShift, job.Reference);
            sw.Stop();

            lock (_gate)
            {
                _working.View = job;
                _working.HasContent = true;
                (_ready, _working) = (_working, _ready);
                Volatile.Write(ref _kernelUs, sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
            }
        }
    }

    /// <summary>
    /// Maps a frame rendered for <paramref name="frame"/> onto the view described by
    /// <paramref name="now"/>. Returns the scale and offset such that
    /// screen = framePixel * scale + offset, keeping every complex coordinate aligned.
    /// </summary>
    public static (float Scale, float OffsetX, float OffsetY) Reproject(
        View frame, int frameW, int frameH, View now, int screenW, int screenH)
    {
        double framePixel = 2.0 * frame.Scale / frameH;
        double screenPixel = 2.0 * now.Scale / screenH;
        double k = framePixel / screenPixel;
        var (dx, dy) = View.CenterDelta(frame, now);

        double tx = dx / screenPixel - (frameW * 0.5 - 0.5) * k + screenW * 0.5 - 0.5;
        double ty = -dy / screenPixel - (frameH * 0.5 - 0.5) * k + screenH * 0.5 - 0.5;

        return ((float)k, (float)tx, (float)ty);
    }

    public void Dispose()
    {
        _running = false;
        lock (_gate) Monitor.PulseAll(_gate);
        _thread.Join(500);
        lock (_gate)
        {
            _ready.Bitmap?.Dispose();
            _working.Bitmap?.Dispose();
        }
    }
}
