using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Radar.Map;

namespace Radar.Views;

/// <summary>
/// Контур кнопок и полей в стиле панелей: срезанные углы (слева сверху и справа снизу), волосяная кромка
/// и короткая акцентная засечка на верхней кромке. Основа шаблонов кнопок и выпадающего списка.
/// </summary>
public class HudFrame : Decorator
{
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<double> ChamferProperty = AvaloniaProperty.Register<HudFrame, double>(nameof(Chamfer), 6);

    static HudFrame() => AffectsRender<HudFrame>(FillProperty, StrokeProperty, AccentProperty, ChamferProperty);

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Chamfer { get => GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width < 2 || r.Height < 2) return;
        if (Fill != null) ctx.DrawGeometry(Fill, null, Glass.Shape(r, Chamfer));
        var inner = r.Deflate(0.5);
        if (Stroke != null) ctx.DrawGeometry(null, new Pen(Stroke, 1), Glass.Shape(inner, Math.Max(0, Chamfer - 0.2)));
        if (Accent != null)
        {
            double c = Math.Min(Chamfer, Math.Min(r.Width, r.Height) / 3);
            ctx.DrawLine(new Pen(Accent, 1.5), new Point(inner.X + c + 2, inner.Y), new Point(inner.X + c + Math.Min(14, inner.Width / 5), inner.Y));
        }
    }
}
