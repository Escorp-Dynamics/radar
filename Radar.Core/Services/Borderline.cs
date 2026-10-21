using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Линия соприкосновения с территорией, откуда запускаются объекты: границы регионов РФ с областями Украины
/// (Сумская, Черниговская, Харьковская, Днепропетровская, Николаевская) — из слоя ориентиров (общие границы регионов),
/// плюс побережье Чёрного моря у Одессы/Николаева (морские пуски). Глубина точки — расстояние до этой линии:
/// объект после пуска удаляется от неё, поэтому глубина вдоль трека в целом растёт.
/// </summary>
public static class Borderline
{
    // регионы «стороны пуска» (Харьковская область — в справочнике единым регионом, контакт по её границе)
    private static readonly HashSet<string> LaunchSide = new(StringComparer.Ordinal)
    {
        "ua_sumy", "ua_chernihiv", "ua_poltava", "ua_dnipro", "ua_mykolaiv", "ua_odesa", "kharkov",
    };

    private static readonly HashSet<string> FrontRegions = new(StringComparer.Ordinal) { "lnr", "dnr", "zaporozhye", "kherson" };

    /// <summary>
    /// Встроенная приблизительная линия боевого соприкосновения от Купянска до устья Днепра (опорные точки, точность
    /// ~10–20 км) — запасной вариант, пока не загружена актуальная линия (<see cref="FrontLineSource"/>).
    /// </summary>
    private static readonly GeoPoint[] FallbackFront =
    [
        new(50.20, 37.85), new(49.71, 37.62), new(49.38, 37.62), new(49.00, 37.85), new(48.87, 38.10), new(48.60, 37.88),
        new(48.40, 37.85), new(48.28, 37.20), new(48.00, 37.25), new(47.84, 36.83), new(47.62, 36.26), new(47.52, 35.79),
        // далее — по Каховскому водохранилищу и Днепру (Энергодар, Каменка-Днепровская, В. Лепетиха, Н. Каховка — левый берег)
        new(47.50, 35.20), new(47.56, 34.75), new(47.53, 34.45), new(47.38, 34.15), new(47.20, 33.85), new(46.95, 33.55),
        new(46.78, 33.40), new(46.70, 33.10), new(46.62, 32.68), new(46.55, 32.40), new(46.48, 32.20),
    ];

    /// <summary>Снимок данных линии: заменяется целиком (потокобезопасно), читатели работают со своим снимком.</summary>
    private sealed record Data(GeoPoint[][] Lines, (GeoPoint[] Part, string Side)[] Sides, GeoPoint[][] Front, GeoPoint[][] Occupied, int Version)
    {
        /// <summary>Расстояния до линии этого снимка (кеш живёт и заменяется вместе со снимком).</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<GeoPoint, double> Depth { get; } = new();
    }

    /// <summary>Исходные данные линии: заменяются одной ссылкой (линия и контуры — всегда из одного обновления).</summary>
    private sealed record Source(GeoPoint[][]? Front, GeoPoint[][] Occupied, int Version);

    private static volatile Source _source = new(null, [], 0);   // Front = null — встроенная линия
    private static volatile Data? _data;
    private static int _version;

    /// <summary>Увеличивается при каждой замене линии (карта перерисовывает подложку, кеши пересчитываются).</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Загружена ли актуальная линия (иначе — встроенная приблизительная).</summary>
    public static bool IsLive => _source.Front != null;

    /// <summary>
    /// Устанавливает актуальную линию соприкосновения (полилинии) и контуры подконтрольной РФ территории в зоне фронта
    /// (для определения стороны). Пустые данные игнорируются.
    /// </summary>
    public static void SetFront(IReadOnlyList<GeoPoint[]> lines, IReadOnlyList<GeoPoint[]>? occupied = null)
    {
        if (lines.Count == 0) return;
        Replace([.. lines], occupied is { Count: > 0 } ? [.. occupied] : []);
    }

    /// <summary>Возврат к встроенной линии (тесты).</summary>
    internal static void ResetFront()
    {
        Replace(null, []);
    }

    private static void Replace(GeoPoint[][]? front, GeoPoint[][] occupied)
    {
        lock (FallbackFront)
        {
            // версия публикуется после замены источника: прочитав новую версию, читатель уже получит новый снимок
            int v = _version + 1;
            _source = new Source(front, occupied, v);
            _data = null;
            Volatile.Write(ref _version, v);
        }
    }

    /// <summary>Линия соприкосновения (для отображения на карте): полилинии.</summary>
    public static IReadOnlyList<GeoPoint[]> FrontLines => Get().Front;

    private static Data Get()
    {
        var d = _data;
        if (d != null) return d;
        // снимок строится под той же блокировкой, что и замена: устаревший снимок не перезапишет новый
        lock (FallbackFront)
        {
            return _data ??= Build(_source);
        }
    }

    /// <summary>
    /// Точка на стороне противника: область Украины, либо в прифронтовых регионах (Харьковская, ЛНР, ДНР, Запорожская,
    /// Херсонская) — по ту сторону линии соприкосновения. У самой линии (до 12 км) — своя сторона: тревога безопаснее.
    /// </summary>
    /// <summary>Регион, часть которого занята противником: его контур целиком не годится как «территория РФ».</summary>
    public static bool IsFrontRegion(string? regionId) => regionId is "kharkov" or "lnr" or "dnr" or "zaporozhye" or "kherson";

    public static bool IsEnemySide(GeoPoint p, string? regionId)
    {
        if (regionId != null && Gazetteer.IsForeign(regionId)) return true;
        if (regionId is not ("kharkov" or "lnr" or "dnr" or "zaporozhye" or "kherson")) return false;
        var d = Get();
        // актуальные данные: по контурам подконтрольной РФ территории
        if (d.Occupied.Length > 0)
        {
            if (d.Occupied.Any(r => RouteNetwork.Inside(r, p))) return false;
            return FrontKm(d, p) >= 12;
        }
        // встроенная линия: идёт с севера на юг и далее на запад — сторона РФ слева
        if (regionId != "kharkov" && FrontKm(d, p) < 12) return false;
        double best = double.MaxValue, cross = 0;
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        var f = FallbackFront;
        for (int i = 0; i + 1 < f.Length; i++)
        {
            var a = f[i];
            var b = f[i + 1];
            double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
            double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
            double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
            double qx = ax + t * dx, qy = ay + t * dy, dd = qx * qx + qy * qy;
            if (dd < best) { best = dd; cross = dx * (-qy) - dy * (-qx); }
        }
        // за северным концом линии (Харьковская обл. у границы РФ) — по долготе: западнее Купянска — сторона противника
        if (p.Lat > f[0].Lat) return p.Lon < f[0].Lon - 0.3;
        return cross < 0;
    }

    private static double FrontKm(Data d, GeoPoint p)
    {
        double best = double.MaxValue;
        foreach (var part in d.Front)
            for (int i = 0; i + 1 < part.Length; i++) best = Math.Min(best, SegKm(p, part[i], part[i + 1]));
        return best;
    }

    private static double SegKm(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
        double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
        double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
        double qx = ax + t * dx, qy = ay + t * dy;
        return Math.Sqrt(qx * qx + qy * qy) * 111.2;
    }

    private static Data Build(Source src)
    {
        var front = src.Front ?? [FallbackFront];
        var res = new List<GeoPoint[]>();
        var sides = new List<(GeoPoint[], string)>();
        foreach (var l in RouteNetwork.Lines)
        {
            if (l.Kind != CorridorKind.Border || !l.Id.StartsWith("border:", StringComparison.Ordinal)) continue;
            var pair = l.Id["border:".Length..].Split('+');
            if (pair.Length != 2) continue;
            bool a = LaunchSide.Contains(pair[0]), b = LaunchSide.Contains(pair[1]);
            if (a == b) continue; // граница внутри одной стороны
            // административные границы ЛНР, ДНР, Запорожской и Херсонской областей — не линия соприкосновения (её задаёт фронт)
            if (FrontRegions.Contains(a ? pair[1] : pair[0])) continue;
            // граница с Харьковской областью южнее Волчанска проходит по уже подконтрольной РФ территории — её заменяет фронт
            var side = a ? pair[0] : pair[1];
            foreach (var part in l.Parts.Where(p => p.Length >= 2)) { res.Add(part); sides.Add((part, side)); }
        }
        // запасной вариант без слоя ориентиров (тесты без ресурса): опорные точки линии от Брянской обл. до Купянска
        if (res.Count == 0)
        {
            GeoPoint[] north = [new(52.35, 31.9), new(52.1, 33.5), new(51.3, 34.5), new(50.4, 35.9), new(50.2, 37.85)];
            res.Add(north);
            sides.Add((north, ""));
        }
        foreach (var f in front) { res.Add(f); sides.Add((f, "front")); }
        return new Data([.. res], [.. sides], front, src.Occupied, src.Version);
    }

    /// <summary>Расстояние от точки до линии соприкосновения, км.</summary>
    public static double DistanceKm(GeoPoint p)
    {
        var data = Get();
        if (data.Depth.TryGetValue(p, out var d)) return d;
        d = Compute(data, p);
        if (data.Depth.Count > 50_000) data.Depth.Clear();
        data.Depth[p] = d;
        return d;
    }

    private static double Compute(Data data, GeoPoint p)
    {
        double best = double.MaxValue;
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        foreach (var part in data.Lines)
            for (int i = 0; i + 1 < part.Length; i++)
            {
                var a = part[i];
                var b = part[i + 1];
                double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
                double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
                double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
                double qx = ax + t * dx, qy = ay + t * dy;
                double d = Math.Sqrt(qx * qx + qy * qy) * 111.2;
                if (d < best) best = d;
            }
        return best;
    }

    /// <summary>Ближайшая к точке точка линии соприкосновения.</summary>
    public static GeoPoint Nearest(GeoPoint p)
    {
        double best = double.MaxValue;
        GeoPoint res = p;
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        foreach (var part in Get().Lines)
            for (int i = 0; i + 1 < part.Length; i++)
            {
                var a = part[i];
                var b = part[i + 1];
                double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
                double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
                double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
                double qx = ax + t * dx, qy = ay + t * dy;
                double d = qx * qx + qy * qy;
                if (d < best) { best = d; res = new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t); }
            }
        return res;
    }

    /// <summary>
    /// Точка линии соприкосновения «позади» точки <paramref name="p"/> по обратному курсу <paramref name="back"/> (±60°),
    /// не дальше <paramref name="maxKm"/>: откуда объект, вероятнее всего, пересёк границу. Подпись — сторона пуска.
    /// </summary>
    public static (GeoPoint Pos, string Label)? Behind(GeoPoint p, double back, double maxKm, double maxAng = 60)
    {
        var data = Get();
        (GeoPoint, string)? best = null;
        double bestScore = double.MaxValue;
        for (int li = 0; li < data.Sides.Length; li++)
        {
            var (part, side) = data.Sides[li];
            for (int i = 0; i < part.Length; i++)
            {
                // вершины линии + середины отрезков: шаг ~ несколько км
                for (int h = 0; h < 2; h++)
                {
                    if (h == 1 && i + 1 >= part.Length) break;
                    var q = h == 0 ? part[i] : Geo.Lerp(part[i], part[i + 1], 0.5);
                    double km = Geo.DistanceKm(p, q);
                    if (km < 5 || km > maxKm) continue;
                    double ang = Geo.AngleDiff(Geo.Bearing(p, q), back);
                    if (ang > maxAng) continue;
                    double score = km * (1 + ang / 30);
                    if (score < bestScore) { bestScore = score; best = (q, side); }
                }
            }
        }
        return best is { } b ? (b.Item1, SideLabel(b.Item2)) : null;
    }

    /// <summary>
    /// Расстояние по лучу из <paramref name="p"/> с курсом <paramref name="bearing"/> до первого пересечения
    /// линии соприкосновения (не дальше <paramref name="maxKm"/>); null — луч её не пересекает.
    /// </summary>
    public static double? FrontCrossingKm(GeoPoint p, double bearing, double maxKm)
    {
        var data = Get();
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        var e = Geo.Destination(p, bearing, maxKm);
        double ex = (e.Lon - p.Lon) * kx, ey = e.Lat - p.Lat;
        double? best = null;
        foreach (var part in data.Front)
            for (int i = 0; i + 1 < part.Length; i++)
            {
                double ax = (part[i].Lon - p.Lon) * kx, ay = part[i].Lat - p.Lat;
                double bx = (part[i + 1].Lon - p.Lon) * kx, by = part[i + 1].Lat - p.Lat;
                double sx = bx - ax, sy = by - ay;
                double den = ex * sy - ey * sx;
                if (Math.Abs(den) < 1e-12) continue;
                double t = (ax * sy - ay * sx) / den, u = (ax * ey - ay * ex) / den;
                if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                double km = t * maxKm;
                if (best is not { } b || km < b) best = km;
            }
        return best;
    }

    private static string SideLabel(string side) => side switch
    {
        "ua_sumy" => "граница, Сумская обл.",
        "ua_chernihiv" => "граница, Черниговская обл.",
        "ua_poltava" => "граница, Полтавская обл.",
        "ua_dnipro" => "линия фронта, Днепропетровская обл.",
        "ua_mykolaiv" => "линия фронта, Николаевская обл.",
        "ua_odesa" => "граница, Одесская обл.",
        "kharkov" => "граница, Харьковская обл.",
        "front" => "линия соприкосновения",
        _ => "граница",
    };

    // ---------------- зоны захода ----------------

    /// <summary>
    /// Откуда объект заходит в район точки: Land — через линию соприкосновения (глубина = удаление от неё),
    /// Sea — Крым и Кубань, заходы с разных сторон моря (глубину считаем от точки входа самого трека),
    /// None — глубокий тыл, правило не применяется.
    /// </summary>
    public enum Zone { None, Land, Sea }

    public static Zone ZoneOf(GeoPoint p)
    {
        // Крым: западнее Перекопа — южнее Каркинитского залива (45.95), далее — южнее Армянска/Чонгара (46.15);
        // юг Херсонской обл. (Скадовск 46.12/32.9, Геническ 46.17) — суша
        if (p.Lon > 32.3 && p.Lon < 36.75 && p.Lat < (p.Lon < 33.6 ? 45.95 : 46.15)) return Zone.Sea;
        if (p.Lat < 46.3 && p.Lon >= 36.75 && p.Lon < 41.5) return Zone.Sea; // Кубань, побережье
        return DistanceKm(p) < 350 ? Zone.Land : Zone.None;
    }

    /// <summary>
    /// Переход a → b на суше идёт к линии соприкосновения, а не вдоль неё: приближение к линии больше <paramref name="minKm"/>
    /// и составляет заметную долю перемещения (угол к линии круче ~35°). Пролёт вдоль изогнутой границы
    /// (Клинцы → Погар у стыка с Белоруссией) тоже сокращает расстояние до линии, но разворотом не является.
    /// </summary>
    public static bool HeadsToLine(GeoPoint a, GeoPoint b, double minKm)
    {
        if (ZoneOf(a) != Zone.Land || ZoneOf(b) != Zone.Land) return false;
        double normal = DistanceKm(a) - DistanceKm(b);
        if (normal <= minKm) return false;
        double move = Geo.DistanceKm(a, b);
        double along = Math.Sqrt(Math.Max(0, move * move - normal * normal));
        return normal >= 0.7 * along;
    }

    /// <summary>
    /// На сколько км переход a → b возвращает объект к месту захода (0 — не возвращает или неприменимо).
    /// На суше — к линии соприкосновения; в морских зонах — к точке входа трека <paramref name="entry"/>.
    /// </summary>
    public static double BackKm(GeoPoint a, GeoPoint b, GeoPoint? entry)
    {
        var za = ZoneOf(a);
        var zb = ZoneOf(b);
        // из Крыма на север, в Херсонскую/Запорожскую область — это к линии, а не облёт полуострова
        // (Красноперекопск → Каланчак — уже другой объект с той стороны Днепра)
        if (za == Zone.Sea && zb == Zone.Land && a.Lon < 36.75) return Math.Max(0, DistanceKm(a) - DistanceKm(b));
        if (za == Zone.None || zb != za) return 0;
        if (za == Zone.Land) return Math.Max(0, DistanceKm(a) - DistanceKm(b));
        if (entry is not { } e || ZoneOf(e) != Zone.Sea) return 0;
        double da = Geo.DistanceKm(e, a);
        // у самой точки входа направление ещё не определено
        return da < 25 ? 0 : Math.Max(0, da - Geo.DistanceKm(e, b));
    }
}
