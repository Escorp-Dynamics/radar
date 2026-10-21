using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Radar.ViewModels;

namespace Radar.Views;

internal static class ChartPalette
{
    /// <summary>Подсвеченный элемент графика сменился (касанием или мышью) — для зеркала на внешнем экране.</summary>
    public static event Action<Control>? HoverChanged;
    public static void RaiseHover(Control c) => HoverChanged?.Invoke(c);

    public static readonly Color Uav = Color.Parse("#F5A04A");
    public static readonly Color Missile = Color.Parse("#FF6B6B");
    public static readonly Color Aviation = Color.Parse("#C98BFF");
    public static readonly Color Other = Color.Parse("#7C8A9C");
    public static readonly IBrush Grid = new ImmutableSolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Label = new ImmutableSolidColorBrush(Color.FromRgb(0x7F, 0x8B, 0x9B));
    public static readonly IBrush TipBg = new ImmutableSolidColorBrush(Color.FromArgb(0xF0, 0x07, 0x09, 0x0D));
    public static readonly IBrush TipFg = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF1));
    public static readonly Typeface Face = new("avares://Radar.Core/Assets/Fonts#Jura", FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Body = new("avares://Radar.Core/Assets/Fonts#IBM Plex Sans");

    public static FormattedText Text(string s, double size, IBrush brush, Typeface? face = null) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Face, size, brush);

    public static void Tip(DrawingContext ctx, Rect bounds, Point at, string text)
    {
        var t = Text(text, 11, TipFg, Body);
        double w = t.Width + 14, h = t.Height + 8;
        double x = Math.Clamp(at.X - w / 2, 0, Math.Max(0, bounds.Width - w));
        double y = Math.Max(0, at.Y - h - 6);
        ctx.FillRectangle(TipBg, new Rect(x, y, w, h));
        ctx.DrawText(t, new Point(x + 7, y + 4));
    }
}

/// <summary>Тревоги по дням: столбцы с сегментами по группам угроз, подписи дат, подсказка при наведении.</summary>
public sealed class DaysChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<StatsDay>?> DaysProperty = AvaloniaProperty.Register<DaysChart, IReadOnlyList<StatsDay>?>(nameof(Days));
    public IReadOnlyList<StatsDay>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    private int _hover = -1;
    /// <summary>Подсвеченный столбец (−1 — нет).</summary>
    public int Hover { get => _hover; set { if (_hover == value) return; _hover = value; InvalidateVisual(); ChartPalette.RaiseHover(this); } }
    private double _lastLabelX;
    // по DayOfWeek (воскресенье — 0): не зависит от культуры системы
    private static readonly string[] DayNames = ["вс", "пн", "вт", "ср", "чт", "пт", "сб"];

    static DaysChart() => AffectsRender<DaysChart>(DaysProperty);

    private static readonly IBrush PeakBrush = new ImmutableSolidColorBrush(Color.Parse("#F2C14E"));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Days is not { Count: > 0 } d) return;
        double bw = Bounds.Width / d.Count;
        int i = Math.Clamp((int)(e.GetPosition(this).X / bw), 0, d.Count - 1);
        Hover = i;
    }

    // касание показывает подсказку столбца (как наведение мышью) и оставляет её, пока не коснутся другого места
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); OnPointerMoved(e); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (e.Pointer.Type == PointerType.Touch) return; Hover = -1; }

    public override void Render(DrawingContext ctx)
    {
        if (Days is not { Count: > 0 } days) return;
        const double Axis = 16;
        double w = Bounds.Width, h = Bounds.Height - Axis;
        if (w < 1 || h < 8) return;
        for (int g = 1; g <= 3; g++) ctx.FillRectangle(ChartPalette.Grid, new Rect(0, Math.Round(h - h * g / 4), w, 1));
        double bw = w / days.Count, gap = bw > 6 ? Math.Max(1, bw * 0.18) : 0;
        int labelEvery = Math.Max(1, (int)Math.Ceiling(30 / Math.Max(1, bw)));
        _lastLabelX = double.NegativeInfinity;
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            double x = i * bw, y = h;
            // подсветка после смены периода: прежний индекс столбца может быть вне нового ряда
            double a = i == _hover || _hover < 0 || _hover >= days.Count ? 1 : 0.55;
            for (int si = 0; si < 4; si++)
            {
                var (v, c) = si switch { 0 => (d.Uav, ChartPalette.Uav), 1 => (d.Missile, ChartPalette.Missile), 2 => (d.Aviation, ChartPalette.Aviation), _ => (d.Other, ChartPalette.Other) };
                if (v <= 0) continue;
                double sh = v * (h - 4);
                ctx.FillRectangle(new ImmutableSolidColorBrush(c, a * 0.9), new Rect(x + gap / 2, y - sh, Math.Max(1, bw - gap), sh));
                y -= sh;
            }
            if (d.Peak)
                ctx.FillRectangle(PeakBrush, new Rect(x + gap / 2, y - 3, Math.Max(1, bw - gap), 2));
            // недельный ряд подписан месяцами — у их первых недель, без прореживания по индексу
            bool weeklyLabel = d.Label.Length > 0 && !char.IsDigit(d.Label[0]);
            if (weeklyLabel ? x - _lastLabelX >= 34 || i == 0 : i % labelEvery == 0)
            {
                var label = i == 0 && d.Label.Length > 0 && char.IsDigit(d.Label[0]) ? $"{d.Day:dd.MM}" : d.Label;
                var t = ChartPalette.Text(label, 9, ChartPalette.Label);
                if (label.Length > 0 && x + bw / 2 - t.Width / 2 >= 0 && x + bw / 2 + t.Width / 2 <= w) { ctx.DrawText(t, new Point(x + bw / 2 - t.Width / 2, h + 3)); _lastLabelX = x; }
            }
        }
        if (_hover >= 0 && _hover < days.Count)
        {
            var d = days[_hover];
            ChartPalette.Tip(ctx, Bounds, new Point(_hover * bw + bw / 2, 18), d.Tip.Length > 0 ? d.Tip : $"{d.Day:dd.MM}, {DayNames[(int)d.Day.DayOfWeek]}: {d.Total} тревог");
        }
    }
}

/// <summary>Тепловая сетка «день недели × час суток».</summary>
public sealed class WeekHourChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<double>>?> CellsProperty =
        AvaloniaProperty.Register<WeekHourChart, IReadOnlyList<IReadOnlyList<double>>?>(nameof(Cells));
    public IReadOnlyList<IReadOnlyList<double>>? Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    private static readonly string[] Dn = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];
    private (int D, int H)? _hover;
    /// <summary>Подсвеченная ячейка (день недели, час) или null.</summary>
    public (int D, int H)? Hover { get => _hover; set { if (_hover == value) return; _hover = value; InvalidateVisual(); ChartPalette.RaiseHover(this); } }

    static WeekHourChart() => AffectsRender<WeekHourChart>(CellsProperty);

    private const double Left = 22, Bottom = 14;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        double cw = (Bounds.Width - Left) / 24, ch = (Bounds.Height - Bottom) / 7;
        // Floor, а не (int): левее/выше сетки (над подписями) получаются отрицательные индексы, а не 0
        int h = (int)Math.Floor((p.X - Left) / cw), d = (int)Math.Floor(p.Y / ch);
        Hover = h is >= 0 and < 24 && d is >= 0 and < 7 ? (d, h) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); OnPointerMoved(e); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (e.Pointer.Type == PointerType.Touch) return; Hover = null; }

    public override void Render(DrawingContext ctx)
    {
        if (Cells is not { Count: 7 } cells) return;
        double cw = (Bounds.Width - Left) / 24, ch = (Bounds.Height - Bottom) / 7;
        if (cw < 1 || ch < 1) return;
        var lo = Color.Parse("#1B2633");
        var mid = Color.Parse("#D9A441");
        var hi = Color.Parse("#E5484D");
        for (int d = 0; d < 7; d++)
        {
            ctx.DrawText(ChartPalette.Text(Dn[d], 9, ChartPalette.Label), new Point(0, d * ch + ch / 2 - 6));
            for (int h = 0; h < 24; h++)
            {
                double v = cells[d][h];
                var (a, z, t) = v < 0.5 ? (lo, mid, v / 0.5) : (mid, hi, (v - 0.5) / 0.5);
                var c = Color.FromRgb((byte)(a.R + (z.R - a.R) * t), (byte)(a.G + (z.G - a.G) * t), (byte)(a.B + (z.B - a.B) * t));
                ctx.FillRectangle(new ImmutableSolidColorBrush(c), new Rect(Left + h * cw + 0.5, d * ch + 0.5, cw - 1, ch - 1));
            }
        }
        for (int h = 0; h < 24; h += 3)
            ctx.DrawText(ChartPalette.Text($"{h:00}", 9, ChartPalette.Label), new Point(Left + h * cw, 7 * ch + 2));
        if (_hover is { } hv)
        {
            ctx.DrawRectangle(null, new Pen(ChartPalette.TipFg, 1), new Rect(Left + hv.H * cw, hv.D * ch, cw, ch));
            ChartPalette.Tip(ctx, Bounds, new Point(Left + hv.H * cw + cw / 2, hv.D * ch),
                $"{Dn[hv.D]}, {hv.H:00}:00–{(hv.H + 1) % 24:00} · {cells[hv.D][hv.H] * cells[hv.D][hv.H] * 100:0}% от пика");
        }
    }
}

/// <summary>Роза направлений захода: 8 секторов, длина — доля треков с этой стороны.</summary>
public sealed class SectorRose : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> SectorsProperty = AvaloniaProperty.Register<SectorRose, IReadOnlyList<double>?>(nameof(Sectors));
    public IReadOnlyList<double>? Sectors { get => GetValue(SectorsProperty); set => SetValue(SectorsProperty, value); }
    private static readonly string[] Names = ["С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ"];

    static SectorRose() => AffectsRender<SectorRose>(SectorsProperty);

    public override void Render(DrawingContext ctx)
    {
        double r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 14;
        if (r < 4) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var ring = new Pen(ChartPalette.Grid, 1);
        for (int k = 1; k <= 3; k++) ctx.DrawEllipse(null, ring, c, r * k / 3, r * k / 3);
        if (Sectors is not { Count: 8 } s) return;
        var fill = new ImmutableSolidColorBrush(Color.Parse("#F2C14E"), 0.55);
        var edge = new Pen(new ImmutableSolidColorBrush(Color.Parse("#F2C14E")), 1);
        for (int i = 0; i < 8; i++)
        {
            double a0 = (i * 45 - 20 - 90) * Math.PI / 180, a1 = (i * 45 + 20 - 90) * Math.PI / 180;
            double len = Math.Max(0.04, s[i]) * r;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(c, true);
                gc.LineTo(new Point(c.X + len * Math.Cos(a0), c.Y + len * Math.Sin(a0)));
                gc.ArcTo(new Point(c.X + len * Math.Cos(a1), c.Y + len * Math.Sin(a1)), new Size(len, len), 0, false, SweepDirection.Clockwise);
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(fill, edge, g);
            double am = (i * 45 - 90) * Math.PI / 180;
            var t = ChartPalette.Text(Names[i], 9, ChartPalette.Label);
            ctx.DrawText(t, new Point(c.X + (r + 8) * Math.Cos(am) - t.Width / 2, c.Y + (r + 8) * Math.Sin(am) - t.Height / 2));
        }
    }
}
