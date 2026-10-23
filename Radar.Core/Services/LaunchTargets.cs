using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Ближайшая к месту пуска точка региона, которую держит РФ. Для прифронтовых регионов контур региона целиком
/// включает и территорию противника: Краматорск лежит внутри контура ДНР, и «ближайшая точка контура» оказывалась
/// на северной границе области, а не у линии соприкосновения на юго-востоке. Цель — только на своей стороне.
/// </summary>
public static class LaunchTargets
{
    /// <param name="origin">Место пуска.</param>
    /// <param name="rings">Контуры региона (замкнутые ломаные).</param>
    /// <param name="extra">Дополнительные кандидаты (точки линии соприкосновения внутри региона).</param>
    /// <param name="allowed">Точка на допустимой стороне (на стороне РФ).</param>
    /// <param name="stepKm">Шаг сэмплирования рёбер контура.</param>
    public static (GeoPoint Pos, double Km)? Nearest(GeoPoint origin, IEnumerable<IReadOnlyList<GeoPoint>> rings,
        IEnumerable<GeoPoint> extra, Func<GeoPoint, bool> allowed, double stepKm = 8)
    {
        GeoPoint? best = null;
        double bestKm = double.MaxValue;
        void Try(GeoPoint p)
        {
            double km = Geo.DistanceKm(origin, p);
            if (km >= bestKm || !allowed(p)) return;
            bestKm = km; best = p;
        }
        foreach (var ring in rings)
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[j]; var b = ring[i];
                int n = Math.Max(1, (int)Math.Ceiling(Geo.DistanceKm(a, b) / stepKm));
                for (int k = 0; k <= n; k++)
                {
                    double t = (double)k / n;
                    Try(new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t));
                }
            }
        foreach (var p in extra) Try(p);
        return best is { } r ? (r, bestKm) : null;
    }

    /// <summary>
    /// Запас к расстоянию до ближайшего региона, км: веер показывает, куда пуск может быть направлен поблизости.
    /// Одна дальность средства (дальний БПЛА — до 1500 км) пропускала бы по лучу в каждый сектор — из Одессы
    /// к Брянской и Белгородской областям через всю Украину, хотя Херсонская и Крым в разы ближе.
    /// </summary>
    public const double FanSlackKm = 150;

    /// <summary>Направления веера: не дальше ближайшего региона плюс <see cref="FanSlackKm"/>.</summary>
    public static IEnumerable<T> WithinReach<T>(IReadOnlyCollection<T> rays, Func<T, double> km, double slackKm = FanSlackKm)
    {
        if (rays.Count == 0) return [];
        double limit = rays.Min(km) + slackKm;
        return rays.Where(r => km(r) <= limit);
    }

    /// <summary>Ломаная, пересэмплированная с шагом <paramref name="stepKm"/> (для точек линии соприкосновения).</summary>
    public static IEnumerable<GeoPoint> Sample(IReadOnlyList<GeoPoint> line, double stepKm = 6)
    {
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1]; var b = line[i];
            int n = Math.Max(1, (int)Math.Ceiling(Geo.DistanceKm(a, b) / stepKm));
            for (int k = 0; k < n; k++)
            {
                double t = (double)k / n;
                yield return new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
            }
        }
        if (line.Count > 0) yield return line[^1];
    }
}
