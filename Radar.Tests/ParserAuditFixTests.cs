using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Исправления разбора по аудиту: границы слов, словоформы, падежи.</summary>
public class ParserAuditFixTests
{
    private static ParsedMessage P(string text) =>
        MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []);

    [Theory]
    [InlineData("В Белгородском районе отбой опасности БПЛА")]
    [InlineData("Шебекинский округ\nотбой")]
    public void AllClearAfterWordEndingInNe(string text) => Assert.Equal(AlertStatus.AllClear, P(text).Status);

    [Fact]
    public void RealNegationStillNotClear() => Assert.NotEqual(AlertStatus.AllClear, P("Курская область, опасность БПЛА, отбоя не было").Status);

    [Theory]
    [InlineData("Абсолютно спокойно, Белгородская область, опасность", ThreatKind.UavStrike)]
    [InlineData("Опасность. Кабардино-Балкарская Республика", ThreatKind.GuidedBomb)]
    public void KindNotFromInsideOtherWords(string text, ThreatKind notKind) => Assert.DoesNotContain(notKind, P(text).Kinds);

    [Fact]
    public void GuidedBombsStillDetected() => Assert.Equal(ThreatType.Aviation, P("Белгородская область, пуски КАБов").Threat);

    [Fact]
    public void KrylovskayaIsNotUavWing() => Assert.NotEqual(ThreatType.Uav, P("Крыловская, Кущевская — опасность").Threat);

    [Fact]
    public void RostovWithSpacedHyphenFound() =>
        Assert.Contains(P("Ростов- на-Дону, опасность БПЛА").Places, p => p.Label.StartsWith("Ростов-на-Дону", StringComparison.Ordinal));

    [Theory]
    [InlineData("Опасность БПЛА по Волгоградской области")]
    [InlineData("Отбой по угрозе БПЛА, Калужская область")]
    public void RegionWordsAreNotRivers(string text) =>
        Assert.DoesNotContain(P(text).Route?.Corridors ?? [], c => c.Kind == CorridorKind.River);

    [Fact]
    public void RiverCasesAreOneCorridor()
    {
        var c = P("Брянская область, БПЛА вдоль Десны, далее по реке Десна").Route!.Corridors.Where(x => x.Kind == CorridorKind.River).ToList();
        Assert.Single(c);
        Assert.Equal("десна", c[0].Name);
    }

    [Fact]
    public void ConjunctionIsNotRoad() =>
        Assert.DoesNotContain(P("Курская область: 2 БПЛА сбиты, а 3 летят на Курск").Route?.Corridors ?? [], c => c.Kind == CorridorKind.Road);

    [Fact]
    public void RoadNumberStillFound() =>
        Assert.Contains(P("Белгородская область, БПЛА вдоль трассы М-2").Route!.Corridors, c => c.Name == "М-2");

    [Theory]
    [InlineData("Курская область, на юге области работает ПВО", null)]
    // «курс юг» без предлога — раньше «с юг» из конца слова «курс» давало обратный курс (0°)
    [InlineData("Брянская область, БПЛА курс юг", null)]
    [InlineData("Брянская область, БПЛА на юг", 180.0)]
    public void HeadingWords(string text, double? heading) => Assert.Equal(heading, P(text).Heading);

    [Fact]
    public void DisplayTextFollowsCleanText()
    {
        var p = P("Белгород, опасность БПЛА");
        _ = p.DisplayText;
        p.CleanText = "другой";
        Assert.Equal("другой", p.DisplayText);
    }
}

public class ParserAuditFixTests2
{
    private static ParsedMessage P(string text) =>
        MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []);

    [Theory]
    // кубанские районы в именительном — «-ской» (как в справочнике и у каналов)
    [InlineData("Каневской район, опасность БПЛА", "Каневской район")]
    [InlineData("Каневском районе опасность БПЛА", "Каневский район")]
    public void KubanDistrictName(string text, string label) => Assert.Contains(P(text).Places, p => p.Label == label);

    [Theory]
    [InlineData("Орёл и район, опасность БПЛА", "Орловский район")]
    [InlineData("Липецк и район — опасность БПЛА", "Липецкий район")]
    [InlineData("Курск и район, опасность БПЛА", "Курский район")]
    public void CityAndItsDistrict(string text, string district)
    {
        var p = P(text);
        Assert.Contains(p.Places, x => x.Label == district && x.IsArea);
        Assert.DoesNotContain(p.Regions, r => r != p.Places[0].RegionId);
    }

    [Theory]
    [InlineData("Белгородская область, реактивные снаряды")]
    [InlineData("Шебекино, обстрел реактивными снарядами")]
    public void RocketShellsAreNotJetUav(string text) => Assert.DoesNotContain(ThreatKind.UavJet, P(text).Kinds);

    [Fact]
    public void MlrsByFullName() => Assert.Contains(ThreatKind.Mlrs, P("Ростовская область, опасность, реактивные системы залпового огня").Kinds);

    [Theory]
    [InlineData("Орловская область, тревога по БПЛА, в том числе реактивным")]
    [InlineData("Брянская область, БПЛА, включая реактивные")]
    public void JetUavStillDetected(string text) => Assert.Contains(ThreatKind.UavJet, P(text).Kinds);
}

/// <summary>Цели, которые раньше не распознавались: двусловные пункты с названием региона и стык областей.</summary>
public class ParserTargetFixTests
{
    private static ParsedMessage P(string text) =>
        MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []);

    [Fact]
    public void StanitsaLuganskayaIsTargetPlace()
    {
        var m = P("От Счастье через Петровку единичный БПЛА в сторону Станицы Луганской\nЛНР");
        Assert.Contains(m.Places, p => p.Label == "Станица Луганская" && p.IsTarget && p.Pos != null);
        Assert.Contains("lnr", m.Regions);
    }

    [Theory]
    [InlineData("Головчино, Ивановская лисица\nГрайворонский МО\nФпв", "Ивановская Лисица", "ivanovo")]
    [InlineData("Брянские Липяги ударный БПЛА в направлении Вейделевки\nБелгородская область", "Брянские Липяги", "bryansk")]
    public void PlaceNamedLikeRegionIsNotRegion(string text, string place, string notRegion)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), ["belgorod"]);
        Assert.Contains(m.Places, p => p.Label == place && p.RegionId == "belgorod");
        Assert.DoesNotContain(notRegion, m.Regions);
    }

    [Theory]
    [InlineData("Опасность БПЛА для станиц Краснодарского края", "krasnodar")]
    [InlineData("БПЛА над станицами Ростовской области", "rostov")]
    public void StanitsasOfRegionKeepRegion(string text, string region) => Assert.Contains(region, P(text).Regions);

    [Theory]
    [InlineData("Перевальное в направлении Таврическая ТЭС фиксация БПЛА Республика Крым", "Таврическая ТЭС")]
    [InlineData("Черноморское, Межводное тревога по БПЛА и далее в направлении Донузлав", "Донузлав")]
    [InlineData("От Арабатской стрелки в направлении Керченского полуострова реактивный пролет", "Керченский полуостров")]
    public void NamedZoneAfterDirectionIsTarget(string text, string zone) =>
        Assert.Contains(P(text).Places, p => p.Label == zone && p.IsTarget);

    [Theory]
    [InlineData("Балаклавская ТЭС и близлежащие\nОпасность по БПЛА", "Балаклавская ТЭС")]
    [InlineData("Побережье от Тарханкут до Севастополя тревога по БПЛА все населенные пункты", "Тарханкут")]
    [InlineData("Поселок Волна, Темрюкский район фиксация БПЛА в направлении Черного моря", "Черноморское побережье Кубани")]
    public void ZoneWithoutDirectionIsNotTargetOrOrigin(string text, string zone) =>
        Assert.DoesNotContain(P(text).Places, p => p.Label == zone && (p.IsTarget || p.IsOrigin));

    [Theory]
    [InlineData("Опасность по БПЛА на Тарханкуте", "Тарханкут")]
    [InlineData("Опасность по БПЛА на Керченском полуострове", "Керченский полуостров")]
    [InlineData("БПЛА на Таврической ТЭС", "Таврическая ТЭС")]
    [InlineData("Предварительно на Кинбурнской Косе уничтожен десант.", "Кинбурнская коса")]
    public void ZoneInLocativeIsNotTarget(string text, string zone) =>
        Assert.DoesNotContain(P(text).Places, p => p.Label == zone && p.IsTarget);

    [Theory]
    [InlineData("Сообщают о повышенной активности фпв на Озеряновке\nГорловка", "Озеряновка")]
    [InlineData("Сообщают о БПЛА на Бессарабке\nГорловка", "Бессарабка")]
    [InlineData("Опасность по БПЛА на Октябрьском\nРеспублика Крым", "Октябрьское")]
    public void PlaceInLocativeIsNotTarget(string text, string place) =>
        Assert.DoesNotContain(P(text).Places, p => p.Label == place && p.IsTarget);

    [Theory]
    [InlineData("Новороссийск и далее на Туапсе\nКраснодарский край", "Туапсе")]
    [InlineData("Калинино и далее на Джанкой\nРеспублика Крым", "Джанкой")]
    [InlineData("БПЛА от Ракитного на Бессарабку\nГорловка", "Бессарабка")]
    [InlineData("Песчаное и далее на Октябрьское\nРеспублика Крым", "Октябрьское")]
    public void PlaceInAccusativeIsTarget(string text, string place) =>
        Assert.Contains(P(text).Places, p => p.Label == place && p.IsTarget);

    [Theory]
    [InlineData("Ракетная опасность от Каховки до Чаплынки", "Каховка", "Чаплынка")]
    [InlineData("Опасность по БПЛА от Миллерово до Каменск-Шахтинский, Ростовская область.", "Миллерово", "Каменск-Шахтинский")]
    [InlineData("Вертолёты противника, опасность от Надеждинка до Яблоновка ДНР", "Надеждинка", "Яблоновка")]
    public void RangeEndIsNotOrigin(string text, string a, string b)
    {
        // «от A до B»: A — откуда (как раньше), B — место тревоги, а не второе «откуда»
        var m = P(text);
        Assert.Contains(m.Places, p => p.Label == a && p.IsOrigin);
        Assert.Contains(m.Places, p => p.Label == b && !p.IsOrigin && !p.IsTarget);
    }

    [Theory]
    [InlineData("От Рохманово и далее в тыловую часть опасность по БПЛА\nБрянская область", "Рохманово")]
    [InlineData("Ракитное, от Борисовки на Грайворон БПЛА\nБелгородская область", "Борисовка")]
    public void FromWithDirectionStaysOrigin(string text, string place) =>
        Assert.Contains(P(text).Places, p => p.Label == place && p.IsOrigin);

    [Fact]
    public void DistrictListAfterViaIsVia()
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            "Вторая волна БПЛА, летят через Суджанский и Большесолдатский районы. В сторону Курска."), ["kursk"]);
        Assert.Contains(m.Places, p => p.Label == "Суджанский район" && p.IsVia);
        Assert.Contains(m.Places, p => p.Label == "Большесолдатский район" && p.IsVia);
        Assert.Contains(m.Places, p => p.Label == "Курск" && p.IsTarget);
    }

    [Fact]
    public void ShotDownOverCityIsNotOrigin() =>
        Assert.DoesNotContain(P("По информации от МО над Курском сбито 8 БПЛА").Places, p => p.Label == "Курск" && p.IsOrigin);

    [Fact]
    public void AtJunctionIsPlaceNotTarget()
    {
        // «на стыке» — где находятся, а не куда летят: регионы остаются регионами тревоги
        var m = P("Группы БПЛА на стыке Калужской и Тульской области и далее на север.");
        Assert.DoesNotContain(m.Places, p => p.IsTarget && p.Label.StartsWith("Стык", StringComparison.Ordinal));
        Assert.Contains("kaluga", m.Regions);
        Assert.Contains("tula", m.Regions);
    }

    [Fact]
    public void DistrictListAfterDirectionIsTarget()
    {
        var m = P("Арабатская Стрелка повторно опасность по БПЛА и далее в направлении Советский, Кировский район, Керченский полуостров, Феодосия");
        Assert.Contains(m.Places, p => p.Label == "Советский район" && p.IsTarget);
        Assert.Contains(m.Places, p => p.Label == "Кировский район" && p.IsTarget);
    }

    [Theory]
    [InlineData("Старый Крым, тревога по БПЛА", false)]
    [InlineData("БПЛА на Старый Крым", true)]
    public void PlaceEndingWithRegionWordIsFound(string text, bool target)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), ["crimea"]);
        Assert.Contains(m.Places, p => p.Label == "Старый Крым" && p.IsTarget == target);
    }

    [Theory]
    [InlineData("Республика Крым, тревога по БПЛА")]
    [InlineData("В Крыму тревога по БПЛА")]
    public void RegionWordAloneStaysRegion(string text)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), ["crimea"]);
        Assert.Contains("crimea", m.Regions);
        Assert.DoesNotContain(m.Places, p => p.Label == "Старый Крым");
    }

    [Theory]
    [InlineData("Ракеты на Каменку-Днепровскую", "zaporozhye", "Каменка-Днепровская")]
    [InlineData("Направление ракеты на Новую Каховку", "kherson", "Новая Каховка")]
    [InlineData("Группа БПЛА летят над трассой в сторону Дмитриева-Льговского", "kursk", "Дмитриев-Льговский")]
    [InlineData("Большое Полпино - 2 БПЛА в сторону центра посёлка и дальше в сторону Малого Полпино", "bryansk", "Малое Полпино")]
    [InlineData("Новомосковск, Тульская область - фиксации БПЛА в направлении Северо-Задонска", "tula", "Северо-Задонск")]
    [InlineData("В Ростове-на-Дону уничтожено 4 БПЛА", "rostov", "Ростов-на-Дону")]
    [InlineData("ВСУ вошли в Казачью Локню", "kursk", "Казачья Локня")]
    public void MultiWordPlaceInObliqueCaseFound(string text, string region, string place)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), [region]);
        Assert.Contains(m.Places, p => p.Label == place);
    }

    [Theory]
    [InlineData("Предварительно от Чертково на Вешенскую далее Михайловка", "rostov", "Вёшенская")]
    [InlineData("Тихорецк на Павловскую БПЛА", "krasnodar", "Павловская")]
    [InlineData("РСЗО на Попасную", "lnr", "Попасная")]
    [InlineData("От Староминской на Кущевскую один БПЛА прошёл", "krasnodar", "Кущёвская")]
    [InlineData("Ростовская область От Сохрановки в сторону Мешковской фиксации", "rostov", "Сохрановка")]
    public void OneWordPlaceInObliqueCaseFound(string text, string region, string place)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), [region]);
        Assert.Contains(m.Places, p => p.Label == place);
    }

    [Theory]
    [InlineData("Дудчаны, тревога по БПЛА", "kherson", "Дудчаны")]
    [InlineData("БПЛА на Красный Лиман", "dnr", "Красный Лиман")]
    [InlineData("Партизаны, Геническ, опасность по БПЛА", "kherson", "Партизаны")]
    [InlineData("Зелёный Под, опасность БПЛА", "kherson", "Зелёный Под")]
    [InlineData("Чернухино, опасность по БПЛА", "lnr", "Чернухино")]
    public void UkrainianSpelledPlaceFoundByRussianName(string text, string region, string place)
    {
        var m = MessageParser.Parse(new RawMessage("test", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), [region]);
        Assert.Contains(m.Places, p => Gazetteer.Normalize(p.Label) == Gazetteer.Normalize(place));
    }

    [Theory]
    [InlineData("Красний Лиман", "Красный Лиман")]
    [InlineData("Чорнухине", "Чернухино")]
    [InlineData("Станично-Луганське", "Станично-Луганское")]
    [InlineData("Ногайськ", "Ногайск")]
    [InlineData("Лиман", "Лиман")]
    public void UkrainianSpellingTransliterated(string uk, string ru) => Assert.Equal(ru, PlaceIndex.UkToRu(uk));

    [Theory]
    [InlineData("locatorru", "Ещё запуск БПЛА на Луганскую Народную Республику, далее на Белгородскую / Воронежскую области.", "Народная")]
    [InlineData("radarrussiia", "Большую часть силы ПВО посадили, те что летели на южные регионы", "Большая")]
    [InlineData("radarrussiia", "Возможна атака на Ставропольскую нефтебазу. Будьте бдительны!", "Ставропольская")]
    public void AdjectiveBeforeCommonNounIsNotPlace(string ch, string text, string notPlace) =>
        Assert.DoesNotContain(MessageParser.Parse(new RawMessage(ch, 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []).Places,
            p => p.Label == notPlace);

    [Theory]
    [InlineData("kupolrussia", "Над территорией Брянской области и Московского региона уничтожено 42 БПЛА", "Московский")]
    [InlineData("LPRalarm", "Таволжанка, Шебекино Большое количество ФПВ Ограничьте движение", "Большое")]
    [InlineData("radarrussiia", "Мирный житель ранен в результате атаки ВСУ на Белгородскую область. Обстреляно село Ржевка Шебекинского района.", "Мирный")]
    [InlineData("radarrussiia", "Ракетный удар нанесен по штабу Черноморского флота в Севастополе", "Черноморское")]
    [InlineData("LPRalarm", "Внимание Противник готовит применение авиации на Угледарском и Донецком направлении", "Донецк")]
    [InlineData("LPRalarm", "Отбой Красного уровня \"Угроза атаки БПЛА\" для г. Елец", "Красное")]
    [InlineData("radarrussiia", "Местность, где была фиксация. Время фиксации. Примерное направление БПЛА.", "Примерное")]
    [InlineData("LPRalarm", "Азовское побережье Крыма Внимание по БПЛА", "Азовское")]
    public void AdjectiveWithItsNounIsNotPlace(string ch, string text, string notPlace) =>
        Assert.DoesNotContain(MessageParser.Parse(new RawMessage(ch, 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []).Places,
            p => p.Label == notPlace);

    [Theory]
    [InlineData("Октябрьское, Красногвардейское тревога по БПЛА Республика Крым", "Октябрьское")]
    [InlineData("Кременная ракетная опасность ЛНР", "Кременная")]
    [InlineData("Долгое фпв Белгородская область", "Долгое")]
    [InlineData("Белгород, Шебекино, Новая Таволжанка опасность по БПЛА", "Новая Таволжанка")]
    public void PlaceNamedAsAdjectiveStillFound(string text, string place) =>
        Assert.Contains(P(text).Places, p => p.Label == place);

    [Theory]
    [InlineData("Кача, Бельбек, Северная сторона Севастополя\nТревога по БПЛА", "Северная сторона")]
    [InlineData("Белгородская область, Пятницкое кольцо", "Пятницкое кольцо")]
    [InlineData("Троицкий пост, Белгородская область", "Троицкий пост")]
    public void TwoWordPlaceWithAdjectiveKept(string text, string place) =>
        Assert.Contains(MessageParser.Parse(new RawMessage("LPRalarm", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), ["crimea", "sevastopol", "belgorod"]).Places,
            p => p.Label == place);

    [Theory]
    [InlineData("LPRalarm", "Новороссийск Сирены воздушной тревоги стихли.", "Сирена")]
    [InlineData("radarrussiia", "Второй лёг в Шебекино", "Второй")]
    [InlineData("radarrussiia", "Пожар полностью локализован.", "Пожар")]
    [InlineData("LPRalarm", "Аэропорт\nДонецк и близлежащие\nТревога по БПЛА\nДНР", "Аэропорт")]
    [InlineData("LPRalarm", "Шебекино Элеватор перекресток Стоп движение ФПВ", "Элеватор")]
    public void CommonNounIsNotPlace(string ch, string text, string notPlace) =>
        Assert.DoesNotContain(MessageParser.Parse(new RawMessage(ch, 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), []).Places,
            p => p.Label == notPlace);

    [Fact]
    public void TwoWordPlaceWithCommonNounKept() =>
        Assert.Contains(MessageParser.Parse(new RawMessage("LPRalarm", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            "Сахарная Головка Севастополь множественные фиксации БПЛА"), ["sevastopol"]).Places, p => p.Label == "Сахарная Головка");

    [Theory]
    [InlineData("lpr1_Belgorod_alarm", "Н Мельницы фпв", "Нижние Мельницы", "belgorod")]
    [InlineData("lpr1_Belgorod_alarm", "Н. Мельницы фпв", "Нижние Мельницы", "belgorod")]
    [InlineData("lpr1_Belgorod_alarm", "От Н. Мельниц фпв в сторону Уразово", "Нижние Мельницы", "belgorod")]
    [InlineData("LPRalarm", "Фпв в районе Нижних Мельниц", "Нижние Мельницы", "belgorod")]
    [InlineData("LPRalarm", "БПЛА в районе Верхних Лубянок", "Верхние Лубянки", "belgorod")]
    [InlineData("locatorru", "БПЛА между Новой Мельницей и трассой Острогожск – Алексеевка, Воронежская область", "Новая Мельница", "voronezh")]
    [InlineData("LPRalarm", "Б. Утриш БПЛА", "Большой Утриш", "krasnodar")]
    [InlineData("lpr1_Belgorod_alarm", "Ст. Нелидовка, Головино\nВнимание по БПЛА", "Старая Нелидовка", "belgorod")]
    [InlineData("lpr1_Belgorod_alarm", "Яс. Зори, Черемошное Бгд\nВнимание БПЛА", "Ясные Зори", "belgorod")]
    [InlineData("lpr1_Kherson_alarm", "Н. Маячка, С. Маячка, В. Копани и близлежащие\nХерсонская область РФ", "Великие Копани", "kherson")]
    [InlineData("lpr1_Kherson_alarm", "Н. Маячка, С. Маячка, В. Копани и близлежащие\nХерсонская область РФ", "Старая Маячка", "kherson")]
    [InlineData("lpr1_LDPR_alarm", "Ст. Луганская\nИ далее в тыл\nТревога по БПЛА\nЛНР", "Станица Луганская", "lnr")]
    [InlineData("locatorru", "Им. Карла Либкнехта, Курчатовский район, Курская область - пролёты БПЛА", "имени Карла Либкнехта", "kursk")]
    [InlineData("lpr1_Rostov_alarm", "Б. Калитва\nОпасность БПЛА", "Белая Калитва", "rostov")]
    [InlineData("lpr1_Belgorod_alarm", "Титовка\nМ. Пристань - 26 -й км СТОП ДВИЖЕНИЕ", "Маслова Пристань", "belgorod")]
    [InlineData("lpr1_Belgorod_alarm", "От Маломихайловки Н. Березово до мелкоп Шбк", "Нижнее Березово-Второе", "belgorod")]
    [InlineData("lpr1_Belgorod_alarm", "Соц. Город БПЛА", "Соцгородок", "belgorod")]
    public void MultiWordPlaceInObliqueOrAbbreviatedForm(string ch, string text, string place, string region)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), [region]);
        Assert.Contains(m.Places, p => p.Label == place);
        Assert.DoesNotContain(m.Places, p => p.Label is "Мельница" or "Мельницы" or "Новое" or "Утриш" or "Нелидовка" or "Маячка" or "Копани" or "Калитва" or "Пристань" or "Берёзово");
    }

    [Theory]
    [InlineData("lpr1_Belgorod_alarm", "В Белгороде работает ПВО", "Белгород")]
    [InlineData("lpr1_LDPR_alarm", "Горловка. Пос. Кирова дрон! Дмг", "Горловка")]
    public void PrepositionIsNotAbbreviation(string ch, string text, string place)
    {
        var m = MessageParser.Parse(new RawMessage(ch, 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), [.. new AppSettings().Channels.First(c => c.Name == ch).Regions]);
        Assert.Contains(m.Places, p => p.Label == place);
        Assert.DoesNotContain(m.Places, p => p.Label is "Киров" || p.Label.StartsWith("Верхн", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("С Маячки БПЛА в сторону Каховки\nХерсонская область РФ", "Старая Маячка")]
    [InlineData("В Копанях фиксация БПЛА\nХерсонская область РФ", "Великие Копани")]
    public void PrepositionWithoutDotIsNotAbbreviation(string text, string notPlace) =>
        Assert.DoesNotContain(MessageParser.Parse(new RawMessage("lpr1_Kherson_alarm", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), text), ["kherson"]).Places,
            p => p.Label == notPlace);

    [Fact]
    public void NamedAfterIsNotPlace()
    {
        var m = P("Горловка! Строитель, квартал 245, посёлок Победы и шахты 5 внимание!");
        Assert.DoesNotContain(m.Places, p => p.Label == "Победа");
        Assert.Contains(P("Горловка\nСтроитель\nПобеда\nвнимание").Places, p => p.Label == "Победа");
    }

    [Theory]
    [InlineData("Новольвовск, Климовский район, Тульская область - пролёты БПЛА на юг Московской области.", null)]
    [InlineData("БПЛА в сторону севера Курской области", null)]
    [InlineData("Счастье на северо-восток БПЛА", 45.0)]
    [InlineData("Группа БПЛА на юг", 180.0)]
    // оборванное «на северо-…» в конце строки — общая часть
    [InlineData("От Рохманово, Брянской области, и на северо\nОпасность по БПЛА", 0.0)]
    [InlineData("Дальше на юго.", 180.0)]
    [InlineData("Северо-Западный район", null)]
    public void CompassPartOfRegionIsNotHeading(string text, double? heading) =>
        Assert.Equal(heading, MessageParser.DetectHeadingText(text));

    [Fact]
    public void NorthCaucasusIsNotPlace() =>
        Assert.DoesNotContain(MessageParser.Parse(new RawMessage("radarrussiia", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            "Республики Северного Кавказа\nОпасность по БПЛА от Краснодарского края и Республика Адыгея"), []).Places,
            p => p.Label is "Северный" or "Северное");

    [Fact]
    public void ListAlongCoastIsNotTarget()
    {
        var m = MessageParser.Parse(new RawMessage("locatorru", 1, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            "Продолжаются фиксации БПЛА от Азовского моря через Керченский полуостров в акваторию Чёрного моря, далее вдоль побережья: Анапа, Новороссийск, Геленджик, Туапсе, Сочи, Адлер."), []);
        Assert.DoesNotContain(m.Places, p => p.Label == "Анапа" && p.IsTarget);
    }

    [Fact]
    public void CityDistrictAliasIsNotAdded() =>
        Assert.Null(PlaceIndex.Find("Приморский район", ["dnr"]) is { Kind: 'P' } h && h.Name == "Приморский район" ? h : null);

    [Fact]
    public void RegionAdjectiveIsNotUkrainianAliasPlace() =>
        Assert.DoesNotContain(P("Опасность по БПЛА на Донецком направлении\nДНР").Places, p => p.Label is "Донецкое" or "Донецьке");

    [Theory]
    [InlineData("Курская область, опасность по БПЛА сохраняется")]
    [InlineData("Сохраняйте спокойствие, Белгородская область")]
    public void KeepWordsAreNotPlaces(string text) =>
        Assert.DoesNotContain(P(text).Places, p => p.Label.StartsWith("Сохран", StringComparison.Ordinal));

    [Theory]
    [InlineData("БПЛА на северо-запад, Тульская область")]
    [InlineData("Северо-Запад\nОпасность по БПЛА\nКурская область")]
    public void CompassWordIsStillNotPlace(string text) =>
        Assert.DoesNotContain(P(text).Places, p => p.Label.StartsWith("Северо", StringComparison.Ordinal));

    [Fact]
    public void RegionWordBeforeOrdinaryWordStaysRegion() =>
        Assert.Contains("bryansk", P("Брянская опасность БПЛА").Regions);

    [Theory]
    [InlineData("Бердянск и область - отбой ракетной и БПЛА опасности")]
    [InlineData("🟢 Курская область: внимание! Отбой ракетной опасности и опасности БПЛА.")]
    [InlineData("Курская область - отбой авиационной ракетной и БПЛА опасности.")]
    [InlineData("Воронежская область - отбой ракетной опасности и реактивным БПЛА.")]
    [InlineData("Курская область отбой ракетной опасности, отбой опасности по БПЛА")]
    public void CombinedClearCoversBothThreats(string text)
    {
        // отбой и ракетной опасности, и БПЛА — снимает тревоги обоих типов (раньше оставались тревоги по БПЛА)
        var m = P(text);
        Assert.Equal(AlertStatus.AllClear, m.Status);
        Assert.Equal(ThreatType.General, m.Threat);
    }

    [Theory]
    [InlineData("Курская область - отбой ракетной опасности!", ThreatType.Missile)]
    [InlineData("Крым - предварительно отбой ракетной опасности. БПЛА сохраняется.", ThreatType.Missile)]
    [InlineData("Орловская область\nОтбой ракетной опасности\nОпасность по обычным БПЛА сохраняется", ThreatType.Missile)]
    [InlineData("Тульская область\nРакетная опасность\nОтбой опасности по БПЛА", ThreatType.Uav)]
    // «БПЛА Фламинго» — крылатая ракета: отбой ракетной опасности, тревоги по БПЛА остаются
    [InlineData("Отбой ракетной опасности по БПЛА Фламинго Краснодарский край", ThreatType.Missile)]
    public void SingleClearKeepsOtherThreat(string text, ThreatType threat)
    {
        // отбой только одной угрозы — другая (БПЛА или ракетная опасность) остаётся
        var m = P(text);
        Assert.Equal(AlertStatus.AllClear, m.Status);
        Assert.Equal(threat, m.Threat);
    }

    [Theory]
    [InlineData("Брянская область отбой ракетной опасности\nТревога по реактивным БПЛА", "bryansk")]
    [InlineData("Отбой по всем ранее объявленным Херсонская область РФ\nВнимание по БПЛА на юге", "kherson")]
    public void ClearThenNewAlertSplitsIntoBlocks(string text, string region)
    {
        // после отбоя объявлена новая угроза — отдельная тревога по БПЛА в том же регионе (раньше терялась)
        var m = P(text);
        Assert.Equal(2, m.Blocks.Count);
        Assert.Equal(AlertStatus.AllClear, m.Blocks[0].Status);
        Assert.NotEqual(AlertStatus.AllClear, m.Blocks[1].Status);
        Assert.Equal(ThreatType.Uav, m.Blocks[1].Threat);
        Assert.Contains(region, m.Blocks[1].Regions);
    }

    [Theory]
    [InlineData("Республика Крым\nСевастополь\nОтбой ракетной опасности\nОпасность по БПЛА в действии")]
    [InlineData("Брянская область\nОтбой ракетной опасности\nТревога по реактивным БПЛА\nсохраняется!")]
    [InlineData("Московская область - отбой опасности по БПЛА.\nПереходим в режим «внимание по БПЛА».")]
    [InlineData("Самарская область\nОпасность по БПЛА\nUPD: Отбой")]
    public void ClearWithRemainingThreatIsNotSplit(string text)
    {
        // угроза названа сохраняющейся, режим «внимание» или тревога до отбоя — одно сообщение отбоя, без новой тревоги
        var m = P(text);
        Assert.Equal(AlertStatus.AllClear, m.Status);
        Assert.Empty(m.Blocks);
    }

    [Theory]
    [InlineData("Продолжаются пролёты БПЛА через Стародуб, Унеча в направлении стыка Смоленской и Калужской областей, массово.")]
    [InlineData("Унеча с юга огромное количество БПЛА и далее на стык Калужской и Смоленской областей")]
    public void RegionJunctionIsOneTargetPoint(string text)
    {
        var m = P(text);
        var t = Assert.Single(m.Places, p => p.IsTarget);
        Assert.Equal("Стык: Калужская — Смоленская", t.Label);
        Assert.InRange(t.Pos!.Value.Lat, 53.5, 54.5);
        Assert.Equal(["bryansk"], m.Regions);
    }
}
