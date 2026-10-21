using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Положение угрозы относительно выбранного пункта или своей точки: «БПЛА в 12 км к юго-западу, идёт к вам (~6 мин)».
/// Позиция — расчётная по треку (счисление от последней отметки) или ближайшая точка тревоги; направление движения — по курсу трека.
/// </summary>
public static class Proximity
{
    /// <summary>Дальше этого расстояния до пункта строка не строится — тревога касается зоны иначе (регион, цель).</summary>
    public const double MaxKm = 150;

    public enum Motion { Unknown, Approaching, Passing, Receding }

    public sealed record Report(WatchArea Place, double DistanceKm, double BearingFromPlace, Motion Motion, double? MissKm, TimeSpan? Eta);

    private static readonly string[] Dirs = ["северу", "северо-востоку", "востоку", "юго-востоку", "югу", "юго-западу", "западу", "северо-западу"];

    /// <summary>«к юго-западу» — где угроза относительно пункта.</summary>
    public static string Side(double bearing) => "к " + Dirs[(int)Math.Round(((bearing % 360) + 360) % 360 / 45, MidpointRounding.AwayFromZero) % 8];

    /// <summary>Ближайший к угрозе выбранный пункт (или своя точка) и движение угрозы относительно него.</summary>
    public static Report? Compute(AlertEvent e, Track? track, IReadOnlyList<WatchArea> areas, double defaultRadiusKm, DateTimeOffset now)
    {
        var places = areas.Where(a => a.Kind == WatchArea.PlaceKind).ToList();
        if (places.Count == 0) return null;
        // расчётное положение по треку, иначе — каждая конкретная точка тревоги (берём ближайшую к пункту)
        var positions = new List<GeoPoint>();
        if (track != null && !track.Cleared) positions.Add(track.EstimatedPosition(now));
        else positions.AddRange(e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Pos));
        if (positions.Count == 0) return null;

        Report? best = null;
        foreach (var a in places)
        {
            var at = new GeoPoint(a.Lat, a.Lon);
            var pos = positions.MinBy(p => Geo.DistanceKm(p, at));
            double km = Geo.DistanceKm(pos, at);
            if (km > MaxKm || best != null && km >= best.DistanceKm) continue;
            double radius = a.RadiusKm > 0 ? a.RadiusKm : defaultRadiusKm;
            var (motion, miss, eta) = MotionOf(track, pos, at, km, radius);
            best = new Report(a, km, Geo.Bearing(at, pos), motion, miss, eta);
        }
        return best;
    }

    private static (Motion, double?, TimeSpan?) MotionOf(Track? t, GeoPoint pos, GeoPoint at, double km, double radius)
    {
        if (t == null || t.Cleared || !t.IsMoving || km < 2) return (Motion.Unknown, null, null);
        // курс у текущего положения: на точку маршрута прогноза в ~15 км впереди (на изогнутом маршруте — не на его конец),
        // иначе общий курс трека
        double? course = t.Bearing;
        if (t.FutureRoute.Count >= 2)
        {
            double len = Geo.PathLength(t.FutureRoute);
            var ahead = Geo.Along(t.FutureRoute, Math.Min(len * 0.999, 15));
            if (Geo.DistanceKm(pos, ahead) <= 3) ahead = Geo.Along(t.FutureRoute, len * 0.999);
            if (Geo.DistanceKm(pos, ahead) > 3) course = Geo.Bearing(pos, ahead);
        }
        if (course is not { } c) return (Motion.Unknown, null, null);
        double toPlace = Geo.Bearing(pos, at);
        double ang = Geo.AngleDiff(c, toPlace);
        // кратчайшее расстояние, на котором пройдёт объект, если сохранит курс
        double miss = ang >= 90 ? km : km * Math.Sin(ang * Math.PI / 180);
        if (ang >= 110) return (Motion.Receding, null, null);
        if (ang < 90 && miss <= Math.Max(radius, 8))
        {
            TimeSpan? eta = t.HasMeasuredSpeed && t.SpeedKmh > 1 ? TimeSpan.FromHours(km * Math.Cos(ang * Math.PI / 180) / t.SpeedKmh) : null;
            return (Motion.Approaching, miss, eta);
        }
        return (Motion.Passing, miss, null);
    }

    /// <summary>Строка для уведомления: «Дача: БПЛА в 12 км к юго-западу, идёт к вам (~6 мин)».</summary>
    public static string Text(Report r, string threat)
    {
        // автоматическая подпись своей точки («Своя точка · Почеп, 6 км СВ») — коротко; своё название — как есть
        var name = r.Place.Name.StartsWith("Своя точка · ", StringComparison.Ordinal) ? "Своя точка" : r.Place.Name;
        string where = r.DistanceKm < 2 ? "над пунктом" : $"в {Math.Round(r.DistanceKm):0} км {Side(r.BearingFromPlace)}";
        string move = r.Motion switch
        {
            Motion.Approaching => ", идёт к вам" + (r.Eta is { } eta ? $" (~{Minutes(eta)})" : ""),
            Motion.Passing => r.MissKm is { } m ? $", проходит стороной (~{Math.Round(m):0} км)" : ", проходит стороной",
            Motion.Receding => ", удаляется",
            _ => "",
        };
        return $"{name}: {threat} {where}{move}";
    }

    private static string Minutes(TimeSpan t) =>
        t.TotalMinutes < 1.5 ? "1 мин" : t.TotalMinutes < 90 ? $"{Math.Round(t.TotalMinutes):0} мин" : $"{t.TotalHours:0.#} ч";
}
