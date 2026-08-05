using System;
using Silk.NET.Input;
using SkiaSharp;

namespace FractalZoom;

/// <summary>What the host should do after the menu handled a key.</summary>
internal enum MenuAction
{
    /// <summary>Not a menu key; the host may handle it.</summary>
    None,

    /// <summary>Handled, nothing further to do.</summary>
    Consumed,

    Resume,
    NewDescent,
    Quit,

    /// <summary>Render and save a still of the current view at the settings on the still page.</summary>
    RenderStill,

    /// <summary>Switch to the fractal picked on the fractal page.</summary>
    PickFractal,
}

/// <summary>
/// The startup menu, drawn over a live preview of the opening view. The zoom is held still while it
/// is open, and every setting takes effect immediately, so the choices can be seen rather than
/// guessed at.
/// </summary>
internal sealed class SettingsScreen : IDisposable
{
    private sealed class Option
    {
        public required string Label { get; init; }
        public required string[] Choices { get; init; }
        public required double[] Values { get; init; }
        public required string Hint { get; init; }

        /// <summary>Continuation of the hint, or empty. Kept beside it so rows can be omitted.</summary>
        public string Hint2 { get; init; } = "";

        public int Index;

        public double Value => Values[Index];
        public string Choice => Choices[Index];
    }

    /// <summary>
    /// Taken from the list itself rather than written out here. Kept in step by construction: a
    /// hand-written copy of the names is one fractal away from being wrong, and when this row held
    /// six entries against a list of twenty-six, picking anything past the sixth indexed off the end
    /// of it and brought the whole program down.
    /// </summary>
    private static string[] FractalNames()
    {
        var names = new string[FractalKind.All.Length];
        for (int i = 0; i < names.Length; i++) names[i] = FractalKind.All[i].Name;
        return names;
    }

    private static double[] FractalValues()
    {
        var values = new double[FractalKind.All.Length];
        for (int i = 0; i < values.Length; i++) values[i] = i;
        return values;
    }

    private readonly Option _fractal = new()
    {
        Label = "Fractal",
        Choices = FractalNames(),
        Values = FractalValues(),
        Hint = "Twenty-six of them. Click the row, or press enter or F, to see the whole list;",
        Hint2 = "the arrows step through it one at a time. Only the Mandelbrot zooms past ~1e13x.",
    };

    private readonly Option _camera = new()
    {
        Label = "Camera",
        Choices = ["Descends by itself", "Explore with the mouse"],
        Values = [0, 1],
        Hint = "Exploring opens on the whole set and hands you the camera: scroll to zoom at the",
        Hint2 = "pointer, drag to pan. E switches at any time; either way the view starts over.",
    };

    private readonly Option _detail = new()
    {
        Label = "Detail",
        Choices = ["Super crisp", "Sharper", "Native", "High", "Balanced", "Fast", "Fastest"],
        Values = [2.0, 1.4, 1.0, 0.85, 0.7, 0.5, 0.35],
        Index = 2,
        Hint = "Pixels the kernel computes, against the window. Above Native it supersamples,",
        Hint2 = "resolving detail finer than a pixel; below it, descents reach much deeper.",
    };

    private readonly Option _speed = new()
    {
        Label = "Zoom speed",
        Choices = ["Drifting", "Slow", "Steady", "Brisk", "Fast", "Headlong"],
        Values = [0.10, 0.18, 0.25, 0.45, 0.80, 1.60],
        Index = 2,
        Hint = "Held constant while the descent lasts. Slower also means a descent gets",
        Hint2 = "further before the kernel outgrows it.",
    };

    private readonly Option _drift = new()
    {
        Label = "Motion sharpness",
        Choices = ["Crisp", "Balanced", "Loose"],
        Values = [1.15, 1.30, 1.60],
        Index = 1,
        Hint = "How much a frame may be stretched while the next one computes. Crisp stays",
        Hint2 = "sharpest; Loose trades that for depth.",
    };

    private readonly Option _renderer = new()
    {
        Label = "Kernel runs on",
        Choices = ["Whichever is faster", "Graphics card", "Processor"],
        Values = [-1, 1, 0],
        Hint = "The card computes far more pixels at once; the processor takes far larger steps",
        Hint2 = "per pixel, which wins once the view is deep. G cycles these at any time.",
    };

    private readonly Option _stillSize = new()
    {
        Label = "Resolution",
        Choices = ["Window", "1080p", "1440p", "4K", "8K", "12K", "16K"],
        Values = [0, 1920, 2560, 3840, 7680, 11520, 15360],
        Index = 3,
        Hint = "Longest edge in pixels; the other follows this window's shape, so the picture is",
        Hint2 = "the composition on screen at a higher resolution, not a different framing of it.",
    };

    private readonly Option _stillSamples = new()
    {
        Label = "Samples per pixel",
        Choices = ["4 (2x2)", "9 (3x3)", "16 (4x4)"],
        Values = [2, 3, 4],
        Index = 1,
        Hint = "Every pixel is computed this many times over and averaged. The live view manages",
        Hint2 = "four at most, and only at Super crisp; the cost is the square of the figure.",
    };

    private readonly Option _stillIterations = new()
    {
        Label = "Iteration budget",
        Choices = ["Same as live", "Double", "Quadruple"],
        Values = [1, 2, 4],
        Index = 1,
        Hint = "The live budget is sized to be affordable sixty times a second, and deep views",
        Hint2 = "spend nearly all of it. A still is paid for once, so it can afford the margin.",
    };

    private readonly Option _stillFormat = new()
    {
        Label = "File format",
        Choices = ["PNG", "JPEG"],
        Values = [0, 1],
        Hint = "PNG keeps every pixel exactly; JPEG is a fraction of the size and good enough for",
        Hint2 = "anything short of further editing.",
    };

    private readonly Option _colours = new()
    {
        Label = "Colours",
        Choices = ["Cycle", "Electric", "Ember", "Aurora", "Abyss", "Copper"],
        Values = [-1, 0, 1, 2, 3, 4],
        Hint = "Cycle picks a new gradient for each descent.",
    };

    private readonly Option _readout = new()
    {
        Label = "Readout",
        Choices = ["Shown", "Hidden"],
        Values = [1, 0],
        Hint = "The depth and timing figures in the corner. H toggles them any time.",
    };

    /// <summary>
    /// Which set of rows the panel is showing. Two pages rather than two panels: the navigation,
    /// the drawing and the look are the same, only the rows and the action at the bottom differ.
    /// </summary>
    private enum Page { Settings, Still, Fractals }

    private Page _page;

    private readonly Option[] _settingsOptions;
    private readonly Option[] _stillOptions;

    private Option[] _options => _page == Page.Still ? _stillOptions : _settingsOptions;

    private (string Label, MenuAction Action)[] Actions => _page == Page.Still ? StillActions : MainActions;

    private string Title => _page == Page.Still ? "Save a still" : "Fractal Zoom";

    private string Subtitle => _page == Page.Still
        ? "Rendered again from scratch at these settings — not a capture of the window."
        : "An endless descent into the Mandelbrot set. Choose how it renders.";

    /// <summary>Rows below the settings that do something rather than hold a value.</summary>
    private static readonly (string Label, MenuAction Action)[] MainActions =
    [
        ("Resume", MenuAction.Resume),
        ("Start a new descent", MenuAction.NewDescent),
        ("Exit", MenuAction.Quit),
    ];

    private static readonly (string Label, MenuAction Action)[] StillActions =
    [
        ("Render and save this view", MenuAction.RenderStill),
        ("Back", MenuAction.Resume),
    ];

    private readonly SKFont _title;
    private readonly SKFont _body;
    private readonly SKFont _small;
    private readonly SKPaint _panel = new() { Color = new SKColor(6, 10, 18, 214), IsAntialias = true };
    private readonly SKPaint _edge = new() { Color = new SKColor(120, 170, 235, 90), IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _bar = new() { Color = new SKColor(80, 140, 220, 78), IsAntialias = true };
    private readonly SKPaint _text = new() { Color = new SKColor(0xEA, 0xF2, 0xFF), IsAntialias = true };
    private readonly SKPaint _muted = new() { Color = new SKColor(0x92, 0xA6, 0xC4), IsAntialias = true };
    private readonly SKPaint _accent = new() { Color = new SKColor(0x7F, 0xC4, 0xFF), IsAntialias = true };
    private readonly SKPaint _dim = new() { Color = new SKColor(0x55, 0x66, 0x7A), IsAntialias = true };

    private int _row;

    /// <param name="gpuAvailable">
    /// False when the card could not run the kernel, in which case the row is left out rather than
    /// offered and refused.
    /// </param>
    public SettingsScreen(SKTypeface typeface, bool gpuAvailable)
    {
        _title = new SKFont(typeface, 13f);
        _body = new SKFont(typeface, 13f);
        _small = new SKFont(typeface, 13f);

        _settingsOptions = gpuAvailable
            ? [_fractal, _camera, _detail, _speed, _drift, _renderer, _colours, _readout]
            : [_fractal, _camera, _detail, _speed, _drift, _colours, _readout];

        _stillOptions = [_stillSize, _stillSamples, _stillIterations, _stillFormat];
        _hasRenderer = gpuAvailable;
    }

    private readonly bool _hasRenderer;

    public bool Open { get; private set; } = true;

    /// <summary>False until the first time the menu is dismissed, which changes the Resume label.</summary>
    public bool HasStarted { get; private set; }

    private int TotalRows => _page == Page.Fractals
        ? FractalKind.All.Length
        : _options.Length + Actions.Length;

    public double Quality => _detail.Value;
    public double Speed => _speed.Value;
    public double Drift => _drift.Value;

    /// <summary>Index into the gradient list, or -1 to keep changing it every descent.</summary>
    public int Palette => (int)_colours.Value;

    public bool ShowHud => _readout.Value > 0.5;

    /// <summary>Which formula the kernels should iterate.</summary>
    public Fractal Fractal => (Fractal)(int)_fractal.Value;

    /// <summary>Whether the camera is steered by the mouse rather than by the director.</summary>
    public bool Explore => _camera.Value > 0.5;

    /// <summary>Longest edge of a saved still in pixels, or 0 to match the window.</summary>
    public int StillLongEdge => (int)_stillSize.Value;

    /// <summary>Samples per axis for a still; the count per pixel is the square of it.</summary>
    public int StillSamples => (int)_stillSamples.Value;

    /// <summary>Multiplier on the live iteration budget for a still.</summary>
    public double StillIterations => _stillIterations.Value;

    /// <summary>True when stills should be written as JPEG rather than PNG.</summary>
    public bool StillJpeg => _stillFormat.Value > 0.5;

    /// <summary>True while the still page is the one being shown.</summary>
    public bool OnStillPage => _page == Page.Still;

    /// <summary>
    /// Two lines the host fills in for the still page: what the current choices work out to, and
    /// where the file will go. The panel knows the settings but not the window or the filesystem.
    /// </summary>
    public string StillSummary { get; set; } = "";

    public string StillDestination { get; set; } = "";

    /// <summary>Progress or outcome of the last still, shown on the page that asked for it.</summary>
    public string StillStatus { get; set; } = "";

    /// <summary>Opens the panel on the still page, which is what P does.</summary>
    public void ShowStill()
    {
        _page = Page.Still;
        _row = 0;
        Open = true;
    }

    /// <summary>
    /// Opens the list of every fractal, which is what F does. A row that cycles through twenty-six
    /// values one click at a time is no way to choose from twenty-six things — you cannot see what is
    /// on offer, and getting to the far end takes twenty-five clicks.
    /// </summary>
    public void ShowFractals(Fractal current)
    {
        _page = Page.Fractals;
        _fractal.Index = (int)current;
        _row = (int)current;
        Open = true;
    }

    /// <summary>Rows on the fractal page, laid out in two columns.</summary>
    private const int FractalColumns = 2;

    /// <summary>
    /// Where the kernel should run: -1 to leave it to whichever is measured faster, 1 for the card,
    /// 0 for the processor. Always 0 when there is no card row to choose from.
    /// </summary>
    public int Renderer => _hasRenderer ? (int)_renderer.Value : 0;

    /// <summary>Reopens the menu, syncing the rows that have their own keys to what those left them at.</summary>
    public void Show(bool hudVisible, int renderer, bool explore, Fractal fractal)
    {
        _readout.Index = hudVisible ? 0 : 1;
        _fractal.Index = (int)fractal;
        _renderer.Index = Math.Max(0, Array.IndexOf(_renderer.Values, (double)renderer));
        _camera.Index = explore ? 1 : 0;
        _page = Page.Settings;
        _row = Math.Min(_row, TotalRows - 1);
        Open = true;
    }

    public void Close()
    {
        Open = false;
        HasStarted = true;
    }

    /// <summary>
    /// Points the menu at the nearest choice to each command-line value, so the two cannot disagree.
    /// Without this the menu would silently override whatever was passed on the command line.
    /// </summary>
    public void Preselect(double quality, double speed, double drift, int renderer, bool explore,
        int stillLongEdge, bool stillJpeg)
    {
        _detail.Index = Nearest(_detail, quality);
        _speed.Index = Nearest(_speed, speed);
        _drift.Index = Nearest(_drift, drift);
        _renderer.Index = Math.Max(0, Array.IndexOf(_renderer.Values, (double)renderer));
        _camera.Index = explore ? 1 : 0;
        _stillFormat.Index = stillJpeg ? 1 : 0;

        int size = Array.IndexOf(_stillSize.Values, (double)stillLongEdge);
        if (size >= 0) _stillSize.Index = size;

        CapSpeedForDetail();
    }

    private static int Nearest(Option option, double value)
    {
        int best = option.Index;
        double bestGap = double.MaxValue;
        for (int i = 0; i < option.Values.Length; i++)
        {
            // Compared in log space: these are all ratio-like quantities.
            double gap = Math.Abs(Math.Log(Math.Max(1e-9, option.Values[i]) / Math.Max(1e-9, value)));
            if (gap < bestGap) { bestGap = gap; best = i; }
        }
        return best;
    }

    public MenuAction HandleKey(Key key)
    {
        if (!Open) return MenuAction.None;

        // The fractal page is a grid of names, so the keys move within it and enter picks one.
        if (_page == Page.Fractals)
        {
            int perColumn = (TotalRows + FractalColumns - 1) / FractalColumns;
            switch (key)
            {
                case Key.Up or Key.W: _row = (_row - 1 + TotalRows) % TotalRows; return MenuAction.Consumed;
                case Key.Down or Key.S: _row = (_row + 1) % TotalRows; return MenuAction.Consumed;
                case Key.Left or Key.A: _row = (_row - perColumn + TotalRows) % TotalRows; return MenuAction.Consumed;
                case Key.Right or Key.D: _row = (_row + perColumn) % TotalRows; return MenuAction.Consumed;

                case Key.Enter or Key.KeypadEnter or Key.Space:
                    _fractal.Index = _row;
                    Close();
                    return MenuAction.PickFractal;

                case Key.Escape:
                    Close();
                    return MenuAction.Resume;

                default:
                    return MenuAction.None;
            }
        }

        switch (key)
        {
            case Key.Up or Key.W:
                _row = (_row - 1 + TotalRows) % TotalRows;
                return MenuAction.Consumed;

            case Key.Down or Key.S:
                _row = (_row + 1) % TotalRows;
                return MenuAction.Consumed;

            case Key.Left or Key.A:
                Step(-1);
                return MenuAction.Consumed;

            case Key.Right or Key.D:
                Step(1);
                return MenuAction.Consumed;

            case Key.Enter or Key.KeypadEnter or Key.Space:
                // The fractal row leads somewhere rather than holding a value you would step through
                // twenty-six times, so entering it opens the list.
                if (_row < _options.Length && ReferenceEquals(_options[_row], _fractal))
                {
                    _page = Page.Fractals;
                    _row = _fractal.Index;
                    return MenuAction.Consumed;
                }

                // On any other settings row, Enter means "done"; on an action row it runs the action.
                if (_row < _options.Length) { Close(); return MenuAction.Resume; }
                var action = Actions[_row - _options.Length].Action;
                if (action != MenuAction.Quit) Close();
                return action;

            case Key.Escape:
                Close();
                return MenuAction.Resume;

            default:
                return MenuAction.None;
        }
    }

    private void Step(int delta)
    {
        if (_row >= _options.Length) return; // action rows have no value to change
        var option = _options[_row];
        option.Index = (option.Index + delta + option.Choices.Length) % option.Choices.Length;
        if (ReferenceEquals(option, _detail)) CapSpeedForDetail();
    }

    /// <summary>
    /// Supersampling multiplies kernel cost by the square of the detail factor, and the zoom rate is
    /// held steady, so at the normal speed a super-crisp descent becomes unsustainable within seconds
    /// and ends almost immediately. The speed row is therefore pulled down to something the setting
    /// can actually hold — visibly, in the menu, rather than by a hidden factor, so it stays
    /// overridable and the UI never disagrees with what is happening.
    /// </summary>
    private void CapSpeedForDetail()
    {
        double detail = _detail.Value;
        if (detail <= 1.0) return;

        double cap = detail >= 1.8 ? 0.10 : 0.18;
        if (_speed.Value > cap) _speed.Index = Nearest(_speed, cap);
    }

    public void Draw(SKCanvas canvas, int width, int height, float dpi)
    {
        _title.Size = 30 * dpi;
        _body.Size = 17 * dpi;
        _small.Size = 13 * dpi;

        if (_page == Page.Fractals) { DrawFractals(canvas, width, height, dpi); return; }

        float pad = 34 * dpi;
        float rowHeight = 34 * dpi;
        // Wide enough that the longest choice — the fractal names run to twenty-odd characters —
        // still leaves the value column clear of the labels.
        float panelWidth = Math.Min(860 * dpi, width * 0.94f);
        float extraLines = _page == Page.Still ? 3 : 0;
        float panelHeight = pad * 2 + _title.Size * 2.6f + TotalRows * rowHeight
                            + (96 + extraLines * 20) * dpi;
        float x = (width - panelWidth) * 0.5f;
        float y = (height - panelHeight) * 0.5f;

        // Dim the preview so the text stays readable over any part of the set.
        canvas.DrawRect(SKRect.Create(0, 0, width, height), new SKPaint { Color = new SKColor(0, 0, 0, 120) });

        var panel = new SKRoundRect(SKRect.Create(x, y, panelWidth, panelHeight), 14 * dpi);
        canvas.DrawRoundRect(panel, _panel);
        _edge.StrokeWidth = dpi;
        canvas.DrawRoundRect(panel, _edge);

        float left = x + pad;
        float right = x + panelWidth - pad;
        float cursor = y + pad + _title.Size;

        canvas.DrawText(Title, left, cursor, _title, _text);
        cursor += _small.Size * 1.8f;
        canvas.DrawText(Subtitle, left, cursor, _small, _muted);
        cursor += _body.Size * 1.9f;

        // Every row's rectangle is kept so the mouse can find it again. The arrows get their own,
        // because clicking one has to step that way rather than just forward.
        _hits = new Hit[TotalRows];

        // The arrows sit in fixed columns, the same on every row, with the value centred between
        // them. Right-aligning the value instead puts each row's pair of arrows somewhere different,
        // which looks ragged and gives the pointer a moving target.
        float arrow = _body.MeasureText("›");
        float gap = 12 * dpi;
        float widest = 0;
        foreach (var candidate in _options)
        {
            foreach (string choice in candidate.Choices)
                widest = Math.Max(widest, _body.MeasureText(choice));
        }

        widest = Math.Min(widest, panelWidth * 0.52f);

        // Kept a clear arrow's width inside the padding, so the whole glyph and the whole of its
        // click target are on the panel rather than half off the edge of it.
        float rightArrowX = right - arrow;
        float leftArrowX = rightArrowX - arrow - widest - gap * 2;
        float valueCentre = (leftArrowX + arrow + gap + rightArrowX - gap) * 0.5f;
        float hitWidth = arrow + gap * 1.4f;

        for (int i = 0; i < _options.Length; i++)
        {
            var option = _options[i];
            bool selected = i == _row;
            float baseline = cursor + _body.Size * 0.75f;
            var rowRect = SKRect.Create(left - 12 * dpi, cursor - rowHeight * 0.18f,
                panelWidth - pad * 2 + 24 * dpi, rowHeight * 0.96f);

            if (selected) canvas.DrawRoundRect(new SKRoundRect(rowRect, 6 * dpi), _bar);

            canvas.DrawText(option.Label, left, baseline, _body, selected ? _text : _muted);

            // Arrows are always drawn, not only on the focused row: they are what tells you the row
            // can be clicked, and a pointer needs to see the target before it aims at it.
            string value = option.Choice;
            float valueWidth = _body.MeasureText(value);

            canvas.DrawText(value, valueCentre - valueWidth * 0.5f, baseline, _body,
                selected ? _accent : _muted);
            canvas.DrawText("‹", leftArrowX, baseline, _body, selected ? _accent : _dim);
            canvas.DrawText("›", rightArrowX, baseline, _body, selected ? _accent : _dim);

            _hits[i] = new Hit
            {
                Row = rowRect,
                Left = SKRect.Create(leftArrowX - gap * 0.7f, rowRect.Top, hitWidth, rowRect.Height),
                Right = SKRect.Create(rightArrowX - gap * 0.7f, rowRect.Top, hitWidth, rowRect.Height),
            };

            cursor += rowHeight;
        }

        cursor += 6 * dpi;
        for (int i = 0; i < Actions.Length; i++)
        {
            int row = _options.Length + i;
            bool selected = row == _row;
            float baseline = cursor + _body.Size * 0.75f;
            string label = Actions[i].Action == MenuAction.Resume && !HasStarted && _page == Page.Settings
                ? "Start"
                : Actions[i].Label;
            var rowRect = SKRect.Create(left - 12 * dpi, cursor - rowHeight * 0.18f,
                panelWidth - pad * 2 + 24 * dpi, rowHeight * 0.96f);

            if (selected) canvas.DrawRoundRect(new SKRoundRect(rowRect, 6 * dpi), _bar);

            canvas.DrawText(selected ? $"› {label}" : $"  {label}", left, baseline, _body,
                selected ? _accent : _muted);

            _hits[row] = new Hit { Row = rowRect };
            cursor += rowHeight;
        }

        cursor += 12 * dpi;
        if (_row < _options.Length)
        {
            canvas.DrawText(_options[_row].Hint, left, cursor, _small, _muted);
            if (!string.IsNullOrEmpty(_options[_row].Hint2))
            {
                cursor += _small.Size * 1.5f;
                canvas.DrawText(_options[_row].Hint2, left, cursor, _small, _muted);
            }
            cursor += _small.Size * 2.2f;
        }
        else
        {
            cursor += _small.Size * 1.2f;
        }

        if (_page == Page.Still)
        {
            canvas.DrawText(StillSummary, left, cursor, _small, _text);
            cursor += _small.Size * 1.5f;
            canvas.DrawText(StillDestination, left, cursor, _small, _muted);
            cursor += _small.Size * 1.5f;
            canvas.DrawText(StillStatus, left, cursor, _small, _accent);
            cursor += _small.Size * 2.0f;
        }

        canvas.DrawText(
            "↑↓ or hover  choose    ←→ or click  change    enter  select    esc  " +
            (_page == Page.Still ? "back" : "resume"),
            left, cursor, _small, _accent);
    }

    /// <summary>
    /// Every fractal at once, in two columns, grouped by which renderer draws it — the three groups
    /// behave differently enough (how deep they go, whether they descend by themselves) that which
    /// group a name is in is worth seeing while choosing.
    /// </summary>
    private void DrawFractals(SKCanvas canvas, int width, int height, float dpi)
    {
        int count = FractalKind.All.Length;
        int perColumn = (count + FractalColumns - 1) / FractalColumns;

        float pad = 30 * dpi;
        float rowHeight = 27 * dpi;
        float panelWidth = Math.Min(880 * dpi, width * 0.96f);
        float panelHeight = pad * 2 + _title.Size * 2.4f + perColumn * rowHeight + 52 * dpi;
        float x = (width - panelWidth) * 0.5f;
        float y = (height - panelHeight) * 0.5f;

        canvas.DrawRect(SKRect.Create(0, 0, width, height), new SKPaint { Color = new SKColor(0, 0, 0, 140) });

        var panel = new SKRoundRect(SKRect.Create(x, y, panelWidth, panelHeight), 14 * dpi);
        canvas.DrawRoundRect(panel, _panel);
        _edge.StrokeWidth = dpi;
        canvas.DrawRoundRect(panel, _edge);

        float left = x + pad;
        float cursor = y + pad + _title.Size;

        canvas.DrawText("Choose a fractal", left, cursor, _title, _text);
        cursor += _small.Size * 1.7f;
        canvas.DrawText(
            "Fields are coloured by the kernel · drawn are geometry · 3D are ray-marched on the card",
            left, cursor, _small, _muted);
        cursor += _body.Size * 1.6f;

        float columnWidth = (panelWidth - pad * 2) / FractalColumns;
        _hits = new Hit[count];

        for (int i = 0; i < count; i++)
        {
            int column = i / perColumn;
            int slot = i % perColumn;
            float itemX = left + column * columnWidth;
            float itemY = cursor + slot * rowHeight;
            var rect = SKRect.Create(itemX - 8 * dpi, itemY - rowHeight * 0.72f,
                columnWidth - 10 * dpi, rowHeight * 0.94f);

            bool selected = i == _row;
            bool current = i == _fractal.Index;
            if (selected) canvas.DrawRoundRect(new SKRoundRect(rect, 5 * dpi), _bar);

            var kind = FractalKind.All[i];
            string mark = current ? "•" : " ";
            canvas.DrawText($"{mark} {kind.Name}", itemX, itemY, _body,
                selected ? _text : current ? _accent : _muted);

            // The group, in the right-hand margin of the column, so the list stays one flat list
            // while still saying which renderer each name belongs to.
            string group = kind.Style switch
            {
                RenderStyle.Drawn => "drawn",
                RenderStyle.Raymarched => "3D",
                _ => kind.Perturbable ? "field, deep" : "field",
            };
            float groupWidth = _small.MeasureText(group);
            canvas.DrawText(group, itemX + columnWidth - groupWidth - 22 * dpi, itemY, _small, _dim);

            _hits[i] = new Hit { Row = rect };
        }

        cursor += perColumn * rowHeight + _small.Size * 0.6f;
        canvas.DrawText("↑↓←→ or hover  choose    enter or click  switch to it    esc  back",
            left, cursor, _small, _accent);
    }

    /// <summary>A row's clickable area, and the two arrows within it that step a value each way.</summary>
    private struct Hit
    {
        public SKRect Row;
        public SKRect Left;
        public SKRect Right;
    }

    /// <summary>
    /// Set on every draw, so hit testing is against what is actually on screen rather than against a
    /// second copy of the layout arithmetic that could drift out of step with it.
    /// </summary>
    private Hit[] _hits = [];

    /// <summary>
    /// Moves the focus to whatever row is under the pointer. Hovering rather than clicking to focus,
    /// because the arrows only make sense next to the row they belong to.
    /// </summary>
    public void HandleMouseMove(float x, float y)
    {
        if (!Open) return;

        for (int i = 0; i < _hits.Length; i++)
        {
            if (_hits[i].Row.Contains(x, y)) { _row = i; return; }
        }
    }

    /// <summary>
    /// Acts on a click. On a value row: the arrows step their own way and anywhere else steps
    /// forward, which is what a click on a choice usually means. On an action row it runs the action.
    /// A click outside the panel is left alone rather than being treated as dismissal — the panel
    /// covers the whole window with a dim, so there is nothing meaningful to click past it onto.
    /// </summary>
    public MenuAction HandleClick(float x, float y)
    {
        if (!Open) return MenuAction.None;

        for (int i = 0; i < _hits.Length; i++)
        {
            if (!_hits[i].Row.Contains(x, y)) continue;

            _row = i;

            if (_page == Page.Fractals)
            {
                _fractal.Index = i;
                Close();
                return MenuAction.PickFractal;
            }

            if (i >= _options.Length)
            {
                var action = Actions[i - _options.Length].Action;
                if (action != MenuAction.Quit && action != MenuAction.RenderStill) Close();
                return action;
            }

            bool onArrow = _hits[i].Left.Contains(x, y) || _hits[i].Right.Contains(x, y);

            // Clicking the fractal row anywhere but its arrows opens the list of all of them: with
            // twenty-six to choose from, stepping one at a time is not the way in.
            if (ReferenceEquals(_options[i], _fractal) && !onArrow)
            {
                _page = Page.Fractals;
                _row = _fractal.Index;
                return MenuAction.Consumed;
            }

            Step(_hits[i].Left.Contains(x, y) ? -1 : 1);
            return MenuAction.Consumed;
        }

        return MenuAction.None;
    }

    /// <summary>Wheel over the panel steps the row under the pointer, which is what a wheel is for.</summary>
    public MenuAction HandleScroll(float notches)
    {
        if (!Open || notches == 0) return MenuAction.None;
        Step(notches > 0 ? 1 : -1);
        return MenuAction.Consumed;
    }

    public void Dispose()
    {
        _title.Dispose();
        _body.Dispose();
        _small.Dispose();
        _panel.Dispose();
        _edge.Dispose();
        _bar.Dispose();
        _text.Dispose();
        _muted.Dispose();
        _accent.Dispose();
        _dim.Dispose();
    }
}
