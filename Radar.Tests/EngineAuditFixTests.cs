using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Исправления движка по аудиту: запоздавшие сообщения, источники, отбой с неизвестными пунктами.</summary>
[Collection("Engine")]
public class EngineAuditFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static AlertEngine NewEngine(HttpClient http) =>
        new(new AppSettings { Channels = [new ChannelConfig { Name = "a" }, new ChannelConfig { Name = "b" }] }, http, new Geocoder(http, new NominatimThrottle()));

    private static List<AlertEvent> Active(AlertEngine e, DateTimeOffset at) =>
        [.. e.Snapshot().Where(x => x.Status != AlertStatus.AllClear && x.ExpiresAt > at)];

    [Theory]
    [InlineData(50.52, 36.52, true)]
    [InlineData(48.30, 38.05, true)]
    [InlineData(50.10, 38.24, false)]
    [InlineData(50.15, 37.89, false)]
    public void AuditNearCityAtLine(double lat, double lon, bool nearCity) =>
        Assert.Equal(nearCity, PlaceIndex.NearestCity(new GeoPoint(lat, lon), EngineAudit.NearCityKm, EngineAudit.NearCityPopulation) != null);

    [Fact]
    public void LateClearDoesNotCancelNewerAlert()
    {
        // история медленного канала: отбой 11:30 пришёл после тревоги 12:00 другого канала
        using var http = new HttpClient();
        var e = NewEngine(http);
        foreach (var _ in e.ReplaySync([new RawMessage("a", 1, T0, "Белгородская область\nОпасность по БПЛА"),
                                         new RawMessage("b", 2, T0.AddMinutes(-30), "Белгородская область\nОтбой опасности БПЛА")])) { }
        Assert.Single(Active(e, T0));
    }

    [Fact]
    public void LateConfirmationDoesNotShortenAlert()
    {
        using var http = new HttpClient();
        var e = NewEngine(http);
        foreach (var _ in e.ReplaySync([new RawMessage("a", 1, T0, "Курская область\nОпасность по БПЛА")])) { }
        var before = Active(e, T0).Single().ExpiresAt;
        // то же объявление, но более раннее сообщение (догрузка истории) — срок и время не откатываются
        foreach (var _ in e.ReplaySync([new RawMessage("b", 2, T0.AddMinutes(-40), "Курская область, опасность БПЛА")])) { }
        var ev = Active(e, T0).Single();
        Assert.True(ev.ExpiresAt >= before);
        Assert.Equal(T0, ev.Time);
    }

    [Fact]
    public void FarTargetFromAnotherSentenceIsNotChainEnd()
    {
        // сводка: «…в сторону Таганрога… По северу ЛНР в сторону Миллерово… Из-под Чернигова в сторону Курской области» —
        // Курская область лежит в другой стороне, чем первая цель, и не становится целью всей тревоги
        using var http = new HttpClient();
        var e = NewEngine(http);
        foreach (var _ in e.ReplaySync([new RawMessage("a", 1, T0,
            "Продолжается полёт БПЛА от Днепропетровской области в сторону ЛДНР в сторону Таганрогского залива - Таганрог - Азов - Ейск.\n" +
            "По северу ЛНР пролёты БПЛА в сторону Миллерово, Чертково, Ростовская область.\n" +
            "Из-под Чернигов пуски ударных БПЛА в сторону Брянской области, Курской области.")])) { }
        var kursk = Gazetteer.Get("kursk")!.Center;
        Assert.DoesNotContain(Active(e, T0), x => x.Target is { } t && Geo.DistanceKm(t, kursk) < 50);
    }

    [Fact]
    public void SameSecondRepostsGiveSameResultInAnyOrder()
    {
        // один текст в одну секунду из канала области и общего канала: обстановка и треки не зависят от порядка прихода
        var t = new DateTimeOffset(2026, 9, 1, 6, 31, 53, TimeSpan.Zero);
        RawMessage[] a =
        [
            new("LPRalarm", 1, t, "Таволжанка Шбк\nВнимание ФПВ"),
            new("lpr1_Belgorod_alarm", 2, t, "Таволжанка Шбк\nВнимание ФПВ"),
        ];
        string Run(IEnumerable<RawMessage> msgs)
        {
            using var http = new HttpClient();
            var e = new AlertEngine(new AppSettings(), http, new Geocoder(http, new NominatimThrottle()));
            foreach (var _ in e.ReplaySync([.. msgs])) { }
            return string.Join(";", e.Snapshot().Select(x => string.Join(",", x.Points.Select(p => $"{p.Label}@{p.Pos.Lat:F2}"))).Order()) + "|" +
                   string.Join(";", e.TrackSnapshot().Select(x => $"{x.Head.Pos.Lat:F2}").Order());
        }
        Assert.Equal(Run(a), Run(a.Reverse()));
    }

    [Fact]
    public void CourseCrossingFrontLineIsNotForecast()
    {
        // Каланчак → Скадовск → Скадовский район: курс на северо-запад в конце снова далёк от линии (за Днепром),
        // но по пути пересекает её — прогноз туда не рисуется
        var t0 = new DateTimeOffset(2026, 6, 24, 20, 10, 0, TimeSpan.Zero);
        RawMessage[] msgs =
        [
            new("lpr1_Kherson_alarm", 1, t0, "Каланчак и близлижащие\nОпасность по БПЛА\nХерсонская область РФ"),
            new("LPRalarm", 2, t0.AddMinutes(8), "Скадовск и близлежащие\nТревога по Баба Яга\nХерсонская область РФ"),
            new("LPRalarm", 3, t0.AddMinutes(44), "Скадовский район\nТревога по БПЛА\nХерсонская область РФ"),
        ];
        using var http = new HttpClient();
        var e = new AlertEngine(new AppSettings(), http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in e.ReplaySync(msgs)) { }
        // «Баба-яга» в Скадовске — тактический коптер: может стать отдельным треком; проверяем все треки
        foreach (var t in e.TrackSnapshot())
            Assert.True(t.FutureRoute.Count < 2 || Borderline.DistanceKm(t.FutureRoute[^1]) >= Borderline.DistanceKm(t.Head.Pos) - 5);
        // центр Скадовского района ближе к линии, чем Каланчак: к короткому треку без курса он не присоединяется
        Assert.DoesNotContain(e.TrackSnapshot(), t => t.Points.Count >= 2 && t.Points[^1].Label == "Скадовский район");
    }

    [Fact]
    public void SourcesCountReadsWithPlural()
    {
        var ev = new AlertEvent { Key = "k", Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = [], RegionNames = "", Signature = "" };
        for (int i = 0; i < 5; i++) ev.Sources.Add(new RawMessage("c" + i, i, T0, ""));
        Assert.Equal("5 источников", ev.SourceCount);
        ev.Sources.Add(new RawMessage("x", 9, T0, ""));
        for (int i = 0; i < 15; i++) ev.Sources.Add(new RawMessage("y" + i, i, T0, ""));
        Assert.Equal("21 источник", ev.SourceCount);
    }

    [Fact]
    public void SnapshotListEnumeratesWhileGrowing()
    {
        var l = new SnapshotList<int>();
        for (int i = 0; i < 10; i++) l.Add(i);
        int n = 0;
        foreach (var x in l) { l.Add(100 + x); n++; }
        Assert.Equal(10, n);
        Assert.Equal(20, l.Count);
    }
}
