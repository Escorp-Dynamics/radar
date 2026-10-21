using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Расстояние, сторона и движение угрозы относительно выбранного пункта (для уведомлений).</summary>
public class ProximityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly WatchArea Dacha = new()
    {
        Kind = WatchArea.PlaceKind, Id = WatchArea.PointPrefix + "@53.240,34.360", Name = "Своя точка · Брянск", Lat = 53.24, Lon = 34.36, RadiusKm = 10,
    };

    private static AlertEvent Ev(GeoPoint p) => new()
    {
        Key = "e", Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = ["bryansk"], RegionNames = "Брянская", Signature = "e",
        Points = [new MapPoint(p, "x", false)],
    };

    private static Track TrackThrough(params GeoPoint[] pts)
    {
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, SpeedKmh = 120, SpeedSamples = 2, Created = T0, Updated = T0.AddMinutes(10 * (pts.Length - 1)) };
        for (int i = 0; i < pts.Length; i++) t.Points.Add(new TrackPoint(pts[i], T0.AddMinutes(10 * i), "p" + i, "c"));
        return t;
    }

    [Fact]
    public void ApproachingFromSouthWest()
    {
        // с юго-запада прямо на точку
        var a = Geo.Destination(new(53.24, 34.36), 225, 40);
        var b = Geo.Destination(new(53.24, 34.36), 225, 20);
        var t = TrackThrough(a, b);
        var r = Proximity.Compute(Ev(b), t, [Dacha], 15, t.Updated)!;
        Assert.Equal(Proximity.Motion.Approaching, r.Motion);
        Assert.InRange(r.DistanceKm, 18, 22);
        var text = Proximity.Text(r, "БПЛА");
        Assert.StartsWith("Своя точка: БПЛА в 20 км к юго-западу, идёт к вам", text);
    }

    [Fact]
    public void ReceedingAndPassing()
    {
        var p = new GeoPoint(53.24, 34.36);
        // удаляется на север
        var t1 = TrackThrough(Geo.Destination(p, 0, 15), Geo.Destination(p, 0, 35));
        Assert.Equal(Proximity.Motion.Receding, Proximity.Compute(Ev(t1.Head.Pos), t1, [Dacha], 15, t1.Updated)!.Motion);
        // проходит стороной: восточнее в 40 км, курс на север
        var t2 = TrackThrough(Geo.Destination(Geo.Destination(p, 90, 40), 180, 30), Geo.Destination(Geo.Destination(p, 90, 40), 180, 10));
        var r2 = Proximity.Compute(Ev(t2.Head.Pos), t2, [Dacha], 15, t2.Updated)!;
        Assert.Equal(Proximity.Motion.Passing, r2.Motion);
        Assert.Contains("стороной", Proximity.Text(r2, "БПЛА"));
    }

    [Fact]
    public void NoTrackGivesDistanceOnly()
    {
        var r = Proximity.Compute(Ev(new(53.0, 34.36)), null, [Dacha], 15, T0)!;
        Assert.Equal(Proximity.Motion.Unknown, r.Motion);
        Assert.Contains("к югу", Proximity.Text(r, "БПЛА"));
        // без выбранных пунктов — ничего
        Assert.Null(Proximity.Compute(Ev(new(53.0, 34.36)), null, [new WatchArea { Kind = WatchArea.RegionKind, Id = "bryansk" }], 15, T0));
    }
}
