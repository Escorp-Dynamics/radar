using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>«ЛДНР» — обе республики; сообщения о пусках — отдельная категория.</summary>
// движок использует общие статические часы (Clock) — тесты с ReplaySync не должны идти параллельно
[Collection("Engine")]
public class LdnrAndLaunchTests
{
    private static ParsedMessage P(string text, params string[] channelRegions) =>
        MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), channelRegions);

    [Theory]
    [InlineData("ЛДНР | Тревога по БПЛА")]
    [InlineData("Тревога по БПЛА в ЛДНР")]
    [InlineData("ЛДНР тревога по БПЛА в направлении Ростовской области")]
    [InlineData("Воронежская область ожидаем массовый прорыв от ЛНР и Белгородской области. | ЛДНР ожидаем пролёт БПЛА")]
    public void LdnrMeansBothRepublics(string text)
    {
        var p = P(text);
        Assert.Contains("lnr", p.Regions);
        Assert.Contains("dnr", p.Regions);
    }

    [Fact]
    public void LdnrAsTargetOfEnemyLaunchKeepsBothRepublics()
    {
        var p = P("Следующая волна БПЛА от Краматорск, Славянск в направлении ЛДНР. Много");
        Assert.Contains(p.Places, x => x.Label == "ЛНР" && x.IsTarget);
        Assert.Contains(p.Places, x => x.Label == "ДНР" && x.IsTarget);
    }

    [Theory]
    [InlineData("Гуково | Донецк Ростовский | Каменск Шахтинский | И близлежащие | Тревога по БПЛА | От ЛДНР")]
    [InlineData("Донецк Ростовской области, Каменск-Шахтинский и близлежащие опасность по БПЛА от ЛДНР")]
    public void DonetskRostovIsNotDonetskDnr(string text)
    {
        var p = P(text);
        var d = Assert.Single(p.Places, x => x.Label.StartsWith("Донецк", StringComparison.Ordinal));
        Assert.Equal("rostov", d.RegionId);
        Assert.DoesNotContain("dnr", p.Regions);
    }

    [Theory]
    [InlineData("Пуски Уаб от Изюма", AlertLevel.Alarm)]
    [InlineData("Чугуев ещё выходы", AlertLevel.Alarm)]
    [InlineData("Пуски БПЛА от Одесской области", AlertLevel.Alarm)]
    [InlineData("Противник готовит запуски БПЛА от Черниговской области", AlertLevel.Attention)]
    [InlineData("Николаевская область возможно пуски Штормов", AlertLevel.Attention)]
    public void LaunchMessagesAreLaunches(string text, AlertLevel level)
    {
        var p = P(text);
        Assert.Equal(AlertStatus.Launch, p.Status);
        Assert.Equal(level, p.Level);
    }

    [Theory]
    // в РФ названо место тревоги — это тревога, пуск лишь указывает источник
    [InlineData("Массовый запуск БПЛА от Черниговской области в сторону Брянской области | Брянская область тревога по БПЛА")]
    [InlineData("Ростовская область тревога по БПЛА от ЛДНР")]
    // нет места пуска — предупреждение для региона канала
    [InlineData("Возможно подготовка к пускам Штормов")]
    // пункт у самой линии соприкосновения — свой, тревога безопаснее
    [InlineData("Орехов пуски Уаб | Запорожская область РФ тревога")]
    public void AlertsWithLaunchWordsStayAlerts(string text)
    {
        var p = P(text, "bryansk");
        Assert.NotEqual(AlertStatus.Launch, p.Status);
    }

    [Fact]
    public void EnemySideByFrontLine()
    {
        Assert.True(Borderline.IsEnemySide(new GeoPoint(48.72, 37.56), "dnr"));   // Краматорск
        Assert.False(Borderline.IsEnemySide(new GeoPoint(48.015, 37.80), "dnr")); // Донецк
        Assert.True(Borderline.IsEnemySide(new GeoPoint(49.21, 37.26), "kharkov")); // Изюм
        Assert.False(Borderline.IsEnemySide(new GeoPoint(46.84, 35.37), "zaporozhye")); // Мелитополь
        Assert.True(Borderline.IsEnemySide(new GeoPoint(47.84, 35.14), "zaporozhye"));  // Запорожье
        Assert.False(Borderline.IsEnemySide(new GeoPoint(50.6, 36.6), "belgorod"));
    }

    private static (List<AlertEvent> Events, IReadOnlyList<Track> Tracks) Run(string text)
    {
        var settings = new AppSettings { Channels = [new ChannelConfig { Name = "test" }] };
        using var http = new HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in engine.ReplaySync([new RawMessage("test", 1, DateTimeOffset.UtcNow.AddMinutes(-2), text)])) { }
        return ([.. engine.Snapshot()], engine.TrackSnapshot());
    }

    [Fact]
    public void LaunchEventDoesNotFillRussianRegionsOrTrack()
    {
        var (events, tracks) = Run("Пуски Уаб от Изюма");
        var e = Assert.Single(events);
        Assert.Equal(AlertStatus.Launch, e.Status);
        Assert.Empty(e.WholeRegionIds);
        Assert.Empty(e.ExpectedRegionIds);
        Assert.NotNull(e.Origin);
        Assert.Empty(tracks);
    }

    [Fact]
    public void LaunchTowardsRussianRegionRaisesAlertThereAndMarksSite()
    {
        // безопасность: житель Белгородской области должен увидеть тревогу, а не только отметку пуска у Изюма
        var (events, _) = Run("Пуски Уаб от Изюма в направлении Белгородской области");
        var launch = Assert.Single(events, x => x.Status == AlertStatus.Launch);
        Assert.Equal("Изюм", launch.Points[0].Label);
        Assert.NotNull(launch.Target);
        Assert.Contains(events, x => x.Status == AlertStatus.Alert && (x.RegionIds.Contains("belgorod") || x.ExpectedRegionIds.Contains("belgorod")));
    }
}

/// <summary>Сибирские республики: регион, пункты, «Горный Алтай» ≠ Алтайский край.</summary>
public class SiberiaRepublicsTests
{
    private static ParsedMessage P(string text) =>
        MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), []);

    [Theory]
    [InlineData("Абакан, Черногорск | Хакасия | Опасность по БПЛА", "khakassia", "Абакан")]
    [InlineData("Республика Тыва, Кызыл — тревога по БПЛА", "tuva", "Кызыл")]
    [InlineData("Горно-Алтайск | Республика Алтай | Внимание по БПЛА", "altai_rep", "Горно-Алтайск")]
    [InlineData("Алтайский край, Бийск — опасность по БПЛА", "altai_krai", "Бийск")]
    [InlineData("Бердск, Искитим | Новосибирская область | Опасность по БПЛА", "novosibirsk", "Бердск")]
    public void RegionAndPlace(string text, string region, string place)
    {
        var p = P(text);
        Assert.Equal([region], p.Regions);
        Assert.Contains(p.Places, x => x.Label == place && x.RegionId == region);
    }

    [Theory]
    [InlineData("Улан-Удэ | Бурятия | Опасность по БПЛА", "buryatia", "Улан-Удэ")]
    [InlineData("Приморский край, Владивосток, Находка — опасность по БПЛА", "primorye", "Владивосток")]
    [InlineData("Хабаровский край, Комсомольск-на-Амуре — тревога по БПЛА", "khabarovsk", "Комсомольск-на-Амуре")]
    [InlineData("Сахалинская область, Южно-Сахалинск | Внимание по БПЛА", "sakhalin", "Южно-Сахалинск")]
    [InlineData("Якутия, Мирный — опасность по БПЛА", "yakutia", "Мирный")]
    [InlineData("Камчатский край, Петропавловск-Камчатский — тревога по БПЛА", "kamchatka", "Петропавловск-Камчатский")]
    [InlineData("ЕАО, Биробиджан — опасность по БПЛА", "jao", "Биробиджан")]
    public void FarEast(string text, string region, string place) => RegionAndPlace(text, region, place);

    [Theory]
    // «Приморский» — пгт в Крыму; «Сахалин» — урочище в Белгородской обл.: не дальневосточные регионы
    [InlineData("Приморский | Береговое и близлежащие | Опасность по БПЛА | Республика Крым", "crimea")]
    [InlineData("Устинка машзавод северный Сахалин Крапивное укрытие ударный БПЛА | Шебекинский район", "belgorod")]
    // опечатка «Приморский край» в перечне районов Запорожской области
    [InlineData("Бердянский район | Приазовский район | Приморский край | Опасность по БПЛА | Запорожская область РФ", "zaporozhye")]
    public void AmbiguousFarEastNamesStayLocal(string text, string region)
    {
        var p = P(text);
        Assert.Contains(region, p.Regions);
        Assert.DoesNotContain("primorye", p.Regions);
        Assert.DoesNotContain("sakhalin", p.Regions);
    }

    [Fact]
    public void GornyAltaiIsRepublic()
    {
        var p = P("Горный Алтай опасность по БПЛА");
        Assert.Equal(["altai_rep"], p.Regions);
        Assert.Empty(p.Places);
    }
}
