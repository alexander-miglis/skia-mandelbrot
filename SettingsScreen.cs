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
        public int Index;

        public double Value => Values[Index];
        public string Choice => Choices[Index];
    }

    private readonly Option[] _options =
    [
        new()
        {
            Label = "Detail",
            Choices = ["Super crisp", "Sharper", "Native", "High", "Balanced", "Fast", "Fastest"],
            Values = [2.0, 1.4, 1.0, 0.85, 0.7, 0.5, 0.35],
            Index = 2,
            Hint = "Pixels the kernel computes, against the window. Above Native it supersamples,"
        },
        new()
        {
            Label = "Zoom speed",
            Choices = ["Drifting", "Slow", "Steady", "Brisk", "Fast", "Headlong"],
            Values = [0.10, 0.18, 0.25, 0.45, 0.80, 1.60],
            Index = 2,
            Hint = "Held constant while the descent lasts. Slower also means a descent gets",
        },
        new()
        {
            Label = "Motion sharpness",
            Choices = ["Crisp", "Balanced", "Loose"],
            Values = [1.15, 1.30, 1.60],
            Index = 1,
            Hint = "How much a frame may be stretched while the next one computes. Crisp stays",
        },
        new()
        {
            Label = "Colours",
            Choices = ["Cycle", "Electric", "Ember", "Aurora", "Abyss", "Copper"],
            Values = [-1, 0, 1, 2, 3, 4],
            Hint = "Cycle picks a new gradient for each descent.",
        },
        new()
        {
            Label = "Readout",
            Choices = ["Shown", "Hidden"],
            Values = [1, 0],
            Hint = "The depth and timing figures in the corner. H toggles them any time.",
        },
    ];

    /// <summary>Rows below the settings that do something rather than hold a value.</summary>
    private static readonly (string Label, MenuAction Action)[] Actions =
    [
        ("Resume", MenuAction.Resume),
        ("Start a new descent", MenuAction.NewDescent),
        ("Exit", MenuAction.Quit),
    ];

    private static readonly string[] SecondLine =
    [
        "resolving detail finer than a pixel; below it, descents reach much deeper.",
        "further before the kernel outgrows it.",
        "sharpest; Loose trades that for depth.",
        "",
        "",
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

    private int _row;

    public SettingsScreen(SKTypeface typeface)
    {
        _title = new SKFont(typeface, 13f);
        _body = new SKFont(typeface, 13f);
        _small = new SKFont(typeface, 13f);
    }

    public bool Open { get; private set; } = true;

    /// <summary>False until the first time the menu is dismissed, which changes the Resume label.</summary>
    public bool HasStarted { get; private set; }

    private int TotalRows => _options.Length + Actions.Length;

    public double Quality => _options[0].Value;
    public double Speed => _options[1].Value;
    public double Drift => _options[2].Value;

    /// <summary>Index into the gradient list, or -1 to keep changing it every descent.</summary>
    public int Palette => (int)_options[3].Value;

    public bool ShowHud => _options[4].Value > 0.5;

    /// <summary>Reopens the menu, syncing the readout row to whatever the H key last left it at.</summary>
    public void Show(bool hudVisible)
    {
        _options[4].Index = hudVisible ? 0 : 1;
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
    public void Preselect(double quality, double speed, double drift)
    {
        _options[0].Index = Nearest(_options[0], quality);
        _options[1].Index = Nearest(_options[1], speed);
        _options[2].Index = Nearest(_options[2], drift);
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
                // On a settings row, Enter means "done"; on an action row it runs the action.
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
        if (_row == 0) CapSpeedForDetail();
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
        double detail = _options[0].Value;
        if (detail <= 1.0) return;

        double cap = detail >= 1.8 ? 0.10 : 0.18;
        if (_options[1].Value > cap) _options[1].Index = Nearest(_options[1], cap);
    }

    public void Draw(SKCanvas canvas, int width, int height, float dpi)
    {
        _title.Size = 30 * dpi;
        _body.Size = 17 * dpi;
        _small.Size = 13 * dpi;

        float pad = 34 * dpi;
        float rowHeight = 34 * dpi;
        float panelWidth = Math.Min(760 * dpi, width * 0.86f);
        float panelHeight = pad * 2 + _title.Size * 2.6f + TotalRows * rowHeight + 96 * dpi;
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

        canvas.DrawText("Fractal Zoom", left, cursor, _title, _text);
        cursor += _small.Size * 1.8f;
        canvas.DrawText("An endless descent into the Mandelbrot set. Choose how it renders.",
            left, cursor, _small, _muted);
        cursor += _body.Size * 1.9f;

        for (int i = 0; i < _options.Length; i++)
        {
            var option = _options[i];
            bool selected = i == _row;
            float baseline = cursor + _body.Size * 0.75f;

            if (selected)
            {
                canvas.DrawRoundRect(
                    new SKRoundRect(SKRect.Create(left - 12 * dpi, cursor - rowHeight * 0.18f,
                        panelWidth - pad * 2 + 24 * dpi, rowHeight * 0.96f), 6 * dpi),
                    _bar);
            }

            canvas.DrawText(option.Label, left, baseline, _body, selected ? _text : _muted);

            string value = selected ? $"‹ {option.Choice} ›" : option.Choice;
            float valueWidth = _body.MeasureText(value);
            canvas.DrawText(value, right - valueWidth, baseline, _body, selected ? _accent : _muted);

            cursor += rowHeight;
        }

        cursor += 6 * dpi;
        for (int i = 0; i < Actions.Length; i++)
        {
            bool selected = _options.Length + i == _row;
            float baseline = cursor + _body.Size * 0.75f;
            string label = Actions[i].Action == MenuAction.Resume && !HasStarted ? "Start" : Actions[i].Label;

            if (selected)
            {
                canvas.DrawRoundRect(
                    new SKRoundRect(SKRect.Create(left - 12 * dpi, cursor - rowHeight * 0.18f,
                        panelWidth - pad * 2 + 24 * dpi, rowHeight * 0.96f), 6 * dpi),
                    _bar);
            }

            canvas.DrawText(selected ? $"› {label}" : $"  {label}", left, baseline, _body,
                selected ? _accent : _muted);
            cursor += rowHeight;
        }

        cursor += 12 * dpi;
        if (_row < _options.Length)
        {
            canvas.DrawText(_options[_row].Hint, left, cursor, _small, _muted);
            if (!string.IsNullOrEmpty(SecondLine[_row]))
            {
                cursor += _small.Size * 1.5f;
                canvas.DrawText(SecondLine[_row], left, cursor, _small, _muted);
            }
            cursor += _small.Size * 2.2f;
        }
        else
        {
            cursor += _small.Size * 1.2f;
        }

        canvas.DrawText("↑↓ choose    ←→ change    enter  select    esc  resume",
            left, cursor, _small, _accent);
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
    }
}
