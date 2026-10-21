using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Подсказки маршрута в тексте и построение маршрута трека.</summary>
public class RouteTests
{
    private static ParsedMessage P(string text, params string[] channelRegions) =>
        MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), channelRegions);

    /// <summary>Пролёт вдоль изогнутой границы (Клинцы → Погар у стыка с Белоруссией) — не разворот к линии.</summary>
    [Fact]
    public void AlongBorderIsNotHeadingToLine()
    {
        var klintsy = new GeoPoint(52.76, 32.24);
        Assert.False(Borderline.HeadsToLine(klintsy, new GeoPoint(52.55, 33.26), 25)); // Погар
        Assert.False(Borderline.HeadsToLine(klintsy, new GeoPoint(52.50, 33.00), 25)); // Стародубский — Погарский
        // прямо к границе — разворот
        Assert.True(Borderline.HeadsToLine(new GeoPoint(52.95, 34.0), new GeoPoint(52.45, 34.0), 25));
    }

    // ---------------- разбор подсказок ----------------

    [Theory]
    [InlineData("Запорожская область | Тревога по БПЛА вдоль трассы Р-280", CorridorKind.Road, "Р-280")]
    [InlineData("Мелитополь | БПЛА по трассе Р 280 в сторону Бердянска", CorridorKind.Road, "Р-280")]
    [InlineData("Ростовская область | М4 много БПЛА", CorridorKind.Road, "М-4")]
    [InlineData("Брянская область | вдоль трассы М3 в направлении Севска", CorridorKind.Road, "М-3")]
    [InlineData("Крым | Таврида от Зуя в направлении Симферополя тревога по БПЛА", CorridorKind.Road, "таврида")]
    [InlineData("Брянская область | БПЛА по руслу реки Десна, в направлении Брянска", CorridorKind.River, "десна")]
    [InlineData("Ростовская область | БПЛА вдоль реки Дон", CorridorKind.River, "дон")]
    [InlineData("Крым | БПЛА вдоль жд путей, с юга", CorridorKind.Rail, null)]
    [InlineData("Херсонская область | БПЛА по берегу от Геническа до Таганрога", CorridorKind.Coast, null)]
    public void CorridorHints(string text, CorridorKind kind, string? name)
    {
        var p = P(text);
        Assert.Contains(p.Route.Corridors, c => c.Kind == kind && (name == null || c.Name == name));
    }

    [Fact]
    public void RoadRefIsNotAnyLetterNumber()
    {
        // «А 6» в тексте даты/номера не путать с трассой — только с явным контекстом не проверяем; главное — «Р-280» не теряется
        Assert.DoesNotContain(P("Курская область | БПЛА 3 шт").Route.Corridors, c => c.Kind == CorridorKind.Road);
    }

    [Theory]
    [InlineData("Брянская область | Запуски БПЛА из Черниговской области в направлении Брянска", "Черниговская область")]
    [InlineData("Запуски БПЛА из Николаевской области с дальнейшим выходом в Чёрное море", "Николаевская область")]
    [InlineData("Курская область | БПЛА от Сум в направлении Курска", "Сумская область")]
    public void ExplicitLaunch(string text, string label)
    {
        var l = P(text).Route.Launch;
        Assert.NotNull(l);
        Assert.True(l!.Explicit);
        Assert.Contains(label.Split(' ')[0][..5], l.Label);
    }

    [Fact]
    public void SeaLaunchSide()
    {
        var l = P("Новороссийск | Опасность по БПЛА с Черного моря").Route.Launch;
        Assert.NotNull(l);
        Assert.Equal("Чёрное море", l!.Label);
    }

    [Fact]
    public void ViaPlaceIsWaypoint()
    {
        var p = P("Луганская область | БПЛА через Литвиновку в направлении Беловодска", "lnr");
        Assert.Contains(p.Places, x => x.Label.StartsWith("Литвиновк") && x.IsVia);
    }

    // ---------------- планировщик ----------------

    private static Track NewTrack(ThreatType threat, params (double Lat, double Lon, int Min)[] pts)
    {
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var t = new Track { Id = 1, Threat = threat, Created = t0, Updated = t0.AddMinutes(pts[^1].Min), SpeedKmh = 180 };
        foreach (var (lat, lon, min) in pts) t.Points.Add(new TrackPoint(new(lat, lon), t0.AddMinutes(min), "", "test"));
        return t;
    }

    [Fact]
    public void LaunchIsInferredBehindTrack()
    {
        // Рыльск → Курск: летит на восток-северо-восток — пуск «сзади», в Сумской области
        var t = NewTrack(ThreatType.Uav, (51.57, 34.68, 0), (51.73, 36.19, 30));
        RoutePlanner.Plan(t);
        Assert.NotNull(t.Launch);
        Assert.False(t.LaunchExplicit);
        Assert.Contains("Сумск", t.LaunchLabel);
        Assert.True(t.PreRoute.Count >= 2);
    }

    [Fact]
    public void FutureGoesThroughViaToTarget()
    {
        var t = NewTrack(ThreatType.Uav, (49.5, 39.0, 0), (49.6, 39.3, 15));
        t.Target = new GeoPoint(50.0, 40.6);
        t.Via.Add((new GeoPoint(49.8, 39.9), "Через"));
        RoutePlanner.Plan(t);
        Assert.Contains(t.FutureRoute, p => Geo.DistanceKm(p, new GeoPoint(49.8, 39.9)) < 0.5);
        Assert.True(Geo.DistanceKm(t.FutureRoute[^1], t.Target.Value) < 0.5);
    }

    [Fact]
    public void AreaTargetBehindIsNotForecast()
    {
        // Рыльск → Ивнянский район, «ожидаем налёт и в направлении Краснодарского края» из сводки: прогноз к краю
        // увёл бы объект назад к линии пуска — направление относится не к этому объекту
        FrontLineSource.LoadForAudit();
        var t = NewTrack(ThreatType.Uav, (51.57, 34.68, 0), (51.10, 36.10, 35));
        t.Target = new GeoPoint(45.3, 39.0);
        t.TargetLabel = "Краснодарский край";
        t.TargetIsArea = true;
        RoutePlanner.Plan(t);
        Assert.True(t.FutureRoute.Count < 2 || Borderline.BackKm(t.Head.Pos, t.FutureRoute[^1], t.Points[0].Pos) <= 20);
    }

    [Fact]
    public void PassedViaIsNotForecastStop()
    {
        // Рохманово → Почеп (на северо-восток), «на север», «далее на Смоленскую область»; «Унечский район» из раннего
        // сообщения — у Рохманово, уже пройден: прогноз не возвращается к нему на запад
        FrontLineSource.LoadForAudit();
        var t = NewTrack(ThreatType.Uav, (52.75, 32.88, 0), (52.93, 33.45, 44));
        t.Target = new GeoPoint(54.9, 32.9);
        t.TargetLabel = "Смоленская область";
        t.TargetIsArea = true;
        t.HeadingHint = 0;
        t.Via.Add((new GeoPoint(52.83, 32.82), "Унечский район"));
        RoutePlanner.Plan(t);
        Assert.DoesNotContain(t.FutureRoute, p => Geo.DistanceKm(p, new GeoPoint(52.83, 32.82)) < 1);
        Assert.True(t.FutureRoute.Count < 2 || Geo.AngleDiff(62, Geo.Bearing(t.Head.Pos, t.FutureRoute[^1])) <= 120);
    }

    [Fact]
    public void CourseAfterViaIsTowardsVia()
    {
        // Счастье → Зеленовка, «к трассе Дон-4 … через Урывский»: продолжение за Урывским — по направлению на него
        // (на восток-юго-восток), а не по последнему изгибу дороги назад, к линии
        FrontLineSource.LoadForAudit();
        var t = NewTrack(ThreatType.Uav, (48.74, 39.23, 0), (48.60, 40.18, 44));
        t.Via.Add((new GeoPoint(48.56, 40.24), "Урывский"));
        t.Corridors.Add(new CorridorHint(CorridorKind.Road, "М-4", "трасса Дон 4"));
        RoutePlanner.Plan(t);
        Assert.True(t.FutureRoute.Count < 2 || Borderline.BackKm(t.Head.Pos, t.FutureRoute[^1], t.Points[0].Pos) <= 20);
    }

    [Fact]
    public void CourseDoesNotTurnBackAlongRoad()
    {
        // Токмакский район → Акимовка (на юго-запад): прямой курс идёт вглубь, а трасса от Акимовки сворачивает
        // на запад, к Днепру — прогноз по ней вёл бы к линии пуска
        FrontLineSource.LoadForAudit();
        var t = NewTrack(ThreatType.Uav, (47.26, 35.71, 0), (46.77, 35.26, 123), (46.70, 35.16, 160));
        if (RouteNetwork.Lines.All(l => l.Id != "Р-280")) return; // слой маршрутов не собран
        // из сообщения «Трасса Р-280 ⏎ Мелитополь - Акимовка»
        t.Corridors.Add(new CorridorHint(CorridorKind.Road, "Р-280", "трасса Р-280"));
        RoutePlanner.Plan(t);
        Assert.True(t.FutureRoute.Count < 2 || Borderline.BackKm(t.Head.Pos, t.FutureRoute[^1], t.Points[0].Pos) <= 20);
    }

    [Fact]
    public void EstimatedPositionFollowsRoute()
    {
        var t = NewTrack(ThreatType.Uav, (49.5, 39.0, 0), (49.6, 39.3, 15));
        t.Target = new GeoPoint(50.0, 40.6);
        t.Via.Add((new GeoPoint(49.9, 39.4), "Через"));
        RoutePlanner.Plan(t);
        var at = t.Head.Time.AddMinutes(5);
        var pos = t.EstimatedPosition(at);
        // через 5 мин на 180 км/ч — ~15 км по маршруту, т. е. на первом участке (к пункту «через»), а не по прямой к цели
        double toVia = Geo.DistanceKm(pos, new GeoPoint(49.9, 39.4));
        Assert.True(toVia < Geo.DistanceKm(t.Head.Pos, new GeoPoint(49.9, 39.4)));
        Assert.InRange(Geo.DistanceKm(t.Head.Pos, pos), 12, 18);
    }

    // ---------------- границы регионов и акватории ----------------

    [Theory]
    [InlineData("По границе Воронежской и Ростовской области в направлении Волгоградской области опасность по БПЛА", new string[0], "border:rostov+voronezh")]
    [InlineData("По границе Курской и Брянской области в направлении Орловской области БПЛА", new string[0], "border:bryansk+kursk")]
    [InlineData("По границе с Курской областью тревога по БПЛА", new[] { "belgorod" }, "border:belgorod+kursk")]
    public void BorderBetweenRegionsBecomesBorderZone(string text, string[] channel, string borderId)
    {
        if (RouteNetwork.BorderBetween("kursk", "belgorod") == null) return; // слой маршрутов без границ
        var p = P(text, channel);
        var b = Assert.Single(p.Places, x => x.BorderId != null);
        Assert.Equal(borderId, b.BorderId);
        Assert.Contains(p.Route.Corridors, c => c.Kind == CorridorKind.Border && c.Name == borderId);
        // регион-направление не становится регионом тревоги
        Assert.DoesNotContain("orel", p.Regions);
    }

    [Fact]
    public void SeaExitTargetsNearestWater()
    {
        if (RouteNetwork.Sea("sea:azov") == null) return;
        var p = P("Геническ и близлежащие | Тревога по БПЛА от Рыково | И далее с возможным выходом в Азовское море | Херсонская область РФ", "kherson");
        var sea = Assert.Single(p.Places, x => x.SeaId != null);
        Assert.True(sea.IsTarget);
        Assert.True(RouteNetwork.IsWater(sea.Pos!.Value));
        // выход в море у Геническа, а не в условный центр моря
        Assert.InRange(Geo.DistanceKm(sea.Pos!.Value, new GeoPoint(46.17, 34.80)), 0, 40);
    }

    [Fact]
    public void SeaExitDoesNotOverrideNamedTargets()
    {
        if (RouteNetwork.Sea("sea:azov") == null) return;
        var p = P("Мелитополь и близлежащие | Тревога по БПЛА | Далее на юго-восток, в направлении | Приморск, Бердянск | Возможно с выходом в Азовское море", "zaporozhye");
        Assert.DoesNotContain(p.Places, x => x.SeaId != null && x.IsTarget);
    }

    [Theory]
    [InlineData("Таганрогский залив | Таганрог и близлежащие | Опасность по БПЛА | Ростовская область", "rostov", "rostov")]
    [InlineData("Приморско-Ахтарск | Ейск | Таганрогский залив | Тревога по БПЛА | Краснодарский край", "krasnodar", "krasnodar")]
    public void GulfIsWaterZoneOfMentionedShore(string text, string channel, string region)
    {
        if (RouteNetwork.Sea("sea:taganrog") == null) return;
        var p = P(text, channel);
        var z = Assert.Single(p.Places, x => x.Label == "Таганрогский залив");
        Assert.Equal(region, z.RegionId);
        Assert.Equal("sea:taganrog", z.SeaId);
        var area = RouteNetwork.SeaArea(z.SeaId!, z.Pos!.Value, 40);
        Assert.True(area.Count >= 3);
        // контур зоны — над водой (с допуском на точки у самого берега)
        Assert.True(area.Count(q => RouteNetwork.IsWater(q)) >= area.Count * 0.8);
    }

    [Theory]
    [InlineData("Стрелковое | Арбатская Стрелка | Тревога по БПЛА через залив Сиваш", "Залив Сиваш", "sea:sivash")]
    [InlineData("Джанкой и близлежащие | Тревога по БПЛА | от Сиваши | Республика Крым", "Залив Сиваш", "sea:sivash")]
    [InlineData("Залив Красноперекопск, Ишунь и далее на юго-восток примерно на Джанкой тревога по БПЛА", "Залив Красноперекопск", "sea:sivash")]
    [InlineData("Керченский полуостров, Керченский пролив, Таманский полуостров | Тревога по БПЛА", "Керченский пролив", null)]
    public void CrimeanWatersAreWaterZones(string text, string label, string? seaId)
    {
        if (RouteNetwork.Sea("sea:azov") == null) return;
        var p = P(text, "crimea");
        var z = Assert.Single(p.Places, x => x.Label == label);
        Assert.NotNull(z.SeaId);
        if (seaId != null && RouteNetwork.Sea(seaId) != null) Assert.Equal(seaId, z.SeaId);
        Assert.True(RouteNetwork.SeaArea(z.SeaId!, z.Pos!.Value, z.ZoneRadiusKm!.Value).Count >= 3);
    }

    [Theory]
    [InlineData("Залив Красноперекопск, Ишунь и далее на юго-восток примерно на Джанкой тревога по БПЛА")]
    [InlineData("Армянск с запада. Перекопский залив, Красноперекопск, залив Красноперекопск | Тревога по БПЛА")]
    public void GulfKeepsShoreTownAlerted(string text)
    {
        // город на берегу залива остаётся под тревогой вместе с зоной над водой: пропуск сигнала опаснее лишнего
        var p = P(text, "crimea");
        Assert.Contains(p.Places, x => x.Label == "Красноперекопск");
        Assert.Contains(p.Places, x => x.Label == "Залив Красноперекопск" && x.SeaId != null);
    }

    [Fact]
    public void SeaToRegionKeepsRegionAlerted()
    {
        // «над Азовским морем в направлении Краснодарского края» — тревога у Краснодарского края, не у другого берега
        var p = P("Большое количество БПЛА над Азовским морем в направлении Краснодарского края");
        Assert.Equal(["krasnodar"], p.Regions);
        // «от Запорожской области в акваторию Азовского моря» — регион не теряется
        Assert.NotEmpty(P("Возможно выходы БПЛА от Запорожской области РФ в акваторию Азовского моря.").Regions);
    }

    [Fact]
    public void SivashVillageIsNotTheGulf()
    {
        // «Чонгар, Сиваш, Медведевка» — село Сиваш (Херсонская обл.); «Сасык-Сиваш» — озеро у Евпатории
        Assert.DoesNotContain(P("Чонгар, Сиваш, Медведевка и близлежащие | Херсонская область РФ", "crimea").Places, x => x.Label == "Залив Сиваш");
        var lake = P("Сасык Сиваш и побережье, Сакская ТЭС, Прибрежное Саки тревога по БПЛА", "crimea");
        Assert.DoesNotContain(lake.Places, x => x.Label is "Залив Сиваш" or "Сиваш");
        Assert.DoesNotContain("kherson", lake.Regions);
    }

    [Theory]
    [InlineData("Бакальская коса | Славное | Раздольненский р-н | Опасность по БПЛА", "crimea", "Бакальская коса")]
    [InlineData("Херсонская область РФ от Каховки до Кинбурнская коса тревога по БПЛА", "kherson", "Кинбурнская коса")]
    [InlineData("Базы отдыха Светлый берёг, Дружба, Азовец | Тревога по БПЛА от Обиточная коса | Запорожская область РФ", "kherson", "Обиточная коса")]
    [InlineData("Ялта ДНР | фиксация БПЛА | в сторону Белосарайской косы", "dnr", "Белосарайская коса")]
    [InlineData("Стрелковое | Арбатская Стрелка | Тревога по БПЛА", "crimea", "Арабатская стрелка")]
    public void SpitsAreNamedZones(string text, string channel, string label)
    {
        var z = Assert.Single(P(text, channel).Places, x => x.Label == label);
        Assert.True(z.IsArea);
        Assert.NotNull(z.ZoneRadiusKm);
    }

    [Theory]
    [InlineData("Арабатская стрелка", 45.85, 35.05, 30, 40)]
    [InlineData("Бакальская коса", 45.75, 33.19, 6, 4)]
    [InlineData("Кинбурнская коса", 46.52, 31.75, 20, 15)]
    public void SpitZoneFollowsCoastline(string label, double lat, double lon, double r, double minKm)
    {
        if (RouteNetwork.Lines.Count == 0) return;
        var c = new GeoPoint(lat, lon);
        var corr = AlertEngine.CoastCorridor(new ParsedPlace { Label = label, Pos = c, IsArea = true, ZoneRadiusKm = r });
        Assert.NotNull(corr);
        Assert.InRange(Geo.PathLength(corr!), minKm, 400);
        Assert.All(corr!, q => Assert.True(Geo.DistanceKm(q, c) <= r * 1.3 + 0.5));
    }

    [Fact]
    public void SivashZoneCoversWholeGulf()
    {
        if (RouteNetwork.Sea("sea:sivash") == null) return;
        var p = P("Стрелковое | Арбатская Стрелка | Тревога по БПЛА через залив Сиваш", "crimea");
        var z = Assert.Single(p.Places, x => x.Label == "Залив Сиваш");
        var area = RouteNetwork.SeaArea(z.SeaId!, z.Pos!.Value, z.ZoneRadiusKm!.Value);
        // от Чонгара на севере до Советского района на юге
        Assert.Contains(area, q => q.Lat > 45.95);
        Assert.Contains(area, q => q.Lat < 45.45);
    }

    [Fact]
    public void BorderCorridorRouteFollowsBorderLine()
    {
        if (RouteNetwork.BorderBetween("kursk", "bryansk") is not { } line) return;
        var part = line.Parts.MaxBy(x => x.Length)!;
        var a = part[2];
        var b = part[^3];
        var t = NewTrack(ThreatType.Uav, (a.Lat, a.Lon, 0), (b.Lat, b.Lon, 30));
        t.Corridors.Add(new CorridorHint(CorridorKind.Border, "border:bryansk+kursk", "по границе"));
        RoutePlanner.Plan(t);
        Assert.Equal("вдоль границы регионов", t.CorridorLabel);
        Assert.True(t.Legs[0].Count > 2);
    }

    [Theory]
    [InlineData("А-144", "Р-298")]
    [InlineData("А-141", "Р-120")]
    [InlineData("А-280", "Р-280")]
    [InlineData("А-291", "А-291")]
    public void OldRoadRefsResolveToCurrentRoad(string asWritten, string expected)
    {
        if (RouteNetwork.Lines.All(l => l.Id != "Р-280")) return; // слой маршрутов не собран
        var found = RouteNetwork.Find(new CorridorHint(CorridorKind.Road, asWritten, asWritten)).Select(l => l.Id).ToList();
        Assert.Contains(expected, found);
        // номера сравниваются точно: «А-29» не должно цеплять «А-291»/«А-290»
        Assert.DoesNotContain("А-290", found);
    }

    [Fact]
    public void LegsFollowRoadWhenAvailable()
    {
        if (RouteNetwork.Lines.All(l => l.Id != "Р-280")) return; // слой маршрутов не собран
        // Мелитополь → Бердянск вдоль Р-280: путь вдоль трассы длиннее прямой, но не петляет
        var t = NewTrack(ThreatType.Uav, (46.85, 35.37, 0), (46.76, 36.79, 40));
        t.Corridors.Add(new CorridorHint(CorridorKind.Road, "Р-280", "Р-280"));
        RoutePlanner.Plan(t);
        Assert.Single(t.Legs);
        Assert.True(t.Legs[0].Count > 2, "путь не пошёл вдоль трассы");
        double direct = Geo.DistanceKm(t.Points[0].Pos, t.Points[1].Pos);
        Assert.InRange(Geo.PathLength(t.Legs[0]), direct, direct * 1.6 + 6);
        Assert.Equal("вдоль трассы Р-280", t.CorridorLabel);
    }

    // ---------------- дороги между пунктами ----------------

    [Theory]
    [InlineData("Курская область | Опасность по БПЛА по трассе Рыльск - Хомутовка", "Рыльск", "Хомутовка")]
    [InlineData("Курская область | ФПВ на трассе Рыльск — Крупец", "Рыльск", "Крупец")]
    [InlineData("Запорожская область РФ | Дорога Токмак - Бердянск опасность по БПЛА", "Токмак", "Бердянск")]
    [InlineData("Белгородская область | По трассе Шебекино - Маслова Пристань тревога по БПЛА", "Шебекино", "Маслова Пристань")]
    public void RoadSegmentBetweenPlaces(string text, string from, string to)
    {
        var p = P(text);
        Assert.Contains(p.Route.Segments, s => s.FromLabel.StartsWith(from[..4]) && s.ToLabel.StartsWith(to[..4]));
    }

    [Fact]
    public void RoadTowardPlace()
    {
        var p = P("Херсонская область РФ | Каланчак | БПЛА по дороге на Геройское", "kherson");
        Assert.Contains(p.Route.Segments, s => s.ToLabel.StartsWith("Геройск"));
    }

    [Fact]
    public void RoadSegmentRoutedOverGraph()
    {
        if (!RoadGraph.Available) return; // граф дорог не собран
        // Рыльск → Хомутовка: по дорогам, длиннее прямой, но без огромного крюка
        var a = new GeoPoint(51.57, 34.68);
        var b = new GeoPoint(51.92, 34.56);
        var r = RoadGraph.Route(a, b);
        Assert.NotNull(r);
        double direct = Geo.DistanceKm(a, b);
        Assert.InRange(Geo.PathLength(r!), direct, direct * 1.8 + 6);
        Assert.True(r!.Count > 4);
    }

    [Theory]
    // расширенное покрытие: Ростовская, Воронежская, Краснодарский край, тыл
    [InlineData(47.23, 39.72, 47.21, 38.94)]   // Ростов-на-Дону → Таганрог
    [InlineData(51.67, 39.20, 50.20, 39.58)]   // Воронеж → Россошь
    [InlineData(45.04, 38.98, 44.72, 37.77)]   // Краснодар → Новороссийск
    [InlineData(54.19, 37.62, 54.63, 39.74)]   // Тула → Рязань (тыл, основная сеть)
    [InlineData(48.70, 44.50, 51.53, 46.03)]   // Волгоград → Саратов
    public void RoadGraphCoversRegions(double la, double lo, double lb, double lob)
    {
        if (!RoadGraph.Available) return;
        var a = new GeoPoint(la, lo);
        var b = new GeoPoint(lb, lob);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = RoadGraph.Route(a, b);
        sw.Stop();
        Assert.NotNull(r);
        Assert.InRange(Geo.PathLength(r!), Geo.DistanceKm(a, b), Geo.DistanceKm(a, b) * 1.8 + 6);
        Assert.True(sw.ElapsedMilliseconds < 500, $"поиск пути {sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void PlaceChainWithTrassaMention()
    {
        var p = P("Рыльск — Марьино, Ивановское — Льгов — Курчатов, Курская область - опасность по БПЛА. Особое внимание трасса.");
        Assert.Contains(p.Route.Segments, s => s.FromLabel.StartsWith("Иванов") && s.ToLabel.StartsWith("Льгов"));
        Assert.Contains(p.Route.Segments, s => s.FromLabel.StartsWith("Льгов") && s.ToLabel.StartsWith("Курчатов"));
    }

    [Fact]
    public void AzovSeaIsNotLaunchArea()
    {
        // Азовское море — тыл: «пуск» с него продлевается по прямой за линию соприкосновения
        var t = NewTrack(ThreatType.Uav, (45.42, 37.75, 0), (44.91, 37.56, 10), (44.77, 38.00, 60));
        t.Launch = new(46.3, 36.6);
        t.LaunchLabel = "Азовское море";
        t.LaunchExplicit = true;
        RoutePlanner.Plan(t);
        if (t.Launch is { } l)
        {
            Assert.False(RoutePlanner.IsAzov(l, t.LaunchLabel));
            Assert.True(l.Lat > 46.4);
        }
    }
}
