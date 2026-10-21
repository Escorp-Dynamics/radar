using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Случаи из валидации полного архива каналов (artifacts/validation): сигналы, которые раньше терялись.</summary>
public class ArchiveFindingsTests
{
    private static ParsedMessage P(string text, params string[] channelRegions) =>
        MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), channelRegions);

    private static bool IsSignal(ParsedMessage p) =>
        !p.IsJunk && !p.IsInformational && (p.Threat != ThreatType.None || p.Status == AlertStatus.AllClear);

    [Theory]
    [InlineData("Зеленоград работа ПВО по БПЛА", "moscow")]
    [InlineData("Кубинка | Тревога по БПЛА", "moscow_obl")]
    [InlineData("Бронницы | Сбитие БПЛА", "moscow_obl")]
    [InlineData("Ахтубинск работа ПВО | По БПЛА", "astrakhan")]
    [InlineData("Приморско-Ахтарск опасность по БПЛА", "krasnodar")]
    public void RearCityWithoutRegion(string text, string region)
    {
        var p = P(text);
        Assert.True(IsSignal(p));
        Assert.Contains(p.Places, x => x.RegionId == region && x.Pos != null);
    }

    [Fact]
    public void SmallAmbiguousVillageStaysUnplaced()
    {
        // «Мариновка» есть в десятке регионов, ни одна не доминирует — не угадываем
        var p = P("Мариновка тревога по БПЛА");
        Assert.DoesNotContain(p.Places, x => x.Label == "Мариновка" && x.Pos != null);
    }

    [Theory]
    [InlineData("Карачаево-Черкесская Республика опасность по БПЛА", "kchr")]
    [InlineData("Кабардино-Балкарская Республика опасность по БПЛА", "kbr")]
    [InlineData("Удмурская Республика - опасность по БПЛА", "udmurtia")]
    public void RepublicAdjectiveForms(string text, string region) => Assert.Contains(region, P(text).Regions);

    [Theory]
    [InlineData("Саратовский район | Саратовская область | Опасность по БПЛА", "saratov", "Саратовский район")]
    [InlineData("Луховицкий район | Московская область | Опасность по БПЛА", "moscow_obl", "Луховицкий район")]
    [InlineData("Унеченский район | Брянская область | Опасность по БПЛА", "bryansk", "Унеченский район")]
    public void DistrictAroundCity(string text, string region, string label)
    {
        var p = P(text);
        Assert.Contains(p.Places, x => x.RegionId == region && x.Label == label && x.Pos != null && x.IsArea);
    }

    [Theory]
    [InlineData("Кировский район | Волгоград | Опасность по БПЛА", "volgograd", 48.607)]
    [InlineData("Тракторный район | Волгоградская область | Опасность по БПЛА", "volgograd", 48.837)]
    [InlineData("Заводской район | Саратов | Работа ПВО", "saratov", 51.469)]
    [InlineData("Заволжский район | Ярославль | Тревога по БПЛА", "yaroslavl", 57.663)]
    [InlineData("Левобережный район | Воронеж | Опасность по БПЛА", "voronezh", 51.580)]
    [InlineData("Чернышевский район | Волгоградская область | Опасность по БПЛА", "volgograd", 48.167)]
    [InlineData("Сеймский округ Курска, БПЛА со стороны Ворошнево в сторону центра", "kursk", 51.700)]
    [InlineData("Пролетарский район | Тула | Опасность по БПЛА", "tula", 54.235)]
    [InlineData("Измайловский район | Липецкая область | Опасность по БПЛА", "lipetsk", 52.636)]
    public void RearCityDistricts(string text, string region, double lat)
    {
        var p = P(text);
        Assert.True(IsSignal(p));
        Assert.Contains(p.Places, x => x.RegionId == region && x.Pos is { } pos && Math.Abs(pos.Lat - lat) < 0.01);
    }

    [Fact]
    public void CompoundDistrictNameIsNotSplit()
    {
        var p = P("Теньгушевский район | Зубово-Полянский район | Республика Мордовия | Опасность по БПЛА от Тамбовской области");
        Assert.Contains(p.Places, x => x.Label.StartsWith("Зубово-Полянский", StringComparison.Ordinal) && x.Pos != null);
        Assert.DoesNotContain(p.Places, x => x.Label == "Зубово");
    }

    [Theory]
    [InlineData("Горловка арта", ThreatType.Artillery)]
    [InlineData("Арта Кременная", ThreatType.Artillery)]
    [InlineData("Шебекино кассеты", ThreatType.Artillery)]
    [InlineData("Новая Каховка и близлежащие опасность по барражирующим боеприпасам", ThreatType.Uav)]
    public void ThreatSlang(string text, ThreatType threat)
    {
        var p = P(text, "dnr", "lnr", "belgorod", "kherson");
        Assert.True(IsSignal(p));
        Assert.Equal(threat, p.Threat);
    }

    [Theory]
    [InlineData("Балаклавская ТЭС, подстанции тревога по БПЛА от Инкерман | 5-й километр, Сапун Гора, Остряки | Фиксации БПЛА", "Остряки", "sevastopol")]
    [InlineData("Камыши, Фиолент, Казачья и далее в направлении Муссон тревога по БПЛА", "Камыши", "sevastopol")]
    [InlineData("Казачка | Севастополь | Внимание по ФПВ/БПЛА", "Казачка", "sevastopol")]
    [InlineData("Евпатория | Катушка | Уютное | Тревога по БПЛА | Повторно | Республика Крым", "Катушка", "crimea")]
    [InlineData("Клепино | Красногвардейское | Октябрьское и близлежащие | Тревога по БПЛА | Республика Крым", "Клепинино", "crimea")]
    [InlineData("Новофедорка | Тревога по реактивным БПЛА | Республика Крым", "Новофёдоровка", "crimea")]
    [InlineData("Горловка БПЛА Бессарабка, Изотова и близлежащие аккуратно", "Шахта Изотова", "dnr")]
    [InlineData("Горловка север кассеты, Гольма и близлежащие аккуратно", "Гольма", "dnr")]
    [InlineData("Урзуф | Ялта | Магшуш | Мариуполь | Сартана | Новоазовск | И близлежащие | Тревога по БПЛА | ДНР", "Мангуш", "dnr")]
    public void LocalToponyms(string text, string label, string region)
    {
        var p = P(text, region == "dnr" ? ["lnr", "dnr"] : region == "sevastopol" ? ["crimea", "sevastopol"] : [region]);
        Assert.True(IsSignal(p));
        Assert.Contains(p.Places, x => x.Label == label && x.RegionId == region && x.Pos != null);
    }

    [Fact]
    public void KazachkaElsewhereIsNotSevastopol()
    {
        var p = P("Казачка | Тревога по БПЛА", "kharkov");
        Assert.DoesNotContain(p.Places, x => x.RegionId == "sevastopol");
    }

    [Fact]
    public void TecSpellingIsZone()
    {
        var p = P("Таврическая ТЭЦ, Симфероподьский район внимание по бпла", "crimea", "sevastopol");
        Assert.Contains(p.Places, x => x.Label == "Таврическая ТЭС" && x.Pos != null);
    }

    [Theory]
    [InlineData("НЕ РЕКЛАМА! | Луганская Народная Республика | Идет набор на военную службу по контракту операторов БПЛА, РЭБ, ПВО, а также все виды воинских специальностей | Требования: | Полный социальный пакет")]
    [InlineData("⚡️ КОНТРАКТ НА 1 ГОД: ОПЕРАТОР БПЛА (БЕЗ перевода в пехоту)⚡️ | Новое подразделение БПЛА открывает набор.")]
    [InlineData("В связи с расширением штатов объявляем набор сотрудников в спецподразделение ОМОН | г. Севастополь | Денежное довольствие от 80 000")]
    [InlineData("Европарламент принял резолюцию с призывом разрешить удары Украины вглубь России | За проголосовали 425 депутатов")]
    public void RecruitmentAndPoliticsAreJunk(string text) => Assert.True(P(text, "lnr", "dnr").IsJunk);

    [Theory]
    [InlineData("Маркетплейс Воронеж, Новая Усмань, Александровка, Воронежская область - опасность по БПЛА.")]
    [InlineData("Высота большая, после Михайловки идут на набор высоты | Пролеты от Волгоградской области в сторону Саратовской продолжаются")]
    public void AlertsWithLookalikeWordsStaySignals(string text) => Assert.True(IsSignal(P(text)));

    [Theory]
    [InlineData("Лиски, не попадите под осколки!")]
    [InlineData("Саратов | Продолжается работа ПВО | Не попадите под осколки | Саратовская область")]
    [InlineData("Севастополь | Не попадите под обломки при сбитиях")]
    public void DebrisWarningIsActiveAlert(string text) => Assert.True(IsSignal(P(text)));
}
