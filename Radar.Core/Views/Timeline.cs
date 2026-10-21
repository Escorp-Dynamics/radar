using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Radar.Views;

/// <summary>
/// Лента времени воспроизведения: гистограмма активности каналов, риски суток, пройденная часть и бегунок.
/// Перетаскивание и клик — перемотка; при наведении — подпись момента под указателем.
/// </summary>
public sealed class Timeline : Control
{
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<Timeline, double>(nameof(Position));
    public static readonly StyledProperty<double[]?> HistogramProperty = AvaloniaProperty.Register<Timeline, double[]?>(nameof(Histogram));
    public static readonly StyledProperty<DateTimeOffset> StartProperty = AvaloniaProperty.Register<Timeline, DateTimeOffset>(nameof(Start));
    public static readonly StyledProperty<DateTimeOffset> EndProperty = AvaloniaProperty.Register<Timeline, DateTimeOffset>(nameof(End));
    public static readonly StyledProperty<Color> AccentProperty = AvaloniaProperty.Register<Timeline, Color>(nameof(Accent), Color.FromRgb(0xF2, 0xC1, 0x4E));

    /// <summary>Положение воспроизведения, 0…1.</summary>
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    /// <summary>Активность по равным отрезкам периода (нормирована к 0…1).</summary>
    public double[]? Histogram { get => GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }
    public DateTimeOffset Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public DateTimeOffset End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>Перемотка: доля периода 0…1 (по отпусканию бегунка или клику).</summary>
    public event Action<double>? SeekRequested;

    private double? _drag;   // доля при перетаскивании (бегунок следует за указателем до отпускания)
    private double? _hover;

    static Timeline()
    {
        AffectsRender<Timeline>(PositionProperty, HistogramProperty, StartProperty, EndProperty, AccentProperty);
        CursorProperty.OverrideDefaultValue<Timeline>(new Cursor(StandardCursorType.Hand));
    }

    private const double Pad = 6;          // поле по краям — бегунок не обрезается
    private const double BarTop = 16;      // гистограмма над дорожкой
    private const double TrackY = 30;

    protected override Size MeasureOverride(Size availableSize) => new(0, 44);

    private double FracAt(double x) => Math.Clamp((x - Pad) / Math.Max(1, Bounds.Width - 2 * Pad), 0, 1);
    private double XAt(double f) => Pad + f * (Bounds.Width - 2 * Pad);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = FracAt(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double f = FracAt(e.GetPosition(this).X);
        _hover = f;
        if (_drag != null) _drag = f;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } f) return;
        _drag = null;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(f);
        // бегунок остаётся на месте до первого обновления после перемотки; SetCurrentValue не снимает привязку
        SetCurrentValue(PositionProperty, f);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // захват отобран (окно потеряло фокус и т. п.) — перетаскивание отменяется без перемотки
        if (_drag == null) return;
        _drag = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private static DateTimeOffset LocalMidnight(DateTime d) => new(d, TimeZoneInfo.Local.GetUtcOffset(d));

    private static readonly ImmutableSolidColorBrush TrackBrush = new(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly ImmutableSolidColorBrush TickBrush = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly ImmutableSolidColorBrush LabelBrush = new(Color.FromRgb(0x7F, 0x8B, 0x9B));
    private static readonly ImmutableSolidColorBrush TipBg = new(Color.FromArgb(0xEE, 0x07, 0x09, 0x0D));
    private static readonly ImmutableSolidColorBrush TipText = new(Color.FromRgb(0xE8, 0xEC, 0xF1));
    private static readonly ImmutablePen KnobPen = new(new ImmutableSolidColorBrush(Color.FromRgb(0x07, 0x09, 0x0D)), 1);
    private static readonly ImmutableSolidColorBrush AheadBrush = new(Color.FromArgb(0x38, 0xB4, 0xBE, 0xCB));
    private readonly Dictionary<int, FormattedText> _dayLabels = [];
    private Color _brushColor;
    private ImmutableSolidColorBrush? _doneBrush, _accentBrush, _glowBrush;

    private void EnsureBrushes(Color accent)
    {
        if (_accentBrush != null && _brushColor == accent) return;
        _brushColor = accent;
        _accentBrush = new ImmutableSolidColorBrush(accent);
        _doneBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        _glowBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
    }

    private FormattedText DayLabel(int day) =>
        _dayLabels.TryGetValue(day, out var t) ? t
        : _dayLabels[day] = new FormattedText(day.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9, LabelBrush);

    private static readonly Typeface Face = new("avares://Radar.Core/Assets/Fonts#Jura", FontStyle.Normal, FontWeight.Bold);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        if (w < 2 * Pad + 10) return;
        double span = (End - Start).TotalSeconds;
        double pos = Math.Clamp(_drag ?? Position, 0, 1);
        double px = XAt(pos);
        var accent = Accent;
        EnsureBrushes(accent);

        // гистограмма активности: пройденная часть — цветом архива, впереди — серым
        if (Histogram is { Length: > 0 } hist)
        {
            double bw = (w - 2 * Pad) / hist.Length;
            var done = _doneBrush!;
            var ahead = AheadBrush;
            for (int i = 0; i < hist.Length; i++)
            {
                double v = Math.Clamp(hist[i], 0, 1);
                if (v <= 0) continue;
                double h = Math.Max(1, v * (TrackY - BarTop - 2));
                double x = Pad + i * bw;
                ctx.FillRectangle(x + bw / 2 <= px ? done : ahead, new Rect(x, TrackY - 2 - h, Math.Max(1, bw - (bw > 3 ? 1 : 0)), h));
            }
        }

        // риски суток (по местному времени); подписи — числа месяца, сколько поместится
        if (span > 0)
        {
            var local = Start.ToLocalTime();
            var day = LocalMidnight(local.Date.AddDays(1));
            int days = (int)Math.Ceiling(span / 86400);
            int labelEvery = Math.Max(1, (int)Math.Ceiling(28 / Math.Max(1, (w - 2 * Pad) / Math.Max(1, days))));
            for (int k = 0; day < End; day = LocalMidnight(day.ToLocalTime().Date.AddDays(1)), k++)
            {
                double x = XAt((day - Start).TotalSeconds / span);
                ctx.FillRectangle(TickBrush, new Rect(Math.Round(x), TrackY - 4, 1, 4));
                if (day.Day % labelEvery == 0 || labelEvery == 1)
                {
                    var t = DayLabel(day.Day);
                    if (x - t.Width / 2 > Pad && x + t.Width / 2 < w - Pad) ctx.DrawText(t, new Point(x - t.Width / 2, TrackY + 4));
                }
            }
        }

        // дорожка и пройденная часть
        ctx.FillRectangle(TrackBrush, new Rect(Pad, TrackY - 1, w - 2 * Pad, 2));
        ctx.FillRectangle(_accentBrush!, new Rect(Pad, TrackY - 1, Math.Max(0, px - Pad), 2));

        // бегунок: ромб с ореолом
        ctx.DrawEllipse(_glowBrush!, null, new Point(px, TrackY), 7, 7);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(px, TrackY - 5), true);
            g.LineTo(new Point(px + 5, TrackY));
            g.LineTo(new Point(px, TrackY + 5));
            g.LineTo(new Point(px - 5, TrackY));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(_accentBrush!, KnobPen, geo);

        // подпись момента под указателем (или бегунком при перетаскивании)
        if ((_drag ?? _hover) is { } hf && span > 0)
        {
            var at = Start.AddSeconds(span * hf).ToLocalTime();
            var text = new FormattedText($"{at.Day} {Months[at.Month - 1]} {at:HH:mm}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 10, TipText);
            double hx = Math.Clamp(XAt(hf) - text.Width / 2 - 5, 0, Math.Max(0, w - text.Width - 10));
            var box = new Rect(hx, 0, text.Width + 10, 14);
            ctx.FillRectangle(TipBg, box);
            ctx.FillRectangle(_accentBrush!, new Rect(Math.Round(XAt(hf)), 14, 1, TrackY - 14 - 6));
            ctx.DrawText(text, new Point(hx + 5, 0.5));
        }
    }

    private static readonly string[] Months = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];
}
