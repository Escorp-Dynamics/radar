using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Построение маршрута трека из всех доступных данных:
///   1) место пуска — явное из сообщений или выведенное: продолжение пройденного пути назад до ближайшего
///      района пусков (Сумская, Черниговская, Харьковская области, Полтава/Днепр, Одесса/Николаев, акватории);
///   2) пройденный путь — по подтверждённым точкам, а между ними вдоль названного ориентира (трасса, ж/д, река, берег),
///      если обе точки лежат рядом с ним и путь вдоль него не слишком извилист;
///   3) прогноз — через промежуточные пункты («через …») к цели, вдоль ориентира (если он идёт в сторону цели),
///      иначе по прямой; без цели — по курсу вдоль ориентира или прямо, на типовую дальность.
/// </summary>
public static class RoutePlanner
{
    /// <summary>Районы пусков (открытые сводки): точки у границы/побережья.</summary>
    /// <remarks>По несколько точек на область: пуск выбирается по направлению «назад» от первой точки трека.</remarks>
    private static readonly (string Label, GeoPoint Pos)[] LaunchAreas =
    [
        ("Сумская обл.", new(51.87, 33.48)), ("Сумская обл.", new(51.34, 33.87)), ("Сумская обл.", new(50.91, 34.80)),
        ("Сумская обл.", new(50.31, 34.90)),
        ("Черниговская обл.", new(51.95, 33.00)), ("Черниговская обл.", new(51.50, 31.30)), ("Черниговская обл.", new(51.20, 32.40)),
        ("Харьковская обл.", new(49.99, 36.23)), ("Харьковская обл.", new(49.45, 36.90)),
        ("Полтавская обл.", new(49.59, 34.55)),
        ("Днепропетровская обл.", new(48.46, 35.05)), ("Днепропетровская обл.", new(47.90, 33.40)),
        ("Запорожская обл.", new(47.84, 35.14)),
        ("Николаевская обл.", new(46.97, 32.00)), ("Николаевская обл.", new(46.55, 31.60)),
        ("Одесская обл.", new(46.48, 30.73)), ("Одесская обл.", new(45.35, 29.60)),
        ("Херсонская обл. (правый берег)", new(46.65, 32.60)),
        ("Чёрное море", new(44.60, 32.40)), ("Чёрное море", new(44.00, 35.50)),
        ("Азовское море", new(46.30, 36.60)),
    ];

    /// <summary>Максимальное удаление точки от ориентира, при котором считаем, что объект идёт вдоль него, км.</summary>
    private const double CorridorSnapKm = 18;

    public static void Plan(Track t)
    {
        if (t.Points.Count == 0) return;
        var lines = ResolveCorridors(t, out var firstHint);
        // подпись — по той подсказке, к которой относится первая найденная линия (а не по первой подсказке вообще)
        t.CorridorLabel = lines.Count > 0 && firstHint != null ? CorridorText(firstHint, lines[0]) : null;
        // «вдоль трассы» без номера или «по трассе А — Б»: путь по графу дорог
        bool byRoads = t.RoadSegments.Count > 0 || t.Corridors.Any(c => c.Kind == CorridorKind.Road && c.Name == null);
        if (t.CorridorLabel == null && t.Target is { } tg && RouteNetwork.SeaAt(tg) is { } sea)
            t.CorridorLabel = sea == "sea:taganrog" ? "в Таганрогский залив" : sea == "sea:black" ? "в Чёрное море" : "в Азовское море";
        if (t.CorridorLabel == null && t.RoadSegments.Count > 0)
            t.CorridorLabel = $"по трассе {t.RoadSegments[^1].FromLabel} — {t.RoadSegments[^1].ToLabel}";
        else if (t.CorridorLabel == null && byRoads) t.CorridorLabel = "вдоль трассы";

        // ---- пройденный путь
        var legs = new List<List<GeoPoint>>(Math.Max(0, t.Points.Count - 1));
        for (int i = 0; i + 1 < t.Points.Count; i++)
            legs.Add(Leg(t.Points[i].Pos, t.Points[i + 1].Pos, lines, byRoads: byRoads));
        t.Legs = legs;

        // ---- предыстория: от места пуска к первой точке
        var first = t.Points[0].Pos;
        if (!t.LaunchExplicit) InferLaunch(t);
        else FixExplicitLaunch(t);
        t.PreRoute = t.Launch is { } l && Geo.DistanceKm(l, first) > 15 ? Leg(l, first, lines, allowCorridor: false) : [];

        // ---- прогноз
        var future = Future(t, lines);
        t.FutureRoute = KeepOnLand(t, future);
        t.ForecastRejected = future.Count >= 2 && t.FutureRoute.Count == 0;
    }

    /// <summary>
    /// БПЛА/ракета над сушей без точной цели не «улетает» в море: прогноз по курсу, кончающийся над водой,
    /// обрезается у берега (у Туапсе, Новороссийска, Анапы угроза приходит с моря, а не уходит в него).
    /// Пересечение залива с выходом на сушу (Ейск → Таганрог) сохраняется. Остаток короче 8 км — прогноза нет.
    /// </summary>
    internal static List<GeoPoint> KeepOnLand(Track t, List<GeoPoint> route)
    {
        if (route.Count < 2 || t.Threat is not (ThreatType.Uav or ThreatType.Missile)) return route;
        if (t.Target != null && !t.TargetIsArea) return route;
        if (RouteNetwork.IsWater(route[0]) || !RouteNetwork.IsWater(route[^1])) return route;
        const double step = 2;
        double total = Geo.PathLength(route), lastLand = 0;
        for (double km = step; km < total; km += step)
            if (!RouteNetwork.IsWater(Geo.Along(route, km))) lastLand = km;
        return lastLand < 8 ? [] : Geo.Split(route, lastLand).Before;
    }

    private static List<RouteLine> ResolveCorridors(Track t, out CorridorHint? firstHint)
    {
        var res = new List<(RouteLine Line, CorridorHint Hint)>();
        foreach (var h in t.Corridors)
            foreach (var l in RouteNetwork.Find(h))
            {
                if (res.Any(x => x.Line == l)) continue;
                // ориентир должен проходить рядом хотя бы с одной точкой трека (иначе это другой участок/омоним)
                if (t.Points.Any(p => RouteNetwork.Nearest(l, p.Pos, CorridorSnapKm) != null)) res.Add((l, h));
            }
        // для неименованных коридоров («вдоль трассы», «вдоль жд») — только ближайшая линия этого вида
        if (res.Count > 1 && t.Corridors.All(c => c.Name == null))
        {
            var head = t.Head.Pos;
            res = [.. res.OrderBy(x => RouteNetwork.Nearest(x.Line, head, CorridorSnapKm)?.DistKm ?? double.MaxValue).Take(1)];
        }
        firstHint = res.Count > 0 ? res[0].Hint : null;
        return [.. res.Select(x => x.Line)];
    }

    private static string CorridorText(CorridorHint h, RouteLine l) => h.Kind switch
    {
        CorridorKind.Road => "вдоль трассы " + (l.Id.StartsWith('М') || l.Id.StartsWith('Р') || l.Id.StartsWith('А') ? l.Id : h.Name ?? ""),
        CorridorKind.Rail => "вдоль ж/д",
        CorridorKind.River => "вдоль реки" + (h.Name is { } n ? " " + Cap(n) : ""),
        CorridorKind.Coast => "вдоль побережья",
        CorridorKind.Border => "вдоль границы регионов",
        _ => "",
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // Кэш отрезков: план пересчитывается при каждом сообщении трека, а пути по дорогам и ориентирам не меняются
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<GeoPoint>> _legCache = new(StringComparer.Ordinal);

    /// <summary>Отрезок между двумя точками: вдоль ориентира, если он подходит, по графу дорог, иначе прямая.</summary>
    private static List<GeoPoint> Leg(GeoPoint a, GeoPoint b, List<RouteLine> lines, bool allowCorridor = true, bool byRoads = false)
    {
        if (!allowCorridor || (!byRoads && lines.Count == 0)) return [a, b];
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{a.Lat:F4},{a.Lon:F4}|{b.Lat:F4},{b.Lon:F4}|{byRoads}|{string.Join(",", lines.Select(l => l.Id))}");
        if (_legCache.TryGetValue(key, out var cached)) return cached;
        var leg = ComputeLeg(a, b, lines, allowCorridor, byRoads);
        if (_legCache.Count > 4000) _legCache.Clear();
        _legCache[key] = leg;
        return leg;
    }

    private static List<GeoPoint> ComputeLeg(GeoPoint a, GeoPoint b, List<RouteLine> lines, bool allowCorridor, bool byRoads)
    {
        double direct = Geo.DistanceKm(a, b);
        if (allowCorridor && byRoads && direct > 3 && RoadGraph.Route(a, b, 1.6) is { Count: >= 2 } road) return road;
        if (allowCorridor && direct > 4)
        {
            List<GeoPoint>? best = null;
            double bestLen = double.MaxValue;
            foreach (var l in lines)
            {
                if (RouteNetwork.Between(l, a, b, CorridorSnapKm) is not { Count: >= 2 } path) continue;
                double len = Geo.PathLength(path);
                // слишком извилисто (петля реки, развязка) — путь вдоль ориентира не правдоподобен;
                // административная граница по OSM повторяет реки и овраги — для неё допуск шире
                if (len > direct * (l.Kind == CorridorKind.Border ? 2.0 : 1.6) + 6) continue;
                if (len < bestLen) { bestLen = len; best = path; }
            }
            if (best != null)
            {
                // «пришить» концы к точным позициям фиксаций
                if (Geo.DistanceKm(best[0], a) > 0.3) best.Insert(0, a);
                if (Geo.DistanceKm(best[^1], b) > 0.3) best.Add(b);
                return best;
            }
        }
        return [a, b];
    }

    /// <summary>
    /// Предполагаемое место пуска: район пусков, лежащий «позади» первой точки по курсу движения (обратный курс ±50°),
    /// ближайший; для ракет — дальше, для FPV/РСЗО — не выводится (это не дальние цели).
    /// </summary>
    private static void InferLaunch(Track t)
    {
        t.Launch = null;
        t.LaunchLabel = null;
        if (t.Threat is not (ThreatType.Uav or ThreatType.Missile or ThreatType.Aviation)) return;
        if (t.Kind is ThreatKind.UavFpv or ThreatKind.UavFiber or ThreatKind.UavHeavy or ThreatKind.GuidedBomb) return;
        var first = t.Points[0].Pos;
        // курс у начала пути: к первой точке не ближе 20 км (соседние отметки в 5–10 км дают случайный курс),
        // иначе к самой дальней из близких, иначе общий курс трека
        var ahead = t.Points.Skip(1).FirstOrDefault(p => Geo.DistanceKm(first, p.Pos) >= 20)
                    ?? t.Points.Skip(1).Where(p => Geo.DistanceKm(first, p.Pos) > 5).MaxBy(p => Geo.DistanceKm(first, p.Pos));
        double? course = ahead != null ? Geo.Bearing(first, ahead.Pos) : t.Bearing;
        if (course is not { } c) return;
        // начало пути зигзагом расходится с общим направлением — общее надёжнее
        var last = t.Head.Pos;
        double total = Geo.DistanceKm(first, last);
        double? overall = total > 10 ? Geo.Bearing(first, last) : null;
        if (overall is { } ov && total > 30 && Geo.AngleDiff(c, ov) > 90) c = ov;
        // результат зависит только от первой точки, курса (с шагом 2°) и класса объекта — дорогие лучи/перебор кешируются
        // дальность зависит от типа средства: РСЗО не пущена за сотни километров, дальний дрон — может
        double reach = LaunchRange.MaxKm(t.Kind, t.Threat);
        var key = (first, (int)Math.Round(c / 2), t.Threat, (int)Math.Round(reach));
        // линия соприкосновения обновилась — выведенные места захода пересчитываются
        var ver = Borderline.Version;
        if (_launchVersion != ver) { _launchCache.Clear(); _launchVersion = ver; }
        if (!_launchCache.TryGetValue(key, out var hit))
        {
            hit = ComputeLaunch(first, (key.Item2 * 2 + 360) % 360, t.Threat, reach);
            if (_launchCache.Count > 20_000) _launchCache.Clear();
            // линия обновилась за время расчёта — устаревший результат в кеш не кладём
            if (ver == Borderline.Version) _launchCache[key] = hit;
        }
        // место пуска оказалось впереди по общему пути — лучше не показывать предысторию, чем показать неверную
        if (hit is { } h && overall is { } o2 && Geo.AngleDiff(Geo.Bearing(first, h.Pos), (o2 + 180) % 360) > 90) hit = null;
        if (hit is { } h2) { t.Launch = h2.Pos; t.LaunchLabel = h2.Label; }
    }

    /// <summary>
    /// Место пуска из текста лежит впереди по пройденному пути: «с Азовского моря» привязано к пункту позднего сообщения
    /// (у Новороссийска, а трек начался у Керчи), либо трек склеен из двух объектов. Акватория — переносим точку
    /// за первую отметку (та же сторона, но позади); иначе предысторию не рисуем — неверная хуже отсутствующей.
    /// </summary>
    private static void FixExplicitLaunch(Track t)
    {
        if (t.Launch is not { } l) return;
        var first = t.Points[0].Pos;
        if (t.Points.Count < 2) { ExplicitNotAzov(t, first); return; }
        var last = t.Head.Pos;
        double total = Geo.DistanceKm(first, last);
        // морская точка у самого берега (ближайшая к пункту часть акватории) тоже проверяется: при пролёте вдоль побережья
        // (Геленджик → Анапа, Туапсе → Сочи) она оказывается впереди по пути
        if (total <= 10 || Geo.DistanceKm(first, l) < 3 || Geo.DistanceKm(first, l) < 10 && !RouteNetwork.IsWater(l)) { ExplicitNotAzov(t, first); return; }
        double back = (Geo.Bearing(first, last) + 180) % 360;
        if (Geo.AngleDiff(Geo.Bearing(first, l), back) <= 90) { ExplicitNotAzov(t, first); return; }
        if (RouteNetwork.IsWater(l) && SeaBehind(first, back) is { } sea)
        {
            t.Launch = sea.Pos;
            t.LaunchLabel = sea.Label;
            ExplicitNotAzov(t, first);
            return;
        }
        t.Launch = null;
        t.LaunchLabel = null;
    }

    /// <summary>
    /// Азовское море (с Таганрогским заливом) — внутреннее море РФ, тыл: пусков оттуда не бывает. Объект лишь пролетел над ним,
    /// а пущен из-за линии соприкосновения. Место пуска переносится по прямой от первой отметки через море за линию
    /// (ближайшее пересечение в секторе ±40° от направления на море, +25 км за линию). null — линии в этой стороне нет.
    /// </summary>
    internal static (GeoPoint Pos, string Label)? BeyondFront(GeoPoint first, GeoPoint sea)
    {
        double dir = Geo.Bearing(first, sea);
        (double Km, double Brg)? best = null;
        double bestScore = double.MaxValue;
        foreach (var off in new[] { 0.0, -10, 10, -20, 20, -30, 30, -40, 40 })
        {
            double brg = (dir + off + 360) % 360;
            if (Borderline.FrontCrossingKm(first, brg, 800) is not { } km) continue;
            double score = km * (1 + Math.Abs(off) / 30);
            if (score < bestScore) { bestScore = score; best = (km, brg); }
        }
        if (best is not { } b) return null;
        return (Geo.Destination(first, b.Brg, b.Km + 25), "из-за линии соприкосновения");
    }

    /// <summary>Точка в акватории Азовского моря или Таганрогского залива (или подпись указывает на неё).</summary>
    internal static bool IsAzov(GeoPoint p, string? label) =>
        RouteNetwork.SeaAt(p) is "sea:azov" or "sea:taganrog" ||
        label != null && (label.Contains("Азовск", StringComparison.Ordinal) || label.Contains("Таганрогск", StringComparison.Ordinal));

    private static (GeoPoint Pos, string Label)? NotFromAzov(GeoPoint first, (GeoPoint Pos, string Label)? hit) =>
        hit is { } h && IsAzov(h.Pos, h.Label) ? BeyondFront(first, h.Pos) : hit;

    /// <summary>«С Азовского моря» — сторона захода, а не место пуска: пуск переносится за линию соприкосновения.</summary>
    private static void ExplicitNotAzov(Track t, GeoPoint first)
    {
        if (t.Launch is not { } l || t.Threat is not (ThreatType.Uav or ThreatType.Missile) || !IsAzov(l, t.LaunchLabel)) return;
        var hit = BeyondFront(first, l);
        t.Launch = hit?.Pos;
        t.LaunchLabel = hit?.Label;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(GeoPoint, int, ThreatType, int), (GeoPoint Pos, string Label)?> _launchCache = new();
    private static int _launchVersion;

    private static (GeoPoint Pos, string Label)? ComputeLaunch(GeoPoint first, double c, ThreatType threat, double reach) =>
        NotFromAzov(first, ComputeLaunchRaw(first, c, threat, reach));

    private static (GeoPoint Pos, string Label)? ComputeLaunchRaw(GeoPoint first, double c, ThreatType threat, double reach)
    {
        double back = (c + 180) % 360;
        // прежние потолки (900 / 700 км) остаются верхней границей, но не выше дальности самого средства
        double maxKm = Math.Min(threat == ThreatType.Missile ? 900 : 700, reach);
        (string Label, GeoPoint Pos)? best = null;
        double bestScore = double.MaxValue;
        foreach (var a in LaunchAreas)
        {
            double km = Geo.DistanceKm(first, a.Pos);
            if (km < 20 || km > maxKm) continue;
            double ang = Geo.AngleDiff(Geo.Bearing(first, a.Pos), back);
            if (ang > 50) continue;
            double score = km * (1 + ang / 35);
            if (score < bestScore) { bestScore = score; best = a; }
        }
        var zone = Borderline.ZoneOf(first);
        // морской заход в Крым/Кубань: путь начинается над морем позади первой отметки (выход на берег — там, где её засекли)
        if (threat is ThreatType.Uav or ThreatType.Missile && zone == Borderline.Zone.Sea &&
            SeaBehind(first, back) is { } sea)
        {
            return sea;
        }
        // сухопутный заход: путь начинается на линии соприкосновения позади первой отметки (если она ближе района пусков)
        // объект, идущий вдоль линии соприкосновения (типично для ЛНР/ДНР), её не пересекает — место захода по курсу не определить
        bool alongFront = zone == Borderline.Zone.Land &&
            Borderline.DistanceKm(Geo.Destination(first, c, 25)) < Borderline.DistanceKm(first) + 8;
        if (alongFront && Borderline.DistanceKm(first) < 120) return null;
        if (threat is ThreatType.Uav or ThreatType.Missile && zone is Borderline.Zone.Land or Borderline.Zone.None &&
            Borderline.Behind(first, back, Math.Min(maxKm, 450)) is { } edge &&
            (best is not { } b0 || Geo.DistanceKm(first, edge.Pos) < Geo.DistanceKm(first, b0.Pos)))
        {
            return edge;
        }
        // глубоко на суше без линии соприкосновения позади — далёкий «район пусков» по ту сторону фронта был бы выдумкой
        if (best is { } far && zone == Borderline.Zone.Land && Geo.DistanceKm(first, far.Pos) > Borderline.DistanceKm(first) + 200) return null;
        return best is { } bb ? (bb.Pos, bb.Label) : null;
    }

    /// <summary>
    /// Точка над открытым морем позади <paramref name="p"/>: по обратному курсу, при движении вдоль берега — со смещением
    /// к морю (до ±80°, с штрафом за отклонение). Точка не ближе 20 км к первой отметке и в 20 км от берега,
    /// чтобы предыстория была видна и не начиналась в бухте. null — моря позади нет (заход по суше через Чонгар/Перекоп).
    /// </summary>
    private static (GeoPoint Pos, string Label)? SeaBehind(GeoPoint p, double back)
    {
        static bool Open(GeoPoint q) => RouteNetwork.SeaAt(q) is "sea:black" or "sea:azov";
        // прямо позади — суша материка (север Крыма: Перекоп, Чонгар) — заход по суше, а не с моря
        bool waterSoon = false;
        for (double km = 3; km <= 40 && !waterSoon; km += 3) waterSoon = RouteNetwork.IsWater(Geo.Destination(p, back, km));
        if (!waterSoon && Borderline.ZoneOf(Geo.Destination(p, back, 40)) != Borderline.Zone.Sea) return null;
        (GeoPoint, string)? best = null;
        double bestScore = double.MaxValue;
        foreach (var off in new[] { 0.0, -20, 20, -40, 40, -60, 60, -80, 80 })
        {
            double brg = (back + off + 360) % 360;
            for (double km = 3; km <= 220; km += 3)
            {
                var q = Geo.Destination(p, brg, km);
                if (!Open(q)) continue;
                var start = Geo.Destination(p, brg, Math.Max(km + 20, 25));
                if (!Open(start)) break; // узкая полоса воды (пролив, бухта) — по этому лучу открытого моря нет
                double score = (km + 20) * (1 + Math.Abs(off) / 25);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (start, RouteNetwork.SeaAt(start) == "sea:black" ? "Чёрное море" : "Азовское море");
                }
                break;
            }
        }
        return best;
    }

    private static List<GeoPoint> Future(Track t, List<RouteLine> lines)
    {
        if (t.Cleared) return [];
        var head = t.Head.Pos;
        var stops = new List<GeoPoint>();
        // промежуточные пункты впереди (не пройденные): те, что ближе к цели/по курсу, чем голова
        foreach (var (pos, _) in t.Via)
        {
            if (Geo.DistanceKm(head, pos) < 5) continue;
            if (t.Points.Any(p => Geo.DistanceKm(p.Pos, pos) < 5)) continue;
            if (t.Bearing is { } br0 && Geo.AngleDiff(br0, Geo.Bearing(head, pos)) > 100) continue;
            // пункт позади по последнему перемещению и рядом с пройденной отметкой (Унечский район у Рохманово, а голова
            // уже в Почепе) — пройден, даже если курс словами («на север») формально оставляет его сбоку
            if (t.Points.Count >= 2 && Geo.DistanceKm(t.Points[^2].Pos, head) >= 10 &&
                Geo.AngleDiff(Geo.Bearing(t.Points[^2].Pos, head), Geo.Bearing(head, pos)) > 100 &&
                t.Points.Take(t.Points.Count - 1).Any(p => Geo.DistanceKm(p.Pos, pos) < Geo.DistanceKm(head, pos))) continue;
            // «через» пункт, который ближе к линии пуска, — это уже пройденное или другой объект: БПЛА не возвращается
            if (t.Target == null && LeadsBack(t, head, pos)) continue;
            stops.Add(pos);
        }
        stops.Sort((x, y) => Geo.DistanceKm(head, x).CompareTo(Geo.DistanceKm(head, y)));

        // «по трассе А — Б»: конец участка впереди — промежуточная точка (если голова уже на этом участке)
        bool byRoads = t.RoadSegments.Count > 0 || t.Corridors.Any(c => c.Kind == CorridorKind.Road && c.Name == null);
        bool segmentEnd = false;
        foreach (var sg in t.RoadSegments)
        {
            foreach (var end in new[] { sg.To, sg.From })
            {
                if (Geo.DistanceKm(head, end) < 5 || t.Points.Any(p => Geo.DistanceKm(p.Pos, end) < 5)) continue;
                if (t.Bearing is { } bb && Geo.AngleDiff(bb, Geo.Bearing(head, end)) > 70) continue;
                // «трасса Токмак — Бердянск»: направление по трассе не сказано — конец у линии пуска не берём
                if (t.Target == null && LeadsBack(t, head, end)) continue;
                if (!stops.Any(x => Geo.DistanceKm(x, end) < 3)) stops.Add(end);
                segmentEnd = true;
                break;
            }
        }
        stops.Sort((x, y) => Geo.DistanceKm(head, x).CompareTo(Geo.DistanceKm(head, y)));

        if (t.Target is { } target && Geo.DistanceKm(head, target) > 3)
        {
            stops.RemoveAll(s => Geo.DistanceKm(s, target) < 3 || Geo.DistanceKm(s, target) > Geo.DistanceKm(head, target) + 10);
            stops.Add(target);
            var path = new List<GeoPoint> { head };
            var cur = head;
            foreach (var s in stops)
            {
                var leg = Leg(cur, s, lines.Where(l => GoesToward(l, cur, s)).ToList(), byRoads: byRoads && Geo.DistanceKm(cur, s) < 150);
                path.AddRange(leg.Skip(1));
                cur = s;
            }
            // цель — регион/район: известно только направление, центр области не место назначения — прогноз обычной длины
            if (t.TargetIsArea)
            {
                // область рядом, но её центр позади по пути (Валуйки → «в направлении Воронежской области»):
                // прогноз — к ближайшему краю области по курсу, а не разворот к центру
                if (stops.Count == 1 && AreaEntry(t, head, target) is { } entry)
                    path = Leg(head, entry, [], byRoads: false);
                double areaKm = CourseKm(t.Threat);
                if (Geo.PathLength(path) > areaKm) path = Geo.Split(path, areaKm).Before;
                // область названа лишь направлением, а прогноз к ней ведёт обратно к линии пуска (сводка «…ожидаем налёт
                // и в направлении Краснодарского края» у трека под Курском) — направление не про этот объект: не рисуем
                if (stops.Count == 1 && path.Count >= 2 && LeadsBack(t, head, path[^1])) return [];
            }
            return path;
        }

        // без цели — по курсу: вдоль ориентира, если он идёт в нужную сторону, иначе прямо
        double? bearing = t.Bearing;
        if (bearing is not { } b) return [];
        if (t.Points.Count < 2 && t.HeadingHint == null && stops.Count == 0) return [];
        // две отметки рядом (Горловка → Горловка — Центр, Каховка → Новая Каховка) у линии соприкосновения:
        // курс по ним — шум привязки, а объект может лететь куда угодно — прогноз лишь ввёл бы в заблуждение
        // то же для двух отметок, ведущих к самой линии (Горловка → Донецк): объекты оттуда приходят, а не уходят туда
        if (t.Points.Count == 2 && t.HeadingHint == null && stops.Count == 0 && Borderline.DistanceKm(head) < 100 &&
            (Geo.DistanceKm(t.Points[0].Pos, t.Points[1].Pos) < 20 ||
             t.Threat is ThreatType.Uav or ThreatType.Missile && Borderline.DistanceKm(head) < Borderline.DistanceKm(t.Points[0].Pos) - 3))
            return [];
        double km = CourseKm(t.Threat);
        var route = new List<GeoPoint> { head };
        var from = head;
        foreach (var s in stops) { route.AddRange(Leg(from, s, lines, byRoads: byRoads).Skip(1)); from = s; }
        // «по трассе А — Б»: дальше конца участка не экстраполируем — это и есть известное направление
        if (segmentEnd) return route;
        double rest = Math.Max(0, km - Geo.PathLength(route));
        if (rest > 1)
        {
            // после промежуточного пункта — направление на него (от головы или предыдущего пункта), а не последний изгиб
            // дороги к нему: дорога может подходить к пункту с обратной стороны (М-4 → «через Урывский» — курс на запад)
            var prevStop = stops.Count > 1 ? stops[^2] : head;
            double course = stops.Count > 0 && Geo.DistanceKm(prevStop, stops[^1]) >= 5 ? Geo.Bearing(prevStop, stops[^1])
                : route.Count >= 2 ? Geo.Bearing(route[^2], route[^1]) : b;
            // курс словами («группа на восток») надёжнее курса по двум соседним отметкам в одном городе (Горловка → Горловка — Центр)
            if (t.HeadingHint is { } hh && stops.Count == 0 && route.Count < 2 && t.Points.Count >= 2 &&
                Geo.DistanceKm(t.Points[^2].Pos, t.Points[^1].Pos) < 15 && Geo.AngleDiff(course, hh) > 90)
                course = hh;
            // курс по отметкам ведёт обратно к линии пуска, а курс словами («на северо-восток») — нет:
            // отметки — центры районов и пункты вдоль границы (Миллерово → Чертково → Кантемировка), словам доверяем больше
            if (t.HeadingHint is { } hh2 && stops.Count == 0 && route.Count < 2 && t.Threat is ThreatType.Uav or ThreatType.Missile &&
                Geo.AngleDiff(course, hh2) > 30 &&
                BackAlong(from, course, rest, t.Points[0].Pos) > 20 &&
                BackAlong(from, hh2, rest, t.Points[0].Pos) <= 20)
                course = hh2;
            // курс, выведенный только из двух отметок, ведёт обратно к линии пуска — это шум привязки
            // (центр района → пункт в нём), а не разворот: прогноз не рисуем. Курс словами («на юг») — сохраняем.
            if (t.HeadingHint == null && stops.Count == 0 && t.Threat is ThreatType.Uav or ThreatType.Missile &&
                BackAlong(from, course, rest, t.Points[0].Pos) > Math.Clamp(Borderline.DistanceKm(from) * 0.5, 8, 20))
                return [];
            // продолжение за промежуточными пунктами ведёт к линии пуска — прогноз заканчивается на последнем пункте
            if (t.HeadingHint == null && stops.Count > 0 && t.Threat is ThreatType.Uav or ThreatType.Missile &&
                BackAlong(from, course, rest, t.Points[0].Pos) > Math.Clamp(Borderline.DistanceKm(from) * 0.5, 8, 20))
                return route;
            List<GeoPoint>? along = null;
            foreach (var l in lines)
                if (RouteNetwork.Follow(l, from, course, rest, CorridorSnapKm) is { Count: >= 2 } f &&
                    Geo.AngleDiff(Geo.Bearing(f[0], f[^1]), course) < 60)
                { along = f; break; }
            // прямой курс прошёл проверку «не назад», а дорога от него сворачивает к линии пуска (Акимовка → по трассе
            // на запад, к Днепру): по дороге не ведём, остаётся прямой курс
            if (along != null && t.Threat is ThreatType.Uav or ThreatType.Missile &&
                Borderline.BackKm(from, along[^1], t.Points[0].Pos) > Math.Clamp(Borderline.DistanceKm(from) * 0.5, 8, 20) &&
                BackAlong(from, course, rest, t.Points[0].Pos) <= 20)
                along = null;
            if (along != null) route.AddRange(along.Skip(Geo.DistanceKm(along[0], from) < 0.3 ? 1 : 0));
            else route.Add(Geo.Destination(from, course, rest));
        }
        return route;
    }

    /// <summary>БПЛА/ракета к этой точке шли бы обратно к линии пуска (не к названной цели).</summary>
    private static bool LeadsBack(Track t, GeoPoint head, GeoPoint p) =>
        t.Threat is ThreatType.Uav or ThreatType.Missile &&
        Borderline.BackKm(head, p, t.Points[0].Pos) > Math.Clamp(Borderline.DistanceKm(head) * 0.5, 8, 20);

    /// <summary>
    /// Наибольшее приближение к линии пуска на протяжении курса (а не только в его конце): курс, пересекающий линию
    /// (Скадовск → на северо-запад, за Днепр), в конце снова далёк от неё, но по пути ведёт назад.
    /// </summary>
    private static double BackAlong(GeoPoint from, double course, double km, GeoPoint entry)
    {
        double best = 0;
        for (double d = 10; d < km; d += 10)
            best = Math.Max(best, Borderline.BackKm(from, Geo.Destination(from, course, d), entry));
        // конец курса проверяется всегда, а не только когда km кратно шагу
        return Math.Max(best, Borderline.BackKm(from, Geo.Destination(from, course, km), entry));
    }

    /// <summary>Регион РФ по точке (контур подложки); задаётся при запуске приложения — без него вход в область не ищется.</summary>
    public static Func<GeoPoint, string?>? RegionAt { get; set; }

    /// <summary>
    /// Точка входа в область-цель: ближайшая точка области в секторе ±60° от курса движения, если направление
    /// на центр области уводит назад (> 90° от курса). null — центр и так впереди или области не найти.
    /// </summary>
    private static GeoPoint? AreaEntry(Track t, GeoPoint head, GeoPoint center)
    {
        if (RegionAt is not { } regionAt || t.Points.Count < 2) return null;
        var prev = t.Points[^2].Pos;
        if (Geo.DistanceKm(prev, head) < 10) return null;
        double course = Geo.Bearing(prev, head);
        if (Geo.AngleDiff(course, Geo.Bearing(head, center)) <= 90) return null;
        var region = regionAt(center);
        if (region == null || regionAt(head) == region) return null;
        GeoPoint? best = null;
        double bestKm = double.MaxValue;
        for (int off = -60; off <= 60; off += 10)
        {
            double brg = (course + off + 360) % 360;
            for (double km = 5; km <= 200 && km < bestKm; km += 5)
            {
                var q = Geo.Destination(head, brg, km);
                if (regionAt(q) != region) continue;
                // чуть вглубь от границы — прогноз заходит в область
                best = Geo.Destination(head, brg, km + 10);
                bestKm = km;
                break;
            }
        }
        return best;
    }

    /// <summary>Длина прогноза по курсу (без точной цели), км.</summary>
    private static double CourseKm(ThreatType threat) =>
        threat switch { ThreatType.Missile => 140, ThreatType.Aviation => 80, ThreatType.Naval => 25, _ => 60 };

    /// <summary>Ориентир ведёт от <paramref name="a"/> к <paramref name="b"/>: обе точки рядом с ним.</summary>
    private static bool GoesToward(RouteLine l, GeoPoint a, GeoPoint b) =>
        RouteNetwork.Nearest(l, a, CorridorSnapKm) != null && RouteNetwork.Nearest(l, b, CorridorSnapKm * 1.5) != null;
}
