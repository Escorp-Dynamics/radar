using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Сводка статистики архива: счётчики, рейтинги, мои зоны, направления, динамика.</summary>
public class StatsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static StatEvent Ev(int hour, ThreatType t, string region, int minutes, string? place = null, double lat = 0, double lon = 0, AlertStatus st = AlertStatus.Alert) =>
        new(T0.AddHours(hour), T0.AddHours(hour).AddMinutes(minutes), t, ThreatKind.Unknown, AlertLevel.Alarm, st, [region],
            place == null ? [] : [place], place == null ? [] : [lat], place == null ? [] : [lon]);

    [Fact]
    public void AlertHoursMergeOverlapsAndStayWithinPeriod()
    {
        // 30 одновременных тревог по часу: «под тревогой» — 1 час, а не 30
        var ev = Enumerable.Range(0, 30).Select(_ => Ev(5, ThreatType.Uav, "belgorod", 60)).ToList();
        // тревога, вышедшая за границу периода, обрезается им
        ev.Add(Ev(7 * 24 - 1, ThreatType.Uav, "kursk", 600));
        var r = StatsSummary.Build(ev, [], T0, T0.AddDays(7), null, [], 15);
        Assert.Equal(2, r.AlertHours, 6);
        Assert.InRange(r.AlertHours, 0, 7 * 24);
        Assert.Equal(1, r.Regions.First(x => x.RegionId == "belgorod").Hours, 6);
    }

    [Fact]
    public void AlertStartIsFirstHistoryEntryNotBorrowedSource()
    {
        // свежая тревога 23:00 забрала (для геокодера) сообщение широкой тревоги 16:30 — начало всё равно 23:00
        var ev = new AlertEvent { Key = "k", Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = [], RegionNames = "", Signature = "" };
        ev.Sources.Add(new RawMessage("a", 1, T0.AddHours(23), ""));
        ev.AddHistory(T0.AddHours(23), "a", "тревога");
        ev.Sources.Add(new RawMessage("b", 2, T0.AddHours(16.5), ""));
        Assert.Equal(T0.AddHours(23), ArchiveStats.AlertStart(ev));
        // без истории — по самому раннему источнику
        var bare = new AlertEvent { Key = "k2", Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = [], RegionNames = "", Signature = "" };
        bare.Sources.Add(new RawMessage("a", 1, T0.AddHours(2), ""));
        bare.Sources.Add(new RawMessage("b", 2, T0.AddHours(1), ""));
        Assert.Equal(T0.AddHours(1), ArchiveStats.AlertStart(bare));
    }

    [Fact]
    public void UnionHoursJoinsTouchingAndSkipsEmpty()
    {
        var ev = new List<StatEvent> { Ev(1, ThreatType.Uav, "a", 60), Ev(2, ThreatType.Uav, "a", 30), Ev(10, ThreatType.Uav, "a", 0), Ev(4, ThreatType.Uav, "a", 15) };
        Assert.Equal(1.75, StatsSummary.UnionHours(ev, T0, T0.AddDays(1)), 6);
    }

    [Fact]
    public void CountsRankingsAndMine()
    {
        var ev = new List<StatEvent>
        {
            Ev(1, ThreatType.Uav, "belgorod", 30, "Шебекино", 50.41, 36.89),
            Ev(2, ThreatType.Uav, "belgorod", 60, "Шебекино", 50.41, 36.89),
            Ev(26, ThreatType.Missile, "kursk", 20, "Курск", 51.73, 36.19, AlertStatus.Impact),
            Ev(50, ThreatType.Uav, "bryansk", 10),
        };
        var watch = new List<WatchArea>
        {
            new() { Kind = WatchArea.PlaceKind, Id = "pt:x", Name = "Дача", Lat = 50.45, Lon = 36.9, RadiusKm = 10 },
            new() { Kind = WatchArea.RegionKind, Id = "kursk", Name = "Курская" },
        };
        var r = StatsSummary.Build(ev, [], T0, T0.AddDays(7), null, watch, 15);
        Assert.Equal(4, r.Alerts);
        Assert.Equal(3, r.Uav);
        Assert.Equal(1, r.Missile);
        Assert.Equal(1, r.Strikes);
        Assert.Equal(4, r.Days.Sum(d => d.ByThreat.Values.Sum()));
        Assert.Equal("belgorod", r.Regions[0].RegionId);
        Assert.Equal(1.5, r.Regions[0].Hours, 3);
        Assert.Equal("Шебекино", r.Places[0].Place);
        // дача в 4,5 км от Шебекино — обе тревоги по нему; Курская — одна тревога
        var dacha = r.Mine.Single(m => m.Name == "Дача");
        Assert.Equal(2, dacha.Count);
        Assert.True(dacha.ClosestKm is > 3 and < 6);
        Assert.Equal(1, r.Mine.Single(m => m.Name == "Курская").Count);
        Assert.Equal(1.0, r.RegionHeat["belgorod"], 3);

        // фильтр региона
        var rb = StatsSummary.Build(ev, [], T0, T0.AddDays(7), "kursk", [], 15);
        Assert.Equal(1, rb.Alerts);
        Assert.Equal(1, rb.Missile);
    }

    [Fact]
    public void SectorsAndTrend()
    {
        // объект пришёл с запада (из точки западнее) — сектор «З»
        var tracks = new List<StatTrack>
        {
            new(T0.AddHours(1), ThreatType.Uav, ThreatKind.Unknown, 51.0, 33.0, 51.0, 35.0, "граница, Сумская обл.", true, 3),
            new(T0.AddHours(2), ThreatType.Uav, ThreatKind.Unknown, 49.0, 36.0, 50.5, 36.0, null, false, 2),
        };
        var r = StatsSummary.Build([], tracks, T0, T0.AddDays(1), null, [], 15);
        Assert.Equal(1, r.Sectors[6]); // запад
        Assert.Equal(1, r.Sectors[4]); // юг
        Assert.Equal("граница, Сумская обл.", r.LaunchAreas.Single().Label);

        // динамика: 2 тревоги против 1 в предыдущие сутки — +100 %
        var ev = new List<StatEvent> { Ev(-5, ThreatType.Uav, "kursk", 10), Ev(3, ThreatType.Uav, "kursk", 10), Ev(5, ThreatType.Uav, "kursk", 10) };
        var r2 = StatsSummary.Build(ev, [], T0, T0.AddDays(1), null, [], 15, ev);
        Assert.Equal(1.0, r2.Trend!.Value, 3);
    }
}
