using Avalonia.Media;

namespace Radar.Map;

/// <summary>Сдержанная тёмная палитра в стиле редакционной инфографики.</summary>
public static class MapTheme
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));
    private static SolidColorBrush B(byte a, string hex)
    {
        var c = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    public static readonly Color Ink = Color.Parse("#0B0E13");

    public static readonly IBrush Sea = B("#0B0E13");
    public static readonly IBrush ForeignLand = B("#14181F");
    public static readonly IBrush RussiaLand = B("#1C222B");
    public static readonly IBrush Lake = B("#0B0E13");
    public static readonly IBrush Coast = B("#262D38");
    public static readonly IBrush River = B("#1A2531");
    public static readonly IBrush RegionBorder = B("#2E3643");
    public static readonly IBrush ForeignBorder = B("#1C222B");
    public static readonly IBrush RuBorder = B("#6B7686");
    public static readonly IBrush FrontLine = B(200, "#B4553F");
    public static readonly IBrush FrontText = B(210, "#9A5546");

    public static readonly IBrush CityDot = B("#9AA5B4");
    public static readonly IBrush CityText = B("#B4BECB");
    public static readonly IBrush MinorCityText = B("#7D8898");
    // пункты вне РФ: холодный приглушённый тон, в 2–3 раза тусклее российских (#B4BECB / #7D8898)
    public static readonly IBrush ForeignCityText = B("#4E5664");
    public static readonly IBrush ForeignMinorCityText = B("#3A414C");
    public static readonly IBrush RegionText = B("#4D5766");
    public static readonly IBrush CountryText = B("#3A424E");
    public static readonly IBrush SeaText = B("#2C3542");
    public static readonly IBrush TextHalo = B(235, "#0B0E13");

    public static readonly IBrush HudText = B("#8C97A7");
    public static readonly IBrush PanelBg = B(235, "#10141A");
    /// <summary>Непрозрачный фон плашек на карте: сквозь него не просвечивают подписи и линии.</summary>
    public static readonly IBrush PanelBgSolid = B("#10141A");
    public static readonly IBrush PanelBorder = B("#232A34");
}
