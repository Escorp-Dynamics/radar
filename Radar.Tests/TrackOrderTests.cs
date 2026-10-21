using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Траектории идут от линии пуска вглубь: без разворотов, петель и «возврата» к границе.</summary>
public class TrackOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Sudzha = new(51.19, 35.27), Kursk = new(51.73, 36.19), Kurchatov = new(51.66, 35.65),
        Rylsk = new(51.57, 34.68), Fatezh = new(52.09, 35.86);

    private static Observation Obs(GeoPoint p, string label, int minutes, string ch = "test") =>
        new(new AlertEvent { Key = label + minutes, Threat = ThreatType.Uav, Status = AlertStatus.Alert, RegionIds = ["kursk"], RegionNames = "Курская", Signature = label }, ThreatType.Uav, ThreatKind.UavGeneric, p, label,
            null, null, null, 1, T0.AddMinutes(minutes), ch, true);

    private static Observation ObsK(GeoPoint p, string label, int minutes, ThreatKind kind, string ch = "test") =>
        Obs(p, label, minutes, ch) with { Kind = kind };

    private static readonly GeoPoint Orel = new(52.97, 36.07), Shchigry = new(51.88, 36.91);

    [Theory]
    // цель «на Орёл» названа у Курска; объект ушёл на восток-северо-восток (Щигры), Орёл — под ~95° к пути.
    // Свежая цель (20 мин) остаётся, старая (61 мин) уступает пройденному пути
    [InlineData(20, true)]
    [InlineData(61, false)]
    public void StaleTargetAgainstPathIsDropped(int minutes, bool kept)
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Kursk, "Курск", 0) with { Target = Orel, TargetLabel = "Орёл" })!;
        Assert.NotNull(t.Target);
        b.Add(Obs(Shchigry, "Щигры", minutes));
        Assert.Single(b.Tracks);
        Assert.Equal(kept, t.Target != null);
    }

    [Theory]
    // «на юг-юго-восток» сказано у Рыльска; объект ушёл на восток (Льгов → Курчатов), тот же курс от Курчатова
    // ведёт к линии. Через 25 мин слова ещё свежие, через час — устарели
    [InlineData(25, true)]
    [InlineData(61, false)]
    public void StaleHeadingTowardsLineIsDropped(int minutes, bool kept)
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Rylsk, "Рыльск", 0) with { Heading = 160 })!;
        b.Add(Obs(new GeoPoint(51.66, 35.27), "Льгов", 10));
        Assert.Equal(160, t.HeadingHint);
        b.Add(Obs(Kurchatov, "Курчатов", minutes));
        Assert.Single(b.Tracks);
        Assert.Equal(kept, t.HeadingHint != null);
    }

    [Fact]
    public void ObjectAgainstStatedCourseIsSeparate()
    {
        // Кировский район (Калужская) → через 38 мин Людиновский район (южнее), но в сообщении «в вашем направлении
        // от Брянской области» — курс на северо-восток: отметка — другой объект, идущий навстречу, а не разворот
        GeoPoint kirov = new(54.13, 34.39), lyudinovo = new(53.92, 34.50);
        var b = new TrackBuilder();
        var t = b.Add(Obs(kirov, "Кировский район", 0))!;
        var n = b.Add(Obs(lyudinovo, "Людиновский район", 38) with { Heading = 32 })!;
        Assert.NotSame(t, n);
        // без курса в сообщении и вглубь (от линии) — то же сближение остаётся одним треком
        var b2 = new TrackBuilder();
        var t2 = b2.Add(Obs(lyudinovo, "Людиновский район", 0))!;
        Assert.Same(t2, b2.Add(Obs(kirov, "Кировский район", 38)));
    }

    [Fact]
    public void ScatteredSummaryOnlyContinuesTracks()
    {
        // «Кимовский ⏎ Воловский ⏎ Щекинский ⏎ Заокский … фиксации» — разнесённые районы, несколько групп
        GeoPoint kimovsk = new(53.97, 38.53), volovo = new(53.56, 38.00), shchekino = new(54.00, 37.52), zaoksky = new(54.73, 37.40);
        var pts = new[] { kimovsk, volovo, shchekino, zaoksky }
            .Select(p => new MapPoint(p, "р-н", false, 20)).ToList();
        Assert.NotNull(TrackBuilder.ScatteredGroups(pts));
        // компактная группа пунктов — один объект
        GeoPoint bogoroditsk = new(53.77, 38.13), uzlovaya = new(53.98, 38.16), donskoy = new(53.97, 38.33);
        Assert.Null(TrackBuilder.ScatteredGroups(new[] { bogoroditsk, uzlovaya, donskoy }.Select(p => new MapPoint(p, "x", false, 5)).ToList()));

        // отметка сводки трека не начинает
        var b = new TrackBuilder();
        Assert.Null(b.Add(Obs(shchekino, "Щёкинский район", 0) with { ContinueOnly = true }));
        Assert.Empty(b.Tracks);
        // но продолжает трек, если лежит по его курсу
        GeoPoint plavsk = new(53.71, 37.29);
        var t = b.Add(Obs(new(53.43, 37.05), "Чернский район", 0))!;
        b.Add(Obs(plavsk, "Плавск", 15));
        Assert.Same(t, b.Add(Obs(shchekino, "Щёкинский район", 30) with { ContinueOnly = true }));
        // и не продолжает далеко от ожидаемого положения или рядом, но поперёк курса (на юго-восток при курсе на северо-восток)
        Assert.Null(b.Add(Obs(volovo, "Воловский район", 45) with { ContinueOnly = true }));
        Assert.Null(b.Add(Obs(new(53.87, 37.75), "Тепло-Огарёвский район", 45) with { ContinueOnly = true }));
    }

    [Fact]
    public void ShortTrackDoesNotTurnTowardsLine()
    {
        FrontLineSource.LoadForAudit();
        // «Калуга … сбитие» → через 29 мин «Бабынинский район … фиксация от 3 БПЛА» (к линии): курса у трека из одной
        // отметки ещё нет, центр соседнего района ближе к линии — это другая группа, а не путь назад
        GeoPoint kaluga = new(54.51, 36.26), babynino = new(54.41, 35.80);
        var b = new TrackBuilder();
        var t = b.Add(Obs(kaluga, "Калуга", 0))!;
        Assert.NotSame(t, b.Add(Obs(babynino, "Бабынинский район", 29)));
        // у линии курс из сообщения в ту же сторону — отметка того же объекта (Каланчак → «Скадовский район, на запад»)
        GeoPoint kalanchak = new(46.25, 33.29), skadovskiy = new(46.30, 32.58);
        var b2 = new TrackBuilder();
        var t2 = b2.Add(Obs(kalanchak, "Каланчак", 0))!;
        Assert.NotSame(t2, b2.Add(Obs(skadovskiy, "Скадовский район", 30)));
        var b3 = new TrackBuilder();
        var t3 = b3.Add(Obs(kalanchak, "Каланчак", 0))!;
        Assert.Same(t3, b3.Add(Obs(skadovskiy, "Скадовский район", 30) with { Heading = 280 }));
        // цель поперёк смещения не оправдывает шаг к линии: «Солдатское, в сторону Воронежа» после «Лиски … опасность» —
        // группа летит на север, а не пришла с востока
        GeoPoint liski = new(50.98, 39.50), soldatskoe = new(50.91, 39.05), voronezh = new(51.66, 39.20);
        var b4 = new TrackBuilder();
        var t4 = b4.Add(Obs(liski, "Лиски", 0))!;
        Assert.NotSame(t4, b4.Add(Obs(soldatskoe, "Солдатское", 29) with { Target = voronezh, TargetLabel = "Воронеж" }));
    }

    [Fact]
    public void ObjectAgainstStatedTargetIsSeparate()
    {
        // «Рамасуха … в сторону Погара» (на юго-запад), затем «от Вадьковки в направлении Трубчевского района» (на восток):
        // отметка у Вадьковки — группа, летящая в другую сторону, а не продолжение пути к границе
        GeoPoint ramasukha = new(52.74, 33.54), vadkovka = new(52.56, 33.32), trubchevsk = new(52.58, 33.77);
        var b = new TrackBuilder();
        var t = b.Add(Obs(ramasukha, "Рамасуха", 0))!;
        var n = b.Add(Obs(vadkovka, "Вадьковка", 9) with { Target = trubchevsk, TargetLabel = "Трубчевский район" })!;
        Assert.NotSame(t, n);
        Assert.Single(t.Points);
    }

    [Fact]
    public void BranchAgainstStatedTargetDoesNotInheritPath()
    {
        // трек «Строитель → севернее Белгорода», затем «от Бессоновки в сторону Белгорода» — группа юго-западнее, летящая
        // на северо-восток: унаследованный путь «Строитель → Бессоновка» вёл бы её против курса, к границе
        GeoPoint stroitel = new(50.79, 36.49), north = new(50.66, 36.45), belgorod = new(50.595, 36.587), bessonovka = new(50.53, 36.30);
        var b = new TrackBuilder();
        var parent = b.Add(Obs(stroitel, "Строитель", 0))!;
        b.Add(Obs(north, "Северный", 20));
        Assert.Equal(2, parent.Points.Count);
        var n = b.Add(Obs(bessonovka, "Бессоновка", 30) with { Target = belgorod, TargetLabel = "Белгород", Channel = "other" })!;
        Assert.NotSame(parent, n);
        Assert.DoesNotContain(n.Points, p => p.Label == "Строитель");
    }

    [Fact]
    public void StandingWarningDoesNotExtendTrack()
    {
        // «Макеевка … опасность» → «Енакиево … опасность сохраняется» → «Горловка опасность сохраняется»: подтверждения
        // действующей опасности по площади, а не засечки — к линии у Горловки трек не тянется
        GeoPoint makeevka = new(48.03, 38.13), enakievo = new(48.22, 38.07), gorlovka = new(48.30, 38.05);
        var b = new TrackBuilder();
        var t = b.Add(Obs(makeevka, "Макеевка", 0))!;
        b.Add(Obs(enakievo, "Енакиево", 11) with { Standing = true });
        b.Add(Obs(gorlovka, "Горловка", 13) with { Standing = true });
        Assert.All(b.Tracks, x => Assert.Single(x.Points));
        // в том же месте подтверждение остаётся тем же треком
        Assert.Same(t, b.Add(Obs(new GeoPoint(48.04, 38.14), "Макеевка — центр", 20) with { Standing = true }));
    }

    [Fact]
    public void FpvDoesNotInheritLongRangePath()
    {
        // дальний БПЛА «Яковлевский район → Белгород», затем «Репное, Шагаровка … опасность по ФПВ» — ФПВ запускается
        // у линии, путь дальнего БПЛА он не продолжает и не наследует
        GeoPoint yakovlevskiy = new(50.80, 36.38), belgorod = new(50.59, 36.59), repnoe = new(50.55, 36.71);
        var b = new TrackBuilder();
        var t = b.Add(Obs(yakovlevskiy, "Яковлевский район", 0))!;
        b.Add(Obs(belgorod, "Белгород", 55));
        Assert.Equal(2, t.Points.Count);
        var n = b.Add(ObsK(repnoe, "Репное", 61, ThreatKind.UavFpv, "other"))!;
        Assert.Equal(2, t.Points.Count);
        Assert.DoesNotContain(n.Points, p => p.Label == "Яковлевский район");
    }

    [Fact]
    public void TwoPointTrackTowardsLineHasUncertainHeading()
    {
        FrontLineSource.LoadForAudit();
        // «Елец … опасность» → «Долгоруковский район» (на юг, к линии): центры соседних районов, не разворот
        var t = new Track { Id = 1, Threat = ThreatType.Uav };
        t.Points.Add(new TrackPoint(new(52.61, 38.34), T0, "Елец", "a"));
        t.Points.Add(new TrackPoint(new(52.34, 38.37), T0.AddMinutes(37), "Долгоруковский район", "a"));
        Assert.True(t.UncertainHeading);
        // курс словами или цель — направление известно
        t.HeadingHint = 180;
        Assert.False(t.UncertainHeading);
        t.HeadingHint = null;
        // вглубь — курс по отметкам показываем
        var deeper = new Track { Id = 2, Threat = ThreatType.Uav };
        deeper.Points.Add(new TrackPoint(new(52.34, 38.37), T0, "Долгоруковский район", "a"));
        deeper.Points.Add(new TrackPoint(new(52.61, 38.34), T0.AddMinutes(37), "Елец", "a"));
        Assert.False(deeper.UncertainHeading);
        // к крупному городу у линии (Старобешево → Донецк) — вероятная цель, курс показываем
        var city = new Track { Id = 3, Threat = ThreatType.Uav };
        city.Points.Add(new TrackPoint(new(47.75, 38.03), T0, "Старобешево", "a"));
        city.Points.Add(new TrackPoint(new(48.00, 37.80), T0.AddMinutes(40), "Донецк", "a"));
        Assert.False(city.UncertainHeading);
        // три отметки — уже траектория
        t.Points.Add(new TrackPoint(new(52.20, 38.40), T0.AddMinutes(50), "Тербуны", "a"));
        Assert.False(t.UncertainHeading);
    }

    [Fact]
    public void FpvTowardsLineIsSeparate()
    {
        // «Долгое фпв ⏎ Валуйский МО» (между Долгим и центром района) → «Валуйский МО» → «Борки фпв»: каждая отметка
        // ближе к границе — ФПВ запускаются с линии и летят от неё, это разные аппараты, а не один «к Украине»
        GeoPoint dolgoe = new(50.23, 38.28), valuyskiy = new(50.19, 38.05), borki = new(50.15, 37.89);
        var b = new TrackBuilder();
        b.Add(ObsK(dolgoe, "Валуйский район — Долгое", 0, ThreatKind.UavFpv));
        b.Add(ObsK(valuyskiy, "Валуйский район", 58, ThreatKind.UavFpv, "other"));
        b.Add(ObsK(borki, "Борки", 60, ThreatKind.UavFpv, "third"));
        foreach (var x in b.Tracks)
            for (int k = 1; k < x.Points.Count; k++)
                Assert.True(Borderline.BackKm(x.Points[k - 1].Pos, x.Points[k].Pos, x.Points[0].Pos) <= 5);
    }

    [Fact]
    public void RepeatedTargetIsFresh()
    {
        // та же цель повторена в новом сообщении — слова свежие, цель остаётся
        var b = new TrackBuilder();
        var t = b.Add(Obs(Kursk, "Курск", 0) with { Target = Orel, TargetLabel = "Орёл" })!;
        b.Add(Obs(Shchigry, "Щигры", 61) with { Target = Orel, TargetLabel = "Орёл" });
        Assert.NotNull(t.Target);
    }

    [Fact]
    public void SpeedConfidenceGrowsWithConsistentSamples()
    {
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, SpeedKmh = 150, SpeedSamples = 1 };
        t.SpeedLog.Add((150, true));
        Assert.Equal(0, t.SpeedConfidence);
        t.SpeedLog.Clear();
        foreach (var v in new[] { 145.0, 150, 155 }) t.SpeedLog.Add((v, false));
        t.SpeedSamples = 3;
        Assert.Equal(2, t.SpeedConfidence);
        Assert.True(t.SpeedSpread < 10);
    }

    [Fact]
    public void UncertainEtaIsShownAsRange()
    {
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, SpeedKmh = 120, SpeedSamples = 1, Target = new GeoPoint(51.73, 36.19) };
        t.SpeedLog.Add((120, true));
        t.Points.Add(new TrackPoint(new GeoPoint(51.73, 34.70), T0, "A", "x"));
        var text = t.EtaText(T0);
        // ~75 км на ~120 км/ч ±25 % — «40 мин – 1 ч 10 мин»
        Assert.Contains("–", text);
        Assert.DoesNotContain("~", text);
        // надёжная скорость — одно значение
        t.SpeedLog.Clear();
        foreach (var v in new[] { 118.0, 120, 122 }) t.SpeedLog.Add((v, false));
        t.SpeedSamples = 3;
        Assert.StartsWith("~", t.EtaText(T0));
    }

    [Fact]
    public void NewObjectNearFrontIsNotGluedToRearTrack()
    {
        // «Хорнет» над Донецком, через 2,5 мин — над Новогродовкой (34 км к линии фронта): это другой объект с той стороны
        GeoPoint donetsk = new(48.03, 37.76), novogrodovka = new(48.16, 37.35);
        var b = new TrackBuilder();
        var t = b.Add(ObsK(donetsk, "Донецк", 0, ThreatKind.UavHornet))!;
        var n = b.Add(ObsK(novogrodovka, "Новогродовка", 2, ThreatKind.UavHornet))!;
        Assert.NotSame(t, n);
        Assert.Single(t.Points);
    }

    [Fact]
    public void SameChannelListingIsSeparateGroups()
    {
        // vrv_radar в одну минуту: «Ефремовский район … Фиксация» и «Каменский район … Фиксация» — две группы, а не перелёт на запад
        GeoPoint efremov = new(53.19, 38.22), kamenskiy = new(53.25, 37.75);
        var b = new TrackBuilder();
        var t = b.Add(Obs(efremov, "Ефремовский район", 0, "vrv_radar"))!;
        var n = b.Add(Obs(kamenskiy, "Каменский район", 0, "vrv_radar"))!;
        Assert.NotSame(t, n);
        Assert.Single(t.Points);
    }

    [Fact]
    public void CrimeaToMainlandNorthIsReturnToLine()
    {
        // Красноперекопск → Каланчак: из Крыма на север, к Днепру — это не облёт полуострова, а возврат к линии
        Assert.True(Borderline.BackKm(new GeoPoint(45.95, 33.79), new GeoPoint(46.255, 33.29), null) > 30);
        // внутри Крыма правило морской зоны прежнее (без точки входа — 0)
        Assert.Equal(0, Borderline.BackKm(new GeoPoint(45.95, 33.79), new GeoPoint(45.12, 34.02), null));
    }

    [Fact]
    public void FpvDoesNotFlyAcrossRegion()
    {
        // FPV у Погара и затем «БПЛА» у Брянска (107 км) — разные объекты, а не «FPV на 150 км/ч»
        GeoPoint pogar = new(52.55, 33.26), starodub = new(52.58, 32.76), bryansk = new(53.24, 34.36);
        var b = new TrackBuilder();
        var fpv = b.Add(ObsK(pogar, "Погар", 0, ThreatKind.UavFpv))!;
        b.Add(ObsK(starodub, "Стародуб", 13, ThreatKind.UavFpv));
        b.Add(ObsK(bryansk, "Брянск", 42, ThreatKind.UavGeneric));
        Assert.Single(fpv.Points);
        Assert.False(fpv.HasMeasuredSpeed);
    }

    [Fact]
    public void LongTrackIsNotRelabeledFpv()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20));
        b.Add(ObsK(Kursk, "Курск", 40, ThreatKind.UavFpv));
        Assert.NotEqual(ThreatKind.UavFpv, t.Kind);
    }

    [Fact]
    public void PointBackTowardBorderStartsNewTrack()
    {
        var b = new TrackBuilder();
        var t1 = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20));
        b.Add(Obs(Kursk, "Курск", 40));
        var t2 = b.Add(Obs(Rylsk, "Рыльск", 50))!;
        Assert.NotSame(t1, t2);
        Assert.Equal(["Суджа", "Курчатов", "Курск"], t1.Points.Select(p => p.Label));
    }

    [Fact]
    public void LateMessageDoesNotCreateZigzag()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20));
        b.Add(Obs(Fatezh, "Фатеж", 45));
        // запоздалое сообщение о точке, которая на пути не лежит (разворот на 180°)
        b.Add(Obs(Rylsk, "Рыльск", 30));
        Assert.DoesNotContain(t.Points, p => p.Label == "Рыльск");
        Assert.Equal("Фатеж", t.Head.Label);
    }

    [Fact]
    public void StraightenRemovesSpike()
    {
        var pts = new List<TrackPoint>
        {
            new(Sudzha, T0, "Суджа", "c"),
            new(Fatezh, T0.AddMinutes(20), "Фатеж", "c"),
            new(Kurchatov, T0.AddMinutes(30), "Курчатов", "c"),
            new(Kursk, T0.AddMinutes(50), "Курск", "c"),
        };
        TrackBuilder.Straighten(pts, ThreatType.Uav);
        Assert.Equal("Курск", pts[^1].Label);
        for (int i = 1; i + 1 < pts.Count; i++)
            Assert.True(Geo.AngleDiff(Geo.Bearing(pts[i - 1].Pos, pts[i].Pos), Geo.Bearing(pts[i].Pos, pts[i + 1].Pos)) <= 110);
    }

    [Fact]
    public void LaunchIsOnBorderBehindFirstPoint()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Kurchatov, "Курчатов", 0))!;
        b.Add(Obs(Kursk, "Курск", 20));
        Assert.NotNull(t.Launch);
        var l = t.Launch!.Value;
        // пуск позади первой точки и у границы, не впереди по курсу
        Assert.True(Borderline.DistanceKm(l) < 15);
        Assert.True(Geo.AngleDiff(Geo.Bearing(Kurchatov, l), (Geo.Bearing(Kurchatov, Kursk) + 180) % 360) <= 60);
        Assert.True(Borderline.DistanceKm(Kurchatov) > Borderline.DistanceKm(l));
    }

    [Fact]
    public void LoopingPointIsNotAppended()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20));
        b.Add(Obs(Kursk, "Курск", 40));
        // продолжение вглубь — не петля; возврат почти к началу пути (не в пройденную точку) — петля
        Assert.False(TrackBuilder.WouldLoop(t, Fatezh));
        Assert.True(TrackBuilder.WouldLoop(t, new GeoPoint(51.33, 35.20)));
        // возврат в пройденную точку — не петля (ветка отрезается при добавлении)
        Assert.False(TrackBuilder.WouldLoop(t, Kurchatov));
    }

    [Fact]
    public void LaunchCourseIgnoresNearbyJitter()
    {
        // вторая отметка в 6 км почти «назад» (уточнение позиции) не должна разворачивать место пуска
        GeoPoint near = new(51.70, 35.62);
        var b = new TrackBuilder();
        var t = b.Add(Obs(Kurchatov, "Курчатов", 0))!;
        b.Add(Obs(near, "Курчатов, окраина", 5));
        b.Add(Obs(Kursk, "Курск", 25));
        if (t.Launch is { } l)
            Assert.True(Geo.AngleDiff(Geo.Bearing(t.Points[0].Pos, l), (Geo.Bearing(t.Points[0].Pos, Kursk) + 180) % 360) <= 90);
    }

    [Fact]
    public void LaunchNotAheadOfOverallPath()
    {
        // начало пути уходит на запад (Чубковичи → Клинцы), но в целом объект летит на восток к Орлу:
        // место пуска не может оказаться впереди по общему пути
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric };
        t.Points.Add(new(new(52.53, 32.60), T0, "Чубковичи", "c"));
        t.Points.Add(new(new(52.76, 32.24), T0.AddMinutes(14), "Клинцы", "c"));
        t.Points.Add(new(new(53.06, 32.85), T0.AddMinutes(29), "Мглин", "c"));
        t.Points.Add(new(new(53.25, 34.37), T0.AddMinutes(80), "Брянск", "c"));
        t.Points.Add(new(new(52.97, 36.06), T0.AddMinutes(115), "Орёл", "c"));
        RoutePlanner.Plan(t);
        if (t.Launch is { } l)
        {
            var first = t.Points[0].Pos;
            Assert.True(Geo.AngleDiff(Geo.Bearing(first, l), (Geo.Bearing(first, t.Head.Pos) + 180) % 360) <= 90);
        }
    }

    [Fact]
    public void CoastalForecastDoesNotGoToSea()
    {
        // Майкоп → Апшеронский район → Туапсе: курс на юго-запад ведёт в Чёрное море — прогноза в море быть не должно
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric };
        t.Points.Add(new(new(44.61, 40.10), T0, "Майкоп", "c"));
        t.Points.Add(new(new(44.47, 39.73), T0.AddMinutes(6), "Апшеронский район", "c"));
        t.Points.Add(new(new(44.098, 39.074), T0.AddMinutes(17), "Туапсе", "c"));
        RoutePlanner.Plan(t);
        Assert.DoesNotContain(t.FutureRoute, p => RouteNetwork.IsWater(p) && Geo.DistanceKm(p, t.Head.Pos) > 3);
        Assert.True(Geo.DistanceKm(t.EstimatedPosition(T0.AddMinutes(40)), t.Head.Pos) < 1);
    }

    [Theory]
    [InlineData("Ейский район тревога по БПЛА действует с моря и далее в направлении Ленинградская, Кущевская, Тимашевск, Краснодар", "leningrad")]
    [InlineData("От Приморско-Ахтарск, Ейск, Краснодарский край - внимание по возможным пролётам БПЛА в направлении Полтавская, Славянск-на-Кубани.", "ua_poltava")]
    [InlineData("Запорожская\nКучугуры\nИльич\nКраснодарский край\nОпасность по БПЛА", "zaporozhye")]
    [InlineData("Николаевская, Ростовская область, от 5 БПЛА на Волгодонск", "ua_mykolaiv")]
    [InlineData("Будённовский район, Ставропольский край - прошли БПЛА в направлении Курская, далее на Моздок", "kursk")]
    [InlineData("Рязанская, Белореченский район, Краснодарский край - фиксации БПЛА в направлении Майкоп", "ryazan")]
    public void KubanStanitsaIsNotRegion(string text, string notRegion)
    {
        var m = MessageParser.Parse(new RawMessage("LPRalarm", 1, T0, text), []);
        Assert.DoesNotContain(notRegion, m.Regions);
        Assert.DoesNotContain(m.Places, p => p.RegionId == notRegion);
    }

    [Theory]
    [InlineData("Краснодарский край\nЛенинградская область\nОпасность по БПЛА", "leningrad")]
    [InlineData("Курская / Орловская / Белгородская / Воронежская / Брянская / Ростовская области, Краснодарский край - меры безопасности!", "voronezh")]
    [InlineData("По авиационным:\nЛДНР сейчас будет отбой\nХерсонская, Запорожская отбой режима Внимание", "zaporozhye")]
    public void RegionNameStaysRegion(string text, string region)
    {
        Assert.Contains(region, MessageParser.Parse(new RawMessage("LPRalarm", 1, T0, text), []).Regions);
    }

    [Fact]
    public void AreaTargetForecastGoesToNearestEdge()
    {
        // Валуйки → на восток, «в направлении Воронежской области»: центр области на северо-востоке позади курса —
        // прогноз к ближайшему краю области впереди. Условный контур: область — севернее 50.3° или восточнее 38.5°
        var saved = RoutePlanner.RegionAt;
        try
        {
            RoutePlanner.RegionAt = p => p.Lat > 50.3 || p.Lon > 38.5 ? "voronezh" : "belgorod";
            var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, Target = new GeoPoint(50.6, 38.2), TargetLabel = "Воронежская область", TargetIsArea = true };
            t.Points.Add(new(new(50.25, 37.70), T0, "Уразово", "c"));
            t.Points.Add(new(new(50.20, 38.10), T0.AddMinutes(15), "Валуйки", "c"));
            t.Target = new GeoPoint(51.0, 37.9);
            RoutePlanner.Plan(t);
            Assert.True(t.FutureRoute.Count >= 2);
            Assert.True(Geo.AngleDiff(Geo.Bearing(t.Head.Pos, t.FutureRoute[^1]), Geo.Bearing(t.Points[0].Pos, t.Head.Pos)) <= 70);
        }
        finally { RoutePlanner.RegionAt = saved; }
    }

    [Fact]
    public void StatedHeadingBeatsFixesLeadingBackToLine()
    {
        // Миллерово → Чертково → Кантемировка, «курсом на северо-восток»: отметки уводят на северо-запад, к границе,
        // прогноз идёт по курсу словами
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, HeadingHint = 26 };
        t.Points.Add(new(new(48.93, 40.40), T0, "Миллерово", "c"));
        t.Points.Add(new(new(49.38, 40.14), T0.AddMinutes(8), "Чертково", "c"));
        t.Points.Add(new(new(49.70, 39.86), T0.AddMinutes(72), "Кантемировка", "c"));
        RoutePlanner.Plan(t);
        Assert.True(t.FutureRoute.Count >= 2);
        Assert.True(Geo.AngleDiff(Geo.Bearing(t.Head.Pos, t.FutureRoute[^1]), 26) < 30);
    }

    [Fact]
    public void NoCourseForecastFromTwoNearbyFrontlineFixes()
    {
        // «Горловка» → «Горловка — Центр»: две отметки в одном городе у линии — курса нет, прогноз не рисуем
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric };
        t.Points.Add(new(new(48.30, 38.05), T0, "Горловка", "c"));
        t.Points.Add(new(new(48.32, 37.98), T0.AddMinutes(10), "Горловка — Центр", "c"));
        RoutePlanner.Plan(t);
        Assert.Empty(t.FutureRoute);
    }

    [Fact]
    public void ExplicitSeaLaunchMovedBehindFirstFix()
    {
        // «с Азовского моря» пришло с поздним сообщением у Новороссийска, а трек начался у Керчи:
        // пуск остаётся морским, но позади первой отметки, а не впереди по пути
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric };
        t.Points.Add(new(new(45.36, 36.47), T0, "Керчь", "c"));
        t.Points.Add(new(new(45.20, 36.72), T0.AddMinutes(17), "Волна", "c"));
        t.Points.Add(new(new(44.72, 37.77), T0.AddMinutes(50), "Новороссийск", "c"));
        t.Launch = new GeoPoint(45.40, 36.67);
        t.LaunchLabel = "Азовское море";
        t.LaunchExplicit = true;
        RoutePlanner.Plan(t);
        if (t.Launch is { } l)
        {
            var first = t.Points[0].Pos;
            Assert.True(Geo.AngleDiff(Geo.Bearing(first, l), (Geo.Bearing(first, t.Head.Pos) + 180) % 360) <= 90);
        }
    }

    [Fact]
    public void CoastalSeaLaunchIsBehindFirstFix()
    {
        // «с моря» у Туапсе: точка в акватории у самого берега, а объект идёт вдоль побережья на Сочи —
        // пуск должен оказаться в море позади Туапсе, а не впереди по пути
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric };
        t.Points.Add(new(new(44.10, 39.07), T0, "Туапсе", "c"));
        t.Points.Add(new(new(43.59, 39.72), T0.AddMinutes(60), "Сочи", "c"));
        t.Points.Add(new(new(43.43, 39.92), T0.AddMinutes(75), "Адлер", "c"));
        t.Launch = new GeoPoint(44.02, 39.06);
        t.LaunchLabel = "Чёрное море";
        t.LaunchExplicit = true;
        RoutePlanner.Plan(t);
        var first = t.Points[0].Pos;
        var l = Assert.NotNull(t.Launch);
        Assert.True(RouteNetwork.IsWater(l));
        Assert.True(Geo.AngleDiff(Geo.Bearing(first, l), (Geo.Bearing(first, t.Head.Pos) + 180) % 360) <= 90);
    }

    [Fact]
    public void FixBackTowardBorderFromStartIsNewTrack()
    {
        // Ракитное → Белгород, затем Шебекино у самой границы — это другой объект, а не возврат того же
        var b = new TrackBuilder();
        var t = b.Add(Obs(new GeoPoint(50.84, 35.86), "Ракитное", 0))!;
        b.Add(Obs(new GeoPoint(50.595, 36.587), "Белгород", 15));
        b.Add(Obs(new GeoPoint(50.405, 36.888), "Шебекино", 40));
        Assert.DoesNotContain(t.Points, p => p.Label == "Шебекино");
    }

    [Fact]
    public void FromSeaFixIsNotGluedToOverlandTrack()
    {
        // трек зашёл в Крым по суше через Чонгар; «Алушта … фиксация с моря» — другой объект, а не продолжение пути
        var b = new TrackBuilder();
        var t = b.Add(Obs(new GeoPoint(46.03, 34.55), "Чонгар", 0))!;
        b.Add(Obs(new GeoPoint(45.61, 34.09), "Краснодарка", 19));
        b.Add(Obs(new GeoPoint(44.99, 34.90), "Муромское", 53));
        Assert.NotNull(t.Launch);
        Assert.False(RouteNetwork.IsWater(t.Launch!.Value));
        var m = MessageParser.Parse(new RawMessage("LPRalarm", 1, T0, "Алушта и близлежащие\nТревога по БПЛА повторно\nФиксация с моря\nРеспублика Крым"), []);
        Assert.True(m.Route?.FromSea);
        var n = b.Add(Obs(new GeoPoint(44.68, 34.41), "Алушта", 66) with { Route = m.Route })!;
        Assert.NotSame(t, n);
        Assert.DoesNotContain(t.Points, p => p.Label == "Алушта");
    }

    [Theory]
    [InlineData("Ростовская область\nС юга ЛНР в направлении Таганрога группа БпЛА", false)]
    [InlineData("Матвеев Курган, Ростов-на-Дону и близлежащие, тревога по БпЛА с ЛНР (с юга республики)", false)]
    [InlineData("Прохоровка БПЛА с севера\nБелгородская область", true)]
    public void FromPartOfRegionIsNotHeading(string text, bool southward)
    {
        // «с юга ЛНР» — из южной части республики, а не «летит с юга на север»
        var m = MessageParser.Parse(new RawMessage("lpr1_Rostov_alarm", 1, T0, text), []);
        if (southward) Assert.Equal(180.0, m.Heading);
        else Assert.False(m.Heading is { } h && Geo.AngleDiff(h, 0) < 45);
    }

    [Theory]
    [InlineData("nabludatel_rf", "Акватория Каспийского моря\nОпасность по БПЛА\nнаблюдатель.рф | подписаться", "Каспийское море")]
    [InlineData("nabludatel_rf", "ГО Наро-Фоминский\nГО Одинцово\nАО Троицкий\nМосковская область\nМеры предосторожности\nнаблюдатель.рф | подписаться", "Одинцово")]
    [InlineData("locatorru", "Астраханская область - порт Оля, гражданские суда находящиеся в акватории Каспийского моря и вблизи порта - внимание по БПЛА.", "Порт Оля")]
    public void FreshChannelFormatsAreSignals(string ch, string text, string place)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), []);
        Assert.False(m.IsJunk);
        Assert.Contains(m.Places, p => p.Label == place);
    }

    [Theory]
    [InlineData("LPRalarm", "Краснадар\nКраснодарский край\nТревога по БПЛА сохраняется", "Краснодар")]
    [InlineData("lpr1_Bryansk_alarm", "Навля\nКарачаев\nБрянск\nТревога по реактивным БПЛА\nБрянская область", "Карачев")]
    [InlineData("lpr1_LDPR_alarm", "Горсокое\nЛНР\nОпасность по БПЛА Хорнет", "Горское")]
    [InlineData("LPRalarm", "Пантелеймоновка, Корсунь, Енакиево, Юнком и близлежащие\nОпасность по БПЛА\nДНР", "Юнокоммунаровск")]
    [InlineData("LPRalarm", "Ген горка\nХерсонская область РФ\nОпасность по БПЛА Хорнет", "Геническая Горка")]
    [InlineData("kupolrussia", "Новоалексеевка\nГенчиская горка\nГеническ\nОпасность по БПЛА\nХерсонская область РФ", "Геническая Горка")]
    [InlineData("locatorru", "Знаменск, Капустин Яр, Ахтубинск, ПС Газовая, АГПЗ, Порт Оля, Астраханская область - опасность по БПЛА.", "Астраханский ГПЗ")]
    public void TypoInPlaceNameResolved(string ch, string text, string place)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), []);
        Assert.Contains(m.Places, p => p.Label == place && p.Pos != null);
    }

    [Theory]
    [InlineData("LPRalarm", "Дятьково\nАнтрацит\nПетровское\nИ далее на Луганск опасность по БПЛА\nЛНР", "Дьяково", "lnr")]
    [InlineData("vrv_radar", "Знаменский район\nАстраханская область\nФиксация БПЛА", "Знаменск", "astrakhan")]
    [InlineData("vrv_radar", "Кантемировский район\nАстраханская область\nФиксация группы БПЛА", "Кантемировский район", "voronezh")]
    public void PlaceOutsideStatedRegionResolved(string ch, string text, string place, string region)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), []);
        var p = Assert.Single(m.Places, x => x.Label == place);
        Assert.Equal(region, p.RegionId);
        Assert.NotNull(p.Pos);
    }

    [Theory]
    [InlineData("lpr1_Belgorod_alarm", "belgorod", "Белгородская область, Шебекенский район, опасность по БПЛА", "Шебекинский округ")]
    [InlineData("lpr1_Belgorod_alarm", "belgorod", "Рякитянском, Ивнянском, Прохоровском, Яковлевском МО\nБеспилотная опасность\nБелгородская область", "Ракитянский округ")]
    [InlineData("lpr1_Kherson_alarm", "kherson", "Князе Григорьевку в направлении\nНижние Сирогозы\nОпасность по БПЛА\nХерсонская область РФ", "Нижние Серогозы")]
    [InlineData("lpr1_Krasnodar_alarm", "krasnodar", "Новотитаровская\nСлавянск -на-Кубани и близлежащие\nОпасность по БПЛА", "Славянск-на-Кубани")]
    [InlineData("lpr1_Kherson_alarm", "zaporozhye", "Дорога\nЧЕРВОНОЕ ПОЛЕ - ОСИПЕНКО - БЕРДЯНСК опасность по БПЛА\nЗапорожская область РФ", "Осипенко")]
    [InlineData("LPRalarm", "crimea", "Черноморский район Межводное, Черноморское\nРаздольненский район,\nзалив Красноперекопск\nРеспублика Крым", "Раздольненский район")]
    public void FrequentUnresolvedNamesResolved(string ch, string chRegion, string text, string place)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), [chRegion]);
        Assert.Contains(m.Places, p => p.Label == place && p.Pos != null);
    }

    [Theory]
    [InlineData("LPRalarm", "", "Филия, Богатырь, Угледар, Комар, Великая Новосёлка и близлежащие ДНР\nТревога по БПЛА", "Филия")]
    [InlineData("lpr1_Crimea_Alarm", "crimea", "Южная бухта\nСапун-гора\nТревога по БПЛА\nСевастополь", "Сапун-гора")]
    [InlineData("lpr1_Belgorod_alarm", "belgorod", "Зарубино, Никольское\nБелгородский район\nОпасность по БПЛА", "Зарубино")]
    [InlineData("lpr1_Crimea_Alarm", "crimea", "Тайган\nТревога по БПЛА\nРеспублика Крым", "Сафари-парк «Тайган»")]
    public void ManuallyAddedPlacesResolved(string ch, string chRegion, string text, string place)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), chRegion.Length > 0 ? [chRegion] : []);
        Assert.Contains(m.Places, p => p.Label == place && p.Pos != null);
    }

    [Fact]
    public void UnknownPlaceInOtherRegionIsNotGuessed()
    {
        // «Почеп» в крымском канале — такого пункта в Крыму нет; брянский Почеп за 800 км угадывать нельзя
        var m = MessageParser.Parse(new RawMessage("lpr1_Crimea_Alarm", 1, T0, "Почеп и близлежащие\nТревога по БПЛА сохраняется\nРеспублика Крым"), ["crimea"]);
        Assert.DoesNotContain(m.Places, p => p.Pos != null && p.RegionId != "crimea");
        Assert.Contains("crimea", m.Regions);
    }

    [Fact]
    public void MayaIsUavTypeNotPlace()
    {
        var m = MessageParser.Parse(new RawMessage("LPRalarm", 1, T0, "Трасса Приморск\nПриазовское\nАктивность бпла Хорнет и Майя\nЗапорожская область РФ"), []);
        Assert.DoesNotContain(m.Places, p => p.Label.Contains("Майя", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ThreatType.Uav, m.Threat);
    }

    [Fact]
    public void ImplausibleSpeedIsNotMeasured()
    {
        // 100 км за 20 мин (300 км/ч) для обычного БПЛА — неточность привязки, а не скорость
        var b = new TrackBuilder();
        var t = b.Add(Obs(new GeoPoint(51.19, 35.27), "А", 0))!;
        b.Add(Obs(Geo.Destination(new GeoPoint(51.19, 35.27), 60, 100), "Б", 20));
        Assert.False(t.HasMeasuredSpeed && t.SpeedKmh > Track.SpeedRange(ThreatKind.UavGeneric, ThreatType.Uav).Max);
        Assert.Equal(0, t.SpeedSamples);
    }

    [Fact]
    public void ObjectHeadingToTrackOriginIsSeparate()
    {
        // трек Курчатов → Курск; «Фатеж и далее на Курск» — встречный объект, не разворот трека из Курска
        var b = new TrackBuilder();
        var t1 = b.Add(Obs(Kurchatov, "Курчатов", 0))!;
        b.Add(Obs(Kursk, "Курск", 20));
        var t2 = b.Add(Obs(Fatezh, "Фатеж", 30) with { Target = Kursk, TargetLabel = "Курск" })!;
        Assert.NotSame(t1, t2);
        Assert.Equal("Курск", t1.Head.Label);
    }

    [Fact]
    public void AreaTargetForecastIsShortAndHasNoEta()
    {
        // «в направлении Орловской области»: центр области — не место назначения, прогноз обычной длины, без времени прибытия
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20) with { Target = new(52.95, 36.07), TargetLabel = "Орловская область", TargetIsArea = true });
        Assert.True(t.TargetIsArea);
        Assert.False(t.HasPointTarget);
        Assert.Null(t.EtaAt(T0.AddMinutes(25)));
        Assert.True(Geo.PathLength(t.FutureRoute) <= 61);
    }

    [Fact]
    public void HeavyCoptersAtDistantTownsAreSeparateObjects()
    {
        // «Снежное ДНР Баба Яга», через час «Стрюково», «Орлово-Ивановка»: тяжёлый коптер — тактический (10–20 км),
        // отметка в 25+ км от начала трека — другой коптер, а не перелёт одного к линии
        FrontLineSource.LoadForAudit();
        var b = new TrackBuilder();
        var t1 = b.Add(ObsK(new(48.03, 38.77), "Снежное", 0, ThreatKind.UavHeavy))!;
        b.Add(ObsK(new(48.18, 38.59), "Стрюково", 49, ThreatKind.UavHeavy));
        var t3 = b.Add(ObsK(new(48.17, 38.47), "Орлово-Ивановка", 63, ThreatKind.UavHeavy))!;
        Assert.NotSame(t1, t3);
        Assert.True(t1.Points.Count <= 2);
    }

    [Fact]
    public void NewGroupHeadingToRearDoesNotJoinDeeperTrack()
    {
        // «Новые Ивайтёнки» → «Рохманово» (вглубь), затем «ещё 2 группы через Погарский район… в тыл» — у линии:
        // это новая группа, а не возврат первой к границе
        FrontLineSource.LoadForAudit();
        var b = new TrackBuilder();
        var t = b.Add(Obs(new(52.78, 33.07), "Новые Ивайтёнки", 0))!;
        b.Add(Obs(new(52.75, 32.88), "Рохманово", 2));
        var rear = new RouteHints { ToRear = true };
        var g = b.Add(Obs(new(52.50, 33.00), "Погарский район — Стародубский район", 55) with { Route = rear })!;
        Assert.NotSame(t, g);
        Assert.Equal(2, t.Points.Count);
        // и не наследует путь ушедшей группы (сразу после её последней отметки)
        var b2 = new TrackBuilder();
        b2.Add(Obs(new(52.78, 33.07), "Новые Ивайтёнки", 0));
        b2.Add(Obs(new(52.75, 32.88), "Рохманово", 2));
        var g2 = b2.Add(Obs(new(52.62, 32.95), "Стародубский район", 10) with { Route = rear })!;
        Assert.Single(g2.Points);
    }

    [Theory]
    [InlineData("Ещё 2 группы БПЛА через Погарский район, Стародубский район, Брянской области в тыл.", true)]
    [InlineData("БПЛА уходят вглубь территории", true)]
    [InlineData("От Рохманово и далее в тыловую часть опасность по БПЛА, много", true)]
    [InlineData("Работа ПВО в тылу", false)]
    [InlineData("Тыловые регионы, опасность по БПЛА", false)]
    public void ToRearWordsDetected(string text, bool rear)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, T0, text), ["bryansk"]);
        Assert.Equal(rear, m.Route?.ToRear ?? false);
    }

    [Fact]
    public void ShortLoopBetweenNearbyTownsIsLoop()
    {
        // Севастополь: Николаевка → Любимовка → Севастополь → Кача — ~45 км пути при ~20 км по прямой: это две группы,
        // а не один объект (порог как в аудите движка — 40 км)
        var pts = new List<TrackPoint>
        {
            new(new(44.96, 33.61), T0, "Николаевка", "c"),
            new(new(44.66, 33.55), T0.AddMinutes(3), "Любимовка", "c"),
            new(new(44.62, 33.52), T0.AddMinutes(5), "Севастополь", "c"),
            new(new(44.78, 33.55), T0.AddMinutes(25), "Кача", "c"),
        };
        Assert.True(TrackBuilder.IsLoop(pts));
    }

    [Fact]
    public void LateInsertDoesNotCreateLoop()
    {
        var pts = new List<TrackPoint>
        {
            new(Sudzha, T0, "Суджа", "c"),
            new(Kurchatov, T0.AddMinutes(20), "Курчатов", "c"),
            new(new(51.33, 35.20), T0.AddMinutes(40), "у Суджи", "c"),
        };
        Assert.True(TrackBuilder.IsLoop(pts));
        Assert.False(TrackBuilder.IsLoop(pts.Take(2).ToList()));
    }

    [Fact]
    public void PassedTargetIsDropped()
    {
        // цель «на Курчатов» заявлена у границы; объект прошёл Курчатов и ушёл к Фатежу — к Курчатову больше не летим
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0) with { Target = Kurchatov, TargetLabel = "Курчатов" })!;
        b.Add(Obs(Kursk, "Курск", 30));
        b.Add(Obs(Fatezh, "Фатеж", 50));
        Assert.Null(t.Target);
        var br = t.Bearing!.Value;
        Assert.True(Geo.AngleDiff(br, Geo.Bearing(Kursk, Fatezh)) < 60);
    }

    [Fact]
    public void LateTargetDoesNotOverrideFresh()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20) with { Target = Fatezh, TargetLabel = "Фатеж" });
        // запоздавшее сообщение (старше головы трека) с другой целью
        b.Add(Obs(Rylsk, "Рыльск", 5) with { Target = Rylsk, TargetLabel = "Рыльск" });
        Assert.Equal("Фатеж", t.TargetLabel);
    }

    [Fact]
    public void FromDirectionIsOrigin()
    {
        var m = MessageParser.Parse(new RawMessage("lpr1_Rostov_alarm", 3, T0, "Миллерово с направления Счастье ЛНР тревога по реактивным БПЛА"), ["rostov"]);
        var s = m.Places.Single(p => p.Label == "Счастье");
        Assert.True(s.IsOrigin);
        Assert.False(s.IsTarget);
    }

    [Fact]
    public void NoisyTwoPointCourseBackToBorderHasNoForecast()
    {
        // центр района → пункт ближе к границе: курс «назад» из шума привязки, прогноз не рисуется
        GeoPoint a = new(51.73, 36.19), nearer = new(51.66, 35.95);
        Assert.InRange(Borderline.DistanceKm(a) - Borderline.DistanceKm(nearer), 8, 30);
        var b = new TrackBuilder();
        var t = b.Add(Obs(a, "Курск", 0))!;
        b.Add(Obs(nearer, "Курский район", 20));
        Assert.Equal(2, t.Points.Count);
        Assert.Empty(t.FutureRoute);
    }

    [Theory]
    [InlineData("LPRalarm", "Ялта ДНР, Урзуф, Мангуш, Мариуполь и близлежащие опасность по БПЛА", "Ялта", "dnr")]
    [InlineData("lpr1_Kursk_alarm", "Калиновка, Хомутовка и близлежащие\nКурская область\nОпасность по ФПВ", "Калиновка", "kursk")]
    [InlineData("LPRalarm", "Красноармейск, Дзержинск, Горловка, Константиновка ДНР\nУАБы", "Дзержинск", "dnr")]
    [InlineData("lpr1_Crimea_Alarm", "Танковое, Куйбышево и далее возможно на Ай-Петри тревога по БПЛА", "Куйбышево", "crimea")]
    public void NamesakeNearOtherPlacesIsPicked(string ch, string text, string label, string region)
    {
        var s = new AppSettings();
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), s.Channels.FirstOrDefault(c => c.Name == ch)?.Regions ?? []);
        var p = m.Places.Single(x => x.Label == label);
        Assert.Equal(region, p.RegionId);
        var others = m.Places.Where(x => x != p && x.Pos != null).ToList();
        Assert.True(others.Min(o => Geo.DistanceKm(o.Pos!.Value, p.Pos!.Value)) < 45);
    }

    [Fact]
    public void DistrictsInDativeListAreFound()
    {
        var m = MessageParser.Parse(new RawMessage("lpr1_Kherson_alarm", 1, T0, "Опасность БпЛА по Бердянскому и Приморскому районам\nЗапорожская область РФ"), ["kherson", "zaporozhye"]);
        Assert.Contains(m.Places, p => p.Label == "Приморский район" && p.RegionId == "zaporozhye" && p.IsArea);
        Assert.Contains(m.Places, p => p.Label == "Бердянский район" && p.RegionId == "zaporozhye" && p.IsArea);
        Assert.DoesNotContain(m.Places, p => p.Pos == null);
        Assert.DoesNotContain("kherson", m.Regions);
    }

    [Theory]
    [InlineData("LPRalarm", "ЛНР. Угроза атаки БПЛА.\nВ границах муниципальных округов: Краснодонский, Лутугинский, Славяносербский, и городов Луганск, Брянка", "Лутугинский район", "lnr")]
    [InlineData("LPRalarm", "Рыльский, Хомутовский, Глушковский район,\nСтык с Брянской областью\nАвиационная ракетная бомбовая тревога.", "Рыльский район", "kursk")]
    [InlineData("kupolrussia", "Кантемировский\nБогучарский район\nВоронежская область", "Кантемировский район", "voronezh")]
    [InlineData("lpr1_Belgorod_alarm", "Ивнянский район\nОбоянский, Солнцевский, Курский и далее на север Курская область\nТревога по БПЛА.", "Обоянский район", "kursk")]
    [InlineData("lpr1_Crimea_Alarm", "Бухты Казачья, Камышовая, Омега\nПарк Победы\nСевастополь\nС моря еще фиксации БПЛА", "Казачья бухта", "sevastopol")]
    [InlineData("lpr1_Kherson_alarm", "Генгорка\nАрбатская Стрелка\nи близлежащие\nТревога по БПЛА", "Геническая Горка", "kherson")]
    public void PreviouslyUnresolvedPlacesAreFound(string ch, string text, string label, string region)
    {
        var s = new AppSettings();
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), s.Channels.FirstOrDefault(c => c.Name == ch)?.Regions ?? []);
        var p = m.Places.Single(x => x.Label == label);
        Assert.Equal(region, p.RegionId);
        Assert.NotNull(p.Pos);
    }

    [Theory]
    [InlineData("lpr1_Crimea_Alarm", "Наши каналы тревог:\nMAX\nОсновной канал тревог\nhttps://max.ru/LPRalarm\n1. Республика Крым и Севастополь\nhttps://max.ru/join/a\n2. Краснодарский Край\nhttps://max.ru/join/b")]
    [InlineData("lpr1_Rostov_alarm", "Просим Вас принять участие в сборе оборудования на МОГи для прикрытия от ударов FPV на ЛБС.\nИтого: 210.000 рублей")]
    public void ChannelServiceTextsAreJunk(string ch, string text)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), []);
        Assert.True(m.IsJunk);
    }

    [Fact]
    public void CrimeanPervomaiskIsNotLuhansk()
    {
        var m = MessageParser.Parse(new RawMessage("lpr1_Crimea_Alarm", 1, T0, "Первомайский район\nПервомайск и близлежащие\nОпасность по БПЛА\nРеспублика Крым"), ["crimea", "sevastopol"]);
        Assert.All(m.Places, p => Assert.Equal("crimea", p.RegionId));
        Assert.DoesNotContain("lnr", m.Regions);
    }

    [Fact]
    public void NamesakeChainResolvesTogether()
    {
        // после переноса Константиновки в ДНР рядом оказываются и Горловка, и Дзержинск
        var m = MessageParser.Parse(new RawMessage("LPRalarm", 1, T0, "Константиновка, Дзержинск, Горловка и близлежащие тревога по УАБ"), []);
        Assert.All(m.Places, p => Assert.Equal("dnr", p.RegionId));
    }

    [Fact]
    public void RegionalCentersStayThemselves()
    {
        var m = MessageParser.Parse(new RawMessage("lpr1_Bryansk_alarm", 1, T0, "Возможные пролеты БПЛА по Брянской области в тыл\nОрел/Калуга\nПролеты очень низко"), ["bryansk"]);
        Assert.Equal("kaluga", m.Places.Single(p => p.Label == "Калуга").RegionId);
        Assert.Equal("orel", m.Places.Single(p => p.Label == "Орёл").RegionId);
    }

    [Fact]
    public void KalachInVolgogradContextIsKalachNaDonu()
    {
        var m = MessageParser.Parse(new RawMessage("vrv_radar", 1, T0, "Суровикино\nВолгоградская область\nФиксация БПЛА в направлении Калач"), []);
        var k = m.Places.Single(p => p.IsTarget);
        Assert.Equal("volgograd", k.RegionId);
        Assert.True(Geo.DistanceKm(k.Pos!.Value, new GeoPoint(48.69, 43.53)) < 10);
        var v = MessageParser.Parse(new RawMessage("lpr1_Voronezh_alarm", 2, T0, "Калач\nТревога по БПЛА"), ["voronezh"]);
        Assert.Equal("voronezh", v.Places.Single().RegionId);
    }

    [Fact]
    public void GlubokiyNextToKamenskDistrictIsRostov()
    {
        // хутор Глубокий в ЛНР — в ~90 км от остальных пунктов; ростовский Глубокий (М4) — прямо среди них
        var m = MessageParser.Parse(new RawMessage("radar_rschs", 1, T0,
            "От Счастье(ЛНР) и Станицы Луганской через Зелёновку/Власовку(Тарасовский район) и далее на Тишкин/Урывский/Глубокий М4, " +
            "Таловая Балка (Каменский район), и потом на Морозовск - фиксации БПЛА. Уходят на Волгоградскую область."), []);
        var g = m.Places.First(p => p.Label == "Глубокий");
        Assert.Equal("rostov", g.RegionId);
    }

    [Theory]
    [InlineData("Донецк Ростовский\nКаменск-Шахтинский\nИ близлежащие\nТревога по БПЛА от ЛНР", "Донецк (Ростовская обл.)", "rostov")]
    [InlineData("От Угловое в направлении Орловка\nФиксация БПЛА\nРеспублика Крым", "Орловка", "sevastopol")]
    [InlineData("г. Алексеевка фиксация БПЛА в направлении г.  Воронеж", "Алексеевка", "belgorod")]
    public void NamesakeResolvedByNeighbours(string text, string label, string region)
    {
        var m = MessageParser.Parse(new RawMessage("kupolrussia", 1, T0, text), []);
        Assert.Equal(region, m.Places.First(p => p.Label == label).RegionId);
    }

    [Theory]
    [InlineData("lpr1_Krasnodar_alarm", "krasnodar", "Новороссийск\nРаевская и близлежащие\nПовторно фиксации БПЛА по прежнему направлению Гайдук, Борисовка и далее на порт", "Борисовка", "krasnodar")]
    [InlineData("lpr1_Bryansk_alarm", "bryansk", "БПЛА Дартс идёт по трассе с Васильевки, в сторону кольца Бугаевка\nПогарский район", "Васильевка", "bryansk")]
    [InlineData("lpr1_Kherson_alarm", "zaporozhye", "Куйбышево (Бильмак) и далее в направлении Бердянск, Ялта ДНР, Мариуполь\nТревога по БПЛА", "Куйбышево", "zaporozhye")]
    [InlineData("lpr1_Voronezh_alarm", "voronezh", "г. Алексеевка фиксация БПЛА в направлении г.  Воронеж", "Алексеевка", "belgorod")]
    public void ChannelRegionNamesakeNextToNeighbours(string ch, string chRegion, string text, string label, string region)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, T0, text), [chRegion]);
        Assert.Equal(region, m.Places.First(p => p.Label == label).RegionId);
    }

    [Fact]
    public void ReturnToVisitedPointCutsBranch()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Sudzha, "Суджа", 0))!;
        b.Add(Obs(Kurchatov, "Курчатов", 20));
        b.Add(Obs(Kursk, "Курск", 40));
        b.Add(Obs(new GeoPoint(51.70, 36.15), "Курск, окраина", 45));
        Assert.True(t.Points.Count <= 4);
        Assert.Equal("Суджа", t.Points[0].Label);
    }

    private static readonly GeoPoint Evpatoria = new(45.19, 33.37), Saki = new(45.13, 33.60), Simferopol = new(44.95, 34.10),
        Tarhankut = new(45.35, 32.50);

    [Fact]
    public void SeaApproachToCrimeaKeepsDirection()
    {
        var b = new TrackBuilder();
        var t = b.Add(Obs(Evpatoria, "Евпатория", 0))!;
        b.Add(Obs(Saki, "Саки", 10));
        b.Add(Obs(Simferopol, "Симферополь", 30));
        // отметка у точки входа после того, как объект ушёл вглубь, — это другой объект
        var t2 = b.Add(Obs(Tarhankut, "Тарханкут", 40))!;
        Assert.NotSame(t, t2);
        Assert.Equal(["Евпатория", "Саки", "Симферополь"], t.Points.Select(p => p.Label));
    }

    [Fact]
    public void BorderRuleDoesNotBlockCrimeaCoastTracks()
    {
        // Крым: на север к Перекопу (к линии соприкосновения) объект может лететь — сухопутное правило здесь не действует
        Assert.Equal(0, Borderline.BackKm(Simferopol, new GeoPoint(45.95, 33.79), null));
    }

    [Fact]
    public void KubanCoastTrackStartsAtSeaBehind()
    {
        GeoPoint gel = new(44.64, 37.93), nov = new(44.72, 37.77), anapa = new(44.83, 37.55);
        var b = new TrackBuilder();
        var t = b.Add(Obs(gel, "Новороссийск — Геленджик", 0))!;
        b.Add(Obs(nov, "Новороссийск", 14));
        b.Add(Obs(anapa, "Анапа — Новороссийск", 25));
        Assert.Equal("Чёрное море", t.LaunchLabel);
        var l = t.Launch!.Value;
        Assert.True(RouteNetwork.Lines.Count == 0 || RouteNetwork.IsWater(l));
        // пуск не впереди по курсу вдоль берега
        Assert.True(Geo.AngleDiff(Geo.Bearing(gel, l), Geo.Bearing(gel, anapa)) > 90);
    }

    [Fact]
    public void DonbassDepthCountsFromFrontNotOldBorder()
    {
        GeoPoint donetsk = new(48.00, 37.80), makeevka = new(48.05, 37.97), khartsyzsk = new(48.04, 38.15), lugansk = new(48.57, 39.31);
        // глубина в ДНР растёт при удалении от линии соприкосновения на восток, а не от административной границы области
        Assert.True(Borderline.DistanceKm(khartsyzsk) > Borderline.DistanceKm(donetsk));
        Assert.True(Borderline.DistanceKm(lugansk) > 60);
        var b = new TrackBuilder();
        var t = b.Add(Obs(donetsk, "Донецк", 0))!;
        b.Add(Obs(makeevka, "Макеевка", 8));
        b.Add(Obs(khartsyzsk, "Харцызск", 16));
        Assert.Equal(3, t.Points.Count);
    }

    [Fact]
    public void DistrictCentreDoesNotBendPath()
    {
        // «Почепский район» (центр района, ±25 км) между Погаром и Игрушино не даёт S-образный изгиб
        var b = new TrackBuilder();
        var t = b.Add(Obs(new(52.50, 33.00), "Погарский район — Стародубский район", 0) with { SpreadKm = 25 })!;
        b.Add(Obs(new(52.91, 33.50), "Почепский район", 1) with { SpreadKm = 25 });
        b.Add(Obs(new(53.00, 33.46), "Игрушино", 5));
        b.Add(Obs(new(53.10, 34.07), "Выгоничи", 22));
        Assert.DoesNotContain(t.Points, p => p.Label == "Почепский район");
    }

    [Fact]
    public void DeepRearTargetTowardsUkraineIsIgnored()
    {
        // у Брянска «в сторону Почепа» — объект в тылу назад к границе не летит: цель не принимается
        var b = new TrackBuilder();
        var t = b.Add(Obs(new(53.25, 34.47), "Объездная", 0) with { Target = new(52.93, 33.45), TargetLabel = "Почеп" })!;
        Assert.Null(t.Target);
    }
}

/// <summary>Треки через движок целиком (ReplaySync подменяет общие часы — только в коллекции «Engine»).</summary>
[Collection("Engine")]
public class TrackEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EnemySideOriginIsNotFirstTrackPoint()
    {
        // «От Харькова фиксация БПЛА в направлении Валуйки» — Харьков место пуска, а не отметка пути
        var e = new AlertEngine(new AppSettings(), new HttpClient(), new Geocoder(new HttpClient(), new NominatimThrottle()));
        var msgs = new[]
        {
            new RawMessage("locatorru", 1, T0, "От Харькова фиксация БПЛА в направлении Валуйки, Белгородская область."),
            new RawMessage("vrv_radar", 2, T0.AddMinutes(30), "Ольховатский район\nФиксация группы БПЛА\nВоронежская область"),
        };
        foreach (var _ in e.ReplaySync(msgs)) { }
        Assert.DoesNotContain(e.TrackSnapshot(), t => t.Points.Any(p => p.Label == "Харьков"));
    }


    [Fact]
    public void SameChannelListingIsNotMergedLater()
    {
        // vrv_radar: «Шумилинская … далее на Волгоградскую» и через 15 с «Казанская … Фиксация» (29 км) — две группы;
        // склейка дублей (перепечатка Шумилинской другим каналом) не должна их соединять
        var t0 = new DateTimeOffset(2025, 8, 29, 18, 50, 56, TimeSpan.Zero);
        RawMessage M(string ch, long id, int sec, string text) => new(ch, id, t0.AddSeconds(sec), text);
        var msgs = new[]
        {
            M("vrv_radar", 24585, 0, "Вешенская, Ростовская область\nПриготовится к сбитию БПЛА\nРаботает ВКС РФ"),
            M("radarrussiia", 33106, 93, "Вешенская, Ростовская область\nПриготовится к сбитию БПЛА\nРаботает ВКС РФ\n❗️Радар по всей России - @radarrussiia"),
            M("nabludatel_rf", 2858, 232, "Вешенская, Ростовская область\nПриготовится к сбитию БПЛА\nРаботает ВКС РФ\n❗️Наблюдаем за всей Россией — наблюдатель.рф"),
            M("vrv_radar", 24586, 283, "Ростовская область, Боковская\n5 БПЛА в вашу сторону далее Вешенская"),
            M("radarrussiia", 33107, 315, "Ростовская область, Боковская\n5 БПЛА в вашу сторону далее Вешенская\n❗️Радар по всей России - @radarrussiia"),
            M("nabludatel_rf", 2859, 580, "Ростовская область, Боковская\n5 БПЛА в вашу сторону далее Вешенская\n❗️Наблюдаем за всей Россией — наблюдатель.рф"),
            M("vrv_radar", 24596, 2898, "Шумилинская, Ростовская область\n2 БПЛА в вашу сторону и далее на Волгоградскую область"),
            M("vrv_radar", 24597, 2913, "Казанская\nРостовская область\nФиксация БПЛА"),
            M("radarrussiia", 33119, 2940, "Шумилинская, Ростовская область\n2 БПЛА в вашу сторону и далее на Волгоградскую область\n❗️Радар по всей России - @radarrussiia"),
            M("nabludatel_rf", 2868, 3306, "Шумилинская, Ростовская область\n2 БПЛА в вашу сторону и далее на Волгоградскую область\n❗️Наблюдаем за всей Россией — наблюдатель.рф"),
        };
        using var http = new HttpClient();
        var e = new AlertEngine(new AppSettings(), http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in e.ReplaySync(msgs)) { }
        Assert.True(!e.TrackSnapshot().Any(x => x.Points.Any(p => p.Label == "Шумилинская") && x.Points.Any(p => p.Label == "Казанская")), "две группы склеены в один трек");
    }

    [Fact]
    public void StandingWarningsDoNotDriveTrackToLine()
    {
        // lpr1_LDPR_alarm, 28.08.2026: опасность по площади Макеевка → Енакиево («сохраняется») → Горловка («сохраняется»)
        var t0 = new DateTimeOffset(2026, 8, 28, 9, 21, 0, TimeSpan.Zero);
        var msgs = new[]
        {
            new RawMessage("lpr1_LDPR_alarm", 48360, t0, "Макеевка, Харцызск, Зугрэс ДНР и близлежащие\nОпасность по БПЛА"),
            new RawMessage("lpr1_LDPR_alarm", 48362, t0.AddMinutes(11), "Пятихатки, Пантелеймоновка, Корсунь, Енакиево ДНР и близлежащие\nОпасность по БПЛА сохраняется"),
            new RawMessage("lpr1_LDPR_alarm", 48363, t0.AddMinutes(13), "Горловка опасность по БПЛА и ФПВ сохраняется\nЗапад, центр быть предельно осторожными"),
        };
        using var http = new HttpClient();
        var e = new AlertEngine(new AppSettings(), http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in e.ReplaySync(msgs)) { }
        Assert.All(e.TrackSnapshot(), x => Assert.Single(x.Points));
    }
}
