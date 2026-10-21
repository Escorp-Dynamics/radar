namespace Radar.Models;

/// <param name="SpreadKm">Неточность привязки: 0 — пункт, иначе радиус района/зоны, по центру которой поставлена точка.</param>
public sealed record TrackPoint(GeoPoint Pos, DateTimeOffset Time, string Label, string Channel, double SpreadKm = 0);

public enum TrackState
{
    /// <summary>Свежие сообщения — объект в движении.</summary>
    Active,
    /// <summary>Давно нет подтверждений — положение неизвестно.</summary>
    Stale,
    /// <summary>Объявлен отбой.</summary>
    Cleared,
}

/// <summary>
/// Траектория объекта (группы объектов), собранная из последовательных сообщений.
/// Хранит подтверждённые точки и кинематическую оценку (скорость, курс), по которой
/// прогнозируется текущее положение между сообщениями.
/// </summary>
public sealed class Track
{
    public required int Id { get; init; }
    public required ThreatType Threat { get; init; }
    /// <summary>Подтип (уточняется по мере поступления сообщений: «БПЛА» → «реактивный БПЛА»).</summary>
    public ThreatKind Kind { get; set; } = ThreatKind.Unknown;
    public ThreatInfo Info => Kind != ThreatKind.Unknown ? ThreatCatalog.Info(Kind) : ThreatCatalog.GroupDefault(Threat);
    public List<TrackPoint> Points { get; } = [];
    public GeoPoint? Target { get; set; }
    public string? TargetLabel { get; set; }
    /// <summary>Цель — регион/район/акватория: на карте только направление (короткий бледный прогноз), без кольца и времени прибытия.</summary>
    public bool TargetIsArea { get; set; }
    /// <summary>Цель — конкретная точка (пункт), до которой имеет смысл рисовать прогноз и считать время.</summary>
    public bool HasPointTarget => Target != null && !TargetIsArea;
    /// <summary>Курс, сообщённый словами («на северо-восток»), если нет цели.</summary>
    public double? HeadingHint { get; set; }
    /// <summary>Время сообщения, назвавшего цель / курс словами: старые указания уступают пройденному пути.</summary>
    public DateTimeOffset TargetAt { get; set; }
    /// <summary>Трек начат подтверждением действующей опасности («… сохраняется»), а не засечкой: продлевается только засечками.</summary>
    public bool Standing { get; set; }
    public DateTimeOffset HeadingAt { get; set; }
    public double SpeedKmh { get; set; }
    /// <summary>Сколько раз скорость была измерена по реальному перемещению (0 — только типовое значение).</summary>
    public int SpeedSamples { get; set; }
    public bool HasMeasuredSpeed => SpeedSamples > 0;
    /// <summary>Последние принятые замеры скорости (км/ч) и признак «по точкам разных каналов».</summary>
    public List<(double Kmh, bool Cross)> SpeedLog { get; } = [];

    /// <summary>Разброс замеров (стандартное отклонение, км/ч) — при 2+ замерах.</summary>
    public double? SpeedSpread
    {
        get
        {
            if (SpeedLog.Count < 2) return null;
            double avg = SpeedLog.Average(x => x.Kmh);
            return Math.Sqrt(SpeedLog.Sum(x => (x.Kmh - avg) * (x.Kmh - avg)) / (SpeedLog.Count - 1));
        }
    }

    /// <summary>Достоверность скорости: 0 — низкая (замеры только по разным каналам или большой разброс), 1 — средняя (один замер одного канала или 2+ с умеренным разбросом), 2 — высокая.</summary>
    public int SpeedConfidence
    {
        get
        {
            if (SpeedLog.Count == 0) return 0;
            double rel = SpeedSpread is { } s ? s / Math.Max(1, SpeedKmh) : 1;
            int same = SpeedLog.Count(x => !x.Cross);
            if (SpeedLog.Count >= 3 && rel <= 0.25 && same >= 2) return 2;
            if (SpeedLog.Count >= 2 && rel <= 0.4 || same >= 1 && SpeedLog.Count == 1) return 1;
            return 0;
        }
    }
    public int Count { get; set; } = 1;
    /// <summary>Планировщик отверг прогноз (курс в море, назад к линии) — и одиночная точка не экстраполируется по курсу.</summary>
    public bool ForecastRejected { get; set; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; set; }
    public bool Cleared { get; set; }
    public HashSet<AlertEvent> Events { get; } = [];
    public HashSet<string> Channels { get; } = new(StringComparer.Ordinal);

    // ---------------- маршрут ----------------

    /// <summary>Место пуска: явное («пуски из Сумской области») или выведенное по курсу (<see cref="LaunchExplicit"/> = false).</summary>
    public GeoPoint? Launch { get; set; }
    public string? LaunchLabel { get; set; }
    public bool LaunchExplicit { get; set; }
    /// <summary>Место пуска задано только стороной из текста («с моря»), а не названным местом.</summary>
    public bool LaunchFromSide { get; set; }
    /// <summary>Линейные ориентиры из сообщений («вдоль трассы Р-280», «по руслу Десны», «по берегу»).</summary>
    public List<Radar.Services.CorridorHint> Corridors { get; } = [];
    /// <summary>Дороги между пунктами из сообщений («по трассе Рыльск — Хомутовка»).</summary>
    public List<Radar.Services.RoadSegmentHint> RoadSegments { get; } = [];
    /// <summary>Промежуточные пункты впереди («через Литвиновку»).</summary>
    public List<(GeoPoint Pos, string Label)> Via { get; } = [];

    /// <summary>Предыстория: от места пуска до первой подтверждённой точки (пусто — пуск неизвестен).</summary>
    public List<GeoPoint> PreRoute { get; set; } = [];
    /// <summary>Пройденный путь: <c>Legs[i]</c> — от <c>Points[i]</c> до <c>Points[i+1]</c> (с учётом ориентиров).</summary>
    public List<List<GeoPoint>> Legs { get; set; } = [];
    /// <summary>Прогноз: от последней подтверждённой точки через промежуточные пункты к цели или по курсу.</summary>
    public List<GeoPoint> FutureRoute { get; set; } = [];
    /// <summary>Подпись ориентира, по которому построен маршрут («вдоль Р-280»), если есть.</summary>
    public string? CorridorLabel { get; set; }

    /// <summary>Точка на участке пройденного пути <paramref name="leg"/> на доле длины <paramref name="frac"/>.</summary>
    public GeoPoint LegPoint(int leg, double frac)
    {
        if (Points.Count == 0) return default;
        if (leg < 0 || leg >= Legs.Count || Legs[leg].Count < 2)
            return Geo.Lerp(Points[Math.Clamp(leg, 0, Points.Count - 1)].Pos, Points[Math.Clamp(leg + 1, 0, Points.Count - 1)].Pos, frac);
        return Geo.Along(Legs[leg], frac * Geo.PathLength(Legs[leg]));
    }

    /// <summary>Пройденная по прогнозу часть (от последней точки до расчётной позиции) и остаток до конца прогноза.</summary>
    public (List<GeoPoint> Travelled, List<GeoPoint> Remaining) RouteSplit(DateTimeOffset at)
    {
        if (FutureRoute.Count < 2) return ([Head.Pos], []);
        double km = TravelKm(at);
        return Geo.Split(FutureRoute, km);
    }

    /// <summary>Курс по маршруту в расчётной позиции (для иконки).</summary>
    public double? RouteBearingAt(DateTimeOffset at)
    {
        if (FutureRoute.Count < 2) return Bearing;
        var (_, rest) = RouteSplit(at);
        for (int i = 1; i < rest.Count; i++)
            if (Geo.DistanceKm(rest[0], rest[i]) > 0.5) return Geo.Bearing(rest[0], rest[i]);
        return Bearing;
    }

    /// <summary>Сколько км пройдено от последней точки по прогнозу к моменту <paramref name="at"/>.</summary>
    private double TravelKm(DateTimeOffset at)
    {
        var dt = at - Head.Time;
        if (Cleared || dt <= TimeSpan.Zero) return 0;
        if (dt > ActiveWindow(Threat)) dt = ActiveWindow(Threat);
        double km = SpeedKmh * dt.TotalHours;
        double len = Geo.PathLength(FutureRoute);
        // без цели — не дальше разумной экстраполяции (длина прогноза уже ограничена планировщиком)
        return Math.Min(km, len);
    }

    public TrackPoint Head => Points[^1];

    public bool IsMoving => Points.Count >= 2 || Target != null || HeadingHint != null;

    public static double DefaultSpeed(ThreatKind k, ThreatType t) =>
        k != ThreatKind.Unknown && ThreatCatalog.Info(k).SpeedKmh > 0 ? ThreatCatalog.Info(k).SpeedKmh : DefaultSpeed(t);

    public static double DefaultSpeed(ThreatType t) => t switch
    {
        ThreatType.Missile => 850,
        ThreatType.Aviation => 650,
        ThreatType.Naval => 55,
        _ => 170,
    };

    public static (double Min, double Max) SpeedRange(ThreatKind k, ThreatType t) => k switch
    {
        ThreatKind.UavJet => (250, 900),
        ThreatKind.UavFpv or ThreatKind.UavFiber => (20, 160),
        ThreatKind.UavHeavy => (20, 120),
        ThreatKind.MissileBallistic => (1000, 6000),
        ThreatKind.Balloon => (5, 120),
        _ => SpeedRange(t),
    };

    public static (double Min, double Max) SpeedRange(ThreatType t) => t switch
    {
        ThreatType.Balloon => (5, 120),
        ThreatType.Missile => (300, 3000),
        ThreatType.Aviation => (300, 1100),
        ThreatType.Naval => (20, 90),
        _ => (60, 260),
    };

    /// <summary>Сколько после последнего сообщения объект считается «в движении».</summary>
    public static TimeSpan ActiveWindow(ThreatType t) => t switch
    {
        ThreatType.Missile => TimeSpan.FromMinutes(6),
        ThreatType.Aviation => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(25),
    };

    public TrackState StateAt(DateTimeOffset now) =>
        Cleared ? TrackState.Cleared : now - Updated > ActiveWindow(Threat) ? TrackState.Stale : TrackState.Active;

    /// <summary>Уверенность 0..1: число подтверждений и независимых каналов.</summary>
    public double Confidence => Math.Min(1.0, 0.35 + 0.12 * Points.Count + 0.12 * Channels.Count);

    /// <summary>Курс в градусах (0 — север): на цель, по последним отрезкам или по словесному указанию.</summary>
    /// <summary>
    /// Курс по двум отметкам без прогноза, цели и курса словами ведёт к линии соприкосновения: это шум привязки
    /// (центры соседних районов), а не разворот. Значок не разворачиваем по такому курсу — показываем без направления.
    /// Отметка в самом крупном городе (Старобешево → Донецк, «Приазовье → Мариуполь») — вероятная цель: курс показываем.
    /// </summary>
    public bool UncertainHeading =>
        Points.Count == 2 && FutureRoute.Count < 2 && Target == null && HeadingHint == null &&
        Threat is ThreatType.Uav or ThreatType.Missile &&
        Radar.Services.Borderline.HeadsToLine(Points[0].Pos, Points[1].Pos, 25) &&
        Radar.Services.PlaceIndex.NearestCity(Points[1].Pos, CityTargetKm, Radar.Services.EngineAudit.NearCityPopulation) == null;

    /// <summary>Отметка в черте крупного города (до 5 км от центра) — город и есть вероятная цель.</summary>
    private const double CityTargetKm = 5;

    public double? Bearing
    {
        get
        {
            if (Target is { } t && Geo.DistanceKm(Head.Pos, t) > 3) return Geo.Bearing(Head.Pos, t);
            // повторные отметки в той же точке курса не задают (курс между совпадающими точками — 0°, «на север»):
            // берём последние несовпадающие
            int i1 = Points.Count - 1;
            int i2 = i1 - 1;
            while (i2 >= 0 && Geo.DistanceKm(Points[i2].Pos, Points[i1].Pos) < 0.3) i2--;
            if (i2 >= 0)
            {
                // усреднение двух последних отрезков сглаживает шум геокодирования
                double b1 = Geo.Bearing(Points[i2].Pos, Points[i1].Pos);
                int i3 = i2 - 1;
                while (i3 >= 0 && Geo.DistanceKm(Points[i3].Pos, Points[i2].Pos) < 0.3) i3--;
                if (i3 >= 0 && Geo.DistanceKm(Points[i2].Pos, Points[i1].Pos) < 25)
                    return Geo.MeanBearing(Geo.Bearing(Points[i3].Pos, Points[i2].Pos), b1, 0.35);
                return b1;
            }
            return HeadingHint;
        }
    }

    /// <summary>Прогноз положения на момент <paramref name="at"/> (счисление пути от последнего подтверждения, по маршруту).</summary>
    public GeoPoint EstimatedPosition(DateTimeOffset at)
    {
        var head = Head.Pos;
        if (!Cleared && FutureRoute.Count >= 2) return Geo.Along(FutureRoute, TravelKm(at));
        if (Cleared || Bearing is not { } br) return head;
        // маршрут уже спланирован, но прогноз отвергнут (курс в море, назад к линии пуска) — объект не «уплывает» по курсу
        if (Target == null && (Legs.Count > 0 || ForecastRejected)) return head;
        var dt = at - Head.Time;
        if (dt <= TimeSpan.Zero) return head;
        if (dt > ActiveWindow(Threat)) dt = ActiveWindow(Threat);
        double km = SpeedKmh * dt.TotalHours;
        if (Target is { } t) km = Math.Min(km, Geo.DistanceKm(head, t));
        else km = Math.Min(km, Threat == ThreatType.Missile ? 150 : 60);
        return km < 0.05 ? head : Geo.Destination(head, br, km);
    }

    /// <summary>Оценка времени до заявленной цели — только при измеренной скорости.</summary>
    public TimeSpan? EtaAt(DateTimeOffset now)
    {
        if (Cleared || TargetIsArea || Target is not { } t || !HasMeasuredSpeed || SpeedKmh < 1) return null;
        double km = Geo.DistanceKm(EstimatedPosition(now), t);
        if (km < 2) return TimeSpan.Zero;
        return TimeSpan.FromHours(km / SpeedKmh);
    }

    /// <summary>
    /// Диапазон времени до цели при неуверенной скорости: скорость ± разброс замеров (не меньше ±15% / ±25% для
    /// средней / низкой достоверности, в пределах физического диапазона класса). null — если оценка надёжна или её нет.
    /// </summary>
    public (TimeSpan Min, TimeSpan Max)? EtaRangeAt(DateTimeOffset now)
    {
        if (EtaAt(now) is not { } eta || eta == TimeSpan.Zero || SpeedConfidence >= 2) return null;
        double rel = Math.Max(SpeedConfidence == 1 ? 0.15 : 0.25, (SpeedSpread ?? 0) / SpeedKmh);
        var (vMin, vMax) = SpeedRange(Kind, Threat);
        // границы класса не должны вытеснять саму измеренную скорость за пределы диапазона
        double fast = Math.Max(SpeedKmh, Math.Min(vMax, SpeedKmh * (1 + rel))), slow = Math.Min(SpeedKmh, Math.Max(vMin, SpeedKmh * (1 - rel)));
        double km = eta.TotalHours * SpeedKmh;
        return (TimeSpan.FromHours(km / fast), TimeSpan.FromHours(km / Math.Max(1, slow)));
    }

    /// <summary>
    /// Время до цели с учётом достоверности: надёжная оценка — «~20 мин», неуверенная — диапазон «15–25 мин»
    /// (если границы после округления различаются).
    /// </summary>
    public string EtaText(DateTimeOffset now)
    {
        var eta = EtaAt(now)!.Value;
        if (EtaRangeAt(now) is not { } r) return FormatEta(eta);
        static double Round(double m) => m < 10 ? Math.Max(1, Math.Round(m)) : m < 60 ? Math.Round(m / 5) * 5 : Math.Round(m / 10) * 10;
        double lo = Round(r.Min.TotalMinutes), hi = Round(r.Max.TotalMinutes);
        if (hi <= lo) return FormatEta(eta);
        static string Hm(double m) => m < 60 ? $"{m:0}" : m % 60 == 0 ? $"{m / 60:0} ч" : $"{Math.Floor(m / 60):0} ч {m % 60:0}";
        return hi < 60 ? $"{lo:0}–{hi:0} мин" : lo < 60 ? $"{lo:0} мин – {Hm(hi)}" + (hi % 60 == 0 ? "" : " мин") : $"{Hm(lo)}–{Hm(hi)}" + (hi % 60 == 0 ? "" : " мин");
    }

    /// <summary>«~5 мин», «~25 мин», «~1 ч 20 мин» — с округлением, отражающим точность оценки.</summary>
    public static string FormatEta(TimeSpan eta)
    {
        double m = eta.TotalMinutes;
        if (m < 10) return $"~{Math.Max(1, Math.Round(m)):0} мин";
        if (m < 57.5) return $"~{Math.Round(m / 5) * 5:0} мин";
        int total = (int)(Math.Round(m / 10) * 10);
        int h = total / 60, mm = total % 60;
        return mm == 0 ? $"~{h} ч" : $"~{h} ч {mm} мин";
    }

    public Track Clone()
    {
        var t = new Track
        {
            Id = Id, Threat = Threat, Kind = Kind, Target = Target, TargetLabel = TargetLabel, TargetIsArea = TargetIsArea, HeadingHint = HeadingHint,
            TargetAt = TargetAt, HeadingAt = HeadingAt, Standing = Standing, SpeedKmh = SpeedKmh, SpeedSamples = SpeedSamples, Count = Count, Created = Created, Updated = Updated, Cleared = Cleared,
        };
        t.Points.AddRange(Points);
        t.SpeedLog.AddRange(SpeedLog);
        foreach (var e in Events) t.Events.Add(e);
        foreach (var c in Channels) t.Channels.Add(c);
        t.Launch = Launch;
        t.LaunchLabel = LaunchLabel;
        t.LaunchExplicit = LaunchExplicit;
        t.LaunchFromSide = LaunchFromSide;
        t.Corridors.AddRange(Corridors);
        t.RoadSegments.AddRange(RoadSegments);
        t.Via.AddRange(Via);
        t.PreRoute = PreRoute;
        t.Legs = Legs;
        t.FutureRoute = FutureRoute;
        t.CorridorLabel = CorridorLabel;
        t.ForecastRejected = ForecastRejected;
        return t;
    }
}

public static class Geo
{
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        const double r = 6371;
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double la1 = a.Lat * Math.PI / 180, la2 = b.Lat * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(la1) * Math.Cos(la2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static double Bearing(GeoPoint a, GeoPoint b)
    {
        double la1 = a.Lat * Math.PI / 180, la2 = b.Lat * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double y = Math.Sin(dLon) * Math.Cos(la2);
        double x = Math.Cos(la1) * Math.Sin(la2) - Math.Sin(la1) * Math.Cos(la2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public static GeoPoint Destination(GeoPoint p, double bearingDeg, double km)
    {
        double d = km / 6371, br = bearingDeg * Math.PI / 180;
        double la1 = p.Lat * Math.PI / 180, lo1 = p.Lon * Math.PI / 180;
        double la2 = Math.Asin(Math.Sin(la1) * Math.Cos(d) + Math.Cos(la1) * Math.Sin(d) * Math.Cos(br));
        double lo2 = lo1 + Math.Atan2(Math.Sin(br) * Math.Sin(d) * Math.Cos(la1), Math.Cos(d) - Math.Sin(la1) * Math.Sin(la2));
        return new GeoPoint(la2 * 180 / Math.PI, lo2 * 180 / Math.PI);
    }

    public static double AngleDiff(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Взвешенное среднее двух курсов (вес <paramref name="wA"/> у первого).</summary>
    public static double MeanBearing(double a, double b, double wA)
    {
        double ra = a * Math.PI / 180, rb = b * Math.PI / 180;
        double x = wA * Math.Cos(ra) + (1 - wA) * Math.Cos(rb), y = wA * Math.Sin(ra) + (1 - wA) * Math.Sin(rb);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Интерполяция по короткой дуге долготы: отрезок через антимеридиан (179° → −179°) не пересекает весь мир.</summary>
    public static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t)
    {
        double dLon = b.Lon - a.Lon;
        if (dLon > 180) dLon -= 360; else if (dLon < -180) dLon += 360;
        double lon = a.Lon + dLon * t;
        if (lon > 180) lon -= 360; else if (lon < -180) lon += 360;
        return new(a.Lat + (b.Lat - a.Lat) * t, lon);
    }

    public static double PathLength(IReadOnlyList<GeoPoint> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += DistanceKm(pts[i - 1], pts[i]);
        return s;
    }

    /// <summary>Точка на полилинии на расстоянии <paramref name="km"/> от начала.</summary>
    public static GeoPoint Along(IReadOnlyList<GeoPoint> pts, double km)
    {
        if (pts.Count == 0) throw new ArgumentException("empty path");
        if (km <= 0 || pts.Count == 1) return pts[0];
        for (int i = 1; i < pts.Count; i++)
        {
            double d = DistanceKm(pts[i - 1], pts[i]);
            if (km <= d) return Lerp(pts[i - 1], pts[i], d < 1e-9 ? 0 : km / d);
            km -= d;
        }
        return pts[^1];
    }

    /// <summary>Делит полилинию в точке на расстоянии <paramref name="km"/>: (пройденное, оставшееся).</summary>
    public static (List<GeoPoint> Before, List<GeoPoint> After) Split(IReadOnlyList<GeoPoint> pts, double km)
    {
        var before = new List<GeoPoint> { pts[0] };
        var after = new List<GeoPoint>();
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = DistanceKm(pts[i - 1], pts[i]);
            if (after.Count == 0 && acc + d >= km)
            {
                var cut = Lerp(pts[i - 1], pts[i], d < 1e-9 ? 0 : (km - acc) / d);
                before.Add(cut);
                after.Add(cut);
            }
            if (after.Count == 0) before.Add(pts[i]);
            else after.Add(pts[i]);
            acc += d;
        }
        if (after.Count == 0) after.Add(pts[^1]);
        return (before, after);
    }
}
