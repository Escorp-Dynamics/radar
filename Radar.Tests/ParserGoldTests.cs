using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>
/// Эталонные случаи из реальных сообщений каналов тревог (разметка вручную).
/// Любое изменение парсера обязано проходить этот набор: пропуск сигнала недопустим.
/// </summary>
public class ParserGoldTests
{
    private static ParsedMessage P(string text, params string[] channelRegions) =>
        MessageParser.Parse(new RawMessage("test", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), channelRegions);

    private static bool IsSignal(ParsedMessage p) =>
        !p.IsJunk && !p.IsInformational && (p.Threat != ThreatType.None || p.Status == AlertStatus.AllClear);

    private static void HasPlace(ParsedMessage p, string label, bool? target = null, bool? origin = null)
    {
        var hit = p.Places.FirstOrDefault(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase) && x.Pos != null);
        Assert.True(hit != null, $"нет пункта «{label}»; найдено: {string.Join("; ", p.Places.Select(x => x.Label + (x.Pos == null ? "?" : "")))}");
        if (target != null) Assert.Equal(target, hit!.IsTarget);
        if (origin != null) Assert.Equal(origin, hit!.IsOrigin);
    }

    // ---------------- Сигналы: тип угрозы и статус ----------------

    [Theory]
    [InlineData("Брянская область | Опасность по БПЛА", ThreatType.Uav, AlertStatus.Alert, "bryansk")]
    [InlineData("Ростовская область | Ракетная опасность", ThreatType.Missile, AlertStatus.Alert, "rostov")]
    [InlineData("ЛДНР | Запорожская область РФ | Авиационная ракетная бомбовая опасность", ThreatType.Aviation, AlertStatus.Alert, "zaporozhye")]
    [InlineData("Ростовская область | Отбой ракетной опасности", ThreatType.Missile, AlertStatus.AllClear, "rostov")]
    [InlineData("Отбой ракетной бомбовой опасности Белгородская область", ThreatType.Aviation, AlertStatus.AllClear, "belgorod")]
    [InlineData("Краснодарский край | Отбой опасности БПЛА", ThreatType.Uav, AlertStatus.AllClear, "krasnodar")]
    [InlineData("Шебекино УАБ Белгородская область", ThreatType.Aviation, AlertStatus.Alert, "belgorod")]
    [InlineData("Белая Берёзка | Брянская область | РСЗО", ThreatType.Artillery, AlertStatus.Alert, "bryansk")]
    [InlineData("Причерноморье Краснодарского края, тревога по БЭК сохраняется", ThreatType.Naval, AlertStatus.Alert, "krasnodar")]
    [InlineData("Маслова Пристань | Тревога по БПЛА Хорнет", ThreatType.Uav, AlertStatus.Alert, "belgorod")]
    [InlineData("Белая Берёзка | опасность по ФПВ", ThreatType.Uav, AlertStatus.Alert, "bryansk")]
    [InlineData("Ростов-на-Дону и близлежащие, тревога по МРШ", ThreatType.Balloon, AlertStatus.Alert, "rostov")]
    [InlineData("Экстренная информация РСЧС (Краснодарский край): Отмена БЕСПИЛОТНОЙ ОПАСНОСТИ на территории муниципальных образований: Ейский, Щербиновский", ThreatType.Uav, AlertStatus.AllClear, "krasnodar")]
    [InlineData("Экстренная информация РСЧС: РАКЕТНАЯ ОПАСНОСТЬ в Шебекинском МО 18:02. Спуститесь в подвальное помещение", ThreatType.Missile, AlertStatus.Alert, "belgorod")]
    public void ThreatAndStatus(string text, ThreatType threat, AlertStatus status, string region)
    {
        var p = P(text);
        Assert.True(IsSignal(p), $"сигнал потерян: junk={p.JunkReason} info={p.IsInformational}");
        Assert.Equal(threat, p.Threat);
        Assert.Equal(status, p.Status);
        Assert.Contains(region, p.Regions);
    }

    [Fact]
    public void ReactiveUavIsUavNotMissile()
    {
        Assert.Equal(ThreatType.Uav, P("Вешенская и далее на север | Реактивный БПЛА или ПКР Нептун | Ростовская область").Threat);
        Assert.Equal(ThreatType.Uav, P("Каменск-Шахтинский и близлежащие | Опасность по БПЛА в том числе реактивные БПЛА").Threat);
    }

    [Fact]
    public void ToponymWithThreatStemIsNotThreat()
    {
        // «Штормовое» — село в Крыму, а не «Шторм»
        var p = P("Штормовое | Абрикосово | Новофедоровка и близлежащие | Тревога по БПЛА | Республика Крым");
        Assert.Equal(ThreatType.Uav, p.Threat);
        HasPlace(p, "Штормовое");
    }

    // ---------------- Сообщения без слова «БПЛА» (агрегаторы) ----------------

    [Theory]
    [InlineData("Шебекино | Белгородская область", "Шебекино")]
    [InlineData("Короча и близлежащие | Белгородская область", "Короча")]
    [InlineData("Сельцо и близлежащие | И далее в тыл | Брянская область", "Сельцо")]
    [InlineData("Ишунь | Красноперекопск и близлежащие | с моря | Республика Крым", "Красноперекопск")]
    [InlineData("Горловка, Пятихатки, Корсунь, Енакиево и близлежащие | повторно | ДНР", "Енакиево")]
    public void LocationOnlyPostIsSignal(string text, string place)
    {
        var p = P(text);
        Assert.True(IsSignal(p), "сообщение-перечень пунктов должно считаться сигналом");
        HasPlace(p, place);
    }

    [Fact]
    public void ShelterCallIsSignal()
    {
        var p = P("Казинка, Валуйки укрытия!");
        Assert.True(IsSignal(p));
        HasPlace(p, "Валуйки");
        Assert.True(IsSignal(P("Адлер, меры предосторожности! К окнам не подходить! Занять надёжное укрытие!")));
    }

    // ---------------- Локализация ----------------

    [Fact]
    public void VillagesFromOfflineIndex()
    {
        var p = P("Маломихайловка х бык Щигоревка Нежеголь Ржевка займите укрытие летит ударный БПЛА | Шебекинский район");
        Assert.Contains("belgorod", p.Regions);
        HasPlace(p, "Нежеголь");
        HasPlace(p, "Ржевка");
    }

    [Fact]
    public void RegionInferredByVotingWithoutRegionName()
    {
        var p = P("Серебрянка Раздольненский район и далее в направлении примерно Первомайское, Войково тревога по БПЛА");
        Assert.Contains("crimea", p.Regions);
        HasPlace(p, "Первомайское", target: true);
    }

    [Fact]
    public void CommonWordToponymStandalone()
    {
        HasPlace(P("Крайнее | Тревога по БПЛА | Республика Крым"), "Крайнее");
        HasPlace(P("Восход | Красногвардейский район | Тревога по БПЛА | Республика Крым"), "Восход");
    }

    [Fact]
    public void CasesAndAbbreviations()
    {
        HasPlace(P("От Гремячки в направлении Курск, Курчатов | Тревога по БПЛА | Курская область"), "Курск", target: true);
        HasPlace(P("От Казачьей Лопани Харьковской области | Ударный БПЛА Дартс в направлении Белгородской Гг"), "Казачья Лопань", origin: true);
        HasPlace(P("Каменка Кашарский район Ростовская область фиксации БПЛА в направлении Вешек"), "Вёшенская", target: true);
        HasPlace(P("Между Суземкой  и Белой Березкой и далее в направлении Трубчевского района | Брянская область"), "Белая Берёзка");
        HasPlace(P("РСЗО на Великую новоселку"), "Великая Новосёлка", target: true);
        Assert.Contains("belgorod", P("Строитель Бгд и близлежащие | Опасность по БПЛА").Regions);
    }

    [Fact]
    public void HyphenatedRoutesSplit()
    {
        var p = P("Трасса Валуйки-Пятницкое-Волоконовка | Угроза атаки ФПВ");
        HasPlace(p, "Валуйки");
        HasPlace(p, "Волоконовка");
        HasPlace(P("Сельцо-Брянск БПЛА разведчик"), "Брянск");
        HasPlace(P("Каменск-Шахтинский | Ростов-на-Дону | БПЛА"), "Ростов-на-Дону");
    }

    [Fact]
    public void DistrictListsFromOfficialMessages()
    {
        var p = P("ОТБОЙ ОПАСНОСТИ АТАКИ БПЛА в Волоконовском, Валуйском, Борисовском, Красногвардейском, Ровеньском, Чернянском МО");
        Assert.Equal(AlertStatus.AllClear, p.Status);
        Assert.True(p.Places.Count(x => x.IsArea) >= 6, string.Join("; ", p.Places.Select(x => x.Label)));
        Assert.DoesNotContain(p.Places, x => x.Label == "Красногвардейское");

        var k = P("Экстренная информация РСЧС: БЕСПИЛОТНАЯ ОПАСНОСТЬ на территории муниципальных образований: Приморско-Ахтарский муниципальный округ, Брюховецкий, Ейский, Каневской, Абинский");
        Assert.Contains("krasnodar", k.Regions);
        Assert.True(k.Places.Count >= 4);
    }

    [Fact]
    public void NamedZones()
    {
        HasPlace(P("Трасса Р-280 | Активность БПЛА Хорнет | Принять меры безопасности"), "Трасса Р-280 «Новороссия»");
        HasPlace(P("Керченский пролив | Опасность по БПЛА с юга"), "Керченский пролив");
        HasPlace(P("ЮБК | Ай-петри и близлежащие | Опасность по БПЛА"), "Южный берег Крыма");
        HasPlace(P("Балаклавская ТЭС и близлежащие | Тревога по БПЛА"), "Балаклавская ТЭС");
    }

    [Fact]
    public void RegionWordsAreNotSettlements()
    {
        // «Курской»/«Краснодарский» — регион, а не село «Курское»/хутор «Краснодарский»
        var p = P("Белгород, Белгородский район | Ракетная опасность! | Потенциальная опасность для Курской области и Воронежской области");
        Assert.DoesNotContain(p.Places, x => x.Label is "Курское" or "Потенциальная");
        Assert.DoesNotContain(P("Анапа | Тревога по БПЛА | Краснодарский край").Places, x => x.Label == "Краснодарский");
        // «Таганрогский залив» — зона над водой, а не пункт «Таганрогский»
        var gulf = P("Ейск и близлежащие, Таганрог, Таганрогский залив | Отбой опасности по БПЛА").Places;
        Assert.DoesNotContain(gulf, x => x.Label.StartsWith("Таганрогский") && x.Label != "Таганрогский залив");
        Assert.All(gulf.Where(x => x.Label == "Таганрогский залив"), x => Assert.True(x.IsArea));
    }

    [Fact]
    public void DirectionRoles()
    {
        var p = P("Шелковичное | И далее в направлении | Скворцово | Гвардейское | Республика Крым");
        HasPlace(p, "Шелковичное", target: false);
        var q = P("От Береговое до Фруктовое | Тревога по БПЛА с моря | Республика Крым");
        HasPlace(q, "Береговое", origin: true);
        HasPlace(P("БПЛА в напрвлении Лисичанск | ЛНР"), "Лисичанск", target: true);
    }

    [Fact]
    public void SourcesInUkraineAreForeign()
    {
        var p = P("Пуски БПЛА от Одесской области");
        Assert.Contains("ua_odesa", p.Regions);
        Assert.True(Gazetteer.IsForeign("ua_odesa"));
        HasPlace(P("От Очакова пуски БПЛА"), "Очаков", origin: true);
    }

    [Fact]
    public void AllBorderRegions()
    {
        var p = P("Все приграничные регионы внимание по возможной подготовке к пускам Штормов");
        Assert.Equal(ThreatType.Missile, p.Threat);
        Assert.True(p.Regions.Count >= 10);
    }

    [Fact]
    public void ClearAllPrevious()
    {
        var p = P("Отбой авиационной ракетной бомбовой опасности по всем ранее объявленным");
        Assert.Equal(AlertStatus.AllClear, p.Status);
        Assert.True(p.RefersToPrevious);
    }

    // ---------------- Мусор и информационные сообщения ----------------

    [Theory]
    [InlineData("Огромное спасибо! | ❤️❤️❤️ | https://pay.cloudtips.ru/p/f2a9c1d8")]
    [InlineData("Собрано для оборудования на МОГи | 3.915.000 рублей | ❤️❤️❤️ | Принять участие в сборе: | https://pay.cloudtips.ru/p/d8f0d6f7")]
    [InlineData("Работа основного чат-бота приема информации восстановлена.")]
    [InlineData("@Lpr1reklama | Наш рекламный менеджер для обсуждения условий размещения.")]
    [InlineData("Наш канал тревог в MAX | https://max.ru/LPRalarm")]
    [InlineData("‼️Если наш канал для вас важен, приносит пользу в тревожные минуты, то вы можете отблагодарить нас по ссылке ниже: | Ваша помощь позволяет нам постоянно развиваться | https://pay.cloudtips.ru/p/991df385")]
    public void JunkIsDropped(string text) => Assert.True(P(text).IsJunk);

    [Theory]
    [InlineData("Севастополь | Учения")]
    [InlineData("Новгородская область | 29 сентября - 07 октября | С 9.00-17.00 будет проводится проверка работоспособности громкоговорителей и сирен.")]
    [InlineData("Вследствие украинской вооруженной агрессии в Горловке погиб мирный житель. Вечная память невинно убиенному.")]
    [InlineData("При звуках сбитий БПЛА, не попадите под осколки. | Напоминаем, что работу ПВО снимать запрещено!")]
    public void InformationalNotOnMap(string text)
    {
        var p = P(text);
        Assert.False(IsSignal(p));
        Assert.False(p.IsJunk); // информационные остаются в ленте
    }

    [Theory]
    // Напоминания, содержащие действующую угрозу, — это сигнал, а не справка
    [InlineData("Напоминаем, что в обход Крыма шли  +- 20 БПЛА. Первые проходят Феодосию в направлении Керчь, Анапа, п. Волна")]
    [InlineData("Напоминаем, что противник до позднего утра завтрашнего дня будет осуществлять массовый запуск БПЛА в направлении ВСЕХ приграничных регионов.")]
    // Длинное предупреждение с призывом — сигнал
    [InlineData("Все населенные пункты Краснодарский край, которые мы пишем о тревоге с моря, настоятельно рекомендуем уходить с открытых участков местности, особенно пляжной зоны. Летят тяжёлые БПЛА с мощными зарядами и поражающими элементами.")]
    public void WarningsAreNeverDropped(string text) => Assert.True(IsSignal(P(text)), "предупреждение потеряно");

    [Theory]
    [InlineData("Курск | Опасность по аэростатам", ThreatKind.Balloon)]
    [InlineData("Белгород | МВШ со сбросами", ThreatKind.Balloon)]
    [InlineData("Брянск | Фиксация Фламинго", ThreatKind.MissileCruise)]
    [InlineData("Севастополь | ПКР Нептун", ThreatKind.MissileAntiShip)]
    [InlineData("Воронеж | реактивный БПЛА", ThreatKind.UavJet)]
    [InlineData("Шебекино | ФПВ ждун на оптоволокне", ThreatKind.UavFiber)]
    [InlineData("Грайворон | Баба Яга над городом", ThreatKind.UavHeavy)]
    [InlineData("Шебекино | Опасность УАБ", ThreatKind.GuidedBomb)]
    [InlineData("Белгород | Баллистика!", ThreatKind.MissileBallistic)]
    [InlineData("Шебекино | Обстрел из РСЗО Град", ThreatKind.Mlrs)]
    [InlineData("Маслова Пристань | Тревога по БПЛА Хорнет", ThreatKind.UavHornet)]
    [InlineData("Причерноморье | Опасность БЭК", ThreatKind.Usv)]
    public void KindDetection(string text, ThreatKind kind)
    {
        var p = P(text);
        Assert.True(p.Kind == kind || p.Kinds.Contains(kind), $"ожидали {kind}, получили {p.Kind} [{string.Join(",", p.Kinds)}]");
    }

    // ---------------- Тыловые каналы (radarrussiia, radar_rschs, nabludatel_rf, radar_samaraaaa …) ----------------

    [Theory]
    [InlineData("Кузнецкий район | Пензенская область | Фиксация БПЛА | ❗️Радар по всей России - @radarrussiia | 🌐 Обход белых списков - @Internet_Boost_bot", "penza", "Кузнецкий район")]
    [InlineData("Татищевский район | Саратовская область | Фиксация БПЛА | наблюдатель.рф | подписаться", "saratov", "Татищевский район")]
    [InlineData("Тула | Тульская область | Работа ПВО по БПЛА | ❗️Радар по всей России - @radarrussiia", "tula", "Тула")]
    [InlineData("Мещовский район | Жиздринский район | Фиксация групп БПЛА", "kaluga", "Мещовский район")]
    public void RearChannelFormat(string text, string region, string place)
    {
        var p = P(text);
        Assert.True(IsSignal(p), $"сигнал потерян: junk={p.JunkReason} info={p.IsInformational}");
        Assert.Equal(ThreatType.Uav, p.Threat);
        Assert.Contains(region, p.Regions.Concat(p.Places.Select(x => x.RegionId)));
        HasPlace(p, place);
    }

    [Fact]
    public void ChannelFooterDoesNotInjectRegion()
    {
        // название канала «Радар Самара» в подписи не должно давать Самарскую область каждому сообщению
        var p = P("Тольятти | Опасность по БПЛА | ❗️Радар БПЛА Самара - @radar_samaraaaa");
        Assert.True(IsSignal(p));
        var q = P("Ульяновская область | Опасность по БПЛА | ❗️Радар БПЛА Самара - @radar_samaraaaa");
        Assert.DoesNotContain("samara", q.Regions);
    }

    [Theory]
    [InlineData("Еще одно сбитие!")]
    [InlineData("Декабристов фиксация")]
    [InlineData("Работа ПВО по КР")]
    [InlineData("Приготовится к работе ПВО!")]
    [InlineData("Республика Марий-Эл | опасность по БПЛА")]
    public void ShortRegionalCuesAreSignals(string text) =>
        Assert.True(IsSignal(P(text, "samara")), "оперативная реплика регионального канала потеряна");

    [Theory]
    [InlineData("Пока без прямой угрозы региону, мониторим")]
    [InlineData("В нашем регионе фиксаций нет! Внимание по небу")]
    [InlineData("Пишите в сообщении канала если услышали или увидели фиксацию...")]
    public void CalmNotesAreNotAlerts(string text) => Assert.False(IsSignal(P(text, "samara")) && P(text, "samara").Status != AlertStatus.AllClear && P(text, "samara").Threat != ThreatType.None);

    [Theory]
    [InlineData("⚠️Вас упомянул администратор: | Пользователь: 112767! | Вход в этот приватный канал достается Вам - БЕСПЛАТНО!")]
    [InlineData("🔥 ЭКСПРЕСС НА 4 МАТЧА — КЭФФ 22.49 | 🇫🇷 Франция - Италия 🇮🇹 | 4 матча → Одна ставка → | ПРИБЫЛЬ 2 249 000")]
    [InlineData("Пригласил(-а) Вас в Закрытый канал")]
    [InlineData("❤️ ВРВ требуется ваша поддержка! | Мы не размещаем рекламу в канале и не зарабатываем на тревогах. | 🙌 Если канал вам полезен — поддержите любой суммой.")]
    [InlineData("❗️Уважаемые подписчики, чтобы не остаться без связи рекомендуем заранее подключить Ускоритель, поэтому просьба не игнорировать данный пост 🤝")]
    public void SpamIsJunk(string text) => Assert.True(P(text, "samara").IsJunk, "спам не отсеян");

    [Fact]
    public void JetAcceleratorIsNotSpam() =>
        Assert.True(IsSignal(P("Авиаторское район ремонтной базы запуски БПЛА с реактивным ускорителем. Много", "crimea")));

    // ---------------- Регионы-направления («Локатор России» и др.) ----------------

    [Theory]
    [InlineData("Новокардаильский, Новониколаевский район, Волгоградская область - массовый пролёт БПЛА в сторону Саратовской области.", "volgograd", "saratov")]
    [InlineData("Олейники, Вейделевский район, Белгородская область - пролёты БПЛА в сторону Воронежской области.", "belgorod", "voronezh")]
    [InlineData("Митрофановка, Кантемировский район, Воронежская область - пролёты БПЛА в направлении Богучар, Петропавловка далее на Волгоградскую область, Саратовскую область.", "voronezh", "saratov")]
    [InlineData("Мценск Орловская область фиксации БПЛА в направлении Тульской области", "orel", "tula")]
    public void TargetRegionIsTrajectoryEndNotAlertArea(string text, string alert, string target)
    {
        var p = P(text);
        Assert.True(IsSignal(p));
        Assert.Contains(alert, p.Regions);
        Assert.DoesNotContain(target, p.Regions);
        Assert.Contains(p.Places, x => x.RegionId == target && x.IsTarget);
    }

    [Fact]
    public void OriginRegionGivesHeading()
    {
        var p = P("Волгоградская область - опасность по БПЛА от Ростовской области.");
        Assert.Equal(["volgograd"], p.Regions);
        Assert.NotNull(p.Heading);
        Assert.InRange(p.Heading!.Value, 0, 180); // с юго-запада на северо-восток

        var q = P("Тольятти, Чапаевск, Сызрань, Самарская область - опасность по БПЛА от южной части Ульяновской области.");
        Assert.DoesNotContain("ulyanovsk", q.Regions);
        Assert.NotNull(q.Heading);
    }

    [Theory]
    // регион — единственное место в сообщении: остаётся под тревогой (лишнее предупреждение безопаснее)
    [InlineData("БПЛА в сторону Саратовской области", "saratov")]
    [InlineData("Воронежская область, Орловская область | Ракетная тревога по ПКР Нептун от Курской области", "orel")]
    public void LoneDirectionRegionStaysAlerted(string text, string region) => Assert.Contains(region, P(text).Regions);

    // ---------------- Компасные уточнения ----------------

    [Fact]
    public void CompassAdverbShiftsPoint()
    {
        var plain = P("Дивеево, Нижегородской области - ударный БПЛА в сторону Арзамаса.");
        var ne = P("северо-восточнее Дивеево, Нижегородской области - предварительно ударный БПЛА в сторону Арзамаса.");
        var a = plain.Places.First(x => x.Label == "Дивеево").Pos!.Value;
        var b = ne.Places.First(x => x.Label == "Дивеево").Pos!.Value;
        Assert.True(b.Lat > a.Lat && b.Lon > a.Lon, "точка не смещена на северо-восток");
        Assert.InRange(Geo.DistanceKm(a, b), 8, 20);
    }

    [Theory]
    [InlineData("Южная часть Ростовской области | Тревога по БПЛА", "rostov", "Юг", -1)]
    [InlineData("Тревога по БПЛА на юге Ульяновской области", "ulyanovsk", "Юг", -1)]
    [InlineData("Северная часть | Опасность по БПЛА", "rostov", "Север", 1)]
    public void RegionPartIsZoneNotWholeRegion(string text, string region, string side, int latSign)
    {
        var p = P(text, region);
        Assert.True(IsSignal(p));
        Assert.Contains(region, p.Regions);
        var zone = p.Places.FirstOrDefault(x => x.RegionId == region && x.Label.StartsWith(side));
        Assert.True(zone != null, $"нет зоны «{side}»: {string.Join("; ", p.Places.Select(x => x.Label))}");
        Assert.Equal(latSign, Math.Sign(zone!.Pos!.Value.Lat - Gazetteer.Get(region)!.Center.Lat));
        Assert.DoesNotContain(p.Places, x => x.Label is "Южная" or "Северная" or "Северное" or "Южное");
    }

    [Fact]
    public void CityPartIsNotRegionPart()
    {
        var p = P("Южная часть Белгорода тревога по БПЛА");
        Assert.DoesNotContain(p.Places, x => x.Label.StartsWith("Юг —"));
        HasPlace(p, "Белгород");
    }

    [Theory]
    // «Отбой не даём», «Отбоя не было», «до отбоя» — тревога продолжается
    [InlineData("Тульская область | Отбой не даём. | Опасность сохраняется")]
    [InlineData("Реактивная цель Брянская область сбита. | Отбой не даём, так как ожидаем пуски Штормов")]
    [InlineData("Напоминаем, что в Орловской области действует режим опасности по БПЛА | Отбоя не было.")]
    [InlineData("Самарская область | Ракетная опасность сохраняется до отбоя")]
    public void NegatedAllClearIsNotAllClear(string text) => Assert.NotEqual(AlertStatus.AllClear, P(text).Status);
}
