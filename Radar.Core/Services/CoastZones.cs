using Radar.Models;

namespace Radar.Services;

/// <summary>
/// «Приазовье» — побережье Азовского моря в Запорожской обл., ДНР, Ростовской обл. и Краснодарском крае
/// (азовский берег Крыма — Арабатская стрелка, север Керченского п-ова — в «Приазовье» каналов не входит).
/// Участки береговой линии слоя ориентиров у Азовского моря и Таганрогского залива, разбитые по регионам;
/// зона рисуется полосой вдоль берега, а не кругом у Бердянска.
/// </summary>
public static class CoastZones
{
    public const string AzovLabel = "Приазовье";
    public static readonly string[] AzovRegions = ["zaporozhye", "dnr", "rostov", "krasnodar"];

    public sealed record Segment(string Region, GeoPoint Mid, IReadOnlyList<GeoPoint> Line);

    /// <summary>Участки берега длиннее 25 км (лиманы и мелкие изгибы отброшены).</summary>
    public static IReadOnlyList<Segment> Azov => _azov.Value;

    /// <summary>Участок, к которому относится точка зоны (по середине участка).</summary>
    public static Segment? AzovAt(GeoPoint p) => Azov.Where(s => Geo.DistanceKm(s.Mid, p) < 1).FirstOrDefault();

    // граница регионов по берегу: Обиточная коса / Белосарайская коса / Новоазовск — Таганрог / Ейск
    private static string? AzovRegion(GeoPoint q) =>
        q.Lat >= 46.3 && q.Lon >= 35.2 && q.Lon < 37.15 ? "zaporozhye" :
        q.Lat >= 46.3 && q.Lon >= 37.15 && q.Lon < 38.22 ? "dnr" :
        q.Lat >= 46.95 && q.Lon >= 38.22 ? "rostov" :
        q.Lat < 46.95 && q.Lat > 45.15 && q.Lon >= 36.7 ? "krasnodar" : null;

    private static bool OnAzov(GeoPoint q)
    {
        foreach (var b in new[] { 0.0, 45, 90, 135, 180, 225, 270, 315 })
            if (RouteNetwork.SeaAt(Geo.Destination(q, b, 2.5)) is "sea:azov" or "sea:taganrog") return true;
        return false;
    }

    private static readonly Lazy<IReadOnlyList<Segment>> _azov = new(() =>
    {
        var res = new List<Segment>();
        foreach (var part in RouteNetwork.Find(new CorridorHint(CorridorKind.Coast, null, "")).SelectMany(l => l.Parts))
        {
            var cur = new List<GeoPoint>();
            string? curReg = null;
            void Flush()
            {
                if (curReg != null && cur.Count > 1 && Geo.PathLength(cur) >= 25)
                {
                    // середина участка по длине — точка зоны (подпись, частичный отбой, близость)
                    double half = Geo.PathLength(cur) / 2, acc = 0;
                    var mid = cur[^1];
                    for (int i = 1; i < cur.Count; i++)
                    {
                        acc += Geo.DistanceKm(cur[i - 1], cur[i]);
                        if (acc >= half) { mid = cur[i]; break; }
                    }
                    res.Add(new Segment(curReg, mid, cur));
                }
                cur = [];
                curReg = null;
            }
            foreach (var q in part)
            {
                var r = OnAzov(q) ? AzovRegion(q) : null;
                if (r != curReg) Flush();
                if (r == null) continue;
                curReg = r;
                cur.Add(q);
            }
            Flush();
        }
        return res;
    }, LazyThreadSafetyMode.ExecutionAndPublication);
}
