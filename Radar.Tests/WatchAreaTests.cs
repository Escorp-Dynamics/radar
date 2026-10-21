using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Зоны наблюдения: регион, район, населённый пункт — какие тревоги их касаются.</summary>
public class WatchAreaTests
{
    private static readonly GeoPoint Shebekino = new(50.413, 36.925);
    private static readonly GeoPoint Valuyki = new(50.197, 38.117);

    private static AlertEvent Ev(string region, params MapPoint[] points) => new()
    {
        Key = "k", Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = [region], RegionNames = region, Signature = "s",
        Points = [.. points],
    };

    private static readonly WatchArea BelgorodRegion = new() { Kind = WatchArea.RegionKind, Id = "belgorod", Region = "belgorod", Name = "Белгородская область" };
    private static readonly WatchArea ValuyskiyDistrict = new() { Kind = WatchArea.DistrictKind, Id = "belgorod|валуйский", Region = "belgorod", Name = "Валуйский" };
    private static readonly WatchArea ShebekinoPlace = new() { Kind = WatchArea.PlaceKind, Id = "Шебекино@50.413,36.925", Region = "belgorod", Name = "Шебекино", Lat = Shebekino.Lat, Lon = Shebekino.Lon };

    // районы точек: у Валуек — Валуйский, у Шебекино — Шебекинский
    private static IEnumerable<string> DistrictsOf(MapPoint p) =>
        Geo.DistanceKm(p.Pos, Valuyki) < 20 ? ["belgorod|валуйский"] : Geo.DistanceKm(p.Pos, Shebekino) < 20 ? ["belgorod|шебекинский"] : [];
    private static string? DistrictAt(GeoPoint g) => DistrictsOf(new MapPoint(g, "", false)).FirstOrDefault();

    [Fact]
    public void EmptySelectionMatchesEverything() =>
        Assert.True(WatchMatcher.Matches(Ev("kursk", new MapPoint(new(51.7, 36.2), "Курск", false)), [], 15));

    [Fact]
    public void RegionMatchesAnyAlertInIt()
    {
        Assert.True(WatchMatcher.Matches(Ev("belgorod", new MapPoint(Valuyki, "Валуйки", false)), [BelgorodRegion], 15));
        Assert.False(WatchMatcher.Matches(Ev("kursk", new MapPoint(new(51.7, 36.2), "Курск", false)), [BelgorodRegion], 15));
    }

    [Fact]
    public void DistrictMatchesOnlyItsOwnPoints()
    {
        Assert.True(WatchMatcher.Matches(Ev("belgorod", new MapPoint(Valuyki, "Валуйки", false)), [ValuyskiyDistrict], 15, DistrictsOf, DistrictAt));
        Assert.False(WatchMatcher.Matches(Ev("belgorod", new MapPoint(Shebekino, "Шебекино", false)), [ValuyskiyDistrict], 15, DistrictsOf, DistrictAt));
    }

    [Fact]
    public void WholeRegionAlertCoversDistrictsAndPlaces()
    {
        var e = Ev("belgorod", new MapPoint(new(50.6, 36.6), "Белгородская область", true));
        e.WholeRegionIds = ["belgorod"];
        Assert.True(WatchMatcher.Matches(e, [ValuyskiyDistrict], 15, DistrictsOf, DistrictAt));
        Assert.True(WatchMatcher.Matches(e, [ShebekinoPlace], 15, DistrictsOf, DistrictAt));
    }

    [Fact]
    public void PlaceMatchesWithinRadius()
    {
        var near = Ev("belgorod", new MapPoint(new(50.45, 36.95), "Маслова Пристань", false)); // ~4.5 км
        var far = Ev("belgorod", new MapPoint(Valuyki, "Валуйки", false));                     // ~90 км
        Assert.True(WatchMatcher.Matches(near, [ShebekinoPlace], 15, DistrictsOf, DistrictAt));
        Assert.False(WatchMatcher.Matches(far, [ShebekinoPlace], 15, DistrictsOf, DistrictAt));
        Assert.False(WatchMatcher.Matches(near, [ShebekinoPlace], 3, DistrictsOf, DistrictAt));
    }

    [Fact]
    public void ThreatHeadingToPlaceMatches()
    {
        var e = Ev("belgorod", new MapPoint(new(50.9, 36.0), "Ракитное", false));
        e.Target = new GeoPoint(50.42, 36.93);
        Assert.True(WatchMatcher.Matches(e, [ShebekinoPlace], 15, DistrictsOf, DistrictAt));
    }

    [Fact]
    public void OldRegionListMigratesToWatchAreas()
    {
        var s = new AppSettings { NotifyRegions = ["belgorod", "kursk"] };
        Assert.True(s.MigrateWatchAreas());
        Assert.Empty(s.NotifyRegions);
        Assert.Equal(["belgorod", "kursk"], s.WatchAreas.Select(a => a.Id));
        Assert.All(s.WatchAreas, a => Assert.Equal(WatchArea.RegionKind, a.Kind));
        Assert.False(s.MigrateWatchAreas());
    }

    [Fact]
    public void PlaceSearchFindsRussianTownsBiggestFirst()
    {
        var res = PlaceIndex.SearchPlaces("шебекино");
        Assert.NotEmpty(res);
        Assert.Equal("Шебекино", res[0].Name);
        Assert.Equal("belgorod", res[0].RegionId);
        Assert.DoesNotContain(res, p => p.RegionId.StartsWith("ua_", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaceOwnRadiusOverridesDefault()
    {
        var near = Ev("belgorod", new MapPoint(new(50.50, 36.98), "Новая Таволжанка", false)); // ~10 км от Шебекино
        var wide = new WatchArea { Kind = WatchArea.PlaceKind, Id = "w", Region = "belgorod", Name = "Шебекино", Lat = Shebekino.Lat, Lon = Shebekino.Lon, RadiusKm = 30 };
        var tight = new WatchArea { Kind = WatchArea.PlaceKind, Id = "t", Region = "belgorod", Name = "Шебекино", Lat = Shebekino.Lat, Lon = Shebekino.Lon, RadiusKm = 5 };
        // общий радиус 5 км, но у пункта свой — 30
        Assert.True(WatchMatcher.Matches(near, [wide], 5, DistrictsOf, DistrictAt));
        // общий 50 км, у пункта свой — 5
        Assert.False(WatchMatcher.Matches(near, [tight], 50, DistrictsOf, DistrictAt));
    }

    [Fact]
    public void CustomPointIsPlaceWithNearestLandmark()
    {
        var a = new WatchArea { Kind = WatchArea.PlaceKind, Id = WatchArea.PointPrefix + WatchArea.PlaceId("", 50.42, 36.94), Lat = 50.42, Lon = 36.94 };
        Assert.True(a.IsPoint);
        Assert.False(new WatchArea { Kind = WatchArea.PlaceKind, Id = WatchArea.PlaceId("Шебекино", 50.413, 36.925) }.IsPoint);
        // ориентир для подписи — ближайший пункт рядом
        var near = PlaceIndex.NearestPlace(new GeoPoint(50.42, 36.94));
        Assert.NotNull(near);
        Assert.True(Geo.DistanceKm(near!.Pos, new GeoPoint(50.42, 36.94)) < 15);
    }
}
