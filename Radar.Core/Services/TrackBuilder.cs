using Radar.Models;

namespace Radar.Services;

/// <summary>Наблюдение, извлечённое из одного сообщения.</summary>
public sealed record Observation(
    AlertEvent Event,
    ThreatType Threat,
    ThreatKind Kind,
    GeoPoint Anchor,
    string Label,
    GeoPoint? Target,
    string? TargetLabel,
    double? Heading,
    int Count,
    DateTimeOffset Time,
    string Channel,
    bool Explicit)
{
    /// <summary>Подсказки маршрута из того же сообщения: коридоры и место пуска.</summary>
    public RouteHints? Route { get; init; }
    /// <summary>Промежуточные пункты («через …»).</summary>
    public IReadOnlyList<(GeoPoint Pos, string Label)> Via { get; init; } = [];
    /// <summary>Цель — регион, район или акватория («в направлении Ростовской области»): известно направление, а не точка.</summary>
    public bool TargetIsArea { get; init; }
    /// <summary>Неточность привязки позиции, км (0 — пункт; район — его радиус).</summary>
    public double SpreadKm { get; init; }
    /// <summary>
    /// Действующее предупреждение, а не новая засечка: «… опасность сохраняется» без направления, перепост без типа угрозы.
    /// Подтверждает трек на месте, но не продлевает его в новую точку.
    /// </summary>
    public bool Standing { get; init; }
    /// <summary>
    /// Отметка из сводки по разнесённым районам («Кимовский ⏎ Воловский ⏎ Щекинский … фиксации»): продолжает уже идущий
    /// трек, но нового не начинает — иначе отметки разных сводок склеиваются в маршруты, которых в сообщениях нет.
    /// </summary>
    public bool ContinueOnly { get; init; }
}

/// <summary>
/// Многоцелевой трекер: сопоставляет наблюдения с треками по прогнозному положению (счисление пути),
/// согласованности курса, заявленной цели и времени; оценивает скорость; сливает дубли.
/// </summary>
public sealed class TrackBuilder
{
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;
    /// <summary>Для аудита: каждое измерение скорости (трек, прежняя точка, наблюдение, км, часы, км/ч, принято).</summary>
    public static Action<Track, TrackPoint, Observation, double, double, double, bool>? SpeedAudit;

    public IReadOnlyList<Track> Tracks => _tracks;

    private static TimeSpan MaxGap(ThreatType t) => t switch
    {
        ThreatType.Missile => TimeSpan.FromMinutes(15),
        ThreatType.Aviation => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(80),
    };

    /// <summary>
    /// Пункты сводки по разнесённым районам (3+ пункта, разброс от центра больше 45 км) — несколько групп, а не один объект;
    /// null — если пункты компактны (одна группа) или их меньше трёх.
    /// </summary>
    public static IReadOnlyList<MapPoint>? ScatteredGroups(IReadOnlyList<MapPoint> points)
    {
        var concrete = points.Where(p => !p.IsRegionLevel && p.RadiusKm <= 40).ToList();
        if (concrete.Count < 3) return null;
        var c = new GeoPoint(concrete.Average(p => p.Pos.Lat), concrete.Average(p => p.Pos.Lon));
        return concrete.Max(p => Geo.DistanceKm(p.Pos, c)) > 45 ? concrete : null;
    }

    /// <summary>Строит наблюдение из разобранного сообщения; null — если в нём нет локализуемого объекта.</summary>
    public static Observation? Observe(AlertEvent ev, ThreatType threat, IReadOnlyList<MapPoint> points, GeoPoint? origin,
        GeoPoint? target, string? targetLabel, double? heading, int count, DateTimeOffset time, string channel)
    {
        if (threat is ThreatType.General or ThreatType.None or ThreatType.Artillery or ThreatType.Ground) return null;
        // «южная часть области» (зона > 40 км) — не позиция объекта, трек по ней не строится
        var concrete = points.Where(p => !p.IsRegionLevel && p.RadiusKm <= 40).ToList();

        GeoPoint anchor;
        string label;
        bool explicitPos;
        double spread;
        // район/зона: точка ставится в центр, объект может быть где угодно в радиусе
        static double Spread(MapPoint p) => p.RadiusKm >= 20 ? p.RadiusKm : 0;
        if (origin is { } o)
        {
            anchor = o;
            var op = concrete.FirstOrDefault(p => p.Pos == o) ?? points.FirstOrDefault(p => p.Pos == o);
            label = op?.Label ?? "";
            explicitPos = concrete.Any(p => p.Pos == o);
            spread = op != null ? Spread(op) : 0;
        }
        else if (concrete.Count is 1 or 2)
        {
            // два соседних пункта — объект между ними
            anchor = concrete.Count == 1 ? concrete[0].Pos : Geo.Lerp(concrete[0].Pos, concrete[1].Pos, 0.5);
            spread = concrete.Max(Spread);
            label = string.Join(" — ", concrete.Select(p => p.Label));
            // далёкие пункты — объект у последнего названного, подпись — его
            if (concrete.Count == 2 && Geo.DistanceKm(concrete[0].Pos, concrete[1].Pos) > 60) { anchor = concrete[^1].Pos; spread = Spread(concrete[^1]); label = concrete[^1].Label; }
            else if (concrete.Count == 2) spread = Math.Max(spread, Geo.DistanceKm(concrete[0].Pos, concrete[1].Pos) / 2);
            explicitPos = true;
        }
        else if (concrete.Count > 2)
        {
            // перечисление пунктов: если они компактны — центр группы, иначе это не один объект
            var c = new GeoPoint(concrete.Average(p => p.Pos.Lat), concrete.Average(p => p.Pos.Lon));
            double far = concrete.Max(p => Geo.DistanceKm(p.Pos, c));
            if (far > 45 && target == null) return null;
            anchor = c;
            label = concrete[0].Label + " и др.";
            explicitPos = true;
            spread = Math.Max(far, concrete.Max(Spread));
        }
        else return null;

        return new Observation(ev, threat, ev.Kind, anchor, label, target, targetLabel, heading, count, time, channel, explicitPos)
        {
            SpreadKm = spread < 15 ? 0 : spread,
        };
    }

    public Track? Add(Observation o)
    {
        if (o.Target is { } ot0 && (Geo.DistanceKm(ot0, o.Anchor) < 12 || TowardsLaunchFromRear(o.Anchor, ot0, o.Threat, null)))
            o = o with { Target = null, TargetLabel = null, TargetIsArea = false };
        if (o.Heading is { } oh0 && TowardsLaunchFromRear(o.Anchor, Geo.Destination(o.Anchor, oh0, 60), o.Threat, null))
            o = o with { Heading = null };
        var track = FindMatch(o);
        if (track == null && o.ContinueOnly) return null;
        if (track == null)
        {
            track = new Track
            {
                Id = _nextId++, Threat = o.Threat, Kind = o.Kind, Created = o.Time, Updated = o.Time, Standing = o.Standing,
                SpeedKmh = Track.DefaultSpeed(o.Kind, o.Threat),
            };
            // группа отделилась от существующего трека (тот же пункт, другая цель) — наследуем общий путь
            var parent = _tracks.FirstOrDefault(t => t.Threat == o.Threat && !t.Cleared &&
                                                     Math.Abs((o.Time - t.Updated).TotalMinutes) < 20 &&
                                                     Geo.DistanceKm(t.Head.Pos, o.Anchor) < 20 && t.Points.Count >= 2 &&
                                                     !GoesBack(t.Points[^2].Pos, o.Anchor, o.Threat, t.Points[0].Pos) &&
                                                     // новая группа «в тыл» у линии не наследует путь группы, ушедшей глубже
                                                     !(o.Route is { ToRear: true } && BackKm(t.Points[^2].Pos, o.Anchor, o.Threat, t.Points[0].Pos) > 10));
            // унаследованный путь не должен давать петлю (Таврическая ТЭС → Ялта → Байдарская долина → Инкерман)
            if (parent != null && IsLoop([.. parent.Points.Take(parent.Points.Count - 1).TakeLast(3), new TrackPoint(o.Anchor, o.Time, o.Label, o.Channel, o.SpreadKm)]))
                parent = null;
            // ФПВ запускается у линии, а не отделяется от группы дальних БПЛА: чужой путь не наследует
            // (у линии, в пределах дальности ФПВ; вдали от неё — в Крыму — ФПВ приносит носитель, и путь его общий)
            if (parent != null && IsShortRange(o.Kind) && !IsShortRange(parent.Kind) && Borderline.DistanceKm(o.Anchor) < FpvLineKm) parent = null;
            // подтверждение опасности по площади не продолжает чужой путь к линии
            if (parent != null && o.Standing && BackKm(parent.Head.Pos, o.Anchor, o.Threat, parent.Points[0].Pos) > 3) parent = null;
            // путь к отметке идёт против цели/курса из сообщения («от Бессоновки в сторону Белгорода» после «Строитель»):
            // это встречная группа, а не ответвление. Только на суше: в морской зоне (Крым) объекты облетают полуостров
            if (parent != null && Borderline.ZoneOf(o.Anchor) != Borderline.Zone.Sea && StatedCourse(o) is { } sc && Geo.DistanceKm(parent.Points[^2].Pos, o.Anchor) > 15 &&
                Geo.AngleDiff(sc, Geo.Bearing(parent.Points[^2].Pos, o.Anchor)) > 120)
                parent = null;
            // как и при сопоставлении: у короткого пути курс не подтверждён, ответвление ближе к линии, чем его начало, — другая группа
            if (parent != null && parent.Points.Count <= 3 && !ShortTrackMayGoBack(parent, o)) parent = null;
            if (parent != null)
            {
                foreach (var p in parent.Points.Take(parent.Points.Count - 1).TakeLast(3)) track.Points.Add(p);
                track.SpeedKmh = parent.SpeedKmh;
                track.SpeedSamples = parent.SpeedSamples;
                track.SpeedLog.AddRange(parent.SpeedLog);
            }
            track.Points.Add(new TrackPoint(o.Anchor, o.Time, o.Label, o.Channel, o.SpreadKm));
            _tracks.Add(track);
        }
        else
        {
            Integrate(track, o);
            if (!o.Standing) track.Standing = false;
        }

        // цель из запоздавшего (более старого) сообщения не перезаписывает свежую
        if (o.Target != null && o.Time >= track.Updated - TimeSpan.FromMinutes(1))
        {
            track.Target = o.Target;
            track.TargetLabel = o.TargetLabel;
            track.TargetIsArea = o.TargetIsArea;
            track.TargetAt = o.Time;
        }
        else if (track.Target is { } t && (Geo.DistanceKm(track.Head.Pos, t) < 12 || TargetBehind(track, t) || StaleBack(track, track.TargetAt, t)))
        {
            // цель достигнута или уже позади (объект прошёл мимо неё) — дальше курс по пройденному пути
            track.Target = null;
            track.TargetLabel = null;
            track.TargetIsArea = false;
        }
        // курс словами из запоздавшего сообщения не перезаписывает свежий (как и цель)
        if (o.Heading != null && (track.HeadingHint == null || o.Time >= track.Updated - TimeSpan.FromMinutes(1)))
        {
            track.HeadingHint = o.Heading;
            track.HeadingAt = o.Time;
        }
        else if (track.HeadingHint is { } hh && (track.Points.Count >= 2 &&
                 Geo.DistanceKm(track.Points[^2].Pos, track.Points[^1].Pos) >= 15 &&
                 Geo.AngleDiff(Geo.Bearing(track.Points[^2].Pos, track.Points[^1].Pos), hh) > 90 ||
                 StaleBack(track, track.HeadingAt, Geo.Destination(track.Head.Pos, hh, 60))))
            // объект ушёл в другую сторону («на север», а следующая отметка южнее) — прежний курс словами устарел
            track.HeadingHint = null;
        ApplyRouteHints(track, o);
        if (IsSpecific(o.Kind) && !IsSpecific(track.Kind) && !(IsShortRange(o.Kind) && SpanKm(track) > ShortRangeKm)) track.Kind = o.Kind;
        track.Count = Math.Max(track.Count, o.Count);
        track.Events.Add(o.Event);
        track.Channels.Add(o.Channel);
        if (o.Time > track.Updated) track.Updated = o.Time;

        // при слиянии трек мог уйти в более старый — возвращаем тот, что остался (маршрут ему уже построен)
        var kept = MergeDuplicates(track);
        if (kept == track)
        {
            Straighten(track.Points, track.Threat);
            RoutePlanner.Plan(track);
        }
        return kept;
    }

    // ---------------- порядок точек: объект летит от линии пуска вглубь ----------------

    /// <summary>
    /// Сколько км «назад к месту захода» составляет переход a → b (0 — не назад или правило неприменимо).
    /// На суше — к линии соприкосновения, в Крыму и на Кубани — к районам морских пусков.
    /// </summary>
    private static double BackKm(GeoPoint a, GeoPoint b, ThreatType threat, GeoPoint? entry) =>
        threat is ThreatType.Uav or ThreatType.Missile ? Borderline.BackKm(a, b, entry) : 0;

    /// <summary>Переход заметно возвращает объект к линии пуска — это не продолжение пути, а другой объект.</summary>
    private static bool GoesBack(GeoPoint a, GeoPoint b, ThreatType threat, GeoPoint? entry) => BackKm(a, b, threat, entry) > 30;

    /// <summary>
    /// Заявленная раньше цель осталась позади: последний отрезок пути (≥ 10 км) уводит от неё (> 110°),
    /// или она заметно ближе к линии пуска, чем текущее положение. Лететь к ней — значит разворачиваться назад.
    /// </summary>
    /// <summary>Курс, названный в сообщении: словами или направлением на цель (не ближе 15 км).</summary>
    private static double? StatedCourse(Observation o) =>
        o.Heading ?? (o.Target is { } st && Geo.DistanceKm(o.Anchor, st) >= 15 ? Geo.Bearing(o.Anchor, st) : null);

    /// <summary>Сколько живёт указание цели/курса, если после него объект сместился: дальше путь важнее старых слов.</summary>
    internal static readonly TimeSpan StaleHint = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Цель или курс названы давно (≥ 30 мин до последней отметки), объект с тех пор сместился, а продолжение к ним
    /// ведёт обратно к линии пуска или против последнего отрезка: указание относилось к прежнему участку пути.
    /// Порог мягче, чем у <see cref="TargetBehind"/>: свежим словам доверяем, старым — только пока они не противоречат пути.
    /// </summary>
    internal static bool StaleBack(Track t, DateTimeOffset at, GeoPoint p)
    {
        if (at == default || t.Points.Count < 2 || t.Head.Time - at < StaleHint) return false;
        var head = t.Head.Pos;
        // с момента указания объект заметно сместился (иначе старые слова и есть последняя информация)
        var since = t.Points.LastOrDefault(q => q.Time <= at)?.Pos ?? t.Points[0].Pos;
        if (Geo.DistanceKm(since, head) < 15) return false;
        if (BackKm(head, p, t.Threat, t.Points[0].Pos) > 15) return true;
        var prev = t.Points[^2].Pos;
        if (Geo.DistanceKm(prev, head) < 10 || Geo.DistanceKm(head, p) < 15) return false;
        return Geo.AngleDiff(Geo.Bearing(prev, head), Geo.Bearing(head, p)) > 90;
    }

    internal static bool TargetBehind(Track t, GeoPoint target)
    {
        var head = t.Head.Pos;
        if (BackKm(head, target, t.Threat, t.Points[0].Pos) > 30) return true;
        if (t.Points.Count < 2) return false;
        var prev = t.Points[^2].Pos;
        if (Geo.DistanceKm(prev, head) < 10 || Geo.DistanceKm(head, target) < 15) return false;
        return Geo.AngleDiff(Geo.Bearing(prev, head), Geo.Bearing(head, target)) > 110;
    }

    /// <summary>
    /// Новая точка замыкает петлю: путь длиннее 60 км и вдвое длиннее прямой от первой точки до новой.
    /// Возврат к уже пройденной точке (≤ 8 км) — не петля: ложное ответвление отрезается при добавлении.
    /// </summary>
    /// <summary>Длина пути, начиная с которой «вдвое длиннее прямой» считается петлёй (как в аудите движка).</summary>
    private const double LoopMinKm = 40;

    internal static bool WouldLoop(Track t, GeoPoint p)
    {
        if (t.Points.Count < 2) return false;
        for (int k = 0; k < t.Points.Count - 1; k++)
            if (Geo.DistanceKm(t.Points[k].Pos, p) < 8) return false;
        double len = Geo.DistanceKm(t.Head.Pos, p);
        for (int k = 1; k < t.Points.Count; k++) len += Geo.DistanceKm(t.Points[k - 1].Pos, t.Points[k].Pos);
        double direct = Geo.DistanceKm(t.Points[0].Pos, p);
        return len > LoopMinKm && len > direct * 2;
    }

    /// <summary>Путь длиннее <see cref="LoopMinKm"/> км и вдвое длиннее прямой от начала до конца.</summary>
    internal static bool IsLoop(List<TrackPoint> pts)
    {
        if (pts.Count < 3) return false;
        double len = 0;
        for (int k = 1; k < pts.Count; k++) len += Geo.DistanceKm(pts[k - 1].Pos, pts[k].Pos);
        return len > LoopMinKm && len > Geo.DistanceKm(pts[0].Pos, pts[^1].Pos) * 2;
    }

    /// <summary>Индекс вставки запоздавшей точки по времени; null — если точка ломает путь (разворот/возврат к линии пуска).</summary>
    private static int? InsertIndex(List<TrackPoint> pts, TrackPoint p)
    {
        int idx = pts.FindIndex(x => x.Time > p.Time);
        if (idx < 0) idx = pts.Count;
        if (idx > 0 && Kink(pts[idx - 1].Pos, p.Pos, idx < pts.Count ? pts[idx].Pos : null)) return null;
        if (idx > 1 && Kink(pts[idx - 2].Pos, pts[idx - 1].Pos, p.Pos)) return null;
        if (idx + 1 < pts.Count && Kink(p.Pos, pts[idx].Pos, pts[idx + 1].Pos)) return null;
        return idx;
    }

    /// <summary>Излом a → b → c больше 110° (на отрезках длиннее 8 км).</summary>
    private static bool Kink(GeoPoint a, GeoPoint b, GeoPoint? c) =>
        c is { } cc && Geo.DistanceKm(a, b) >= 8 && Geo.DistanceKm(b, cc) >= 8 &&
        Geo.AngleDiff(Geo.Bearing(a, b), Geo.Bearing(b, cc)) > 110;

    /// <summary>
    /// Выпрямляет пройденный путь: убирает «шпильки» (точку, после которой объект разворачивается назад —
    /// это неточная привязка или чужая отметка) и точки, после которых путь возвращается к линии пуска.
    /// Последняя точка (свежее подтверждение) не удаляется никогда.
    /// </summary>
    internal static void Straighten(List<TrackPoint> pts, ThreatType threat)
    {
        bool changed = true;
        while (changed && pts.Count >= 3)
        {
            changed = false;
            for (int i = pts.Count - 2; i >= 1; i--)
            {
                var a = pts[i - 1].Pos;
                var b = pts[i].Pos;
                var c = pts[i + 1].Pos;
                if (Kink(a, b, c) || BackKm(b, c, threat, pts[0].Pos) > 30 || Geo.DistanceKm(a, c) < 6 && Geo.DistanceKm(a, b) > 15 ||
                    Vague(pts[i], a, c))
                {
                    pts.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
        // первая точка «глубже» второй — начало пути перепутано
        if (pts.Count >= 2 && BackKm(pts[0].Pos, pts[1].Pos, threat, null) > 30) pts.RemoveAt(0);
    }

    /// <summary>
    /// Промежуточная отметка по центру района («Почепский район») лежит в пределах его радиуса от прямой между соседями —
    /// она не уточняет путь, а лишь даёт ложный изгиб (S-образное «виляние»): объект был где-то в районе, по пути.
    /// </summary>
    private static bool Vague(TrackPoint b, GeoPoint a, GeoPoint c) =>
        b.SpreadKm > 0 && Geo.DistanceKm(a, c) >= 15 && SegKm(b.Pos, a, c) <= b.SpreadKm;

    private static double SegKm(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        double kx = Math.Cos(p.Lat * Math.PI / 180);
        double ax = (a.Lon - p.Lon) * kx, ay = a.Lat - p.Lat, bx = (b.Lon - p.Lon) * kx, by = b.Lat - p.Lat;
        double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
        double t = L < 1e-12 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / L, 0, 1);
        return Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)) * 111.2;
    }

    /// <summary>
    /// Цель лежит позади, к линии пуска, а объект уже в тылу (дальше 50 км от линии соприкосновения): БПЛА и ракеты,
    /// пересёкшие линию, назад не разворачиваются — это неверно понятое сообщение («объездная в сторону Почепа»).
    /// </summary>
    internal static bool TowardsLaunchFromRear(GeoPoint from, GeoPoint target, ThreatType threat, GeoPoint? entry) =>
        threat is ThreatType.Uav or ThreatType.Missile && Borderline.ZoneOf(from) == Borderline.Zone.Land &&
        Borderline.DistanceKm(from) > 50 && BackKm(from, target, threat, entry) > 25;

    /// <summary>
    /// Отметку можно присоединить к короткому треку (курс ещё не подтверждён), если она не ближе к линии, чем начало трека,
    /// либо её объясняет курс или цель из сообщения, согласованные со смещением. Иначе это другая группа
    /// (Калуга → Бабынинский район; Красный Луч → «Алчевск и далее на восток» — объект шёл бы на север, к линии;
    /// Прохоровский → «Корочанский район, в сторону Воронежа» — цель говорит, куда группа летит дальше, а не откуда пришла).
    /// </summary>
    private static bool ShortTrackMayGoBack(Track t, Observation o)
    {
        var start = t.Points[0].Pos;
        if (Geo.DistanceKm(start, o.Anchor) <= 20) return true;
        // трек уже сместился — путь есть, недопустим лишь заметный итоговый возврат (Красный Луч → Алчевск → Алмазная);
        // две отметки на одном месте («Красный Луч» → «Красный Луч и близлежащие») пути не дают
        // курс из сообщения поперёк шага («Алчевск и далее на восток» при шаге на север) запаса не даёт
        var sc = StatedCourse(o);
        if (sc is { } c0 && Geo.AngleDiff(c0, Geo.Bearing(t.Head.Pos, o.Anchor)) < 60) return true;
        bool moved = sc == null && Geo.DistanceKm(start, t.Head.Pos) > 20;
        if (BackKm(start, o.Anchor, o.Threat, start) <= (moved ? 25 : 10)) return true;
        if (sc != null) return false;
        // подлёт к крупному городу у линии (Яковлевский район → Белгород) — вероятная цель удара, а не возврат
        return PlaceIndex.NearestCity(o.Anchor, EngineAudit.NearCityKm, EngineAudit.NearCityPopulation) != null;
    }

    /// <summary>Коридоры, промежуточные пункты и место пуска из сообщения — в трек.</summary>
    private static void ApplyRouteHints(Track track, Observation o)
    {
        if (o.Route is { } r)
        {
            foreach (var c in r.Corridors)
                if (!track.Corridors.Any(x => x.Kind == c.Kind && x.Name == c.Name)) track.Corridors.Add(c);
            foreach (var sg in r.Segments)
                if (!track.RoadSegments.Any(x => x.FromLabel == sg.FromLabel && x.ToLabel == sg.ToLabel)) track.RoadSegments.Add(sg);
            if (track.RoadSegments.Count > 4) track.RoadSegments.RemoveRange(0, track.RoadSegments.Count - 4);
            // явный пуск важнее выведенного; более ранний явный не перезаписываем
            if (r.Launch is { Explicit: true } l && !(track.LaunchExplicit && !track.LaunchFromSide))
            {
                track.Launch = l.Pos;
                track.LaunchLabel = l.Label;
                track.LaunchExplicit = true;
                track.LaunchFromSide = false;
            }
            else if (r.Launch is { } l2 && track.Launch == null)
            {
                // сторона («с моря») задана сообщением — не выводим заново, но названное место пуска её заменит
                track.Launch = l2.Pos;
                track.LaunchLabel = l2.Label;
                track.LaunchExplicit = true;
                track.LaunchFromSide = true;
            }
        }
        foreach (var v in o.Via)
            if (!track.Via.Any(x => Geo.DistanceKm(x.Pos, v.Pos) < 3)) track.Via.Add(v);
        if (track.Via.Count > 6) track.Via.RemoveRange(0, track.Via.Count - 6);
    }

    private static void Integrate(Track t, Observation o)
    {
        var p = new TrackPoint(o.Anchor, o.Time, o.Label, o.Channel, o.SpreadKm);

        // сообщение пришло не по порядку — вставляем по времени, если точка ложится на путь без излома
        if (o.Time < t.Head.Time - TimeSpan.FromMinutes(1))
        {
            bool dup = t.Points.Any(x => Geo.DistanceKm(x.Pos, o.Anchor) < 5);
            if (!dup && InsertIndex(t.Points, p) is { } idx)
            {
                bool wasLoop = IsLoop(t.Points);
                t.Points.Insert(idx, p);
                if (!wasLoop && IsLoop(t.Points)) t.Points.RemoveAt(idx);
                // тот же предел, что и при добавлении в голову; первая точка (вход/место пуска) сохраняется
                else if (t.Points.Count > 16) t.Points.RemoveAt(1);
            }
            return;
        }

        var head = t.Head;
        double d = Geo.DistanceKm(head.Pos, o.Anchor);
        if (d < 5)
        {
            // то же место — подтверждение. Повтор того же канала значит «объект всё ещё здесь» — время обновляем;
            // перепечатка другим каналом запаздывает и время прибытия не сдвигает (иначе следующий отрезок даёт завышенную скорость)
            if (o.Channel == head.Channel) t.Points[^1] = head with { Time = o.Time > head.Time ? o.Time : head.Time };
            return;
        }

        // возврат к уже пройденной точке — отрезок после неё был ложным ответвлением
        for (int k = t.Points.Count - 2; k >= 0; k--)
            if (Geo.DistanceKm(t.Points[k].Pos, o.Anchor) < 8)
            {
                t.Points.RemoveRange(k, t.Points.Count - k);
                t.Points.Add(p);
                return;
            }

        if (t.Bearing is { } br && t.Points.Count >= 2 && Geo.AngleDiff(br, Geo.Bearing(head.Pos, o.Anchor)) > 80 && d < 40)
        {
            // уточнение позиции «назад» — заменяем последнюю точку, без зигзага
            t.Points[^1] = p;
            return;
        }

        // оценка скорости по реальному смещению (сглаживание)
        // скорость измеряем, только если интервал достаточен, чтобы погрешность времени публикации (~1–3 мин) была мала
        double hours = (o.Time - head.Time).TotalHours;
        // точки разных каналов: у каждого своя задержка публикации — короткий интервал между ними ненадёжен
        bool cross = o.Channel != head.Channel;
        if (hours >= (cross ? 12.0 : 5.0) / 60 && d >= 10)
        {
            var (min, max) = Track.SpeedRange(t.Kind, t.Threat);
            double raw = d / hours;
            // быстрее максимума класса — это неточность привязки (центр района, «и ближайшие»), а не скорость:
            // показывать её как измеренную (260 км/ч у «Шахеда») нельзя
            bool accepted = raw >= min * 0.6 && raw <= max;
            SpeedAudit?.Invoke(t, head, o, d, hours, raw, accepted);
            if (accepted)
            {
                double v = Math.Clamp(raw, min, max);
                // первое измерение замещает типовое значение, дальнейшие — сглаживаются
                double wv = cross ? 0.25 : 0.45;
                t.SpeedKmh = t.SpeedSamples == 0 ? v : t.SpeedKmh * (1 - wv) + v * wv;
                t.SpeedLog.Add((v, cross));
                if (t.SpeedLog.Count > 8) t.SpeedLog.RemoveAt(0);
                t.SpeedSamples++;
            }
        }

        t.Points.Add(p);
        // первая точка — место захода (глубина от линии, вывод места пуска): убираем вторую, а не её
        if (t.Points.Count > 16) t.Points.RemoveAt(1);
    }

    private Track? FindMatch(Observation o)
    {
        Track? best = null;
        double bestScore = double.MaxValue;
        // точки других треков в том же месте — для проверки перепечаток (считаются один раз на наблюдение)
        var sameSpot = new List<(Track T, TrackPoint P)>();
        foreach (var x in _tracks)
            if (!x.Cleared && x.Threat == o.Threat)
                foreach (var p in x.Points)
                    if (Geo.DistanceKm(p.Pos, o.Anchor) < 5) sameSpot.Add((x, p));
        foreach (var t in _tracks)
        {
            if (t.Threat != o.Threat || t.Cleared) continue;
            if (IsSpecific(t.Kind) && IsSpecific(o.Kind) && t.Kind != o.Kind) continue;
            var dt = o.Time - t.Updated;
            if (dt < -MaxGap(o.Threat) || dt > MaxGap(o.Threat)) continue;

            var head = t.Head.Pos;
            var predicted = t.EstimatedPosition(o.Time);
            double dHead = Geo.DistanceKm(head, o.Anchor);
            // подтверждение действующей опасности («Горловка … сохраняется») не ведёт путь обратно к линии: это повтор
            // опасности по площади у фронта, а не засечка; вглубь — допустимо (Сватово → «Старобельск … сохраняется»)
            if (o.Standing && dHead > 5 && BackKm(head, o.Anchor, o.Threat, t.Points[0].Pos) > 3) continue;
            // FPV-отметка в десятках км от трека — другой объект (FPV не перелетает из Погара в Брянск);
            // и трек дальнего БПЛА не становится «FPV» от соседней тактической отметки
            if (IsShortRange(o.Kind) || IsShortRange(t.Kind))
            {
                if (dHead > ShortRangeKm || Geo.DistanceKm(t.Points[0].Pos, o.Anchor) > ShortRangeKm) continue;
                if (IsShortRange(o.Kind) != IsShortRange(t.Kind) && IsSpecific(o.Kind) && IsSpecific(t.Kind)) continue;
            }
            double dPred = Geo.DistanceKm(predicted, o.Anchor);

            // канал перечисляет группы подряд («Ефремовский район … Фиксация» и через минуту «Каменский район … Фиксация»):
            // смещение на 20+ км за 0–3 минуты по одному каналу — это другая группа, а не перелёт
            if (t.Head.Channel == o.Channel && Math.Abs((o.Time - t.Head.Time).TotalMinutes) < 3 && dHead > 20) continue;
            // перепечатка пункта, который тот же канал уже отнёс к другой группе («Шумилинская» у nabludatel_rf после
            // vrv_radar «Шумилинская … ⏎ Казанская»): у трека Казанской её быть не должно
            if (sameSpot.Count > 0 && sameSpot.Any(x => x.T != t &&
                    t.Points.Any(q => q.Channel == x.P.Channel && Math.Abs((q.Time - x.P.Time).TotalMinutes) < 3 && Geo.DistanceKm(q.Pos, x.P.Pos) > 20)))
                continue;

            // ворота: объект физически мог оказаться в этой точке
            // от последнего подтверждения (Updated): расширение ворот от времени отметки пути (Head.Time) на архиве
            // склеивало разные объекты у одного города в петли — подтверждение «объект всё ещё здесь» ворота сужает
            double minutes = Math.Max(8, Math.Abs(dt.TotalMinutes));
            // диапазон скоростей — по виду сообщения: общий «БПЛА» не разрешает реактивных скоростей даже треку реактивного
            // (иначе на архиве склеивались далёкие отметки разных объектов: Майкоп → Горловка за 26 мин)
            var (vMin, vMax) = Track.SpeedRange(o.Kind, o.Threat);
            // измеренная скорость сужает ворота (с запасом на неточность времени публикации)
            double vGate = t.SpeedSamples > 0 ? Math.Clamp(t.SpeedKmh * 1.8, vMin, vMax) : vMax;
            double reach = vGate * minutes / 60 + 25;
            if (dHead > reach) continue;

            // объект не возвращается к линии пуска: отметка ближе к границе — другой объект
            var entry = t.Points[0].Pos;
            double back = BackKm(head, o.Anchor, o.Threat, entry);
            // у самой линии возврат к ней — это уже новый объект с той стороны (Донецк → Новогродовка — разные «Хорнеты»)
            if (back > (Borderline.DistanceKm(o.Anchor) < 20 ? 18 : 30) && dHead > 20) continue;
            // у короткого трека курс ещё не подтверждён: отметка ближе к линии, чем место захода, — другая группа
            // (Калуга → Бабынинский район, Красный Луч → Алчевск → Алмазная)
            if (t.Points.Count <= 2 && dHead > 20 && !ShortTrackMayGoBack(t, o)) continue;
            // «ползучий» возврат мелкими шагами: от самой глубокой точки всего пройденного пути;
            // и итоговый возврат от начала пути (Ракитное → Белгородский район → Шебекино — это разные объекты)
            double backTrack = 0;
            for (int k = 0; k < t.Points.Count; k++)
                backTrack = Math.Max(backTrack, BackKm(t.Points[k].Pos, o.Anchor, o.Threat, entry));
            if (backTrack > 35) continue;
            if (t.Points.Count >= 2 && BackKm(entry, o.Anchor, o.Threat, entry) > 22) continue;
            // петля: путь с новой точкой много длиннее прямой от начала — это уже другой объект
            // (разделить на два трека безопаснее, чем склеить два объекта в один)
            if (WouldLoop(t, o.Anchor)) continue;
            // «фиксация с моря», а трек пришёл по суше (место захода не над водой) — это другой объект
            if (o.Route is { FromSea: true } && t.Launch is { } tl && !RouteNetwork.IsWater(tl)) continue;
            // «ещё группы через Погарский район в тыл»: объект только что пересёк линию и уходит вглубь — трек,
            // ушедший вглубь раньше, отметка вернула бы к линии (Рохманово → Погарский район): это новая группа
            if (o.Route is { ToRear: true } && back > 10 && dHead > 15) continue;

            // разные заявленные цели в разных направлениях — это разные объекты (разделение групп)
            if (o.Target is { } nt0 && t.Target is { } ot0 && Geo.DistanceKm(nt0, ot0) > 40 &&
                Geo.AngleDiff(Geo.Bearing(o.Anchor, nt0), Geo.Bearing(o.Anchor, ot0)) > 45)
                continue;
            if (o.Heading is { } oh && t.Bearing is { } tb0 && Geo.AngleDiff(oh, tb0) > 70) continue;
            // сообщение называет курс («в вашем направлении от Брянской области» — на северо-восток), а от трека к отметке
            // объект шёл бы навстречу этому курсу (Кировский район → Людиновский — на юг): это другой объект
            // то же для цели («от Вадьковки в направлении Трубчевского района» после «Рамасуха … в сторону Погара»): группы навстречу
            if (StatedCourse(o) is { } oh2 && dHead > 15 && Geo.AngleDiff(oh2, Geo.Bearing(head, o.Anchor)) > 120) continue;
            // ФПВ/«Баба-яга» запускаются с линии и летят от неё: отметка ближе к линии — другой аппарат (Долгое → Борки)
            if ((IsShortRange(o.Kind) || IsShortRange(t.Kind)) && back > 5) continue;
            if (o.Target is { } nt1 && Geo.DistanceKm(o.Anchor, nt1) >= 15)
            {
                // «Фатеж и далее на Курск», а трек уже в Курске: объект летит туда, где трек есть, — это другой объект
                if (dHead > 20 && Geo.DistanceKm(nt1, head) < 15) continue;
                // сообщение ведёт объект туда, откуда трек пришёл («Весёлое и далее на Мелитополь» после «Мелитополь → Весёлое»), —
                // это встречный объект, а не разворот
                if (t.Points.Count >= 2)
                {
                    var prev = t.Points[^2].Pos;
                    if (Geo.DistanceKm(prev, head) >= 10 && Geo.AngleDiff(Geo.Bearing(prev, head), Geo.Bearing(o.Anchor, nt1)) > 120) continue;
                }
            }

            double score = Math.Min(dPred, dHead) * (1 + back / 25);
            bool consistent = dHead < 30;

            if (t.Bearing is { } br && dHead > 15)
            {
                double ang = Geo.AngleDiff(br, Geo.Bearing(head, o.Anchor));
                if (ang > 110) continue;
                score *= 1 + ang / 60;
                if (ang < 45) consistent = true;
            }

            if (t.Target is { } tt)
            {
                double before = Geo.DistanceKm(head, tt), after = Geo.DistanceKm(o.Anchor, tt);
                // отметка в стороне, противоположной заявленной цели трека, — другой объект (новая цель придёт с ним)
                if (dHead > 25 && after > before + 0.5 * dHead && o.Target == null &&
                    Geo.AngleDiff(Geo.Bearing(head, tt), Geo.Bearing(head, o.Anchor)) > 120)
                    continue;
                if (after < before - 0.25 * dHead) { score *= 0.6; consistent = true; }
                if (after < 25) { score *= 0.5; consistent = true; }
            }
            if (o.Target is { } nt && t.Target is { } ot && Geo.DistanceKm(nt, ot) < 60) { score *= 0.5; consistent = true; }
            if (o.Heading is { } h && t.Bearing is { } b2 && Geo.AngleDiff(h, b2) < 45) { score *= 0.7; consistent = true; }

            // каналы одного региона ведут один и тот же объект
            if (t.Channels.Contains(o.Channel)) score *= 0.85;

            if (!consistent && dHead > 60) continue;
            // район из сводки — грубая привязка без направления: продолжает трек, только если лежит там, где объект и ожидается
            // (рядом с расчётным положением либо рядом с последней отметкой, по курсу трека)
            // у трека из одной отметки курса нет — сводка задала бы его сама (Шайковка → «Кировский район» к линии)
            if (o.ContinueOnly && (t.Points.Count < 2 || t.Bearing == null || !(dPred < 30 || dHead < 25) ||
                                   dHead > 15 && Geo.AngleDiff(t.Bearing.Value, Geo.Bearing(head, o.Anchor)) > 60))
                continue;
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>Сливает треки, которые описывают один и тот же объект (сообщения из разных каналов).</summary>
    /// <returns>Трек, в который вошёл <paramref name="t"/> (сам <paramref name="t"/>, если он остался).</returns>
    private Track MergeDuplicates(Track t)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var o = _tracks[i];
            if (o == t || o.Threat != t.Threat || o.Cleared) continue;
            // два трека из одних подтверждений опасности не складываются в путь к линии (Енакиево + Горловка «сохраняется»)
            if (o.Standing && t.Standing && Geo.DistanceKm(o.Head.Pos, t.Head.Pos) > 5 &&
                Math.Max(BackKm(o.Head.Pos, t.Head.Pos, t.Threat, null), BackKm(t.Head.Pos, o.Head.Pos, t.Threat, null)) > 3) continue;
            if (IsSpecific(o.Kind) && IsSpecific(t.Kind) && o.Kind != t.Kind) continue;
            if (Math.Abs((o.Updated - t.Updated).TotalMinutes) > 12) continue;
            // слияние не должно растянуть FPV-трек на десятки км
            if ((IsShortRange(o.Kind) || IsShortRange(t.Kind)) && Geo.DistanceKm(o.Points[0].Pos, t.Points[0].Pos) > ShortRangeKm) continue;
            if (Geo.DistanceKm(o.Head.Pos, t.Head.Pos) > 15) continue;
            if (o.Bearing is { } a && t.Bearing is { } b && Geo.AngleDiff(a, b) > 50) continue;
            // начала путей по разные стороны: один уже глубоко, другой у границы и «догоняет» назад — не сливаем
            if (GoesBack(o.Points[0].Pos, t.Head.Pos, t.Threat, null) || GoesBack(t.Points[0].Pos, o.Head.Pos, t.Threat, null)) continue;
            // один канал перечислил две группы подряд («Шумилинская … ⏎ Казанская … Фиксация» через 15 с, 29 км) —
            // это разные объекты, как и при сопоставлении наблюдения с треком
            if (o.Points.Any(a => t.Points.Any(b => a.Channel == b.Channel && Math.Abs((a.Time - b.Time).TotalMinutes) < 3 && Geo.DistanceKm(a.Pos, b.Pos) > 20)))
                continue;

            // остаётся более старый трек — визуальная непрерывность на карте
            var (keep, drop) = o.Created <= t.Created ? (o, t) : (t, o);
            var merged = new List<TrackPoint>(keep.Points);
            foreach (var p in drop.Points)
                if (merged.All(x => Geo.DistanceKm(x.Pos, p.Pos) > 5)) merged.Add(p);
            merged.Sort((x, y) => x.Time.CompareTo(y.Time));
            Straighten(merged, keep.Threat);
            while (merged.Count > 16) merged.RemoveAt(1);
            // пути, сложенные вместе, дают петлю — это разные объекты, сошедшиеся в одной точке
            if (IsLoop(merged) && !IsLoop(keep.Points) && !IsLoop(drop.Points)) continue;
            // ФПВ (оба трека) летят от линии: общий путь с шагом к ней — два разных аппарата (как и при сопоставлении отметки)
            if (IsShortRange(keep.Kind) && IsShortRange(drop.Kind) &&
                Enumerable.Range(1, merged.Count - 1).Any(k => BackKm(merged[k - 1].Pos, merged[k].Pos, keep.Threat, merged[0].Pos) > 5))
                continue;
            keep.Points.Clear();
            keep.Points.AddRange(merged);
            foreach (var e in drop.Events) keep.Events.Add(e);
            foreach (var c in drop.Channels) keep.Channels.Add(c);
            // остаётся более старый трек, но цель и курс — более свежие (только что сообщённые не уступают устаревшим)
            if (drop.Target != null && (keep.Target == null || drop.TargetAt > keep.TargetAt))
            {
                keep.Target = drop.Target;
                keep.TargetLabel = drop.TargetLabel;
                keep.TargetIsArea = drop.TargetIsArea;
                keep.TargetAt = drop.TargetAt;
            }
            if (drop.HeadingHint != null && (keep.HeadingHint == null || drop.HeadingAt > keep.HeadingAt))
            {
                keep.HeadingHint = drop.HeadingHint;
                keep.HeadingAt = drop.HeadingAt;
            }
            // трек из одних подтверждений «сохраняется» — только если оба такие
            keep.Standing &= drop.Standing;
            foreach (var c in drop.Corridors)
                if (!keep.Corridors.Any(x => x.Kind == c.Kind && x.Name == c.Name)) keep.Corridors.Add(c);
            foreach (var sg in drop.RoadSegments)
                if (!keep.RoadSegments.Any(x => x.FromLabel == sg.FromLabel && x.ToLabel == sg.ToLabel)) keep.RoadSegments.Add(sg);
            foreach (var v in drop.Via)
                if (!keep.Via.Any(x => Geo.DistanceKm(x.Pos, v.Pos) < 3)) keep.Via.Add(v);
            if (drop.LaunchExplicit && !keep.LaunchExplicit) { keep.Launch = drop.Launch; keep.LaunchLabel = drop.LaunchLabel; keep.LaunchExplicit = true; }
            keep.Count = Math.Max(keep.Count, drop.Count);
            if (!IsSpecific(keep.Kind)) keep.Kind = drop.Kind;
            if (drop.SpeedSamples > keep.SpeedSamples) { keep.SpeedKmh = drop.SpeedKmh; keep.SpeedSamples = drop.SpeedSamples; keep.SpeedLog.Clear(); keep.SpeedLog.AddRange(drop.SpeedLog); }
            if (drop.Updated > keep.Updated) keep.Updated = drop.Updated;
            _tracks.Remove(drop);
            RoutePlanner.Plan(keep);
            if (drop == t) return keep;
        }
        return t;
    }

    /// <summary>FPV, дроны на оптоволокне и тяжёлые коптеры («Баба-яга»): тактические, радиус 10–20 км.</summary>
    internal static bool IsShortRange(ThreatKind k) => k is ThreatKind.UavFpv or ThreatKind.UavFiber or ThreatKind.UavHeavy;
    internal const double ShortRangeKm = 25;
    /// <summary>Дальность ФПВ от линии: ближе — аппарат запущен с неё, дальше — его принёс носитель.</summary>
    internal const double FpvLineKm = 40;

    /// <summary>Размах трека: наибольшее удаление его точек от первой.</summary>
    private static double SpanKm(Track t) => t.Points.Count == 0 ? 0 : t.Points.Max(p => Geo.DistanceKm(t.Points[0].Pos, p.Pos));

    private static bool IsSpecific(ThreatKind k) =>
        k is not (ThreatKind.Unknown or ThreatKind.UavGeneric or ThreatKind.MissileGeneric or ThreatKind.AviationGeneric);

    public void ClearWhere(Func<GeoPoint, bool> inArea, ThreatType? threat)
    {
        foreach (var t in _tracks)
            if ((threat == null || t.Threat == threat) && inArea(t.Head.Pos))
                t.Cleared = true;
    }

    public void Clear() => _tracks.Clear();

    /// <summary>Повтор сообщения другим каналом: канал добавляется к трекам этой тревоги (без новой отметки).</summary>
    public void AddChannel(AlertEvent ev, string channel)
    {
        foreach (var t in _tracks)
            if (t.Events.Contains(ev)) t.Channels.Add(channel);
    }

    public void Expire(DateTimeOffset now, TimeSpan lifetime, Action<Track>? retired = null) =>
        _tracks.RemoveAll(t =>
        {
            if (now - t.Updated <= (t.Cleared ? TimeSpan.FromMinutes(4) : lifetime)) return false;
            retired?.Invoke(t);
            return true;
        });
}
