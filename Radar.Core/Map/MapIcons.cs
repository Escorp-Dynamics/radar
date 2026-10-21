using Avalonia;
using Avalonia.Media;
using Radar.Models;

namespace Radar.Map;

/// <summary>Векторные силуэты угроз в квадрате 24×24, нос направлен вверх (для поворота по курсу).</summary>
public static class MapIcons
{
    /// <summary>SVG-path данные по ключу иконки (см. <see cref="ThreatInfo.Icon"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        // БПЛА самолётного типа: дельтакрыл
        ["uav"] = "M12 1.5 L22.5 19 L13.6 16.6 L12 20.5 L10.4 16.6 L1.5 19 Z",
        // ударный (Лютый/Бобр): прямое крыло, двухбалочный хвост
        ["uav_strike"] = "M11 2 H13 V9 H22.5 V11.5 H13 V17 L16.5 19 V21 H13.2 L12 22.5 L10.8 21 H7.5 V19 L11 17 V11.5 H1.5 V9 H11 Z",
        // реактивный: стреловидное крыло + сопло
        ["uav_jet"] = "M12 0.8 L13.4 6 L21.5 15 L21.5 16.6 L13.4 13.2 L13.4 18.4 L15.6 20.6 L15.6 22 L12 21.2 L8.4 22 L8.4 20.6 L10.6 18.4 L10.6 13.2 L2.5 16.6 L2.5 15 L10.6 6 Z",
        // малый (Хорнет): компактный дельтакрыл с вырезом
        ["uav_small"] = "M12 4 L19 18 L12 15 L5 18 Z",
        // FPV-квадрокоптер: X-рама с роторами
        ["fpv"] = "M9.5 9.5 L4.8 4.8 A2.8 2.8 0 1 1 6.2 3.4 L10.9 8.1 H13.1 L17.8 3.4 A2.8 2.8 0 1 1 19.2 4.8 L14.5 9.5 V11.5 L14.5 14.5 L19.2 19.2 A2.8 2.8 0 1 1 17.8 20.6 L13.1 15.9 H10.9 L6.2 20.6 A2.8 2.8 0 1 1 4.8 19.2 L9.5 14.5 Z",
        // FPV на оптоволокне: квадрокоптер + «хвост»-катушка
        ["fpv_fiber"] = "M9.5 7.5 L5.2 3.2 A2.4 2.4 0 1 1 6.6 1.8 L10.9 6.1 H13.1 L17.4 1.8 A2.4 2.4 0 1 1 18.8 3.2 L14.5 7.5 V11.5 L18.8 15.8 A2.4 2.4 0 1 1 17.4 17.2 L13.1 12.9 H12.8 V16 C12.8 18 15 18.5 15 20.5 C15 22.3 13 23 12 23 L11.6 22 C12.6 22 13.6 21.6 13.6 20.5 C13.6 19.2 11.2 18.4 11.2 16 V12.9 H10.9 L6.6 17.2 A2.4 2.4 0 1 1 5.2 15.8 L9.5 11.5 Z",
        // тяжёлый гексакоптер («Баба-яга»)
        ["heavy_copter"] = "M10 9 L6.5 5.5 A2.6 2.6 0 1 1 8.5 3.6 L11 7.2 H13 L15.5 3.6 A2.6 2.6 0 1 1 17.5 5.5 L14 9 L17.6 10.6 A2.6 2.6 0 1 1 17.6 13.4 L14 15 L17.5 18.5 A2.6 2.6 0 1 1 15.5 20.4 L13 16.8 H11 L8.5 20.4 A2.6 2.6 0 1 1 6.5 18.5 L10 15 L6.4 13.4 A2.6 2.6 0 1 1 6.4 10.6 Z",
        // носитель/«крыло»: широкое летающее крыло
        ["uav_carrier"] = "M12 6 L23 13 L23 15 L15 13.5 L12 17 L9 13.5 L1 15 L1 13 Z",
        // разведчик: тонкий фюзеляж, длинное прямое крыло
        ["uav_recon"] = "M11.3 2.5 H12.7 V10 H23 V11.6 H12.7 V18.5 H15.5 V20 H8.5 V18.5 H11.3 V11.6 H1 V10 H11.3 Z",

        // ракеты
        ["missile"] = "M12 0.5 C13.3 2 13.6 3.6 13.6 5.5 L13.6 16.5 L16.5 20 L16.5 22 L13.4 20.8 L12 23.5 L10.6 20.8 L7.5 22 L7.5 20 L10.4 16.5 L10.4 5.5 C10.4 3.6 10.7 2 12 0.5 Z",
        // крылатая: фюзеляж + раскрытые крылья посередине
        ["cruise"] = "M12 0.5 C13.1 1.8 13.3 3.2 13.3 5 V9.5 L20.5 12.5 V14 L13.3 12.8 V18 L15.6 20.4 V22 L12 21.2 L8.4 22 V20.4 L10.7 18 V12.8 L3.5 14 V12.5 L10.7 9.5 V5 C10.7 3.2 10.9 1.8 12 0.5 Z",
        // ПКР: короткие крылья, высокий стабилизатор
        ["antiship"] = "M12 0.5 C13.2 1.8 13.5 3.4 13.5 5 V10 L18 13 V14.5 L13.5 13.5 V18.5 L17 21 V22.5 L12 21.5 L7 22.5 V21 L10.5 18.5 V13.5 L6 14.5 V13 L10.5 10 V5 C10.5 3.4 10.8 1.8 12 0.5 Z",
        // баллистическая: остроконечная, толстая, без крыльев, малые рули внизу
        ["ballistic"] = "M12 0 L14.5 6 V19 L17.5 22.5 V23.5 H6.5 V22.5 L9.5 19 V6 Z",
        // реактивный снаряд (HIMARS): тонкий, четыре оперения
        ["rocket"] = "M12 1 L13.2 4 V18 L15.5 21.5 V23 L12 21.8 L8.5 23 V21.5 L10.8 18 V4 Z",

        // авиация
        ["aircraft"] = "M12 1.6 C12.9 1.6 13.4 2.6 13.4 4 V9 L22 13.8 V15.8 L13.4 13.2 V18.2 L16 20.2 V22 L12 21 L8 22 V20.2 L10.6 18.2 V13.2 L2 15.8 V13.8 L10.6 9 V4 C10.6 2.6 11.1 1.6 12 1.6 Z",
        // планирующая бомба: каплевидный корпус + раскрытые крылья
        ["bomb"] = "M12 1 C14.2 1 15 4 15 7 V15 L18 18.5 V20 H13.8 L12 22.5 L10.2 20 H6 V18.5 L9 15 V7 C9 4 9.8 1 12 1 Z M3 10 H21 V11.5 H3 Z",
        ["helicopter"] = "M11 4 H13 V7.3 C15 7.8 16 9.6 16 12 C16 14.3 14.6 15.6 13 16 V21 H15.5 V22.5 H8.5 V21 H11 V16 C9.4 15.6 8 14.3 8 12 C8 9.6 9 7.8 11 7.3 Z M1.5 6.2 H22.5 V7.6 H1.5 Z",

        // флот
        ["usv"] = "M12 1.5 L15.8 7 V18.5 L14.2 22.5 H9.8 L8.2 18.5 V7 Z M10.4 9 H13.6 V12.5 H10.4 Z",
        ["uuv"] = "M12 2 C14 2 14.5 4 14.5 6 V18 L16.5 20.5 V22 L12 21 L7.5 22 V20.5 L9.5 18 V6 C9.5 4 10 2 12 2 Z",

        // наземный огонь (символы, не поворачиваются)
        ["mlrs"] = "M3 15 H21 V19 H3 Z M5 20 A1.5 1.5 0 1 0 5.01 20 Z M19 20 A1.5 1.5 0 1 0 19.01 20 Z M6 14 L17 4 L19 6 L8 15 Z",
        ["artillery"] = "M12 1.5 L14.2 8.6 L21.5 6.5 L16.4 12 L21.5 17.5 L14.2 15.4 L12 22.5 L9.8 15.4 L2.5 17.5 L7.6 12 L2.5 6.5 L9.8 8.6 Z",
        ["mines"] = "M12 6 A6 6 0 1 1 11.99 6 Z M11 1 H13 V5 H11 Z M11 19 H13 V23 H11 Z M1 11 H5 V13 H1 Z M19 11 H23 V13 H19 Z M4.2 5.6 L5.6 4.2 L8.4 7 L7 8.4 Z M15.6 17 L17 15.6 L19.8 18.4 L18.4 19.8 Z",

        // аэростат: оболочка + подвес
        ["balloon"] = "M12 1 C16.5 1 19.5 4.3 19.5 8.5 C19.5 12.5 15.5 15.5 13.2 17 H10.8 C8.5 15.5 4.5 12.5 4.5 8.5 C4.5 4.3 7.5 1 12 1 Z M10.6 17.6 H13.4 L12.9 19.5 H11.1 Z M10 20 H14 V23 H10 Z",
        // ДРГ: силуэт человека
        ["sabotage"] = "M12 2 A3 3 0 1 1 11.99 2 Z M8 9 H16 L17 16 H14.5 L14 23 H10 L9.5 16 H7 Z",

        ["alert"] = "M10.4 3 L13.6 3 L13 15 L11 15 Z M10.3 18.6 A1.7 1.7 0 1 0 13.7 18.6 A1.7 1.7 0 1 0 10.3 18.6 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = [];

    public static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var g)) return g;
        g = Geometry.Parse(Paths.TryGetValue(key, out var d) ? d : Paths["alert"]);
        Cache[key] = g;
        return g;
    }

    public static Geometry Get(ThreatType t) => Get(ThreatCatalog.GroupDefault(t).Icon);

    public static Geometry Get(ThreatInfo info) => Get(info.Icon);

    /// <summary>Иконка имеет направление (имеет смысл поворачивать по курсу).</summary>
    public static bool IsDirectional(string icon) => icon is not ("fpv" or "fpv_fiber" or "heavy_copter" or "mlrs" or "artillery" or "mines" or "balloon" or "sabotage" or "alert" or "helicopter");

    public static bool IsDirectional(ThreatType t) => IsDirectional(ThreatCatalog.GroupDefault(t).Icon);

    public static Matrix Transform(Point center, double size, double angleRad) =>
        Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(size / 24, size / 24) *
        Matrix.CreateRotation(angleRad) * Matrix.CreateTranslation(center.X, center.Y);
}
