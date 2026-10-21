using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Отбой по «части области» снимает только тревоги в этой части.</summary>
// движок использует общие статические часы (Clock) — тесты с ReplaySync не должны идти параллельно
[Collection("Engine")]
public class AllClearPartTests
{
    private static List<AlertEvent> Run(params string[] texts) => RunCh(texts.Select(t => ("test", t)).ToArray());

    private static List<AlertEvent> RunCh(params (string Ch, string Text)[] items)
    {
        var texts = items.Select(i => i.Text).ToArray();
        var settings = new AppSettings { Channels = [new ChannelConfig { Name = "test" }, new ChannelConfig { Name = "other" }] };
        using var http = new HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var t0 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var msgs = items.Select((t, i) => new RawMessage(t.Ch, i + 1, t0.AddMinutes(i), t.Text.Replace(" | ", "\n"))).ToList();
        foreach (var _ in engine.ReplaySync(msgs)) { }
        return engine.Snapshot().Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > t0.AddMinutes(texts.Length)).ToList();
    }

    [Fact]
    public void ClearsOnlyThatPart()
    {
        var active = Run(
            "Таганрог | Ростовская область | Опасность по БПЛА",
            "Миллерово | Ростовская область | Опасность по БПЛА",
            "Отбой опасности по БПЛА в южной части Ростовской области");
        Assert.DoesNotContain(active, e => e.Points.Any(p => p.Label == "Таганрог"));
        Assert.Contains(active, e => e.Points.Any(p => p.Label == "Миллерово"));
    }

    [Fact]
    public void ClearsPartAlertOfSameSide()
    {
        var active = Run(
            "Южная часть Ростовской области | Тревога по БПЛА",
            "Северная часть Ростовской области | Тревога по БПЛА",
            "Южная часть Ростовской области | Отбой опасности по БПЛА");
        Assert.DoesNotContain(active, e => e.Points.Any(p => p.Label.StartsWith("Юг")));
        Assert.Contains(active, e => e.Points.Any(p => p.Label.StartsWith("Север")));
    }

    [Fact]
    public void PartClearKeepsWholeRegionAlert()
    {
        var active = Run(
            "Ростовская область | Опасность по БПЛА",
            "Отбой опасности по БПЛА в южной части Ростовской области");
        Assert.Contains(active, e => e.RegionIds.Contains("rostov"));
    }

    [Fact]
    public void TargetRegionIsExpectedNotAlerted()
    {
        var active = Run("Новокардаильский, Новониколаевский район, Волгоградская область - массовый пролёт БПЛА в сторону Саратовской области.");
        var ev = Assert.Single(active);
        Assert.Contains("saratov", ev.ExpectedRegionIds);
        Assert.DoesNotContain("saratov", ev.WholeRegionIds);
        Assert.DoesNotContain("saratov", ev.RegionIds);
    }

    [Fact]
    public void ClearInOneRegionNarrowsMultiRegionAlert()
    {
        var active = Run(
            "Белгородская область, Курская область, Воронежская область | Ракетная опасность",
            "Белгородская область | Отбой ракетной опасности");
        var ev = Assert.Single(active);
        Assert.DoesNotContain("belgorod", ev.RegionIds);
        Assert.Contains("kursk", ev.RegionIds);
        Assert.Contains("voronezh", ev.RegionIds);
        Assert.True(ev.HasPartialClear);
        Assert.Contains("Белгородская", ev.PartialClearText);
    }

    [Fact]
    public void MissileClearRemovesAviationMissileAlert()
    {
        // «Авиационная ракетно-бомбовая опасность» → «Отбой ракетной опасности» — одно и то же
        Assert.Empty(Run("Курская область и близлежащие | Авиационная ракетно бомбовая опасность", "Отбой ракетной опасности Курская область"));
    }

    [Fact]
    public void FederalCityClearIsRegionWide()
    {
        Assert.Empty(Run("Фиолент, Балаклава | Севастополь | Опасность по БПЛА", "Севастополь | Отбой опасности по БПЛА"));
    }

    [Fact]
    public void FederalCityClearKeepsFarPointsOfOtherRegion()
    {
        // «Севастополь, ЮБК — отбой»: тревога «Угловое» (Бахчисарайский р-н, Крым) с Севастополем остаётся по Угловому
        var active = Run("Угловое | Севастополь | Опасность по БПЛА", "Севастополь | ЮБК и близлежащие | Отбой беспилотной опасности");
        Assert.Contains(active, e => e.Points.Any(p => p.Label == "Угловое"));
        // «Крым и Севастополь — отбой» (весь Крым, без пунктов) снимает её полностью
        Assert.Empty(Run("Угловое | Севастополь | Опасность по БПЛА", "Крым и Севастополь | Отбой опасности по БПЛА"));
    }

    [Fact]
    public void NarrowedAlertMergesIntoWiderOne()
    {
        // «Орловская область» после отбоя по Московской — внутри тревоги «Тульская, Орловская области»: одна тревога, не две
        var active = RunCh(
            ("test", "Московская область | Орловская область | Опасность по БПЛА"),
            ("other", "Тульская область | Орловская область | Работа ПВО по БПЛА"),
            ("test", "Московская область | Отбой опасности по БПЛА"));
        var orel = active.Where(e => e.RegionIds.Contains("orel")).ToList();
        Assert.Single(orel);
        Assert.Contains("tula", orel[0].RegionIds);
    }

    [Theory]
    [InlineData("Воронеж | Воронежская область | Опасность по реактивным БПЛА",
        "Воронежская область - отбой опасности по реактивным БПЛА. Опасность по БПЛА сохраняется. | Все зафиксированые ранее реактивные БПЛА ушли на Котовск. Часть была сбита.")]
    [InlineData("Белгород | Белгородская область | Ракетная опасность", "Белгородская область - ракеты сбиты, летели на Шебекино, отбой ракетной опасности!")]
    [InlineData("Белгород | Белгородская область | Ракетная опасность", "Отбой ракетной опасности Белгородская область | Были на Волчанск")]
    public void ClearWithWhereaboutsIsRegionWide(string alert, string clear)
    {
        // «ушли на Котовск», «летели на Шебекино» — рассказ о целях, а не место отбоя: отбой по всему региону
        Assert.Empty(Run(alert, clear));
    }

    [Theory]
    [InlineData("LPRalarm", "Весь Юг | Ракетная опасность", true)]
    [InlineData("LPRalarm", "Отбой ракетной опасности весь Юг", true)]
    [InlineData("radarrussiia", "Отбой юг по авиации", true)]
    [InlineData("LPRalarm", "Юг | Внимание по авиации", true)]
    [InlineData("LPRalarm", "Весь юг!", true)]
    [InlineData("radarrussiia", "Юг | Московская область | Опасность по БПЛА", false)]
    [InlineData("LPRalarm", "Юг | ЛНР | Тревога по БПЛА", false)]
    [InlineData("LPRalarm", "Ракета летит на юг", false)]
    public void SouthMacroRegion(string ch, string text, bool south)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), []);
        Assert.Equal(south, m.Regions.Contains("crimea") && m.Regions.Contains("rostov") && m.Regions.Contains("krasnodar"));
        // юг канала с собственным регионом — юг этого региона, а не макрорегион
        var own = MessageParser.Parse(new RawMessage("lpr1_Rostov_alarm", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), ["rostov"]);
        Assert.DoesNotContain("crimea", own.Regions);
    }

    [Theory]
    [InlineData("LPRalarm", "Приазовье | Ракетная опасность", new string[0], "zaporozhye,dnr,rostov,krasnodar")]
    [InlineData("LPRalarm", "Запорожская область РФ | Приазовье | Ракетная опасность", new string[0], "zaporozhye")]
    [InlineData("lpr1_Rostov_alarm", "Приазовье | Опасность по БПЛА", new[] { "rostov" }, "rostov")]
    public void AzovCoastIsStripAlongShore(string ch, string text, string[] chRegions, string regions)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), chRegions);
        var coast = m.Places.Where(p => p.Label == "Приазовье").ToList();
        Assert.NotEmpty(coast);
        Assert.Equal(regions.Split(',').Order(), coast.Select(p => p.RegionId!).Distinct().Order());
        Assert.All(coast, p => Assert.NotNull(CoastZones.AzovAt(p.Pos!.Value)));
    }

    [Theory]
    [InlineData("LPRalarm", "Причерноморье | Ракетная опасность", new string[0], true)]
    [InlineData("radarrussiia", "Причерноморье КК | Опасность по БПЛА", new string[0], true)]
    [InlineData("LPRalarm", "Херсонская область РФ | Причерноморье | Тревога по БПЛА", new string[0], false)]
    [InlineData("lpr1_Crimea_Alarm", "Причерноморье | Опасность по БПЛА", new[] { "crimea", "sevastopol" }, false)]
    public void BlackSeaCoastIsKuban(string ch, string text, string[] chRegions, bool kuban)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), chRegions);
        Assert.Equal(kuban, m.Places.Any(p => p.Label == "Черноморское побережье Кубани"));
    }

    [Fact]
    public void AzovClearInOneRegionKeepsRestOfCoast()
    {
        var active = Run("Приазовье | Ракетная опасность", "Отбой ракетной опасности | Ростовская область");
        var e = Assert.Single(active);
        Assert.DoesNotContain("rostov", e.RegionIds);
        Assert.Contains("zaporozhye", e.RegionIds);
        Assert.All(e.Points.Where(p => !p.IsRegionLevel), p => Assert.NotNull(p.Corridor));
        Assert.Empty(Run("Приазовье | Ракетная опасность", "Отбой ракетной опасности Приазовье"));
    }

    [Theory]
    [InlineData("Шебекино | Белгородская область | Ракетная опасность", "РСЧС: Внимание! Шебекинский городской округ. Отбой ракетной опасности.")]
    [InlineData("Суджанский район | Курская область | Опасность по БПЛА", "Отбой опасности по БПЛА Суджа | Курская область")]
    public void CityAndItsDistrictAreSamePlace(string alert, string clear)
    {
        Assert.Empty(Run(alert, clear));
    }

    [Fact]
    public void AircraftWordIsNotPlace()
    {
        // деревня Самолёт есть в Брянской области — «Самолёт отработал по ЛБС» её не касается
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, "Самолёт отработал по ЛБС Курской области\nОтбой тревоги в Белгородской и Курской областях."), []);
        Assert.DoesNotContain(m.Places, p => p.Label == "Самолет");
        Assert.DoesNotContain("bryansk", m.Regions);
    }

    [Fact]
    public void StatusBlocksInOneMessage()
    {
        // nabludatel_rf: «Азов, Таганрог … Ейск ⏎ Опасность по БПЛА ⏎ Курская … Ярославская область ⏎ отбой опасности по БПЛА»
        var active = RunCh(
            ("other", "Ейск | Краснодарский край | Опасность по БПЛА"),
            ("other", "Курская область | Опасность по БПЛА"),
            ("test", "Азов | Таганрог | Ростовская область | Ейск | Краснодарский край | Опасность по БПЛА | Курская область | Орловская область | отбой опасности по БПЛА"));
        Assert.Contains(active, e => e.Points.Any(p => p.Label == "Ейск"));
        Assert.DoesNotContain(active, e => e.RegionIds.Contains("kursk"));
        // обычное сообщение «Отбой ракетной опасности ⏎ Угроза по БПЛА остаётся» блоками не делится
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, "Воронежская область\nОтбой ракетной опасности.\nУгроза по БПЛА остаётся"), []);
        Assert.Empty(m.Blocks);
    }

    [Fact]
    public void RegionPartIsOneZoneWithRefinementsOnTop()
    {
        // «Южная часть Ростовской области» — одна точка-зона части (а не по точке на каждый район),
        // уточнение по Азовскому району — отдельная тревога поверх; отбой части снимает зону
        var active = Run("Южная часть Ростовской области | Опасность по БПЛА", "Азовский район | Ростовская область | Тревога по БПЛА");
        var part = Assert.Single(active, e => e.Points.Any(p => p.PartRegion == "rostov"));
        Assert.Single(part.Points, p => !p.IsRegionLevel);
        Assert.Contains(active, e => e.Points.Any(p => p.Label == "Азовский район"));
        Assert.Empty(Run("Южная часть Ростовской области | Опасность по БПЛА", "Отбой опасности по БПЛА в южной части Ростовской области"));
    }

    [Fact]
    public void CityAndRegionClearIsRegionWide()
    {
        // «Белгород и область — отбой» снимает и тревоги по Шебекино, а не только по Белгороду
        Assert.Empty(Run("Шебекино | Белгородская область | Ракетная опасность", "Белгород и область - отбой ракетной опасности"));
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, "Курск и район - отбой ракетной опасности"), []);
        Assert.Contains(m.Places, p => p.Label == "Курский район");
    }

    [Fact]
    public void SouthAlertClearedBySouthClear()
    {
        Assert.Empty(RunCh(("test", "Весь Юг | Ракетная опасность"), ("test", "Отбой ракетной опасности весь Юг")));
    }

    [Theory]
    [InlineData("Сочи | Отбой не даём! | Фиксация в море | Передвигается от Абхазии в сторону Сочи и обратно | Отбой дадим от служб!")]
    [InlineData("Направление на Макеевку, угроза сохраняется! | Как фиксация пусков прекратится, дадим отбой!")]
    [InlineData("Ожидайте отбой по РСЧС. Ракеты были уничтожены и подавлены.")]
    public void PromisedClearIsNotClear(string text)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), []);
        Assert.NotEqual(AlertStatus.AllClear, m.Status);
    }

    [Fact]
    public void DistrictNameVariantsMatch()
    {
        Assert.Empty(Run("Чертковский район | Ростовская область | Тревога по БПЛА", "Чертковский | Ростовская область | Отбой опасности по БПЛА"));
    }

    [Fact]
    public void ClearOfOtherTypeKeepsAlert()
    {
        Assert.Single(Run("Курская область | Опасность по БПЛА", "Курская область | Отбой ракетной опасности"));
    }

    [Fact]
    public void ClearOfOnePlaceKeepsOthers()
    {
        var active = Run(
            "Шебекино, Грайворон, Валуйки | Белгородская область | Опасность по БПЛА",
            "Шебекино | Белгородская область | Отбой опасности по БПЛА");
        var ev = Assert.Single(active);
        Assert.DoesNotContain(ev.Points, p => p.Label == "Шебекино");
        Assert.Contains(ev.Points, p => p.Label == "Грайворон");
        Assert.True(ev.HasPartialClear);
        Assert.Contains("Шебекино", ev.PartialClearText);
        Assert.Contains("Валуйки", ev.PartialClearText);
    }

    [Fact]
    public void ClearOfAllPlacesEndsAlert()
    {
        Assert.Empty(Run(
            "Шебекино, Грайворон | Белгородская область | Опасность по БПЛА",
            "Шебекино | Белгородская область | Отбой опасности по БПЛА",
            "Грайворон | Белгородская область | Отбой опасности по БПЛА"));
    }

    [Fact]
    public void RepeatAfterPartialClearRestoresAlert()
    {
        var active = Run(
            "Шебекино, Грайворон | Белгородская область | Опасность по БПЛА",
            "Шебекино | Белгородская область | Отбой опасности по БПЛА",
            "Шебекино, Грайворон | Белгородская область | Опасность по БПЛА");
        var ev = Assert.Single(active);
        Assert.Contains(ev.Points, p => p.Label == "Шебекино");
        Assert.False(ev.HasPartialClear);
    }

    [Fact]
    public void BareClearEndsLastAnnouncementOfSameChannel()
    {
        var active = RunCh(
            ("other", "Курская область | Опасность по БПЛА"),
            ("test", "Брянская область | Опасность по БПЛА"),
            ("test", "Псковская область | Опасность по БПЛА"),
            ("test", "Тверская область | Опасность по БПЛА"),
            ("test", "Смоленская область | Опасность по БПЛА"),
            ("test", "Ростовская область | Ракетная опасность"),
            ("test", "Отбой"));
        Assert.DoesNotContain(active, e => e.RegionIds.Contains("rostov"));
        Assert.Contains(active, e => e.RegionIds.Contains("bryansk")); // более раннее объявление — не «последнее»
        Assert.Contains(active, e => e.RegionIds.Contains("kursk"));   // чужой канал не трогаем
    }

    [Fact]
    public void BareTypedClearMatchesType()
    {
        var active = RunCh(
            ("test", "Ростовская область | Ракетная опасность"),
            ("test", "Ростовская область | Опасность по БПЛА"),
            ("test", "Отбой ракетной опасности"));
        Assert.Single(active);
        Assert.Equal(ThreatType.Uav, active[0].Threat);
    }
}
