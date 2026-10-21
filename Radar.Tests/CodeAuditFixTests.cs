using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Исправления по широкому аудиту кода: геопоиск, разбор, нормализация, настройки, слияние треков.</summary>
public class CodeAuditFixTests
{
    [Fact]
    public void FindPrefersSettlementInAnyContextRegion()
    {
        // в Ростовской области «Успенская» — станция, в Краснодарском крае — станица
        var p = PlaceIndex.Find("Успенская", ["rostov", "krasnodar"]);
        Assert.NotNull(p);
        Assert.Equal('P', p!.Kind);
        Assert.Equal("krasnodar", p.RegionId);
    }

    [Fact]
    public void FindUniqueIgnoresSameNamedNatureObjects()
    {
        // гора Кременная в Крыму не делает город в ЛНР неоднозначным
        var p = PlaceIndex.FindUnique("Кременная", ["lnr", "crimea"]);
        Assert.NotNull(p);
        Assert.Equal("lnr", p!.RegionId);
    }

    [Theory]
    [InlineData("Ку\u0301рская область", "курская")]
    [InlineData("Ке\u0308рчь\u00ad", "керчь")]
    public void MarksDoNotSplitWords(string text, string word)
    {
        Assert.Contains(word, Gazetteer.Tokenize(Gazetteer.StripMarks(text)));
    }

    [Fact]
    public void StatusLinesWithEmojiSplitIntoBlocks()
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow,
            "Ейск\nКраснодарский край\n⚠️ Опасность по БПЛА\nКурская область\nОрловская область\n✅ Отбой опасности по БПЛА"), []);
        Assert.Equal(2, m.Blocks.Count);
        Assert.Contains(m.Blocks, b => b.Status == AlertStatus.AllClear && b.Regions.Contains("kursk"));
    }

    [Fact]
    public void SettingsNormalizeFixesNullsAndRanges()
    {
        var s = new AppSettings { Channels = null!, WatchAreas = null!, HiddenKinds = null!, PollIntervalSeconds = 0, HistoryHours = 100000, AlertLifetimeMinutes = -5, AnimationQuality = 7 };
        SettingsStore.Normalize(s);
        Assert.NotEmpty(s.Channels);
        Assert.NotNull(s.WatchAreas);
        Assert.NotNull(s.HiddenKinds);
        Assert.Equal(5, s.PollIntervalSeconds);
        Assert.Equal(72, s.HistoryHours);
        Assert.Equal(5, s.AlertLifetimeMinutes);
        Assert.Equal(2, s.AnimationQuality);
        Assert.Equal(1, new AppSettings().AnimationQuality);
    }

    [Fact]
    public void HamletByExactFormLosesToTownByLemma()
    {
        // «до Каменки»: хутор «Каменки» (0 жителей) — не то же, что Каменка (9 тыс.) в 120 км от него
        var p = PlaceIndex.Find("Каменки", ["voronezh"]);
        Assert.NotNull(p);
        Assert.Equal("Каменка", p!.Name);
    }

    [Fact]
    public void BearingSkipsRepeatedPoint()
    {
        var t0 = DateTimeOffset.UtcNow;
        var a = new GeoPoint(50, 36); var b = new GeoPoint(50, 37);
        var tr = new Track { Id = 1, Threat = ThreatType.Uav };
        tr.Points.Add(new TrackPoint(a, t0, "a", "c"));
        tr.Points.Add(new TrackPoint(b, t0.AddMinutes(10), "b", "c"));
        tr.Points.Add(new TrackPoint(b, t0.AddMinutes(20), "b", "c"));
        Assert.InRange(tr.Bearing!.Value, 80, 100);
    }
}

[Collection("Engine")]
public class ReplayStopTests
{
    [Fact]
    public async Task StoppedReplayDoesNotFreezeClock()
    {
        // архив прошлого месяца; движок истории останавливают сразу, пока идёт первая перемотка
        var t0 = DateTimeOffset.UtcNow.AddDays(-40);
        var msgs = Enumerable.Range(0, 4000).Select(i => new RawMessage("test", i + 1, t0.AddSeconds(i * 30), $"Белгород — БПЛА {i}")).ToList();
        try
        {
            for (int k = 0; k < 5; k++)
            {
                var eng = new AlertEngine(new AppSettings { OnlineGeocoding = false }, new HttpClient(), new Geocoder(new HttpClient(), new NominatimThrottle()));
                eng.StartReplay(msgs, 60, msgs[0].Time, msgs[^1].Time);
                await Task.Delay(30 + k * 20);
                eng.Dispose();
                Clock.StopSimulation();
                await Task.Delay(600);
                Assert.False(Clock.IsSimulated, $"попытка {k}: часы снова в режиме архива");
                Assert.True(Math.Abs((Clock.Now - DateTimeOffset.UtcNow).TotalMinutes) < 1);
            }
        }
        finally { Clock.StopSimulation(); }
    }
}

public class StatsAuditFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static StatEvent Ev(DateTimeOffset t, string[] regions, string[] places, string[] placeRegions) =>
        new(t, t.AddMinutes(30), ThreatType.Uav, ThreatKind.Unknown, AlertLevel.Alarm, AlertStatus.Alert, regions, places,
            [.. places.Select(_ => 0.0)], [.. places.Select(_ => 0.0)]) { PlaceRegions = placeRegions };

    [Fact]
    public void PlaceKeepsItsOwnRegion()
    {
        var ev = new List<StatEvent>
        {
            Ev(T0.AddHours(1), ["lipetsk", "sevastopol"], ["Елец", "Бельбек"], ["lipetsk", "sevastopol"]),
            Ev(T0.AddHours(2), ["sevastopol", "lipetsk"], ["Бельбек"], ["sevastopol"]),
        };
        var r = StatsSummary.Build(ev, [], T0, T0.AddDays(7), null, [], 15);
        var belbek = r.Places.Where(p => p.Place == "Бельбек").ToList();
        Assert.Single(belbek);
        Assert.Equal(2, belbek[0].Count);
        Assert.Equal("sevastopol", belbek[0].RegionId);
    }

    [Fact]
    public void MonthTrendComparesWithPreviousMonthPerDay()
    {
        // февраль: 28 тревог (по одной в сутки), март: 31 — та же интенсивность, роста нет
        var feb = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var ev = new List<StatEvent>();
        for (int d = 0; d < 28; d++) ev.Add(Ev(feb.AddDays(d).AddHours(5), ["kursk"], [], []));
        for (int d = 0; d < 31; d++) ev.Add(Ev(T0.AddDays(d).AddHours(5), ["kursk"], [], []));
        var r = StatsSummary.Build(ev, [], T0, T0.AddMonths(1), null, [], 15, ev);
        Assert.NotNull(r.Trend);
        Assert.InRange(r.Trend!.Value, -0.01, 0.01);
    }

    [Fact]
    public void NormalizeFixesWatchAreasAndLevel()
    {
        var s = new AppSettings { NotifyMinLevel = "7", WatchRadiusKm = 250 };
        s.WatchAreas = [new WatchArea { Kind = null!, Id = null!, Name = null! }, new WatchArea { Kind = WatchArea.PlaceKind, Id = "x@1,2", Name = "x", RadiusKm = 500 }];
        SettingsStore.Normalize(s);
        Assert.Equal(nameof(AlertLevel.Danger), s.NotifyMinLevel);
        Assert.Equal(100, s.WatchRadiusKm);
        Assert.Single(s.WatchAreas);
        Assert.Equal(100, s.WatchAreas[0].RadiusKm);
    }
}

[Collection("Engine")]
public class PollIntervalTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<section class=\"tgme_channel_history\"></section>") });
        }
    }

    [Fact]
    public async Task ShorterIntervalAppliesWithoutWaitingOldPause()
    {
        var h = new CountingHandler();
        var http = new HttpClient(h);
        var settings = new AppSettings { OnlineGeocoding = false, OnlineFrontLine = false, PollIntervalSeconds = 600, Channels = [new() { Name = "a" }] };
        using var eng = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        eng.Start();
        // первый опрос (история) и старт долгой паузы
        for (int i = 0; i < 50 && Volatile.Read(ref h.Calls) == 0; i++) await Task.Delay(100);
        await Task.Delay(500);
        int before = Volatile.Read(ref h.Calls);
        settings.PollIntervalSeconds = 5;
        eng.PollIntervalChanged();
        for (int i = 0; i < 80 && Volatile.Read(ref h.Calls) == before; i++) await Task.Delay(100);
        Assert.True(Volatile.Read(ref h.Calls) > before, "после смены интервала опрос не возобновился в пределах 8 с");
    }
}

[Collection("Engine")]
public class StatsRegionTrackTests
{
    [Fact]
    public void TrackEndingInNeighbourIsNotCountedForRegion()
    {
        var t0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        // оба трека кончаются внутри рамки Белгородской области, но второй — уже в Воронежской
        var tracks = new List<StatTrack>
        {
            new(t0.AddHours(1), ThreatType.Uav, ThreatKind.Unknown, 50.0, 35.0, 50.6, 36.6, null, false, 3),
            new(t0.AddHours(2), ThreatType.Uav, ThreatKind.Unknown, 50.0, 35.0, 50.9, 38.4, null, false, 3),
        };
        var saved = RoutePlanner.RegionAt;
        try
        {
            RoutePlanner.RegionAt = p => p.Lon > 38.0 ? "voronezh" : "belgorod";
            var r = StatsSummary.Build([], tracks, t0, t0.AddDays(7), "belgorod", [], 15);
            Assert.Equal(1, r.Sectors.Sum());
        }
        finally { RoutePlanner.RegionAt = saved; }
    }
}
