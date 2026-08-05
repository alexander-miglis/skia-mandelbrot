using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace FractalZoom;

/// <summary>
/// Renders the current view properly and writes it to the pictures folder.
///
/// Deliberately not a screen capture. The view is computed again from scratch at whatever resolution
/// was asked for, supersampled, and with a larger iteration budget than the live kernel runs at, so a
/// still can be several times the size of the window it was taken from and cleaner than anything that
/// was ever on screen. The window it came from is irrelevant except for its shape.
///
/// Two decisions worth recording:
///
/// It runs on the CPU kernel, not the card's. The card is much faster, but it is busy keeping the
/// display at vsync, and a still is a one-off that nobody is watching a clock on — so this way the
/// zoom does not stall, the camera stays under your control while it works, and the picture comes out
/// of <see cref="Mandelbrot"/>, which is the reference implementation of the two and the one with the
/// approximation table.
///
/// It renders in horizontal bands rather than all at once. Two reasons: it is what makes progress
/// reportable, and it keeps the supersampled buffer small — a 4K still at 2x would otherwise want
/// 224 MB of scratch, where a band wants about 14 MB. Each band is computed with one extra row above
/// and below, which is then discarded, because the colouring reads its neighbours: without the
/// overlap every band boundary would be coloured as though it were the edge of the image, and would
/// show as a faint seam.
/// </summary>
internal sealed class StillRenderer
{
    /// <summary>Band height in supersampled rows. Large enough to keep every core busy inside one.</summary>
    private const int BandRows = 256;

    private const int JpegQuality = 95;

    private volatile string _status = "";
    private volatile bool _busy;

    /// <summary>Read and written from two threads, hence the interlocked access rather than volatile.</summary>
    private long _progressBits;

    /// <summary>True while a still is being rendered or encoded. Starting another is refused.</summary>
    public bool Busy => _busy;

    /// <summary>What to tell the user: progress while it runs, then where it went.</summary>
    public string Status => _status;

    /// <summary>Name of the last file written, or empty until one has been.</summary>
    public string LastFile => _lastFile;

    private volatile string _lastFile = "";

    /// <summary>0 to 1 through the bands, for a readout.</summary>
    public double Progress => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _progressBits));

    /// <summary>
    /// Sample-iterations a second, from the last still that finished, or 0 until one has. Measured
    /// rather than assumed so the estimate shown in the menu is this machine's rather than a guess:
    /// a still's cost is per sample and per iteration, and both are known before it starts.
    /// </summary>
    public double SamplesPerSecond => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _rateBits));

    private long _rateBits;

    private void SetProgress(double value) =>
        Interlocked.Exchange(ref _progressBits, BitConverter.DoubleToInt64Bits(value));

    /// <summary>
    /// Where stills are written: the pictures folder, or the working directory if the system has
    /// none configured. Resolved once so the menu can say where a picture is going to end up before
    /// it is asked for, rather than only afterwards.
    /// </summary>
    public static string Folder { get; } = ResolveFolder();

    private static string ResolveFolder()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(folder) || !Directory.Exists(folder)
            ? Directory.GetCurrentDirectory()
            : folder;
    }

    /// <summary>
    /// Starts a still of <paramref name="view"/> at <paramref name="width"/> x
    /// <paramref name="height"/>. Returns false if one is already in flight.
    /// </summary>
    /// <param name="supersample">Samples per axis; the cost is the square of it.</param>
    /// <param name="iterationHeadroom">
    /// Multiplier on the live view's iteration budget. That budget is sized to be affordable sixty
    /// times a second and is measurably tight — deep views spend most of it — so a still, which is
    /// paid for once, can buy the margin instead of the speed.
    /// </param>
    public bool Start(
        FractalRenderer.View view, Mandelbrot.Palette palette,
        int width, int height, int supersample, double iterationHeadroom,
        SKEncodedImageFormat format, string nameStem)
    {
        if (_busy) return false;

        _busy = true;
        SetProgress(0);
        _status = "still 0%";

        Task.Run(() =>
        {
            try
            {
                string path = Render(view, palette, width, height, supersample, iterationHeadroom,
                    format, nameStem);
                _lastFile = Path.GetFileName(path);
                _status = $"still saved: {_lastFile}";
                Console.WriteLine($"Wrote {path}");
            }
            catch (Exception ex)
            {
                _status = $"still failed: {ex.Message}";
                Console.Error.WriteLine($"Still failed: {ex}");
            }
            finally
            {
                _busy = false;
            }
        });

        return true;
    }

    private string Render(
        FractalRenderer.View view, Mandelbrot.Palette palette,
        int width, int height, int supersample, double iterationHeadroom,
        SKEncodedImageFormat format, string nameStem)
    {
        var clock = Stopwatch.StartNew();

        int ss = Math.Clamp(supersample, 1, 4);
        int sourceW = width * ss;
        int sourceH = height * ss;
        int maxIter = (int)Math.Clamp(view.MaxIterations * iterationHeadroom, 1, 4_000_000);

        // One pixel of the supersampled grid, which every band shares.
        double pixel = 2.0 * view.Scale / sourceH;

        using var output = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));

        int bandRows = Math.Max(ss, BandRows - BandRows % ss);
        var field = new float[sourceW * (bandRows + 2)];
        using var band = new SKBitmap(
            new SKImageInfo(sourceW, bandRows + 2, SKColorType.Bgra8888, SKAlphaType.Opaque));

        for (int top = 0; top < sourceH; top += bandRows)
        {
            int rows = Math.Min(bandRows, sourceH - top);

            // The discarded margin: one row of context wherever there is an image to take it from.
            int above = top > 0 ? 1 : 0;
            int below = top + rows < sourceH ? 1 : 0;
            int renderTop = top - above;
            int renderRows = rows + above + below;

            // Same pixel size, so the band's own half-height and centre are what place it.
            double bandScale = view.Scale * renderRows / sourceH;
            double bandCenterY = view.CenterY + (sourceH * 0.5 - renderTop - renderRows * 0.5) * pixel;

            Mandelbrot.Render(
                band.GetPixels(), band.RowBytes, sourceW, renderRows, field,
                view.CenterX, bandCenterY, bandScale,
                maxIter, palette, view.PaletteShift, view.Reference, view.Kind,
                view.OriginX, view.OriginY);

            Resolve(band, above, output, top / ss, rows / ss, ss);

            double done = Math.Min(1.0, (top + rows) / (double)sourceH);
            SetProgress(done);
            _status = $"still {done * 100:0}%";
        }

        // Recorded against the iteration ceiling rather than the iterations actually run, which is
        // the same thing the estimate is computed from, so the two are consistent even though most
        // pixels escape long before the ceiling.
        double work = (double)sourceW * sourceH * maxIter;
        Interlocked.Exchange(ref _rateBits,
            BitConverter.DoubleToInt64Bits(work / Math.Max(0.001, clock.Elapsed.TotalSeconds)));

        _status = "still encoding";
        string path = Save(output, format, nameStem);

        Console.WriteLine(
            $"Rendered a {width}x{height} still at {ss}x{ss} samples, {maxIter} iterations, " +
            $"in {clock.Elapsed.TotalSeconds:0.0}s.");
        return path;
    }

    /// <summary>
    /// Averages one band's samples down into the output rows they belong to. A box over exactly the
    /// samples that make up each output pixel, in the same gamma-encoded space the card's resolve
    /// pass averages in, so the two agree.
    /// </summary>
    private static unsafe void Resolve(SKBitmap band, int skipRows, SKBitmap output, int outTop, int outRows, int ss)
    {
        int outW = output.Width;
        byte* src = (byte*)band.GetPixels();
        byte* dst = (byte*)output.GetPixels();
        int srcStride = band.RowBytes, dstStride = output.RowBytes;
        int samples = ss * ss;

        for (int oy = 0; oy < outRows; oy++)
        {
            uint* row = (uint*)(dst + (long)(outTop + oy) * dstStride);

            for (int ox = 0; ox < outW; ox++)
            {
                if (ss == 1)
                {
                    row[ox] = *(uint*)(src + (long)(skipRows + oy) * srcStride + ox * 4);
                    continue;
                }

                int b = 0, g = 0, r = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    byte* line = src + (long)(skipRows + oy * ss + sy) * srcStride + (long)ox * ss * 4;
                    for (int sx = 0; sx < ss; sx++)
                    {
                        b += line[sx * 4 + 0];
                        g += line[sx * 4 + 1];
                        r += line[sx * 4 + 2];
                    }
                }

                row[ox] = 0xFF000000u
                          | ((uint)(r / samples) << 16)
                          | ((uint)(g / samples) << 8)
                          | (uint)(b / samples);
            }
        }
    }

    /// <summary>
    /// Writes the picture to the pictures folder, or beside the executable if the system has no such
    /// folder configured. Never overwrites: a suffix is added until the name is free.
    /// </summary>
    private static string Save(SKBitmap bitmap, SKEncodedImageFormat format, string nameStem)
    {
        string extension = format == SKEncodedImageFormat.Jpeg ? "jpg" : "png";
        string path = Path.Combine(Folder, $"{nameStem}.{extension}");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Folder, $"{nameStem}-{n}.{extension}");

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, JpegQuality);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }
}
