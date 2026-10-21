using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Radar.Views;

/// <summary>
/// Нижняя кромка заголовка — индикатор обмена данными. На линии всегда не больше одного бегущего блика:
/// при обычном опросе каналов — один проход слева направо (не чаще раза в несколько секунд),
/// при длительной загрузке (история, ручное обновление) — проходы повторяются без пауз, пока она идёт.
/// В покое — ровная тонкая линия без акцентов (чтобы не выглядело зависшей загрузкой); без связи блик серый.
/// </summary>
public sealed class LoadingLine : Control
{
    public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<LoadingLine, bool>(nameof(IsBusy));
    public static readonly StyledProperty<bool> IsOfflineProperty = AvaloniaProperty.Register<LoadingLine, bool>(nameof(IsOffline));

    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public bool IsOffline { get => GetValue(IsOfflineProperty); set => SetValue(IsOfflineProperty, value); }

    private static readonly Color Accent = Color.FromRgb(0x7C, 0xD4, 0xFF);
    private static readonly Color Muted = Color.FromRgb(0x6A, 0x76, 0x86);
    private const double SweepSeconds = 1.6;
    /// <summary>Обычные опросы идут почти непрерывно (каналы опрашиваются со сдвигом) — проходы не чаще этого.</summary>
    private const double RoutineGapSeconds = 6;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _sweepStart = double.NaN;
    private double _lastRoutine = double.NegativeInfinity;
    private bool _frameRequested;

    static LoadingLine()
    {
        AffectsRender<LoadingLine>(IsBusyProperty, IsOfflineProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<LoadingLine>(false);
    }

    /// <summary>Начат запрос к источнику: один проход блика, если сейчас ничего не бежит.</summary>
    public void Pulse()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (!double.IsNaN(_sweepStart) || now - _lastRoutine < RoutineGapSeconds) return;
        _lastRoutine = now;
        _sweepStart = now;
        RequestFrame();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsBusyProperty && IsBusy && double.IsNaN(_sweepStart))
        {
            _sweepStart = _clock.Elapsed.TotalSeconds;
            RequestFrame();
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        if (TopLevel.GetTopLevel(this) is not { } top) { InvalidateVisual(); return; }
        _frameRequested = true;
        top.RequestAnimationFrame(_ => { _frameRequested = false; InvalidateVisual(); });
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Math.Max(1, Bounds.Height);
        if (w < 2) return;
        double now = _clock.Elapsed.TotalSeconds;

        // проход закончился: при длительной загрузке — следующий сразу, иначе линия успокаивается
        if (!double.IsNaN(_sweepStart) && now - _sweepStart >= SweepSeconds)
            _sweepStart = IsBusy ? _sweepStart + SweepSeconds * Math.Floor((now - _sweepStart) / SweepSeconds) : double.NaN;
        bool sweeping = !double.IsNaN(_sweepStart);

        var color = IsOffline ? Muted : Accent;
        ctx.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), new Rect(0, 0, w, h));

        if (sweeping)
        {
            double k = (now - _sweepStart) / SweepSeconds;
            double e = 0.5 - 0.5 * Math.Cos(Math.PI * k); // плавный разгон и торможение
            double span = Math.Clamp(w * 0.28, 160, 420);
            double x = e * (w + span) - span;
            // мягкие оба конца, светлое ядро в середине
            var brush = new ImmutableLinearGradientBrush(
            [
                new ImmutableGradientStop(0, Color.FromArgb(0, color.R, color.G, color.B)),
                new ImmutableGradientStop(0.5, Color.FromArgb(255, color.R, color.G, color.B)),
                new ImmutableGradientStop(0.62, Color.FromArgb(255, 0xE6, 0xF6, 0xFF)),
                new ImmutableGradientStop(1, Color.FromArgb(0, color.R, color.G, color.B)),
            ], startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
            ctx.FillRectangle(brush, new Rect(x, 0, span, h));
            RequestFrame();
        }
    }
}
