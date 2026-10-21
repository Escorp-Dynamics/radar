using Radar.Models;

namespace Radar.Services;

/// <summary>Сводка статистики за период (с фильтром по региону): все блоки дашборда.</summary>
public sealed class StatsReport
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public int Alerts { get; init; }
    public double AlertHours { get; init; }
    public int Uav { get; init; }
    public int Missile { get; init; }
    public int Strikes { get; init; }
    /// <summary>Изменение числа тревог к предыдущему периоду той же длины, доля (null — нет данных).</summary>
    public double? Trend { get; init; }
    public (DateTime Day, int Count) PeakDay { get; init; }
    /// <summary>По дням (местное время): число тревог по группам угроз.</summary>
    public List<(DateTime Day, Dictionary<ThreatType, int> ByThreat)> Days { get; init; } = [];
    /// <summary>День недели (0 — пн) × час суток: число тревог.</summary>
    public int[,] WeekHour { get; init; } = new int[7, 24];
    public List<(string RegionId, int Count, double Hours)> Regions { get; init; } = [];
    public List<(string Place, string RegionId, int Count)> Places { get; init; } = [];
    public List<(ThreatType Threat, ThreatKind Kind, int Count)> Kinds { get; init; } = [];
    /// <summary>Направления захода: сектор (0 — с севера, по 45°) → число треков.</summary>
    public int[] Sectors { get; init; } = new int[8];
    public List<(string Label, int Count)> LaunchAreas { get; init; } = [];
    /// <summary>Мои зоны: тревог, часов, медианная длительность, ближайший подход (км, к какому пункту).</summary>
    public List<(string Name, int Count, double Hours, double MedianMinutes, double? ClosestKm)> Mine { get; init; } = [];
    /// <summary>Тревог по регионам — для раскраски карты (0…1 к максимуму).</summary>
    public Dictionary<string, double> RegionHeat { get; init; } = [];
}

public static class StatsSummary
{
    /// <summary>
    /// Сводка за [<paramref name="from"/>, <paramref name="to"/>) по событиям и трекам архива.
    /// <paramref name="region"/> — только тревоги этого региона; <paramref name="prev"/> — события предыдущего периода (для динамики).
    /// </summary>
    /// <summary>Медиана отсортированного ряда: при чётном числе элементов — среднее двух средних, а не верхний из них.</summary>
    internal static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>
    /// Часы, в которые была активна хотя бы одна из тревог: пересекающиеся интервалы не суммируются (иначе «часов под тревогой»
    /// превышает длину самого периода), а края обрезаются границами периода.
    /// </summary>
    public static double UnionHours(IEnumerable<StatEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        long f = from.UtcTicks, t = to.UtcTicks;
        var iv = new List<(long S, long E)>();
        foreach (var e in events)
        {
            long s = Math.Max(e.Start.UtcTicks, f), en = Math.Min(e.End.UtcTicks, t);
            if (en > s) iv.Add((s, en));
        }
        if (iv.Count == 0) return 0;
        iv.Sort((a, b) => a.S.CompareTo(b.S));
        long total = 0, cs = iv[0].S, ce = iv[0].E;
        for (int i = 1; i < iv.Count; i++)
        {
            var (s, en) = iv[i];
            if (s <= ce) { if (en > ce) ce = en; }
            else { total += ce - cs; cs = s; ce = en; }
        }
        total += ce - cs;
        return TimeSpan.FromTicks(total).TotalHours;
    }

    public static StatsReport Build(IReadOnlyList<StatEvent> all, IReadOnlyList<StatTrack> tracks, DateTimeOffset from, DateTimeOffset to,
        string? region, IReadOnlyList<WatchArea> watch, int watchRadiusKm, IReadOnlyList<StatEvent>? prev = null)
    {
        bool In(StatEvent e) => e.Start >= from && e.Start < to && (region == null || e.Regions.Contains(region));
        var ev = all.Where(In).ToList();

        var days = new SortedDictionary<DateTime, Dictionary<ThreatType, int>>();
        for (var d = from.ToLocalTime().Date; d < to.ToLocalTime().Date.AddDays(to.ToLocalTime().TimeOfDay > TimeSpan.Zero ? 1 : 0); d = d.AddDays(1))
            days[d] = [];
        var wh = new int[7, 24];
        foreach (var e in ev)
        {
            var l = e.Start.ToLocalTime();
            if (!days.TryGetValue(l.Date, out var byThreat)) days[l.Date] = byThreat = [];
            byThreat[Group(e.Threat)] = byThreat.GetValueOrDefault(Group(e.Threat)) + 1;
            wh[((int)l.DayOfWeek + 6) % 7, l.Hour]++;
        }
        var peak = days.Select(kv => (kv.Key, kv.Value.Values.Sum())).DefaultIfEmpty().MaxBy(x => x.Item2);

        // по регионам — один проход со словарём: за всё время это ~260 тыс. тревог × несколько регионов
        var byRegion = new Dictionary<string, List<StatEvent>>(StringComparer.Ordinal);
        foreach (var e in ev)
            foreach (var r in e.Regions)
            {
                if (!byRegion.TryGetValue(r, out var l)) byRegion[r] = l = [];
                l.Add(e);
            }
        var regions = byRegion.Select(kv => (kv.Key, kv.Value.Count, UnionHours(kv.Value, from, to))).OrderByDescending(x => x.Item2).ToList();
        // регион пункта — свой у каждого (тревога «Елец, Бельбек» — Липецкая область и Севастополь)
        var placeCount = new Dictionary<(string, string), int>();
        foreach (var e in ev)
            for (int i = 0; i < e.Places.Length; i++)
            {
                var pr = i < e.PlaceRegions.Length && e.PlaceRegions[i].Length > 0 ? e.PlaceRegions[i] : e.Regions.Length > 0 ? e.Regions[0] : "";
                if (region != null && pr != region) continue;
                var key = (e.Places[i], pr);
                placeCount[key] = placeCount.GetValueOrDefault(key) + 1;
            }
        var places = placeCount.OrderByDescending(kv => kv.Value).Take(15).Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
        var kinds = ev.GroupBy(e => (Group(e.Threat), e.Kind)).Select(g => (g.Key.Item1, g.Key.Kind, g.Count()))
            .OrderByDescending(x => x.Item3).ToList();

        // направления захода — по трекам, начавшимся в периоде (в регионе — если трек закончился в его границах:
        // по контуру региона, а без карты регионов — по его рамке, куда попадают и соседи)
        var sectors = new int[8];
        var launch = new Dictionary<string, int>(StringComparer.Ordinal);
        var regionAt = RoutePlanner.RegionAt;
        foreach (var t in tracks)
        {
            if (t.Start < from || t.Start >= to || t.Points < 2) continue;
            if (region != null && regionAt != null)
            {
                if (regionAt(new GeoPoint(t.ToLat, t.ToLon)) != region) continue;
            }
            else if (region != null && Gazetteer.Get(region) is { } rg)
            {
                var b = rg.DefaultBox;
                if (!(t.ToLat >= b.MinLat && t.ToLat <= b.MaxLat && t.ToLon >= b.MinLon && t.ToLon <= b.MaxLon)) continue;
            }
            var a = new GeoPoint(t.FromLat, t.FromLon);
            var z = new GeoPoint(t.ToLat, t.ToLon);
            if (Geo.DistanceKm(a, z) < 15) continue;
            // откуда пришёл: обратный курс
            double from180 = (Geo.Bearing(z, a) + 360) % 360;
            sectors[(int)Math.Round(from180 / 45) % 8]++;
            if (t.LaunchLabel is { Length: > 0 } ll) launch[ll] = launch.GetValueOrDefault(ll) + 1;
        }

        // мои зоны: тревоги в зоне пункта (радиус) или по его региону; ближайший подход — до отметок тревоги
        var mine = new List<(string, int, double, double, double?)>();
        foreach (var w in watch)
        {
            List<StatEvent> hit;
            double? closest = null;
            if (w.Kind == WatchArea.PlaceKind)
            {
                var c = new GeoPoint(w.Lat, w.Lon);
                double r = w.RadiusKm > 0 ? w.RadiusKm : watchRadiusKm;
                hit = [];
                foreach (var e in all.Where(e => e.Start >= from && e.Start < to))
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < e.Lats.Length; i++) best = Math.Min(best, Geo.DistanceKm(c, new GeoPoint(e.Lats[i], e.Lons[i])));
                    if (best <= r) hit.Add(e);
                    if (best < double.MaxValue && (closest == null || best < closest)) closest = best;
                }
            }
            else
            {
                var id = w.Kind == WatchArea.RegionKind ? w.Id : w.Region;
                hit = all.Where(e => e.Start >= from && e.Start < to && e.Regions.Contains(id)).ToList();
            }
            var durs = hit.Select(e => e.Duration.TotalMinutes).Order().ToList();
            mine.Add((w.Name, hit.Count, UnionHours(hit, from, to), Median(durs), closest));
        }

        double? trend = null;
        if (prev != null)
        {
            // календарный месяц сравнивается с предыдущим месяцем (в пересчёте на сутки), остальное — с окном той же длины
            var span = to - from;
            // по календарной дате: при переводе часов смещения from и to различаются, и равенство DateTimeOffset ложно
            var lf = from.ToLocalTime().Date; var lt = to.ToLocalTime().Date;
            bool month = lf.Day == 1 && lt.Day == 1 && lt == lf.AddMonths(1);
            var prevFrom = month ? from.AddMonths(-1) : from - span;
            int p = prev.Count(e => e.Start >= prevFrom && e.Start < from && (region == null || e.Regions.Contains(region)));
            // архив начинается внутри прошлого периода — сравнение с неполными данными дало бы ложный рост
            bool prevFull = prev.Count > 0 && prev.Min(e => e.Start) <= prevFrom + TimeSpan.FromDays(1);
            if (p > 0 && prevFull) trend = ev.Count / span.TotalDays / (p / (from - prevFrom).TotalDays) - 1;
        }

        // тепловая карта — всегда по всем регионам (выбранный регион выделяется отдельно)
        var heat = all.Where(e => e.Start >= from && e.Start < to).SelectMany(e => e.Regions).GroupBy(r => r).ToDictionary(g => g.Key, g => (double)g.Count());
        double hmax = heat.Count > 0 ? heat.Values.Max() : 1;
        foreach (var k in heat.Keys.ToList()) heat[k] = Math.Sqrt(heat[k] / hmax);

        return new StatsReport
        {
            From = from,
            To = to,
            Alerts = ev.Count,
            AlertHours = UnionHours(ev, from, to),
            Uav = ev.Count(e => e.Threat == ThreatType.Uav),
            Missile = ev.Count(e => e.Threat == ThreatType.Missile),
            Strikes = ev.Count(e => e.Status is AlertStatus.Impact or AlertStatus.AirDefense || e.Level == AlertLevel.Strike),
            Trend = trend,
            PeakDay = peak.Item2 == 0 ? (default, 0) : (peak.Key, peak.Item2),
            Days = [.. days.Select(kv => (kv.Key, kv.Value))],
            WeekHour = wh,
            Regions = regions,
            Places = places,
            Kinds = kinds,
            Sectors = sectors,
            LaunchAreas = [.. launch.OrderByDescending(kv => kv.Value).Take(8).Select(kv => (kv.Key, kv.Value))],
            Mine = mine,
            RegionHeat = heat,
        };
    }

    /// <summary>Группа для графиков: БПЛА, ракеты, авиация (КАБ), остальное.</summary>
    public static ThreatType Group(ThreatType t) => t switch
    {
        ThreatType.Uav or ThreatType.Missile or ThreatType.Aviation => t,
        ThreatType.Artillery => ThreatType.Artillery,
        _ => ThreatType.General,
    };
}
