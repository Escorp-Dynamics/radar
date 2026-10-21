using SkiaSharp;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Radar.Models;

namespace Radar.Map;

public enum FeatureKind : byte
{
    Land = 0,
    Region = 2,
    ForeignRegion = 3,
    Lake = 4,
    River = 5,
    City = 6,
    RussiaOutline = 7,
    /// <summary>Район / муниципальный или городской округ (OSM admin_level=6). Id = «регион|ключ».</summary>
    District = 8,
}

public sealed class BasemapFeature
{
    public required FeatureKind Kind { get; init; }
    public required int Lod { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Point Label { get; init; }
    public required int Rank { get; init; }
    public required Rect Bounds { get; init; }
    /// <summary>Контуры в мировых координатах Web Mercator (0..256 на нулевом зуме).</summary>
    public required Point[][] Parts { get; init; }

    private Geometry? _geometry;
    private Geometry?[]? _byZoom;
    private SKPath?[]? _pathByZoom;

    /// <summary>Полная геометрия (точные проверки попадания).</summary>
    public Geometry Geometry => _geometry ??= Build(Parts);

    /// <summary>
    /// Геометрия, упрощённая под масштаб отрисовки: допуск ~0.3 px на экране (Дуглас—Пекер), мелкие части отброшены.
    /// Строится лениво на каждый целый уровень зума — при отдалении контур в десятки раз легче.
    /// </summary>
    public Geometry GeometryAt(double scale)
    {
        int z = Math.Clamp((int)Math.Floor(Math.Log2(scale)), 0, MaxZoomBucket);
        var cache = _byZoom ?? Interlocked.CompareExchange(ref _byZoom, new Geometry?[MaxZoomBucket + 1], null) ?? _byZoom!;
        return cache[z] ?? Interlocked.CompareExchange(ref cache[z], BuildSimplified(0.3 / Math.Pow(2, z)), null) ?? cache[z]!;
    }

    /// <summary>
    /// То же упрощение в виде SKPath — для фоновой отрисовки подложки напрямую через Skia
    /// (объекты Avalonia привязаны к UI-потоку). Готовый путь только читается — безопасен из любого потока.
    /// </summary>
    public SKPath PathAt(double scale)
    {
        int z = Math.Clamp((int)Math.Floor(Math.Log2(scale)), 0, MaxZoomBucket);
        var cache = _pathByZoom ?? Interlocked.CompareExchange(ref _pathByZoom, new SKPath?[MaxZoomBucket + 1], null) ?? _pathByZoom!;
        if (cache[z] is { } ready) return ready;
        var path = BuildPath(PartsAt(scale));
        var prev = Interlocked.CompareExchange(ref cache[z], path, null);
        if (prev != null) { path.Dispose(); return prev; }
        return path;
    }

    private List<Point[]>?[]? _partsByZoom;

    private List<Point[]> PartsAt(double scale)
    {
        int z = Math.Clamp((int)Math.Floor(Math.Log2(scale)), 0, MaxZoomBucket);
        var cache = _partsByZoom ?? Interlocked.CompareExchange(ref _partsByZoom, new List<Point[]>?[MaxZoomBucket + 1], null) ?? _partsByZoom!;
        return cache[z] ?? Interlocked.CompareExchange(ref cache[z], SimplifiedParts(0.3 / Math.Pow(2, z)), null) ?? cache[z]!;
    }

    /// <summary>
    /// Путь для обводки, обрезанный до видимой области: только участки контура, пересекающие <paramref name="view"/>
    /// (разомкнутыми ломаными). Skia обводит путь целиком, даже вне экрана, — для гигантских контуров (граница РФ,
    /// берег) на крупном масштабе это десятки миллисекунд. null — объект и так целиком в кадре, рисовать <see cref="PathAt"/>.
    /// </summary>
    public SKPath? StrokePathIn(double scale, Rect view)
    {
        if (view.Contains(Bounds)) return null;
        var p = new SKPath();
        bool closed = Closed;
        foreach (var part in PartsAt(scale))
        {
            int n = part.Length;
            if (n < 2) continue;
            int segs = closed ? n : n - 1;
            bool open = false;
            for (int i = 0; i < segs; i++)
            {
                var a = part[i];
                var b = part[(i + 1) % n];
                bool vis = Math.Max(a.X, b.X) >= view.Left && Math.Min(a.X, b.X) <= view.Right
                        && Math.Max(a.Y, b.Y) >= view.Top && Math.Min(a.Y, b.Y) <= view.Bottom;
                if (!vis) { open = false; continue; }
                if (!open) { p.MoveTo((float)a.X, (float)a.Y); open = true; }
                p.LineTo((float)b.X, (float)b.Y);
            }
        }
        return p;
    }

    private SKPath BuildPath(IReadOnlyList<Point[]> parts)
    {
        bool closed = Closed;
        var p = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var part in parts)
        {
            if (part.Length < 2) continue;
            p.MoveTo((float)part[0].X, (float)part[0].Y);
            for (int i = 1; i < part.Length; i++) p.LineTo((float)part[i].X, (float)part[i].Y);
            if (closed) p.Close();
        }
        return p;
    }

    private const int MaxZoomBucket = 14;
    private bool Closed => Kind is not (FeatureKind.River or FeatureKind.RussiaOutline);

    private Geometry BuildSimplified(double tol) => Build(SimplifiedParts(tol));

    private List<Point[]> SimplifiedParts(double tol)
    {
        var parts = new List<Point[]>(Parts.Length);
        foreach (var part in Parts)
        {
            if (part.Length < 2) continue;
            if (Closed)
            {
                // часть меньше пикселя не видна
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var v in part)
                {
                    if (v.X < minX) minX = v.X;
                    if (v.X > maxX) maxX = v.X;
                    if (v.Y < minY) minY = v.Y;
                    if (v.Y > maxY) maxY = v.Y;
                }
                if (maxX - minX < tol * 2 && maxY - minY < tol * 2) continue;
            }
            var simple = Simplify(part, tol);
            if (Closed && simple.Length < 3) continue;
            parts.Add(simple);
        }
        return parts;
    }

    /// <summary>Итеративный Дуглас—Пекер (без рекурсии — контуры бывают на десятки тысяч вершин).</summary>
    private static Point[] Simplify(Point[] pts, double tol)
    {
        int n = pts.Length;
        if (n <= 3) return pts;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        double tol2 = tol * tol;
        var stack = new Stack<(int, int)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            var pa = pts[a];
            var pb = pts[b];
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y, len2 = dx * dx + dy * dy;
            int best = -1;
            double bestD = tol2;
            for (int i = a + 1; i < b; i++)
            {
                var p = pts[i];
                double d2;
                if (len2 < 1e-18) d2 = (p.X - pa.X) * (p.X - pa.X) + (p.Y - pa.Y) * (p.Y - pa.Y);
                else
                {
                    double cross = (p.X - pa.X) * dy - (p.Y - pa.Y) * dx;
                    d2 = cross * cross / len2;
                }
                if (d2 > bestD) { bestD = d2; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true;
            stack.Push((a, best));
            stack.Push((best, b));
        }
        int count = 0;
        for (int i = 0; i < n; i++) if (keep[i]) count++;
        if (count == n) return pts;
        var res = new Point[count];
        for (int i = 0, j = 0; i < n; i++) if (keep[i]) res[j++] = pts[i];
        return res;
    }

    private Geometry Build(IReadOnlyList<Point[]> parts)
    {
        bool closed = Closed;
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        foreach (var part in parts)
        {
            if (part.Length < 2) continue;
            ctx.BeginFigure(part[0], closed);
            for (int i = 1; i < part.Length; i++) ctx.LineTo(part[i]);
            ctx.EndFigure(closed);
        }
        return g;
    }
}

/// <summary>
/// Встроенная векторная подложка (Natural Earth 1:10m, public domain) — без сетевых запросов.
/// Генерируется скриптом tools/build_basemap.py.
/// </summary>
public static class Basemap
{
    public const double WorldSize = 256;

    private static readonly Lazy<IReadOnlyList<BasemapFeature>> _features = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Dictionary<string, BasemapFeature[]>> _districts = new(() =>
        Features.Where(f => f.Kind == FeatureKind.District).GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Lod).ToArray(), StringComparer.Ordinal));

    /// <summary>Контур района по региону и названию; null — если контура нет (тогда рисуется круг).</summary>
    public static BasemapFeature? District(string regionId, string name, int lod)
    {
        if (!_districts.Value.TryGetValue(regionId + "|" + DistrictKey(name), out var arr)) return null;
        return arr.FirstOrDefault(f => f.Lod == lod) ?? arr[0];
    }

    /// <summary>
    /// Районы, которые покрывает точка тревоги: названный район, районы «части региона» или район, в котором лежит пункт
    /// (как при подсветке на карте). Для сопоставления с выбранными пользователем районами.
    /// </summary>
    public static IEnumerable<string> DistrictsOf(MapPoint p)
    {
        if (p.IsRegionLevel) yield break;
        if (p.DistrictRegion is { } dr && District(dr, p.Label, 1) is { } df) { yield return df.Id; yield break; }
        if (p.PartRegion is { } pr && DistrictsInPart(pr, p.PartBearing) is { Count: > 0 } part)
        {
            foreach (var id in part) yield return id;
            yield break;
        }
        if (DistrictAt(p.Pos) is { } host) yield return host;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Point, bool> _inRussia = new();

    /// <summary>
    /// Точка (мировые координаты) внутри регионов РФ, включая новые регионы. Кеш — подписи повторяются в каждом кадре.
    /// По детальным контурам (LOD1), как и <see cref="RegionAt"/>: у границы обе проверки дают одинаковый ответ.
    /// </summary>
    public static bool InRussia(Point w)
    {
        // вдали от границ ответ даёт одна ячейка маски — без словаря и блокировок; точный обход и его кэш — только у границ
        switch (_russiaMask.Value.Query(w.X, w.Y))
        {
            case Radar.Services.RegionMask.Inside: return true;
            case Radar.Services.RegionMask.Outside: return false;
        }
        if (_inRussia.TryGetValue(w, out var hit)) return hit;
        // граничных точек немного; предел — страховка от роста при долгой работе
        if (_inRussia.Count > 50_000) _inRussia.Clear();
        return _inRussia[w] = InRussiaExact(w);
    }

    // Маска строится один раз (десятки миллисекунд) при первом обращении. Шаг ≈ 0,02 мировых единицы (~2 км):
    // вдали от границ ответ даёт одна ячейка, у границ — прежний точный обход контуров.
    private static readonly Lazy<Radar.Services.RegionMask> _russiaMask = new(() =>
        Radar.Services.RegionMask.Build([.. Of(FeatureKind.Region, 1).Select(f => f.Parts.Select(p => p.Select(q => (q.X, q.Y)).ToArray()).ToArray())], 0.02),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool InRussiaExact(Point p)
    {
        foreach (var f in Of(FeatureKind.Region, 1))
            if (f.Bounds.Contains(p) && Contains(f, p)) return true;
        return false;
    }

    /// <summary>Регион РФ, в котором лежит точка (по контуру), или null.</summary>
    public static string? RegionAt(GeoPoint p)
    {
        var w = Project(p);
        foreach (var f in Of(FeatureKind.Region, 1))
            if (f.Bounds.Contains(w) && Contains(f, w)) return f.Id;
        return null;
    }

    /// <summary>Каталог районов для поиска: нормализованное название, id, название, регион.</summary>
    public static IReadOnlyList<(string Key, string Id, string Name, string Region)> DistrictCatalog() =>
        [.. _districts.Value.Select(kv => kv.Value[^1])
            .Where(f => f.Id.IndexOf('|') > 0)
            .Select(f => (Key: f.Name.ToLowerInvariant().Replace('ё', 'е'), f.Id, f.Name, Region: f.Id[..f.Id.IndexOf('|')]))
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)];

    /// <summary>Контур района по идентификатору («регион|ключ») на нужном уровне детализации.</summary>
    public static BasemapFeature? DistrictById(string id, int lod) =>
        _districts.Value.TryGetValue(id, out var arr) ? arr.FirstOrDefault(f => f.Lod == lod) ?? arr[0] : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, double?, int), BasemapFeature?> _partShapes = new();

    /// <summary>
    /// «Южная часть области» одной фигурой: объединение контуров районов этой части (внутренние границы районов
    /// исчезают). Подписывается и закрашивается как одна зона; уточнения по отдельным районам ложатся поверх.
    /// </summary>
    public static BasemapFeature? PartShape(string regionId, double? bearing, int lod) => _partShapes.GetOrAdd((regionId, bearing, lod), key =>
    {
        var ids = DistrictsInPart(key.Item1, key.Item2);
        if (ids.Count == 0) return null;
        SKPath? acc = null;
        foreach (var id in ids)
        {
            if (DistrictById(id, key.Item3) is not { } f) continue;
            using var p = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var part in f.Parts)
            {
                if (part.Length < 3) continue;
                p.MoveTo((float)part[0].X, (float)part[0].Y);
                for (int i = 1; i < part.Length; i++) p.LineTo((float)part[i].X, (float)part[i].Y);
                p.Close();
            }
            if (acc == null) { acc = new SKPath(p); continue; }
            var u = acc.Op(p, SKPathOp.Union);
            if (u != null) { acc.Dispose(); acc = u; }
        }
        if (acc == null) return null;
        var parts = new List<Point[]>();
        using (acc)
        using (var it = acc.CreateRawIterator())
        {
            var pts = new SKPoint[4];
            var cur = new List<Point>();
            SKPathVerb v;
            while ((v = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (v)
                {
                    case SKPathVerb.Move:
                        if (cur.Count >= 3) parts.Add([.. cur]);
                        cur = [new Point(pts[0].X, pts[0].Y)];
                        break;
                    case SKPathVerb.Line: cur.Add(new Point(pts[1].X, pts[1].Y)); break;
                    case SKPathVerb.Quad: cur.Add(new Point(pts[2].X, pts[2].Y)); break;
                    case SKPathVerb.Conic: cur.Add(new Point(pts[2].X, pts[2].Y)); break;
                    case SKPathVerb.Cubic: cur.Add(new Point(pts[3].X, pts[3].Y)); break;
                    case SKPathVerb.Close:
                        if (cur.Count >= 3) parts.Add([.. cur]);
                        cur = [];
                        break;
                }
            }
            if (cur.Count >= 3) parts.Add([.. cur]);
        }
        // крошечные «дырки» на стыках районов — артефакт объединения, не показываем
        double Area(Point[] r)
        {
            double a = 0;
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++) a += (r[j].X + r[i].X) * (r[j].Y - r[i].Y);
            return Math.Abs(a) / 2;
        }
        double maxA = parts.Count > 0 ? parts.Max(Area) : 0;
        parts = parts.Where(r => Area(r) > maxA * 0.002).ToList();
        if (parts.Count == 0) return null;
        double minX = parts.Min(r => r.Min(q => q.X)), maxX = parts.Max(r => r.Max(q => q.X));
        double minY = parts.Min(r => r.Min(q => q.Y)), maxY = parts.Max(r => r.Max(q => q.Y));
        var label = ids.Select(id => DistrictById(id, key.Item3)).Where(f => f != null)
            .Select(f => f!.Label).Aggregate((X: 0.0, Y: 0.0, N: 0), (s, p) => (s.X + p.X, s.Y + p.Y, s.N + 1));
        return new BasemapFeature
        {
            Kind = FeatureKind.District, Lod = key.Item3, Id = $"part:{key.Item1}:{key.Item2?.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "c"}",
            Name = PartName(key.Item1, key.Item2), Label = new Point(label.X / label.N, label.Y / label.N), Rank = 0,
            Bounds = new Rect(minX, minY, maxX - minX, maxY - minY), Parts = [.. parts],
        };
    });

    private static string PartName(string regionId, double? bearing)
    {
        string side = bearing switch
        {
            null => "Центральная",
            var b => ((int)Math.Round(((b.Value % 360) + 360) % 360 / 45) % 8) switch
            {
                0 => "Северная", 1 => "Северо-восточная", 2 => "Восточная", 3 => "Юго-восточная",
                4 => "Южная", 5 => "Юго-западная", 6 => "Западная", _ => "Северо-западная",
            },
        };
        var region = Radar.Services.Gazetteer.Get(regionId)?.Name ?? "";
        // «Ростовская область» → «Ростовской обл.»: коротко, как на картах
        var gen = region.EndsWith("ская область") ? region[..^"ская область".Length] + "ской обл." : region;
        return $"{side} часть {gen}".Trim();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, double?), string[]> _parts = new();

    /// <summary>
    /// Районы, составляющие часть региона: «южная часть» — районы, чей центр лежит в южной половине
    /// (по проекции на направление), «центральная» — ближайшие к центру. Пусто — контуров районов нет.
    /// </summary>
    public static IReadOnlyList<string> DistrictsInPart(string regionId, double? bearing) => _parts.GetOrAdd((regionId, bearing), key =>
    {
        var prefix = key.Item1 + "|";
        var ds = _districts.Value.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (Id: kv.Key, F: kv.Value[^1])).ToList();
        if (ds.Count < 3) return [];
        // центры районов с весом площади (крупные районы на окраине не смещают «центр» региона)
        double area(BasemapFeature f) => Math.Max(1e-9, f.Bounds.Width * f.Bounds.Height);
        double tw = ds.Sum(d => area(d.F));
        var c = new Point(ds.Sum(d => d.F.Label.X * area(d.F)) / tw, ds.Sum(d => d.F.Label.Y * area(d.F)) / tw);
        if (key.Item2 is not { } b)
        {
            var near = ds.OrderBy(d => Math.Pow(d.F.Label.X - c.X, 2) + Math.Pow(d.F.Label.Y - c.Y, 2)).ToList();
            return [.. near.Take(Math.Max(1, (int)Math.Round(ds.Count * 0.3))).Select(d => d.Id)];
        }
        double rad = b * Math.PI / 180;
        // мировые координаты: X — на восток, Y — на юг
        double ux = Math.Sin(rad), uy = -Math.Cos(rad);
        var proj = ds.Select(d => (d.Id, T: (d.F.Label.X - c.X) * ux + (d.F.Label.Y - c.Y) * uy)).ToList();
        double min = proj.Min(p => p.T), max = proj.Max(p => p.T);
        if (max - min < 1e-9) return [];
        // дальние ~45 % протяжённости региона в нужную сторону (центральная полоса в «часть» не входит)
        return [.. proj.Where(p => (p.T - min) / (max - min) >= 0.55).Select(p => p.Id)];
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), string?> _districtAt = new();

    /// <summary>Район, в котором находится точка (по точному контуру), или null.</summary>
    public static string? DistrictAt(GeoPoint p)
    {
        // ключ с шагом ~100 м — для кэша достаточно
        var key = ((int)Math.Round(p.Lat * 1000), (int)Math.Round(p.Lon * 1000));
        // точки приходят со всей карты (клики, наведение, тревоги) — кэш ограничен, чтобы не расти бесконечно
        if (_districtAt.Count > 100_000) _districtAt.Clear();
        return _districtAt.GetOrAdd(key, _ =>
        {
            // города-округа часто лежат внутри района (кольца без «дыр») — берём наименьший содержащий контур
            var w = Project(p);
            BasemapFeature? best = null;
            foreach (var arr in _districts.Value.Values)
            {
                var f = arr[^1]; // самый детальный контур
                if (!f.Bounds.Contains(w) || !Contains(f, w)) continue;
                if (best == null || f.Bounds.Width * f.Bounds.Height < best.Bounds.Width * best.Bounds.Height) best = f;
            }
            return best?.Id;
        });
    }

    /// <summary>Чётно-нечётная проверка попадания точки в контур (с учётом дыр).</summary>
    private static bool Contains(BasemapFeature f, Point w)
    {
        bool inside = false;
        foreach (var part in f.Parts)
            for (int i = 0, j = part.Length - 1; i < part.Length; j = i++)
            {
                var a = part[i];
                var b = part[j];
                if ((a.Y > w.Y) != (b.Y > w.Y) && w.X < (b.X - a.X) * (w.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _neighbours = new(StringComparer.Ordinal);

    /// <summary>
    /// Смежные районы (в т. ч. соседних регионов): контуры имеют общий участок границы.
    /// Контуры упрощены независимо, поэтому сравниваются вершины с допуском ~1 км.
    /// </summary>
    public static IReadOnlyList<string> DistrictNeighbours(string id) => _neighbours.GetOrAdd(id, key =>
    {
        if (!_districts.Value.TryGetValue(key, out var arr)) return [];
        var a = arr[^1];
        const double eps = 0.008; // ≈ 0.011° ≈ 1 км в мировых координатах
        var box = a.Bounds.Inflate(eps);
        // сетка вершин исходного района для быстрого поиска близких точек
        var grid = new HashSet<(int, int)>();
        foreach (var part in a.Parts)
            foreach (var v in part) grid.Add(((int)Math.Floor(v.X / eps), (int)Math.Floor(v.Y / eps)));
        var result = new List<string>();
        foreach (var (otherId, oarr) in _districts.Value)
        {
            if (otherId == key) continue;
            var b = oarr[^1];
            if (!b.Bounds.Intersects(box)) continue;
            int close = 0;
            foreach (var part in b.Parts)
            {
                foreach (var v in part)
                {
                    int gx = (int)Math.Floor(v.X / eps), gy = (int)Math.Floor(v.Y / eps);
                    bool hit = false;
                    for (int dx = -1; dx <= 1 && !hit; dx++)
                        for (int dy = -1; dy <= 1 && !hit; dy++)
                            hit = grid.Contains((gx + dx, gy + dy));
                    if (hit && ++close >= 4) break;
                }
                if (close >= 4) break;
            }
            // город-анклав внутри района (дыры не выгружаются): объемлющий район тоже сосед
            if (close >= 4 || Contains(b, a.Label) || Contains(a, b.Label) && b.Bounds.Width < a.Bounds.Width) result.Add(otherId);
        }
        return [.. result];
    });

    /// <summary>Нормализация названия района: «Валуйский район» / «Валуйский МО» / «Валуйский муниципальный округ» → «валуйский».</summary>
    public static string DistrictKey(string name)
    {
        var n = name.ToLowerInvariant().Replace('ё', 'е').Trim();
        string[] suffixes = [" муниципальный район", " муниципальный округ", " городской округ", " район", " округ", " городской", " мо", " го", " р-н"];
        foreach (var suf in suffixes)
            if (n.EndsWith(suf, StringComparison.Ordinal)) { n = n[..^suf.Length]; break; }
        foreach (var pre in new[] { "муниципальный округ ", "муниципальный район ", "поселение ", "район ", "округ ", "городской округ ", "город ", "муниципальное образование ", "го " })
            if (n.StartsWith(pre, StringComparison.Ordinal)) n = n[pre.Length..];
        return n.Trim();
    }

    public static IReadOnlyList<BasemapFeature> Features => _features.Value;

    private static readonly Lazy<Dictionary<(FeatureKind, int), BasemapFeature[]>> _byKind = new(() =>
        Features.GroupBy(f => (f.Kind, f.Lod)).ToDictionary(g => g.Key, g => g.ToArray()));

    /// <summary>Объекты одного вида и уровня детализации (без перебора всей подложки).</summary>
    public static IReadOnlyList<BasemapFeature> Of(FeatureKind kind, int lod) =>
        _byKind.Value.TryGetValue((kind, lod), out var a) ? a : [];

    private static readonly Lazy<Dictionary<string, BasemapFeature[]>> _regionsById = new(() =>
        Features.Where(f => f.Kind == FeatureKind.Region).GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Lod).ToArray(), StringComparer.Ordinal));

    /// <summary>Контур региона по идентификатору на нужном уровне детализации.</summary>
    public static BasemapFeature? RegionById(string id, int lod) =>
        _regionsById.Value.TryGetValue(id, out var arr) ? arr.FirstOrDefault(f => f.Lod == lod) ?? arr[0] : null;


    /// <summary>Запускает фоновую загрузку данных как можно раньше.</summary>
    public static void Preload() => Task.Run(() =>
    {
        _ = _features.Value;
        // маска РФ строится заранее, а не на первом кадре подписей
        try { _ = _russiaMask.Value; }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"маска РФ: {ex.Message}"); }
    });


    public static Point Project(GeoPoint p) => Project(p.Lat, p.Lon);

    public static Point Project(double lat, double lon)
    {
        // долгота за пределами ±180 (трек через антимеридиан) заворачивается в мир; сами ±180 не трогаем
        if (lon > 180 || lon < -180) lon -= 360 * Math.Floor((lon + 180) / 360);
        double x = (lon + 180) / 360 * WorldSize;
        double r = Math.Clamp(lat, -85.05, 85.05) * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * WorldSize;
        return new Point(x, y);
    }

    // геометрия подложки: долготы >180° (восток Чукотки) остаются справа от x=WorldSize
    private static Point ProjectGeom(double lat, double lon)
    {
        double x = (lon + 180) / 360 * WorldSize;
        double r = Math.Clamp(lat, -85.05, 85.05) * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * WorldSize;
        return new Point(x, y);
    }

    public static GeoPoint Unproject(Point w)
    {
        double lon = w.X / WorldSize * 360 - 180;
        if (lon > 180 || lon < -180) lon -= 360 * Math.Floor((lon + 180) / 360);
        double n = Math.PI - 2 * Math.PI * w.Y / WorldSize;
        return new GeoPoint(180 / Math.PI * Math.Atan(Math.Sinh(n)), lon);
    }

    private static IReadOnlyList<BasemapFeature> Load()
    {
        using var res = typeof(Basemap).Assembly.GetManifestResourceStream("Radar.basemap.bin.gz")
                        ?? throw new InvalidOperationException("Ресурс basemap.bin.gz не найден");
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        if (Encoding.ASCII.GetString(data[..4]) != Radar.Services.BasemapFormat.Magic) throw new InvalidDataException("Неверный формат подложки");
        int pos = 4;
        var list = new List<BasemapFeature>(2048);
        double[] lats = new double[4096], lons = new double[4096];
        while (true)
        {
            byte kind = data[pos++];
            if (kind == 255) break;
            byte lod = data[pos++];
            var id = ReadStr(data, ref pos);
            var name = ReadStr(data, ref pos);
            float labLat = ReadF(data, ref pos), labLon = ReadF(data, ref pos);
            int rank = BitConverter.ToInt32(data.Slice(pos, 4));
            pos += 4;
            uint parts = BitConverter.ToUInt32(data.Slice(pos, 4));
            pos += 4;

            var arr = new Point[parts][];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int p = 0; p < parts; p++)
            {
                int n = (int)BitConverter.ToUInt32(data.Slice(pos, 4));
                pos += 4;
                if (lats.Length < n) { lats = new double[n * 2]; lons = new double[n * 2]; }
                Radar.Services.BasemapFormat.ReadPart(data, ref pos, lats.AsSpan(0, n), lons.AsSpan(0, n));
                var pts = new Point[n];
                for (int i = 0; i < n; i++) pts[i] = ProjectGeom(lats[i], lons[i]);
                arr[p] = pts;
            }

            foreach (var q in arr)
                foreach (var w in q)
                {
                    if (w.X < minX) minX = w.X;
                    if (w.Y < minY) minY = w.Y;
                    if (w.X > maxX) maxX = w.X;
                    if (w.Y > maxY) maxY = w.Y;
                }

            var k = (FeatureKind)kind;
            // официальные названия субъектов РФ (в Natural Earth часть регионов подписана по-старому)
            if (k == FeatureKind.Region && Radar.Services.Gazetteer.Get(id) is { } reg) name = reg.Name;
            list.Add(new BasemapFeature
            {
                Kind = k,
                Lod = lod,
                Id = id,
                Name = name,
                Label = k == FeatureKind.City ? arr[0][0] : Project(labLat, labLon),
                Rank = rank,
                Bounds = new Rect(minX, minY, Math.Max(maxX - minX, 1e-6), Math.Max(maxY - minY, 1e-6)),
                Parts = arr,
            });
        }
        return list;
    }

    private static string ReadStr(Span<byte> d, ref int pos)
    {
        int len = BitConverter.ToUInt16(d.Slice(pos, 2));
        pos += 2;
        var s = Encoding.UTF8.GetString(d.Slice(pos, len));
        pos += len;
        return s;
    }

    private static float ReadF(Span<byte> d, ref int pos)
    {
        var f = BitConverter.ToSingle(d.Slice(pos, 4));
        pos += 4;
        return f;
    }
}
