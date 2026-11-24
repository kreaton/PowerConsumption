using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerConsumption.Wpf.Controls;

/// <summary>One bar: one value per series (stacked bottom-up), its x-axis label, and whether to draw it pale.</summary>
/// <param name="Values">One value per <see cref="BarChart.Series"/>, in the same order. Negative values stack downwards.</param>
/// <param name="Label">Text under the bar (shown for every n-th bar when space is short).</param>
/// <param name="Muted">Draw pale, e.g. for incomplete data; the legend then explains it with <see cref="BarChart.MutedLabel"/>.</param>
/// <param name="SeparatorBefore">Draw a thin vertical line before the bar (e.g. at each Monday).</param>
public sealed record ChartBar(double[] Values, string Label, bool Muted = false, bool SeparatorBefore = false)
{
    /// <summary>The height of the positive stack; used for the scale and the ▲ peak marker.</summary>
    public double Sum => Values.Where(v => v > 0).Sum();
}

/// <summary>A series in the stack, or the optional line: its legend label and colour.</summary>
public sealed record ChartSeries(string Label, Color Color);

/// <summary>A line drawn over the bars on its own axis at the right (e.g. temperature). Null values leave a gap.</summary>
/// <param name="Series">Legend label and colour.</param>
/// <param name="Values">One value per bar, drawn at the bar's centre.</param>
/// <param name="Unit">Shown in the legend, e.g. "°C". The right-axis ticks pick their decimals from the step size.</param>
public sealed record ChartLine(ChartSeries Series, double?[] Values, string Unit);

/// <summary>
/// A stacked bar chart that fills its area, drawn directly with <see cref="DrawingContext"/>.
/// Gridlines fall on round numbers, hovering a bar shows <see cref="TooltipText"/>, and
/// double-clicking a bar raises <see cref="BarActivated"/> (used to drill down).
/// </summary>
/// <remarks>
/// It is a <see cref="Control"/> rather than a FrameworkElement only to inherit
/// <see cref="Control.Foreground"/> and the font from the theme; it has no template. Text and grid
/// lines are drawn in the foreground colour at reduced opacity, so it works in light and dark mode.
/// Properties are plain CLR properties set from code; setting <see cref="Bars"/> or
/// <see cref="Line"/> redraws, so set the other properties first.
/// </remarks>
public sealed class BarChart : Control
{
    // Space around the plot area for the y-axis labels (left), legend (top), x labels (bottom)
    // and, when there is a line, its axis labels (right).
    private const double LeftMargin = 56, TopMargin = 32, BottomMargin = 30, RightMargin = 14, LineAxisWidth = 44;

    // One tooltip that follows the mouse; opened and positioned by hand in OnMouseMove rather than
    // through ToolTipService, which would only show it once per control. Monospaced, so callers
    // can line up columns with padding.
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private IReadOnlyList<ChartBar> _bars = [];
    private ChartLine? _line;

    /// <summary>Index of the bar under the mouse, or -1.</summary>
    private int _hover = -1;

    /// <summary>The plot area of the last render, used to map mouse positions to bars.</summary>
    private Rect _plot;

    public BarChart()
    {
        _tip.PlacementTarget = this;
        Focusable = false;
        SnapsToDevicePixels = true;
        FontSize = 12;
    }

    /// <summary>Series names and colours, bottom of the stack first; also the legend.</summary>
    public IReadOnlyList<ChartSeries> Series { get; set; } = [new("Value", Colors.SeaGreen)];

    /// <summary>Unit shown at the top right, e.g. "kWh".</summary>
    public string Unit { get; set; } = "";

    /// <summary>Number format of the ▲ peak marker.</summary>
    public string ValueFormat { get; set; } = "0.##";

    /// <summary>Legend text for pale bars; shown only when some bar is muted.</summary>
    public string? MutedLabel { get; set; }

    /// <summary>Shown in the middle when there are no bars.</summary>
    public string EmptyText { get; set; } = "No data.";

    /// <summary>Returns the tooltip text for a bar index; no tooltip when null.</summary>
    public Func<int, string>? TooltipText { get; set; }

    /// <summary>Raised with the bar's index when a bar is double-clicked.</summary>
    public event Action<int>? BarActivated;

    /// <summary>The bars, left to right. Setting them closes the tooltip and redraws.</summary>
    public IReadOnlyList<ChartBar> Bars
    {
        get => _bars;
        set
        {
            _bars = value;
            _hover = -1;
            _tip.IsOpen = false;
            InvalidateVisual();
        }
    }

    /// <summary>An optional line over the bars with its own right-hand axis; null for none.</summary>
    public ChartLine? Line
    {
        get => _line;
        set
        {
            _line = value;
            InvalidateVisual();
        }
    }

    // ---------------- drawing ----------------

    /// <summary>
    /// Draws, in order: legend, y labels, separators, hover highlight, the stacked bars, the y
    /// gridlines (over the bars), the ▲ peak marker, the x-axis and finally the optional line on top.
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        // A transparent fill so the whole area gets mouse events.
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(size));

        var ink = Foreground ?? Brushes.Black;
        var muted = WithOpacity(ink, 0.6);
        // Horizontal gridlines are drawn over the bars, so they are bold enough to read across them.
        var gridPen = new Pen(WithOpacity(ink, 0.45), 2);
        var zeroPen = new Pen(WithOpacity(ink, 0.75), 2);
        var separatorPen = new Pen(WithOpacity(ink, 0.12), 1);
        var axisPen = new Pen(WithOpacity(ink, 0.35), 1);
        gridPen.Freeze();
        zeroPen.Freeze();
        separatorPen.Freeze();
        axisPen.Freeze();

        DrawLegend(dc, ink, muted);

        var right = RightMargin + (_line is null ? 0 : LineAxisWidth);
        _plot = new Rect(LeftMargin, TopMargin,
            Math.Max(10, size.Width - LeftMargin - right), Math.Max(10, size.Height - TopMargin - BottomMargin));

        if (_bars.Count == 0)
        {
            var empty = Text(EmptyText, muted);
            dc.DrawText(empty, new Point(_plot.Left + (_plot.Width - empty.Width) / 2, _plot.Top + (_plot.Height - empty.Height) / 2));
            return;
        }

        // Scale: positive parts stack up from zero, negative ones (a negative spot price) down.
        var high = _bars.Max(b => b.Values.Where(v => v > 0).Sum());
        var low = _bars.Min(b => b.Values.Where(v => v < 0).Sum());
        var (yMin, yMax, step) = Scale(low, high, includeZero: true);
        double Y(double v) => _plot.Top + (yMax - v) / (yMax - yMin) * _plot.Height;

        var tickFormat = TickFormat(step);
        for (var v = yMin; v <= yMax + step / 1000; v += step)
        {
            var label = Text(v.ToString(tickFormat, CultureInfo.CurrentCulture), muted);
            dc.DrawText(label, new Point(_plot.Left - 6 - label.Width, Y(v) - label.Height / 2));
        }

        // Each bar owns an equal slot of the width. The gap between bars is a quarter of the slot
        // (at most 12 px), shrinking to a hairline when slots get narrower than 8 px.
        var slot = _plot.Width / _bars.Count;
        var gap = slot >= 8 ? Math.Min(slot * 0.25, 12) : Math.Min(1, slot * 0.15);

        for (var i = 1; i < _bars.Count; i++)
        {
            if (_bars[i].SeparatorBefore)
            {
                var x = Snap(_plot.Left + i * slot);
                dc.DrawLine(separatorPen, new Point(x, _plot.Top), new Point(x, _plot.Bottom));
            }
        }

        if (_hover >= 0 && _hover < _bars.Count)
            dc.DrawRectangle(WithOpacity(ink, 0.07), null, new Rect(_plot.Left + _hover * slot, _plot.Top, slot, _plot.Height));

        // Full and pale (muted) brush per series. The hovered bar is always drawn at full colour.
        var brushes = Series.Select(s => new SolidColorBrush(s.Color)).ToArray();
        var paleBrushes = Series.Select(s => new SolidColorBrush(Color.FromArgb(100, s.Color.R, s.Color.G, s.Color.B))).ToArray();

        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var x = _plot.Left + i * slot + gap / 2;
            var w = Math.Max(1, slot - gap);
            // Running tops of the positive and negative stacks.
            double up = 0, down = 0;
            for (var p = 0; p < bar.Values.Length && p < Series.Count; p++)
            {
                var value = bar.Values[p];
                if (value == 0 || double.IsNaN(value))
                    continue;
                var brush = bar.Muted && i != _hover ? paleBrushes[p] : brushes[p];
                if (value > 0)
                {
                    dc.DrawRectangle(brush, null, new Rect(x, Y(up + value), w, Math.Max(0, Y(up) - Y(up + value))));
                    up += value;
                }
                else
                {
                    dc.DrawRectangle(brush, null, new Rect(x, Y(down), w, Math.Max(0, Y(down + value) - Y(down))));
                    down += value;
                }
            }
        }

        // Horizontal gridlines on top of the bars; 2 px wide, so they sit on whole pixels.
        for (var v = yMin; v <= yMax + step / 1000; v += step)
        {
            var y = Math.Round(Y(v));
            dc.DrawLine(Math.Abs(v) < 1e-9 ? zeroPen : gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
        }

        // ▲ on the highest bar.
        if (_bars.Count > 1)
        {
            var peak = Enumerable.Range(0, _bars.Count).MaxBy(i => _bars[i].Sum);
            if (_bars[peak].Sum > 0)
            {
                var text = Text("▲ " + _bars[peak].Sum.ToString(ValueFormat, CultureInfo.CurrentCulture), ink);
                var cx = _plot.Left + (peak + 0.5) * slot;
                var tx = Math.Clamp(cx - text.Width / 2, _plot.Left, Math.Max(_plot.Left, _plot.Right - text.Width));
                dc.DrawText(text, new Point(tx, Math.Max(TopMargin - 14, Y(_bars[peak].Sum) - text.Height - 1)));
            }
        }

        dc.DrawLine(axisPen, new Point(_plot.Left, Snap(_plot.Bottom)), new Point(_plot.Right, Snap(_plot.Bottom)));
        DrawXAxis(dc, slot, muted);

        if (_line is not null)
            DrawLine(dc, slot, muted);
    }

    /// <summary>
    /// The optional line, scaled to its own data (not from zero, so small changes in temperature
    /// stay visible) with tick labels on the right. A null value breaks the line.
    /// </summary>
    private void DrawLine(DrawingContext dc, double slot, Brush muted)
    {
        var line = _line!;
        var values = line.Values.Take(_bars.Count).ToArray();
        var present = values.OfType<double>().ToList();
        if (present.Count == 0)
            return;

        var (yMin, yMax, step) = Scale(present.Min(), present.Max(), includeZero: false);
        double Y(double v) => _plot.Top + (yMax - v) / (yMax - yMin) * _plot.Height;

        var tickFormat = TickFormat(step);
        for (var v = yMin; v <= yMax + step / 1000; v += step)
        {
            var label = Text(v.ToString(tickFormat, CultureInfo.CurrentCulture), muted);
            dc.DrawText(label, new Point(_plot.Right + 6, Y(v) - label.Height / 2));
        }

        var brush = new SolidColorBrush(line.Series.Color);
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round };
        Point? previous = null;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } v)
            {
                previous = null;
                continue;
            }
            var point = new Point(_plot.Left + (i + 0.5) * slot, Y(v));
            if (previous is { } prev)
                dc.DrawLine(pen, prev, point);
            if (slot >= 6)
                dc.DrawEllipse(brush, null, point, 2.5, 2.5);
            previous = point;
        }
    }

    /// <summary>Legend across the top: a swatch per series, the muted swatch if used, the line, and the unit at the right.</summary>
    private void DrawLegend(DrawingContext dc, Brush ink, Brush muted)
    {
        double x = 4;
        var items = Series.Select(s => (s.Label, Brush: (Brush)new SolidColorBrush(s.Color))).ToList();
        if (MutedLabel is not null && _bars.Any(b => b.Muted))
        {
            var c = Series[0].Color;
            items.Add((MutedLabel, new SolidColorBrush(Color.FromArgb(100, c.R, c.G, c.B))));
        }
        foreach (var (label, brush) in items)
        {
            dc.DrawRectangle(brush, null, new Rect(x, 8, 11, 11));
            var text = Text(label, ink);
            dc.DrawText(text, new Point(x + 15, 13.5 - text.Height / 2));
            x += 15 + text.Width + 14;
        }

        if (_line is { } line)
        {
            var brush = new SolidColorBrush(line.Series.Color);
            dc.DrawLine(new Pen(brush, 2), new Point(x, 13.5), new Point(x + 14, 13.5));
            var text = Text($"{line.Series.Label} ({line.Unit}, right axis)", ink);
            dc.DrawText(text, new Point(x + 18, 13.5 - text.Height / 2));
        }

        var unit = Text(Unit, muted);
        dc.DrawText(unit, new Point(Math.Max(x, ActualWidth - RightMargin - unit.Width), 13.5 - unit.Height / 2));
    }

    /// <summary>The bar labels under the plot, centred under their bars.</summary>
    private void DrawXAxis(DrawingContext dc, double slot, Brush muted)
    {
        // Label every n-th bar so labels never overlap.
        var widest = _bars.Max(b => Text(b.Label, muted).Width) + 6;
        var every = Math.Max(1, (int)Math.Ceiling(widest / slot));
        for (var i = 0; i < _bars.Count; i += every)
        {
            var text = Text(_bars[i].Label, muted);
            dc.DrawText(text, new Point(_plot.Left + (i + 0.5) * slot - text.Width / 2, _plot.Bottom + 4));
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Text in the control's font, measured for the current monitor's DPI.</summary>
    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A frozen copy of a (theme) brush at the given opacity, for muted text and grid lines.</summary>
    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.CloneCurrentValue();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    /// <summary>Puts a 1 px line in the middle of a pixel so it is drawn crisp, not blurred over two.</summary>
    private static double Snap(double v) => Math.Round(v) + 0.5;

    /// <summary>
    /// An axis from <paramref name="low"/> to <paramref name="high"/> on round numbers: about five
    /// steps of 1, 2, 2.5 or 5 × 10ⁿ, with 4 % headroom above the highest value for the ▲ label.
    /// Bars include zero; a line need not.
    /// </summary>
    private static (double Min, double Max, double Step) Scale(double low, double high, bool includeZero)
    {
        if (includeZero)
        {
            low = Math.Min(0, low);
            high = Math.Max(0, high);
        }
        // A flat series (all equal, or all zero) still needs a non-empty axis.
        if (high - low < 1e-9)
        {
            high += 1;
            low -= includeZero ? 0 : 1;
        }
        var step = NiceStep((high - low) / 5);
        var max = Math.Ceiling(high * (high > 0 ? 1.04 : 1) / step) * step;
        var min = Math.Floor(low / step) * step;
        if (includeZero && max < step)
            max = step;
        return (min, max, step);
    }

    /// <summary>As many decimals as the step needs (2.5 → "0.#"), so no tick label is rounded.</summary>
    private static string TickFormat(double step)
    {
        static bool IsWhole(double x) => Math.Abs(x - Math.Round(x)) < 1e-9;
        return IsWhole(step) ? "#,0" : IsWhole(step * 10) ? "0.#" : "0.##";
    }

    /// <summary>Rounds a step size up to 1, 2, 2.5, 5 or 10 times a power of ten.</summary>
    private static double NiceStep(double raw)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / magnitude;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * magnitude;
    }

    /// <summary>The bar whose slot contains <paramref name="p"/>, or -1 outside the plot.</summary>
    private int IndexAt(Point p) =>
        _bars.Count > 0 && _plot.Contains(p)
            ? Math.Clamp((int)((p.X - _plot.Left) / (_plot.Width / _bars.Count)), 0, _bars.Count - 1)
            : -1;

    // ---------------- mouse ----------------

    /// <summary>Highlights the bar under the mouse and moves the tooltip along with it.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var index = IndexAt(p);

        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }

        if (index < 0 || TooltipText is null)
        {
            _tip.IsOpen = false;
            return;
        }

        // Only replace the content when it changes; resetting it on every move makes it flicker.
        var text = TooltipText(index);
        if (!Equals(_tip.Content, text))
            _tip.Content = text;
        _tip.HorizontalOffset = p.X + 16;
        _tip.VerticalOffset = p.Y + 14;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    /// <summary>Raises <see cref="BarActivated"/> for the bar double-clicked.</summary>
    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var index = IndexAt(e.GetPosition(this));
        if (index >= 0)
        {
            _tip.IsOpen = false;
            BarActivated?.Invoke(index);
        }
    }
}
