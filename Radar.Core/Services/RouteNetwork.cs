using System.IO.Compression;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>Линейный ориентир: трасса, железная дорога, река, береговая линия.</summary>
public sealed class RouteLine
{
    public required CorridorKind Kind { get; init; }
    public required string Id { get; init; }
    /// <summary>Названия в нижнем регистре: номер трассы и его варианты, народные названия, русские названия рек.</summary>
    public required string[] Names { get; init; }
    public required GeoPoint[][] Parts { get; init; }
}

/// <summary>Точка на полилинии: часть, сегмент, доля сегмента, сама точка и расстояние до исходной точки.</summary>
public readonly record struct LinePos(int Part, int Seg, double T, GeoPoint Pos, double DistKm);

/// <summary>
/// Офлайн-слой линейных ориентиров (Assets/routes.bin.gz, tools/build_routes.py) и геометрические запросы:
/// поиск по названию, ближайшая точка, подпуть вдоль линии между двумя точками.
/// </summary>
public static class RouteNetwork
{
    private static readonly Lazy<List<RouteLine>> _lines = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<RouteLine> Lines => _lines.Value;

    public static void Preload() => Task.Run(() => _lines.Value).ContinueWith(
        t => System.Diagnostics.Trace.WriteLine($"RouteNetwork: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);

    private static List<RouteLine> Load()
    {
        var list = new List<RouteLine>();
        using var res = typeof(RouteNetwork).Assembly.GetManifestResourceStream("Radar.routes.bin.gz");
        if (res == null) return list;
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var d = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (d.Length < 4 || Encoding.ASCII.GetString(d[..4]) != "RRT1") return list;
        int pos = 4;
        while (pos < d.Length)
        {
            byte kind = d[pos++];
            if (kind == 255) break;
            var id = Str(d, ref pos);
            var names = Str(d, ref pos).Split('|', StringSplitOptions.RemoveEmptyEntries);
            uint parts = BitConverter.ToUInt32(d.Slice(pos, 4));
            pos += 4;
            var arr = new GeoPoint[parts][];
            for (int p = 0; p < parts; p++)
            {
                uint n = BitConverter.ToUInt32(d.Slice(pos, 4));
                pos += 4;
                var pts = new GeoPoint[n];
                for (int i = 0; i < n; i++)
                {
                    pts[i] = new GeoPoint(BitConverter.ToSingle(d.Slice(pos, 4)), BitConverter.ToSingle(d.Slice(pos + 4, 4)));
                    pos += 8;
                }
                arr[p] = pts;
            }
            list.Add(new RouteLine { Kind = (CorridorKind)kind, Id = id, Names = names, Parts = arr });
        }
        return list;

        static string Str(Span<byte> d, ref int pos)
        {
            int len = BitConverter.ToUInt16(d.Slice(pos, 2));
            var s = Encoding.UTF8.GetString(d.Slice(pos + 2, len));
            pos += 2 + len;
            return s;
        }
    }

    /// <summary>Линии, подходящие под подсказку: по номеру/названию трассы, названию реки или по виду (ж/д, берег).</summary>
    public static IEnumerable<RouteLine> Find(CorridorHint hint)
    {
        CorridorKind? kind = hint.Kind;
        if (kind == CorridorKind.Border)
        {
            // граница — только между названными регионами («по границе Курской и Брянской»); «вдоль границы» без регионов неоднозначно
            if (hint.Name is { } bid && BorderById(bid) is { } bl) yield return bl;
            yield break;
        }
        if (kind == CorridorKind.Sea) yield break;
        if (hint.Name == null)
        {
            foreach (var l in Lines) if (l.Kind == kind) yield return l;
            yield break;
        }
        var n = Gazetteer.Normalize(hint.Name);
        // префиксное совпадение — только для словесных названий (падежи), номера трасс сравниваются точно
        bool fuzzy = n.Length >= 4 && !n.Any(char.IsDigit);
        foreach (var l in Lines)
        {
            if (l.Kind != kind) continue;
            if (l.Id.Equals(hint.Name, StringComparison.OrdinalIgnoreCase) || l.Names.Any(x => x == n || (fuzzy && x.StartsWith(n[..Math.Max(4, n.Length - 2)], StringComparison.Ordinal))))
                yield return l;
        }
    }

    /// <summary>Ближайшая к <paramref name="p"/> точка линии (в пределах <paramref name="maxKm"/>).</summary>
    public static LinePos? Nearest(RouteLine line, GeoPoint p, double maxKm)
    {
        LinePos? best = null;
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        double lim = maxKm / 111.0;
        for (int pi = 0; pi < line.Parts.Length; pi++)
        {
            var pts = line.Parts[pi];
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                // быстрый отсев по рамке
                if (Math.Min(a.Lat, b.Lat) - lim > p.Lat || Math.Max(a.Lat, b.Lat) + lim < p.Lat) continue;
                if ((Math.Min(a.Lon, b.Lon) - p.Lon) * kx > lim || (p.Lon - Math.Max(a.Lon, b.Lon)) * kx > lim) continue;
                double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
                double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
                double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
                var q = new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
                double km = Geo.DistanceKm(p, q);
                if (km <= maxKm && (best == null || km < best.Value.DistKm)) best = new LinePos(pi, i, t, q, km);
            }
        }
        return best;
    }

    /// <summary>
    /// Путь вдоль линии от проекции <paramref name="a"/> до проекции <paramref name="b"/> (в пределах одной части).
    /// null — если точки далеко от линии или лежат на разных несвязанных частях.
    /// </summary>
    public static List<GeoPoint>? Between(RouteLine line, GeoPoint a, GeoPoint b, double maxOffKm)
    {
        if (Nearest(line, a, maxOffKm) is not { } pa || Nearest(line, b, maxOffKm) is not { } pb) return null;
        if (pa.Part != pb.Part) return null;
        var pts = line.Parts[pa.Part];
        var res = new List<GeoPoint> { pa.Pos };
        double sa = pa.Seg + pa.T, sb = pb.Seg + pb.T;
        if (sa <= sb)
            for (int i = pa.Seg + 1; i <= pb.Seg; i++) res.Add(pts[i]);
        else
            for (int i = pa.Seg; i > pb.Seg; i--) res.Add(pts[i]);
        res.Add(pb.Pos);
        return res;
    }

    /// <summary>
    /// Продолжение вдоль линии от проекции <paramref name="from"/> на расстояние до <paramref name="km"/> в сторону,
    /// ближайшую к курсу <paramref name="bearing"/>.
    /// </summary>
    public static List<GeoPoint>? Follow(RouteLine line, GeoPoint from, double bearing, double km, double maxOffKm)
    {
        if (Nearest(line, from, maxOffKm) is not { } p) return null;
        var pts = line.Parts[p.Part];
        // направление вдоль линии — по курсу самого сегмента: проекция в вершине (T = 0 или 1) совпадает с одним из концов, курс на него вырожден
        int s0 = Math.Min(p.Seg, pts.Length - 2), s1 = s0 + 1;
        if (s0 < 0) return null;
        double segBearing = Geo.Bearing(pts[s0], pts[s1]);
        bool forward = Geo.AngleDiff(segBearing, bearing) <= Geo.AngleDiff((segBearing + 180) % 360, bearing);
        var res = new List<GeoPoint> { p.Pos };
        double acc = 0;
        int i = forward ? p.Seg + 1 : p.Seg;
        while (i >= 0 && i < pts.Length && acc < km)
        {
            var next = pts[i];
            double d = Geo.DistanceKm(res[^1], next);
            if (acc + d > km)
            {
                res.Add(Geo.Lerp(res[^1], next, (km - acc) / Math.Max(d, 1e-6)));
                break;
            }
            acc += d;
            res.Add(next);
            i += forward ? 1 : -1;
        }
        return res.Count >= 2 ? res : null;
    }

    // ---------------- общие границы регионов ----------------

    /// <summary>Id линии общей границы двух регионов (порядок не важен): «border:a+b», a &lt; b.</summary>
    public static string BorderKey(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"border:{a}+{b}" : $"border:{b}+{a}";

    public static RouteLine? BorderBetween(string a, string b) => BorderById(BorderKey(a, b));

    private static RouteLine? BorderById(string id)
    {
        foreach (var l in Lines)
            if (l.Kind == CorridorKind.Border && l.Id == id) return l;
        return null;
    }

    // ---------------- акватории ----------------

    private static readonly Lazy<List<RouteLine>> _seas = new(() => [.. Lines.Where(l => l.Kind == CorridorKind.Sea)]);

    /// <summary>Контур акватории по id («sea:azov», «sea:taganrog», «sea:black»).</summary>
    public static RouteLine? Sea(string id) => _seas.Value.FirstOrDefault(l => l.Id == id);

    /// <summary>Акватория, в которой лежит точка (самая мелкая: залив раньше моря); null — суша или нет данных.</summary>
    public static string? SeaAt(GeoPoint p)
    {
        string? best = null;
        double bestArea = double.MaxValue;
        foreach (var (id, ring, box, area) in _seaRings.Value)
        {
            // кольца отсортированы по площади: первое найденное — самое мелкое
            if (area >= bestArea) break;
            if (p.Lat < box.MinLat || p.Lat > box.MaxLat || p.Lon < box.MinLon || p.Lon > box.MaxLon) continue;
            if (Inside(ring, p)) { bestArea = area; best = id; }
        }
        return best;
    }

    private static readonly Lazy<(string Id, GeoPoint[] Ring, (double MinLat, double MaxLat, double MinLon, double MaxLon) Box, double Area)[]> _seaRings =
        new(() => [.. _seas.Value.SelectMany(s => s.Parts.Where(r => r.Length >= 3).Select(r =>
                (s.Id, r, (r.Min(q => q.Lat), r.Max(q => q.Lat), r.Min(q => q.Lon), r.Max(q => q.Lon)), BoxArea(r))))
            .OrderBy(x => x.Item4)], LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Точка находится над водой (любая акватория слоя).</summary>
    public static bool IsWater(GeoPoint p) => SeaAt(p) != null;

    /// <summary>
    /// Ближайшая к <paramref name="p"/> точка акватории, отступив от берега на <paramref name="offshoreKm"/>
    /// (выход в море «у Бердянска» — у Бердянска, а не в центре моря). null — нет контура.
    /// </summary>
    public static GeoPoint? SeaPointNear(string seaId, GeoPoint p, double offshoreKm = 8)
    {
        if (Sea(seaId) is not { } s) return null;
        if (s.Parts.Any(r => Inside(r, p))) return p;
        GeoPoint? best = null;
        double bestKm = double.MaxValue;
        foreach (var ring in s.Parts)
        {
            var line = new RouteLine { Kind = CorridorKind.Sea, Id = s.Id, Names = [], Parts = [ring] };
            if (Nearest(line, p, 400) is { } np && np.DistKm < bestKm) { bestKm = np.DistKm; best = np.Pos; }
        }
        if (best is not { } shore) return null;
        // шаг от берега внутрь акватории: в сторону от исходной точки, пока точка не окажется над водой
        double brg = Geo.Bearing(p, shore);
        for (double k = offshoreKm; k <= offshoreKm * 4; k += offshoreKm)
        {
            var q = Geo.Destination(shore, brg, k);
            if (s.Parts.Any(r => Inside(r, q))) return q;
        }
        return shore;
    }

    /// <summary>
    /// Часть акватории вокруг <paramref name="center"/> радиусом <paramref name="radiusKm"/>: круг, обрезанный по
    /// контуру моря (зона «Таганрогский залив» не заходит на сушу). Пусто — нет данных.
    /// </summary>
    public static List<GeoPoint> SeaArea(string seaId, GeoPoint center, double radiusKm)
    {
        if (Sea(seaId) is not { } s) return [];
        // весь контур, если он целиком в пределах радиуса (залив), иначе — вершины контура в радиусе + дуга круга над водой
        var res = new List<GeoPoint>();
        const int N = 72;
        foreach (var ring in s.Parts)
        {
            if (ring.All(q => Geo.DistanceKm(q, center) <= radiusKm)) return [.. ring];
        }
        // изрезанный залив (Сиваш): лучи из центра упираются в косы и острова — берём целиком часть, где лежит центр
        if (WholeContourSeas.Contains(seaId) && s.Parts.FirstOrDefault(r => Inside(r, center)) is { } host)
            return [.. host];
        // полярная развёртка: по каждому азимуту — дальняя точка над водой в пределах радиуса (луч от центра до берега)
        for (int i = 0; i < N; i++)
        {
            double brg = i * 360.0 / N;
            GeoPoint? last = null;
            double step = Math.Max(0.5, radiusKm / 40), gap = 0;
            for (double k = 0; k <= radiusKm; k += step)
            {
                var q = Geo.Destination(center, brg, k);
                // вода этой акватории или соседней (Керченский пролив — на стыке Азовского и Чёрного морей)
                if (s.Parts.Any(r => Inside(r, q)) || IsWater(q)) { last = q; gap = 0; }
                // отмели и островки (Сиваш) — короткие разрывы не обрывают луч
                else if (last != null && (gap += step) > 3) break;
            }
            if (last is { } l) res.Add(l);
        }
        return res.Count >= 3 ? res : [];
    }

    private static readonly HashSet<string> WholeContourSeas = new(StringComparer.Ordinal) { "sea:sivash" };

    private static double BoxArea(GeoPoint[] ring)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var q in ring)
        {
            minLat = Math.Min(minLat, q.Lat); maxLat = Math.Max(maxLat, q.Lat);
            minLon = Math.Min(minLon, q.Lon); maxLon = Math.Max(maxLon, q.Lon);
        }
        return (maxLat - minLat) * (maxLon - minLon);
    }

    /// <summary>Точка внутри замкнутого контура (чётность пересечений).</summary>
    public static bool Inside(GeoPoint[] ring, GeoPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Lat > p.Lat) != (b.Lat > p.Lat) && p.Lon < (b.Lon - a.Lon) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lon)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Длина полилинии, км.</summary>
    public static double Length(IReadOnlyList<GeoPoint> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Geo.DistanceKm(pts[i - 1], pts[i]);
        return s;
    }
}
