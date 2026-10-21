using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Касается ли тревога выбранных зон наблюдения (регионы, районы, населённые пункты) — для уведомлений,
/// пометок «моя зона» и фильтра ленты. Геометрия районов приходит извне (контуры — в слое карты).
/// </summary>
public static class WatchMatcher
{
    /// <param name="radiusKm">Радиус по умолчанию для пунктов без своего радиуса.</param>
    /// <param name="districtsOf">Районы, которые покрывает точка тревоги (названный район, район вокруг пункта, районы «части региона»).</param>
    /// <param name="districtAt">Район, в котором лежит координата.</param>
    public static bool Matches(AlertEvent e, IReadOnlyList<WatchArea> areas, double radiusKm,
        Func<MapPoint, IEnumerable<string>>? districtsOf = null, Func<GeoPoint, string?>? districtAt = null)
    {
        if (areas.Count == 0) return true;
        foreach (var a in areas)
            if (Matches(e, a, radiusKm, districtsOf, districtAt)) return true;
        return false;
    }

    public static bool Matches(AlertEvent e, WatchArea a, double radiusKm,
        Func<MapPoint, IEnumerable<string>>? districtsOf = null, Func<GeoPoint, string?>? districtAt = null)
    {
        switch (a.Kind)
        {
            case WatchArea.RegionKind:
                return e.RegionIds.Contains(a.Id) || e.WholeRegionIds.Contains(a.Id) || e.ExpectedRegionIds.Contains(a.Id);

            case WatchArea.DistrictKind:
            {
                // тревога по всему региону касается и каждого его района
                if (e.WholeRegionIds.Contains(a.Region)) return true;
                if (!e.RegionIds.Contains(a.Region) && !e.ExpectedRegionIds.Contains(a.Region) && e.Target == null) return false;
                if (districtsOf != null)
                    foreach (var p in e.Points)
                        if (!p.IsRegionLevel && districtsOf(p).Contains(a.Id)) return true;
                return e.Target is { } t && districtAt?.Invoke(t) == a.Id;
            }

            case WatchArea.PlaceKind:
            {
                if (e.WholeRegionIds.Contains(a.Region)) return true;
                var at = new GeoPoint(a.Lat, a.Lon);
                if (a.RadiusKm > 0) radiusKm = a.RadiusKm;
                foreach (var p in e.Points)
                {
                    if (p.IsRegionLevel) continue;
                    if (Geo.DistanceKm(p.Pos, at) <= radiusKm) return true;
                    // пункт внутри зоны района без контура в подложке (круг района шире радиуса пункта)
                    if (p.DistrictRegion != null && districtsOf?.Invoke(p)?.Any() != true && Geo.DistanceKm(p.Pos, at) <= p.RadiusKm) return true;
                }
                if (e.Target is { } t && Geo.DistanceKm(t, at) <= radiusKm) return true;
                // тревога по району, в котором лежит пункт
                if (districtsOf != null && districtAt?.Invoke(at) is { } home)
                    foreach (var p in e.Points)
                        if (!p.IsRegionLevel && p.DistrictRegion != null && districtsOf(p).Contains(home)) return true;
                return false;
            }
        }
        return false;
    }
}
