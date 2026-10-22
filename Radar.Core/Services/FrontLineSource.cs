using System.Globalization;
using System.Text;
using System.Text.Json;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Актуальная линия соприкосновения из открытых данных DeepStateMap (deepstatemap.live, GeoJSON без ключей).
/// Загружается не чаще раза в сутки, кешируется на диске; без сети и кеша действует встроенная приблизительная линия.
/// Из данных берётся граница полигонов «окуповано» (плюс Крым и ОРДЛО), обращённая к подконтрольной Украине суше:
/// участки по государственной границе с РФ, по морю и внутри зоны не являются линией соприкосновения.
/// </summary>
public static class FrontLineSource
{
    public const string Url = "https://deepstatemap.live/api/history/last";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Когда линия загружена из сети (по кешу), null — используется встроенная.</summary>
    public static DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>Момент, на который составлены данные (публикация DeepStateMap), null — неизвестен или встроенная линия.</summary>
    public static DateTimeOffset? DataTime { get; private set; }

    /// <summary>Линия заменена (из кеша или сети) — для обновления подписи в легенде.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Загружает линию из кеша (сразу) и, если кеш старше суток, — из сети в фоне. Новая линия применяется
    /// к расчётам (<see cref="Borderline"/>) и сохраняется в кеш.
    /// </summary>
    public static async Task RefreshAsync(HttpClient http, CancellationToken ct)
    {
        var cache = AppPaths.FrontCache;
        DateTimeOffset cachedAt = default;
        if (TryReadCache(cache, out var cached, out var cachedOcc, out var at, out var cachedData))
        {
            cachedAt = at;
            // тот же кеш уже применён (повторная проверка раз в 6 ч): линию не перестраиваем — это сбросило бы
            // кеши расстояний и мест пуска и перерисовало карту без изменений
            if (LoadedAt == cachedAt) { if (DateTimeOffset.UtcNow - cachedAt < MaxAge) return; }
            else
            {
            Borderline.SetFront(cached, cachedOcc);
            LoadedAt = cachedAt;
            DataTime = cachedData;
            Changed?.Invoke();
            }
        }
        if (DateTimeOffset.UtcNow - cachedAt < MaxAge) return;
        try
        {
            // сайт карты заблокирован в части сетей — при сетевом сбое напрямую повтор через ротатор прокси
            var bytes = await TelegramRoute.SendAsync(http, async (c, t) =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Radar/1.0 (civil defense map)");
                using var resp = await c.SendAsync(req, t).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(t).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            var (lines, occupied) = Extract(bytes);
            if (lines.Count == 0) return;
            Borderline.SetFront(lines, occupied);
            LoadedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            DataTime = ReadDataTime(bytes);
            WriteCache(cache, lines, occupied, LoadedAt.Value, DataTime);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException
                                       or KeyNotFoundException or IndexOutOfRangeException or FormatException or UnauthorizedAccessException)
        {
            // без сети (или при неожиданном формате ответа) остаётся кеш или встроенная линия
        }
    }

    /// <summary>Загрузка при запуске и далее проверка раз в сутки (приложение может работать неделями).</summary>
    public static async Task RefreshLoopAsync(HttpClient http, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // неожиданное исключение (необычная геометрия в ответе) не должно молча остановить обновление линии на дни
            try { await RefreshAsync(http, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("front refresh: " + ex); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Время публикации данных: поле «id» ответа DeepStateMap — unix-время версии карты. Правдоподобным считаем
    /// значение в пределах последних двух лет (иначе это не время, а идентификатор).
    /// </summary>
    internal static DateTimeOffset? ReadDataTime(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var unix))
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(unix);
                if (t > DateTimeOffset.UtcNow.AddYears(-2) && t < DateTimeOffset.UtcNow.AddDays(1)) return t;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException) { }
        return null;
    }

    /// <summary>Аудит (--audit/--engine-audit): линия из локального файла GeoJSON (переменная RADAR_FRONT_GEOJSON).</summary>
    public static void LoadForAudit()
    {
        var fn = Environment.GetEnvironmentVariable("RADAR_FRONT_GEOJSON");
        if (string.IsNullOrEmpty(fn) || !File.Exists(fn)) return;
        var (lines, occ) = Extract(File.ReadAllBytes(fn));
        Borderline.SetFront(lines, occ);
        Console.WriteLine($"front line: {lines.Count} parts from {fn}");
    }

    // ---------------- разбор GeoJSON ----------------

    /// <summary>
    /// Линия соприкосновения из GeoJSON DeepStateMap: внешние контуры полигонов «окуповано» (цвета #a52714 и #880e4f)
    /// в рамке фронта, без участков вдоль государственной границы РФ и по морю. Полилинии (lat, lon).
    /// </summary>
    internal static (List<GeoPoint[]> Lines, List<GeoPoint[]> Occupied) Extract(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var features = root.TryGetProperty("map", out var map) ? map.GetProperty("features") : root.GetProperty("features");
        var rings = new List<GeoPoint[]>();
        foreach (var f in features.EnumerateArray())
        {
            if (!f.TryGetProperty("properties", out var props) || !f.TryGetProperty("geometry", out var geom)) continue;
            var fill = props.TryGetProperty("fill", out var fl) ? fl.GetString() : null;
            if (fill is not ("#a52714" or "#880e4f")) continue;
            var type = geom.GetProperty("type").GetString();
            var coords = geom.GetProperty("coordinates");
            if (type == "Polygon") AddOuter(coords, rings);
            else if (type == "MultiPolygon") foreach (var poly in coords.EnumerateArray()) AddOuter(poly, rings);
        }
        // Участок контура — линия соприкосновения, если по ту его сторону (в ~0,8 км) нет другой подконтрольной РФ
        // территории (общие границы соседних полигонов DeepState), нет моря и это не государственная граница РФ.
        var boxes = rings.Select(Box).ToArray();
        bool InsideAny(GeoPoint q, int except)
        {
            for (int k = 0; k < rings.Count; k++)
                if (k != except && In(boxes[k], q) && RouteNetwork.Inside(rings[k], q)) return true;
            return false;
        }
        var res = new List<GeoPoint[]>();
        for (int ri = 0; ri < rings.Count; ri++)
        {
            var ring = rings[ri];
            var cur = new List<GeoPoint>();
            for (int i = 0; i + 1 < ring.Length; i++)
            {
                var a0 = ring[i];
                var b0 = ring[i + 1];
                if (IsFrontEdge(a0, b0, ri))
                {
                    if (cur.Count == 0) cur.Add(a0);
                    cur.Add(b0);
                    continue;
                }
                Flush();
            }
            Flush();

            void Flush()
            {
                if (cur.Count >= 2 && PathKm(cur) > 3) res.Add(Simplify(cur, 0.003));
                cur = [];
            }

            bool IsFrontEdge(GeoPoint a0, GeoPoint b0, int self)
            {
                var m = new GeoPoint((a0.Lat + b0.Lat) / 2, (a0.Lon + b0.Lon) / 2);
                if (m.Lat < 46.1 || m.Lat > 51.5 || m.Lon < 31.5 || m.Lon > 40.3) return false;
                if (NearRussianBorder(m)) return false;
                // Приазовье (Молочный лиман, Утлюкский лиман, косы) — тыл, линии соприкосновения южнее 46,8° восточнее Днепра нет
                if (m.Lat < 46.8 && m.Lon > 33.9) return false;
                double brg = Geo.Bearing(a0, b0);
                var left = Geo.Destination(m, brg - 90, 0.8);
                var right = Geo.Destination(m, brg + 90, 0.8);
                bool leftOwn = RouteNetwork.Inside(ring, left);
                if (leftOwn == RouteNetwork.Inside(ring, right)) return false; // вырожденный участок
                var outside = leftOwn ? right : left;
                if (InsideAny(outside, self)) return false;
                if (RouteNetwork.SeaAt(outside) != null) return false;
                return true;
            }
        }
        // контуры для определения стороны (все кольца «окуповано»), упрощённые
        var occ = rings.Select(r => Simplify([.. r], 0.002)).Where(r => r.Length >= 4).ToList();
        return (res, occ);
    }

    private static void AddOuter(JsonElement polygon, List<GeoPoint[]> rings)
    {
        var outer = polygon.EnumerateArray().FirstOrDefault();
        if (outer.ValueKind != JsonValueKind.Array) return;
        var pts = new List<GeoPoint>(outer.GetArrayLength());
        foreach (var c in outer.EnumerateArray())
        {
            double lon = c[0].GetDouble(), lat = c[1].GetDouble();
            pts.Add(new GeoPoint(lat, lon));
        }
        if (pts.Count >= 4 && pts.Any(p => p.Lat > 44.3 && p.Lat < 51.5 && p.Lon > 31.5 && p.Lon < 40.3)) rings.Add([.. pts]);
    }

    private static (double MinLat, double MaxLat, double MinLon, double MaxLon) Box(GeoPoint[] r) =>
        (r.Min(p => p.Lat), r.Max(p => p.Lat), r.Min(p => p.Lon), r.Max(p => p.Lon));

    private static bool In((double MinLat, double MaxLat, double MinLon, double MaxLon) b, GeoPoint p) =>
        p.Lat >= b.MinLat && p.Lat <= b.MaxLat && p.Lon >= b.MinLon && p.Lon <= b.MaxLon;

    private static readonly Lazy<GeoPoint[][]> _ruBorder = new(() =>
    {
        // граница РФ (до 2022) с Украиной из слоя ориентиров: Белгородская/Курская/Брянская/Воронежская/Ростовская —
        // с Харьковской, Сумской, Черниговской, Луганской и Донецкой областями
        var ru = new HashSet<string>(StringComparer.Ordinal) { "belgorod", "kursk", "bryansk", "voronezh", "rostov" };
        var ua = new HashSet<string>(StringComparer.Ordinal) { "kharkov", "ua_sumy", "ua_chernihiv", "lnr", "dnr" };
        var res = new List<GeoPoint[]>();
        foreach (var l in RouteNetwork.Lines)
        {
            if (l.Kind != CorridorKind.Border || !l.Id.StartsWith("border:", StringComparison.Ordinal)) continue;
            var pair = l.Id["border:".Length..].Split('+');
            if (pair.Length == 2 && (ru.Contains(pair[0]) && ua.Contains(pair[1]) || ru.Contains(pair[1]) && ua.Contains(pair[0])))
                res.AddRange(l.Parts);
        }
        return [.. res];
    });

    private static bool NearRussianBorder(GeoPoint p)
    {
        foreach (var part in _ruBorder.Value)
            for (int i = 0; i + 1 < part.Length; i++)
                if (SegmentKm(p, part[i], part[i + 1]) < 2.5) return true;
        return false;
    }

    private static double SegmentKm(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
        double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
        double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
        double qx = ax + t * dx, qy = ay + t * dy;
        return Math.Sqrt(qx * qx + qy * qy) * 111.2;
    }

    private static double PathKm(List<GeoPoint> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Geo.DistanceKm(pts[i - 1], pts[i]);
        return s;
    }

    /// <summary>Дуглас — Пекер (градусы): тысячи вершин DeepStateMap → сотни, точность ~300 м.</summary>
    private static GeoPoint[] Simplify(List<GeoPoint> pts, double tol)
    {
        if (pts.Count < 3) return [.. pts];
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0;
            int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = PerpDeg(pts[i], pts[a], pts[b]);
                if (d > best) { best = d; bi = i; }
            }
            if (bi >= 0 && best > tol)
            {
                keep[bi] = true;
                stack.Push((a, bi));
                stack.Push((bi, b));
            }
        }
        return [.. pts.Where((_, i) => keep[i])];
    }

    private static double PerpDeg(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        double dx = b.Lon - a.Lon, dy = b.Lat - a.Lat, L = dx * dx + dy * dy;
        if (L < 1e-14) return Math.Sqrt((p.Lon - a.Lon) * (p.Lon - a.Lon) + (p.Lat - a.Lat) * (p.Lat - a.Lat));
        double t = Math.Clamp(((p.Lon - a.Lon) * dx + (p.Lat - a.Lat) * dy) / L, 0, 1);
        double qx = a.Lon + t * dx - p.Lon, qy = a.Lat + t * dy - p.Lat;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // ---------------- кеш ----------------

    /// <summary>
    /// Формат: первая строка — «unix-время загрузки [unix-время данных]»; далее по строке на полилинию:
    /// «L lat,lon lat,lon …» — линия, «O …» — контур подконтрольной РФ территории.
    /// </summary>
    internal static void WriteCache(string path, List<GeoPoint[]> lines, List<GeoPoint[]> occupied, DateTimeOffset at, DateTimeOffset? dataTime = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            if (dataTime is { } dt) sb.Append(' ').Append(dt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
            foreach (var (tag, set) in new[] { ("L", lines), ("O", occupied) })
                foreach (var l in set)
                    sb.Append(tag).Append(' ').Append(string.Join(' ', l.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Lat:F5},{p.Lon:F5}")))).Append('\n');
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool TryReadCache(string path, out List<GeoPoint[]> lines, out List<GeoPoint[]> occupied, out DateTimeOffset at,
        out DateTimeOffset? dataTime)
    {
        lines = [];
        occupied = [];
        at = default;
        dataTime = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var r = new StreamReader(path);
            var head = (r.ReadLine() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length == 0 || !long.TryParse(head[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) return false;
            at = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (head.Length > 1 && long.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var du))
                dataTime = DateTimeOffset.FromUnixTimeSeconds(du);
            while (r.ReadLine() is { } line)
            {
                var pts = new List<GeoPoint>();
                var toks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length < 2 || toks[0] is not ("L" or "O")) continue;
                foreach (var tok in toks.Skip(1))
                {
                    var c = tok.Split(',');
                    if (c.Length == 2 && double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
                        && double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
                        pts.Add(new GeoPoint(la, lo));
                }
                if (pts.Count >= 2) (toks[0] == "L" ? lines : occupied).Add([.. pts]);
            }
            if (lines.Count == 0) at = default;
            return lines.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // битый заголовок (время вне диапазона) или нет доступа — кеша нет
            lines = [];
            occupied = [];
            at = default;
            return false;
        }
    }
}
