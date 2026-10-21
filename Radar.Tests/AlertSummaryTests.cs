using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

public class AlertSummaryTests
{
    [Theory]
    // всё уже в заголовке (регион) и плашках (уровень, тип) — текста нет
    [InlineData("Ярославская область Опасность по МВШ", "yaroslavl", "")]
    [InlineData("Ярославская область\nОпасность по МВШ", "yaroslavl", "")]
    [InlineData("Воронежская область\nТревога по БПЛА", "voronezh", "")]
    [InlineData("Брянская область\nКурская область\nОрловская область\nТревога по БПЛА\nСрочно принять меры безопасности", "bryansk,kursk,orel", "")]
    [InlineData("Краснодарский край - отбой опасности по БПЛА.", "krasnodar", "")]
    // пункты, направления и подробности остаются
    [InlineData("Клинцы и близлежащие\nТревога по БПЛА\nБрянская область", "bryansk", "Клинцы и близлежащие")]
    [InlineData("Тула\nТульская область\nРабота ПВО по БПЛА", "tula", "Тула")]
    [InlineData("Мичуринск, Тамбовская область - меры предосторожности при БПЛА.", "tambov", "Мичуринск")]
    [InlineData("Безенчук\nЧапаевск\nСамарская область\nОпасность по БПЛА", "samara", "Безенчук, Чапаевск")]
    [InlineData("От Кантемировки на восток, северо-восток\nТревога по БПЛА сохраняется\nВоронежская область", "voronezh", "От Кантемировки на восток, северо-восток")]
    [InlineData("Вейделевка\nВалуйки и близлежащие\nБелгородская область\nТревога по БПЛА\nИ далее в направлении\nВоронежской области", "belgorod",
        "Вейделевка, Валуйки и близлежащие, И далее в направлении, Воронежской области")]
    // статус и регион внутри строки
    [InlineData("Курск и близлежащие опасность по БПЛА", "kursk", "Курск и близлежащие")]
    [InlineData("Горловка ДНР и близлежащие\nОпасность по БПЛА и ФПВ", "dnr", "Горловка и близлежащие")]
    [InlineData("Мариуполь и близлежащие, тревога по БпЛА", "dnr", "Мариуполь и близлежащие")]
    [InlineData("Северодонецк и ближайшие опасность по БПЛА\nПовторно\nЛНР", "lnr", "Северодонецк и ближайшие")]
    [InlineData("Морозовск и близлежащие тревога по БПЛА и далее в направлении Волгоградской области", "rostov",
        "Морозовск и близлежащие и далее в направлении Волгоградской области")]
    [InlineData("Луганск и близлежащие\nТревога по БПЛА остаётся в действии\nСоблюдайте меры безопасности", "lnr", "Луганск и близлежащие")]
    [InlineData("Орловская область повторно опасность по БПЛА", "orel", "")]
    [InlineData("Луганск ракетная опасность", "lnr", "Луганск")]
    // по архиву: дежурные фразы и регионы события в косвенных падежах
    [InlineData("Белгород, Белгородский район\nРакетная опасность! Займите безопасные места!\nПотенциальная опасность для Курской области и Воронежской области",
        "belgorod,kursk,voronezh", "Белгород, Белгородский район")]
    [InlineData("ЛДНР\nАвиационная ракетно-бомбовая опасность", "lnr,dnr", "")]
    [InlineData("Горловка — опасность применения противником ударных БПЛА", "dnr", "Горловка")]
    [InlineData("На территории региона опасность сохраняется", "kursk", "")]
    [InlineData("Энергодар - Работино\nВнимание", "zaporozhye", "Энергодар - Работино")]
    [InlineData("Орёл и Орловская область работа ПВО по БПЛА", "orel", "Орёл")]
    // фиксации и сбития: остаётся только количество
    [InlineData("Фиксация от 3 БПЛА\nТульская область", "tula", "от 3 шт")]
    [InlineData("Одоевский район\nТульская область\nФиксация 5 БПЛА", "tula", "Одоевский район, 5 шт")]
    [InlineData("Орёл\nОрловская область\nСбитие БПЛА", "orel", "Орёл")]
    [InlineData("Белгородский район\nЕщё сбитие БПЛА", "belgorod", "Белгородский район")]
    [InlineData("Калужская область\nФиксации групп БПЛА", "kaluga", "группа")]
    [InlineData("Чеховский район\nФиксация БПЛА в направлении Подольск", "moscow_obl", "Чеховский район, в направлении Подольск")]
    [InlineData("Множественные фиксации БПЛА\nМосковская область", "moscow_obl", "много")]
    [InlineData("Отбой авиационной ракетной бомбовой опасности Курская область и близлежащие", "kursk", "")]
    [InlineData("Мельничное, Ударное\nРеспублика Крым", "crimea", "Мельничное, Ударное")]
    // «от ЛНР» — откуда угроза, не подпись региона
    [InlineData("Краснодон и близлежащие, тревога по БпЛА\nРостовская область, новые группы БпЛА от ЛНР", "rostov,lnr",
        "Краснодон и близлежащие, новые группы БпЛА от ЛНР")]
    public void DuplicatesOfHeaderAndBadgesAreRemoved(string text, string regions, string expected) =>
        Assert.Equal(expected, AlertSummary.Clean(text, regions.Split(','), allClear: text.Contains("отбой", StringComparison.OrdinalIgnoreCase), Groups(text)));

    // тип угрозы — из разбора, как у события
    private static IEnumerable<ThreatType> Groups(string text)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UnixEpoch, text), []);
        return m.Kinds.Select(k => ThreatCatalog.Info(k).Group).Append(m.Threat);
    }

    [Theory]
    // другой тип, чем у события, — не дубль плашки
    [InlineData("Курская область\nРакетная опасность", "kursk", ThreatType.Uav, "Ракетная опасность")]
    [InlineData("Брянская область\nТревога по БПЛА", "bryansk", ThreatType.Missile, "Тревога по БПЛА")]
    [InlineData("Ярославская область Опасность по МВШ", "yaroslavl", ThreatType.Uav, "Опасность по МВШ")]
    [InlineData("Луганск\nАтакмсы", "lnr", ThreatType.Missile, "Луганск, Атакмсы")]
    [InlineData("Крым\nВнимание по авиации и Баллистике", "crimea", ThreatType.Aviation, "Внимание по авиации и Баллистике")]
    // тот же тип — дубль
    [InlineData("Курская область\nРакетная опасность", "kursk", ThreatType.Missile, "")]
    [InlineData("Крым - ракетная опасность по ПКР \"Нептун\".", "crimea", ThreatType.Missile, "")]
    [InlineData("Авиационная ракетная опасность Херсонская область РФ", "kherson", ThreatType.Missile, "")]
    [InlineData("Крым\nВнимание по авиации", "crimea", ThreatType.Missile, "Внимание по авиации")]
    // «Дроновка» — пункт, не «дрон»
    [InlineData("Ведётся обстрел, с.Дроновка", "belgorod", ThreatType.Uav, "Ведётся обстрел, с.Дроновка")]
    public void ThreatWordsAreRemovedOnlyForEventType(string text, string regions, ThreatType group, string expected) =>
        Assert.Equal(expected, AlertSummary.Clean(text, regions.Split(','), allClear: false, [group]));

    [Theory]
    // отбой: «отбой …», режим после отбоя, время, междометия и подписи — уже видно по плашке и времени
    [InlineData("Морозовск Ростовская область\nОтбой внимания по БПЛА", "rostov", ThreatType.Uav, "Морозовск")]
    [InlineData("Отбой опасности БПЛА по ранее объявленным\nРеспублика Крым", "crimea", ThreatType.Uav, "")]
    [InlineData("Воронежская область\n8.03 Отбой опасности атаки БПЛА в регионе!", "voronezh", ThreatType.Uav, "")]
    [InlineData("Воронежская область\nОтбой опасности БПЛА\nРежим повышенного внимания сохраняется", "voronezh", ThreatType.Uav, "")]
    [InlineData("Херсонская область отбой внимания. Все хорошо.👌", "kherson", ThreatType.General, "")]
    [InlineData("Отбой авиационной ракетной бомбовой опасности Курская область\n❗️Наблюдаем за всей Россией — наблюдатель.рф", "kursk", ThreatType.Aviation, "")]
    [InlineData("Самарская область - отбой ракетной опасности!\nПВО - герои!", "samara", ThreatType.Missile, "")]
    // пункты отбоя остаются
    [InlineData("Севастополь, запад Крыма\nОтбой ракетной опасности", "crimea,sevastopol", ThreatType.Missile, "запад Крыма")]
    [InlineData("Республика Марий Эл\nОтбой опасности по БПЛА", "mari", ThreatType.Uav, "")]
    [InlineData("Чувашская Республика, Чеченская Республика - отбой ракетной опасности!", "chuvashia,chechnya", ThreatType.Missile, "")]
    public void AllClearBoilerplateIsRemoved(string text, string regions, ThreatType group, string expected) =>
        Assert.Equal(expected, AlertSummary.Clean(text, regions.Split(','), allClear: true, [group]));

    [Fact]
    public void AlarmKeepsModeAndTime() =>
        Assert.Equal("Режим повышенного внимания сохраняется", AlertSummary.Clean("Воронежская область\nРежим повышенного внимания сохраняется", ["voronezh"], allClear: false, [ThreatType.Uav]));

    [Fact]
    public void PartialClearInsideAlarmIsKept() =>
        Assert.Equal("Курская область - отбой", AlertSummary.Clean("Курская область - отбой\nОрловская область\nТревога по БПЛА", ["orel", "kursk"], allClear: false, [ThreatType.Uav]));

    [Fact]
    public void CityNamedLikeRegionIsKept() =>
        Assert.Equal("Орёл", AlertSummary.Clean("Орёл\nОрловская область\nОпасность по БПЛА", ["orel"], allClear: false, [ThreatType.Uav]));

    [Fact]
    public void OtherRegionIsKept() =>
        Assert.Equal("Курская область", AlertSummary.Clean("Курская область\nОпасность по БПЛА", ["orel"], allClear: false, [ThreatType.Uav]));

    [Theory]
    [InlineData("a", "@a")]
    [InlineData("a,b,c", "@a, @b, @c")]
    [InlineData("a,b,c,d,a", "@a, @b и ещё 2")]
    public void SourcesAreShortened(string channels, string expected) =>
        Assert.Equal(expected, AlertSummary.Sources(channels.Split(',')));
}
