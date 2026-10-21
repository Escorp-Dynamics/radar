using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Radar.Map;

namespace Radar.Views;

/// <summary>
/// Стеклянная панель «прибора» поверх карты: размытие подложки, тонировка, волосяная кромка, срезанные углы
/// и (по желанию) угловые метки с акцентом. Замена <c>Border</c> для всех панелей над картой.
/// </summary>
public class GlassPanel : Decorator
{
    public static readonly StyledProperty<double> ChamferProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Chamfer), 7);

    public static readonly StyledProperty<Color> TintProperty =
        AvaloniaProperty.Register<GlassPanel, Color>(nameof(Tint), Glass.Tint);

    public static readonly StyledProperty<double> BlurProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Blur), Glass.BlurSigma);

    public static readonly StyledProperty<bool> ShowBorderProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(ShowBorder), true);

    public static readonly StyledProperty<bool> BracketsProperty =
        AvaloniaProperty.Register<GlassPanel, bool>(nameof(Brackets), true);

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<GlassPanel, Color>(nameof(Accent), Glass.Accent);

    /// <summary>Мягкая тень вокруг панели (рисуется только снаружи, стекло не затемняет).</summary>
    public static readonly StyledProperty<BoxShadows> ShadowProperty =
        AvaloniaProperty.Register<GlassPanel, BoxShadows>(nameof(Shadow));

    /// <summary>Подсветка акцентом (непрозрачность 0…1) поверх стекла по его контуру — для кнопок-панелей вместо прямоугольной подложки.</summary>
    public static readonly StyledProperty<double> HighlightProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(Highlight));

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(ChamferProperty, TintProperty, BlurProperty, ShowBorderProperty, BracketsProperty, AccentProperty, ShadowProperty, HighlightProperty);
    }

    public double Chamfer { get => GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double Blur { get => GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public bool ShowBorder { get => GetValue(ShowBorderProperty); set => SetValue(ShowBorderProperty, value); }
    public bool Brackets { get => GetValue(BracketsProperty); set => SetValue(BracketsProperty, value); }
    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public BoxShadows Shadow { get => GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    private IBrush? _hlBrush;
    private byte _hlAlpha;

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        Glass.DrawShadow(context, r, Chamfer, Shadow);
        Glass.Draw(context, r, Chamfer, Tint, Blur, ShowBorder, Brackets, Accent);
        // подсветка наведения — по тому же контуру, что и стекло
        if (Highlight > 0.001)
            {
            byte ha = (byte)(Math.Clamp(Highlight, 0, 1) * 255);
            if (_hlBrush == null || _hlAlpha != ha) { _hlAlpha = ha; _hlBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(ha, 0x7C, 0xD4, 0xFF)); }
            context.DrawGeometry(_hlBrush, null, Glass.Shape(r, Chamfer));
        }
    }
}
