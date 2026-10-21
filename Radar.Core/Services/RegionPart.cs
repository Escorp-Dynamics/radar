using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Принадлежность точки «части региона» («южная часть Ростовской области»).
/// Точный ответ даёт слой карты (по контурам районов этой части — тем же, что подсвечиваются);
/// без карты (тесты, нет контуров) — по прямоугольнику региона.
/// </summary>
public static class RegionPart
{
    /// <summary>Точная проверка по районам: null — нет данных (точка вне районов региона, нет контуров).</summary>
    public static Func<string, double?, GeoPoint, bool?>? Exact { get; set; }

    public static bool Contains(string region, double? bearing, GeoPoint p)
    {
        if (Exact?.Invoke(region, bearing, p) is { } exact) return exact;
        if (Gazetteer.Get(region) is not { } reg) return false;
        double y = (p.Lat - reg.Center.Lat) / reg.HalfLat, x = (p.Lon - reg.Center.Lon) / reg.HalfLon;
        if (Math.Abs(x) > 1.2 || Math.Abs(y) > 1.2) return false;
        if (bearing is not { } b) return x * x + y * y <= 0.35 * 0.35;
        double rad = b * Math.PI / 180;
        // проекция на направление стороны света; с запасом от середины — отбой гасит только явно лежащее в части
        return x * Math.Sin(rad) + y * Math.Cos(rad) >= 0.15;
    }
}
