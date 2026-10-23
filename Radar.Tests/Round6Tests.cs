using System.Diagnostics;
using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Аудит, круги 5–6: словоформы угроз, уровни, бэктрекинг регулярок.</summary>
public class Round6Tests
{
    private static ParsedMessage P(string text) =>
        MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []);

    [Theory]
    [InlineData("Курская область. БПЛА, температура 30 градусов", ThreatKind.Mlrs)]
    [InlineData("Белгородская область. Катерина. Опасность по БПЛА", ThreatKind.Usv)]
    public void KindNotFromWordStem(string text, ThreatKind notKind) => Assert.DoesNotContain(notKind, P(text).Kinds);

    [Fact]
    public void PossibleAlarmIsOnlyAttention() => Assert.Equal(AlertLevel.Attention, P("Возможна тревога по БПЛА, Курская область").Level);

    [Fact]
    public void AircraftModelsDetected() => Assert.Equal(ThreatType.Aviation, P("Белгородская область, взлетел Су-34").Threat);

    [Fact]
    public void NegatedThreatIsNotDanger() => Assert.NotEqual(AlertLevel.Danger, P("Курская область. Угрозы БПЛА нет").Level);

    [Fact]
    public void SummaryCleanDoesNotBacktrack()
    {
        P("Курская область, опасность БПЛА");
        var sw = Stopwatch.StartNew();
        AlertSummary.Clean("Курская область БПЛА" + string.Concat(Enumerable.Repeat(" ,", 24)) + " x", ["kursk"], false, [ThreatType.Uav]);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void LongWhitespaceLineParsesQuickly()
    {
        // прогрев: первый разбор загружает индексы мест и компилирует регулярки — это не то, что мерим
        P("Курская область, опасность БПЛА");
        var sw = Stopwatch.StartNew();
        P("Курская область, опасность БПЛА\nx" + new string(' ', 4000) + "y");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Theory]
    [InlineData("Белгородская область. Кинжал, опасность", ThreatType.Missile)]
    [InlineData("Крым. Калибры в море, опасность", ThreatType.Missile)]
    [InlineData("Опасность. Запуск Х-101, Курская область", ThreatType.Missile)]
    [InlineData("Опасность. Циркон, Одесская область", ThreatType.Missile)]
    public void MissileNamesDetected(string text, ThreatType expected) => Assert.Equal(expected, P(text).Threat);

    [Fact]
    public void ThunderIdiomIsNotBallistic() => Assert.DoesNotContain(ThreatKind.MissileBallistic, P("Белгородская область. Гром среди ясного неба. Опасность БПЛА").Kinds);

    [Fact]
    public void ClearMissileKeepsUav()
    {
        var m = P("Курская область отбой ракетной опасности, БПЛА летят");
        Assert.NotEqual(AlertStatus.AllClear, m.Status);
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(40.0)]
    [InlineData(120.0)]
    [InlineData(400.0)]
    public void EtaRangeAlwaysContainsEta(double speed)
    {
        var t = new Track { Id = 1, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, SpeedKmh = speed, SpeedSamples = 1, Target = new GeoPoint(51.73, 36.19) };
        t.SpeedLog.Add((speed, true));
        t.Points.Add(new TrackPoint(new GeoPoint(51.73, 34.70), T0, "A", "x"));
        var eta = t.EtaAt(T0)!.Value;
        var r = t.EtaRangeAt(T0)!.Value;
        Assert.True(r.Min <= eta && eta <= r.Max, $"{r.Min} ≤ {eta} ≤ {r.Max}");
    }

    [Theory]
    [InlineData("{\"id\":\"abc\",\"map\":{}}")]
    [InlineData("{\"id\":null,\"map\":{}}")]
    public void FrontDataTimeIgnoresNonNumericId(string json) =>
        Assert.Null(FrontLineSource.ReadDataTime(System.Text.Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ArchiveMessageWithHugeTimestampIsSkipped() => Assert.Null(MessageCache.Parse("chan\t1\t99999999999999\ttext"));

    [Fact]
    public void ThrottleBlocksAfterServerRefusal()
    {
        var th = new NominatimThrottle();
        Assert.False(th.Blocked);
        th.Block(TimeSpan.FromSeconds(30));
        Assert.True(th.Blocked);
    }

    [Fact]
    public void SummaryCleanHandlesLongWhitespace()
    {
        P("Курская область, опасность БПЛА");
        var sw = Stopwatch.StartNew();
        AlertSummary.Clean("Курская область БПЛА" + new string(' ', 6000) + "x" + new string(' ', 6000), ["kursk"], false, [ThreatType.Uav]);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void SummaryTrimsDanglingConjunction() =>
        Assert.False(AlertSummary.Clean("Белгород, Курская область и", ["kursk"], false, [ThreatType.Uav]).TrimEnd().EndsWith(" и", StringComparison.Ordinal));

    [Theory]
    [InlineData("Белгородская область. Шторм на море, ветер. Тревога по БПЛА")]
    [InlineData("Крым. Штормовое предупреждение. Опасность БПЛА")]
    [InlineData("Крым. Сильный шторм. Опасность БПЛА")]
    public void WeatherStormIsNotMissile(string text)
    {
        var m = P(text);
        Assert.Equal(ThreatType.Uav, m.Threat);
        Assert.DoesNotContain(ThreatKind.MissileCruise, m.Kinds);
    }

    [Theory]
    [InlineData("Белгородская область. Шторм, опасность")]
    [InlineData("Крым. Пуск Storm Shadow, опасность")]
    [InlineData("Белгородская область. Ракеты Шторм, опасность")]
    public void StormMissileStillDetected(string text) => Assert.Equal(ThreatType.Missile, P(text).Threat);

    [Fact]
    public void WingWordIsNotCarrier() => Assert.DoesNotContain(ThreatKind.UavCarrier, P("Курская область. Крыло здания повреждено. Опасность БПЛА").Kinds);

    [Fact]
    public void WingCarrierStillDetected() => Assert.Contains(ThreatKind.UavCarrier, P("Курская область. БПЛА Крыло, опасность").Kinds);

    [Fact]
    public void LerpTakesShortArcAcrossAntimeridian()
    {
        var m = Geo.Lerp(new GeoPoint(65, 179), new GeoPoint(65, -179), 0.5);
        Assert.True(Math.Abs(Math.Abs(m.Lon) - 180) < 1e-9, $"{m.Lon}");
    }

    [Fact]
    public void LerpOrdinarySegmentUnchanged() => Assert.Equal(40, Geo.Lerp(new GeoPoint(50, 30), new GeoPoint(50, 50), 0.5).Lon, 9);

    [Theory]
    [InlineData(new double[] { 10, 60 }, 35)]
    [InlineData(new double[] { 5, 5, 5, 120 }, 5)]
    [InlineData(new double[] { 5, 120 }, 62.5)]
    [InlineData(new double[] { 7 }, 7)]
    [InlineData(new double[] { }, 0)]
    public void MedianOfEvenSampleIsAverageOfMiddle(double[] sorted, double expected) => Assert.Equal(expected, StatsSummary.Median(sorted), 9);

    // Точная проверка: чётно-нечётное правило по каждому объекту, объединение объектов
    private static bool Exact((double X, double Y)[][][] feats, double x, double y)
    {
        foreach (var f in feats)
        {
            bool inside = false;
            foreach (var ring in f)
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    var (ax, ay) = ring[i]; var (bx, by) = ring[j];
                    if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
                }
            if (inside) return true;
        }
        return false;
    }

    [Fact]
    public void RegionMaskAgreesWithExactCheck()
    {
        // два объекта: квадрат с дыркой и соседний треугольник; точки на разных расстояниях от границ
        (double, double)[][] sq = [[(0, 0), (10, 0), (10, 10), (0, 10)], [(3, 3), (3, 7), (7, 7), (7, 3)]];
        (double, double)[][] tri = [[(10, 0), (20, 0), (15, 9)]];
        var feats = new[] { sq, tri };
        var mask = RegionMask.Build(feats, 0.25);
        var rnd = new Random(5);
        int decided = 0, border = 0;
        for (int n = 0; n < 20000; n++)
        {
            double x = rnd.NextDouble() * 24 - 2, y = rnd.NextDouble() * 14 - 2;
            var q = mask.Query(x, y);
            if (q == RegionMask.Border) { border++; continue; }
            decided++;
            Assert.Equal(Exact(feats, x, y), q == RegionMask.Inside);
        }
        Assert.True(decided > border, $"решено {decided}, у границы {border}");
    }

    [Fact]
    public void EmptyRegionMaskIsOutside() => Assert.Equal(RegionMask.Outside, RegionMask.Build([], 0.1).Query(1, 1));

    [Fact]
    public void LegacyDataDirIsCopiedOnceAndLeftIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "radar-mig-" + Guid.NewGuid().ToString("N"));
        var oldDir = Path.Combine(root, "RadarRF"); var newDir = Path.Combine(root, "Radar");
        Directory.CreateDirectory(Path.Combine(oldDir, "sounds"));
        File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(oldDir, "sounds", "x.wav"), "w");
        try
        {
            AppPaths.MigrateLegacy(oldDir, newDir);
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(newDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "sounds", "x.wav")));
            Assert.True(File.Exists(Path.Combine(oldDir, "settings.json")), "старый каталог не тронут");
            // новый каталог уже есть — повторный перенос ничего не перезаписывает
            File.WriteAllText(Path.Combine(newDir, "settings.json"), "{\"b\":2}");
            AppPaths.MigrateLegacy(oldDir, newDir);
            Assert.Equal("{\"b\":2}", File.ReadAllText(Path.Combine(newDir, "settings.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingLegacyDirDoesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "radar-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AppPaths.MigrateLegacy(Path.Combine(root, "RadarRF"), Path.Combine(root, "Radar"));
            Assert.False(Directory.Exists(Path.Combine(root, "Radar")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static IReadOnlyList<(double X, double Y)> Arc(double cx, double cy, double r, double a0, double a1, int n)
        => [.. Enumerable.Range(0, n + 1).Select(i => (cx + r * Math.Cos(a0 + (a1 - a0) * i / n), cy + r * Math.Sin(a0 + (a1 - a0) * i / n)))];

    private static double DistToPolyline(IReadOnlyList<(double X, double Y)> line, (double X, double Y) p)
    {
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            var (ax, ay) = line[i - 1]; var (bx, by) = line[i];
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / l2, 0, 1);
            best = Math.Min(best, Math.Sqrt((ax + dx * t - p.X) * (ax + dx * t - p.X) + (ay + dy * t - p.Y) * (ay + dy * t - p.Y)));
        }
        return best;
    }

    [Fact]
    public void FrontLabelFollowsCurveKeepsGapAndReadsLeftToRight()
    {
        var run = Arc(400, 600, 300, -0.2, -2.9, 90);
        var spot = FrontLabel.Pick([run], 120, (0, 0, 800, 600), 10);
        Assert.NotNull(spot);
        Assert.True(spot![^1].X > spot[0].X, "текст не должен идти справа налево");
        // базовая линия повторяет изгиб: все её точки на одном расстоянии от линии (концентрическая дуга), без пересечений
        var d = spot.Select(p => DistToPolyline(run, p)).ToList();
        Assert.True(d.Min() > 4, $"надпись слишком близко к линии: {d.Min():0.0}");
        Assert.True(d.Max() - d.Min() < 3, $"зазор не должен гулять: {d.Min():0.0}–{d.Max():0.0}");
    }

    [Fact]
    public void FrontLabelNeverTouchesJaggedLine()
    {
        // зубчатая линия вдоль плавной дуги: надпись не должна пересекать исходные зубцы
        var rnd = new Random(3);
        var jag = Arc(500, 700, 400, -0.1, -3.0, 220).Select(p => (X: p.X + (rnd.NextDouble() - 0.5) * 9, Y: p.Y + (rnd.NextDouble() - 0.5) * 9)).ToList();
        var spot = FrontLabel.Pick([jag], 130, (0, 0, 1000, 700), 10);
        Assert.NotNull(spot);
        foreach (var p in spot!) Assert.True(DistToPolyline(jag, p) > 3, $"точка надписи на линии: {DistToPolyline(jag, p):0.0}");
    }

    [Fact]
    public void FrontLabelSkipsTightLoop()
    {
        var loop = Arc(100, 100, 12, 0, 6.0, 80);
        Assert.Null(FrontLabel.Pick([loop], 120, (0, 0, 300, 300), 10));
    }

    [Fact]
    public void FrontLabelStaysInsideAreaAndOutOfBlocked()
    {
        var line = new List<(double X, double Y)> { (0, 100), (1000, 100) };
        var spot = FrontLabel.Pick([line], 100, (200, 0, 600, 200), 10, (x, y) => x > 300 && x < 520);
        Assert.NotNull(spot);
        Assert.All(spot!, p => { Assert.InRange(p.X, 200, 600); Assert.False(p.X > 300 && p.X < 520); });
    }

    [Fact]
    public void FrontLabelTooShortLineGivesNothing() =>
        Assert.Null(FrontLabel.Pick([new List<(double X, double Y)> { (0, 0), (40, 0) }], 120, (0, -10, 500, 10), 10));

    [Theory]
    [InlineData(ThreatKind.Mlrs, ThreatType.Artillery, 70)]
    [InlineData(ThreatKind.Artillery, ThreatType.Artillery, 40)]
    [InlineData(ThreatKind.UavFpv, ThreatType.Uav, 25)]
    [InlineData(ThreatKind.GuidedBomb, ThreatType.Aviation, 90)]
    [InlineData(ThreatKind.MissileBallistic, ThreatType.Missile, 300)]
    [InlineData(ThreatKind.MissileCruise, ThreatType.Missile, 560)]
    public void LaunchRangeByWeapon(ThreatKind kind, ThreatType group, double km) => Assert.Equal(km, LaunchRange.MaxKm(kind, group));

    [Fact]
    public void MlrsCannotReachDeepRegions()
    {
        // РСЗО достаёт на десятки километров — заведомо не до Краснодарского края (≈ 500 км от Краматорска)
        Assert.True(LaunchRange.MaxKm(ThreatKind.Mlrs, ThreatType.Artillery) < 100);
        Assert.True(LaunchRange.MaxKm(ThreatKind.Mlrs, ThreatType.Artillery) < LaunchRange.MaxKm(ThreatKind.MissileCruise, ThreatType.Missile));
    }

    [Fact]
    public void UnknownKindFallsBackToGroup()
    {
        Assert.Equal(LaunchRange.GroupKm(ThreatType.Missile), LaunchRange.MaxKm(ThreatKind.Unknown, ThreatType.Missile));
        Assert.Equal(LaunchRange.UnknownKm, LaunchRange.MaxKm(ThreatKind.Unknown, ThreatType.General));
    }

    [Fact]
    public void SeveralNamedKindsTakeTheLongest() =>
        Assert.Equal(560, LaunchRange.MaxKm(ThreatKind.Mlrs, [ThreatKind.Mlrs, ThreatKind.MissileCruise], ThreatType.Missile));

    [Fact]
    public void MlrsLaunchEventUsesShortRange()
    {
        var m = P("Краматорск, пуск РСЗО");
        Assert.Contains(ThreatKind.Mlrs, m.Kinds);
        Assert.True(LaunchRange.MaxKm(m.Kind, m.Kinds, m.Threat) < 100);
    }

    [Fact]
    public void LaunchTargetIsOnAllowedSideNotNearestContourPoint()
    {
        // контур региона — квадрат 0..10 по широте и долготе; «своя» сторона — восточная половина (lon >= 6)
        GeoPoint[] ring = [new(0, 0), new(0, 10), new(10, 10), new(10, 0)];
        var origin = new GeoPoint(5, 2); // у западного края: ближайшая точка контура — на западе (чужая сторона)
        var hit = LaunchTargets.Nearest(origin, [ring], [], p => p.Lon >= 6, 20);
        Assert.NotNull(hit);
        Assert.True(hit!.Value.Pos.Lon >= 6, $"цель на чужой стороне: {hit.Value.Pos}");
        // цель — именно ближайшая из допустимых: на границе допустимой половины, а не на дальнем краю
        Assert.InRange(hit.Value.Pos.Lon, 6, 6.6);
    }

    [Fact]
    public void LaunchTargetUsesFrontLinePointsInsideRegion()
    {
        GeoPoint[] ring = [new(0, 0), new(0, 10), new(10, 10), new(10, 0)];
        var front = LaunchTargets.Sample([new GeoPoint(2, 6), new GeoPoint(8, 6)]).ToList();
        var hit = LaunchTargets.Nearest(new GeoPoint(5, 3), [ring], front, p => p.Lon >= 6, 50);
        Assert.NotNull(hit);
        Assert.Equal(6, hit!.Value.Pos.Lon, 6);
    }

    [Fact]
    public void LaunchTargetNoneWhenNothingAllowed() =>
        Assert.Null(LaunchTargets.Nearest(new GeoPoint(5, 5), [new GeoPoint[] { new(0, 0), new(0, 10), new(10, 10) }], [], _ => false));

    [Fact]
    public void LaunchFanSkipsRegionsAcrossTheCountry()
    {
        // пуск БПЛА из Одессы: ближайшие регионы РФ по секторам (км до контура) — Херсонская рядом, Брянская и Белгородская через всю Украину
        (string Name, double Km)[] sectors = [("Херсонская", 71), ("Запорожская", 278), ("Белгородская", 562), ("Брянская", 640)];
        var shown = LaunchTargets.WithinReach(sectors, s => s.Km).Select(s => s.Name).ToList();
        Assert.Equal(["Херсонская"], shown);
        // пуск из Сум: Курская и Белгородская обе рядом — веер на обе
        Assert.Equal(2, LaunchTargets.WithinReach([("Курская", 30.0), ("Белгородская", 34.0), ("Липецкая", 238.0)], s => s.Item2).Count());
        Assert.Empty(LaunchTargets.WithinReach(Array.Empty<(string, double)>(), s => s.Item2));
    }

    [Theory]
    [InlineData(150, 130, 0.9, 3.4)]   // круто изогнутая дуга: внутренняя сторона заметно короче
    [InlineData(300, 130, 0.5, 2.6)]
    [InlineData(80, 100, 0.6, 3.6)]
    public void FrontLabelBaselineAlwaysFitsTheText(double radius, double textLen, double a0, double a1)
    {
        var run = Arc(500, 800, radius * 2, -a0, -a0 - a1, 160);
        var spot = FrontLabel.Pick([run], textLen, (0, 0, 1000, 900), 10);
        // на любой стороне изгиба базовая линия обязана быть не короче текста, иначе рисование по пути обрежет начало надписи
        if (spot != null) Assert.True(FrontLabel.Length(spot) >= textLen - FrontLabel.Slack, $"путь {FrontLabel.Length(spot):0.0} короче текста {textLen}");
    }

    [Fact]
    public void FrontLabelOnInnerSideOfTightBendStillFits()
    {
        // дуга, у которой внутри изгиба место под текст есть только если путь не сократился: проверяем обе стороны
        var run = Arc(500, 400, 180, 0.2, 2.9, 120);
        for (int i = 0; i < 3; i++)
        {
            var spot = FrontLabel.Pick([run], 130 + 10 * i, (0, 0, 1000, 800), 10);
            if (spot != null) Assert.True(FrontLabel.Length(spot) >= 130 + 10 * i - FrontLabel.Slack);
        }
    }

    [Fact]
    public void AutostartDesktopEntryQuotesPathsWithSpaces()
    {
        Assert.Equal("/opt/radar/Radar --autostart", Autostart.DesktopExec("/opt/radar/Radar", [Autostart.Flag]));
        Assert.Equal("\"/home/u/My Apps/Radar\" --autostart", Autostart.DesktopExec("/home/u/My Apps/Radar", [Autostart.Flag]));
        // процент в пути не должен стать кодом подстановки Exec
        Assert.Contains("%%", Autostart.DesktopExec("/a b/100%/Radar", []));
        var file = Autostart.DesktopFile("/opt/radar/Radar", [Autostart.Flag]);
        Assert.StartsWith("[Desktop Entry]\nType=Application\n", file);
        Assert.Contains("Exec=/opt/radar/Radar --autostart\n", file);
    }

    [Fact]
    public void AutostartPlistEscapesXml()
    {
        var plist = Autostart.LaunchAgentPlist("/Apps/R&D <x>/Radar", [Autostart.Flag]);
        Assert.Contains("/Apps/R&amp;D &lt;x&gt;/Radar", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
    }

    [Fact]
    public void TraySettingsRoundTrip()
    {
        var s = new AppSettings { CloseToTray = true, StartWithSystem = true, StartMinimized = false };
        var json = System.Text.Json.JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.True(back.CloseToTray && back.StartWithSystem && !back.StartMinimized);
        // старый файл настроек без новых полей: трей выключен, свёрнутый старт включён
        var old = System.Text.Json.JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.AppSettings)!;
        Assert.False(old.CloseToTray); Assert.False(old.StartWithSystem); Assert.True(old.StartMinimized);
    }
}
