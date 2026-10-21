using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

public sealed class ParsedPlace
{
    public required string Label { get; init; }
    public string? RegionId { get; set; }
    public GeoPoint? Pos { get; set; }
    public bool IsTarget { get; init; }
    public bool IsOrigin { get; init; }
    /// <summary>Промежуточный пункт маршрута («через Литвиновку», «через Геническ в направлении …»).</summary>
    public bool IsVia { get; init; }
    /// <summary>Район/округ, а не населённый пункт (больший радиус зоны).</summary>
    public bool IsArea { get; init; }
    /// <summary>Явный радиус зоны (для именованных зон), км.</summary>
    public double? ZoneRadiusKm { get; init; }
    /// <summary>Требуется геокодирование (населённый пункт не найден во встроенном справочнике).</summary>
    public string? GeocodeQuery { get; init; }
    /// <summary>«Южная часть области»: регион и сторона света (null — центр). Рисуется районами этой части.</summary>
    public string? PartRegion { get; init; }
    public double? PartBearing { get; init; }
    /// <summary>Акватория («Таганрогский залив», «Азовское море»): id контура в слое ориентиров («sea:…»).</summary>
    public string? SeaId { get; init; }
    /// <summary>«По границе Курской и Брянской областей»: id линии общей границы («border:a+b»).</summary>
    public string? BorderId { get; init; }
}

public sealed class ParsedMessage
{
    /// <summary>Отсылка к ранее объявленному («по всем ранее объявленным», «по указанным направлениям»).</summary>
    public bool RefersToPrevious { get; set; }
    public required RawMessage Raw { get; init; }
    public ThreatType Threat { get; set; }
    public ThreatKind Kind { get; set; }
    public List<ThreatKind> Kinds { get; set; } = [];
    public AlertLevel Level { get; set; } = AlertLevel.Alarm;
    public AlertStatus Status { get; set; }
    public List<string> Regions { get; } = [];
    public List<ParsedPlace> Places { get; } = [];
    public List<ParsedPlace> PendingPlaces { get; } = [];
    /// <summary>Повторное применение отбоя после геокодирования его пунктов (без проверки дублей и без своей отметки).</summary>
    internal bool Reapply { get; set; }
    /// <summary>Курс, указанный словами («на северо-восток», «с юга»), в градусах.</summary>
    public double? Heading { get; set; }
    /// <summary>Оценка количества объектов («группа», «2 БПЛА», «массированно»).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Сообщение подтверждает уже объявленную угрозу («сохраняется», «повторно»).</summary>
    public bool IsContinuation { get; set; }
    public string CleanText { get => _cleanText; set { _cleanText = value; _displayText = null; } }
    private string _cleanText = "";
    private string? _displayText;
    /// <summary>Текст для ленты и карточек: без промо-строк каналов.</summary>
    public string DisplayText => _displayText ??= MessageParser.StripPromo(CleanText);
    /// <summary>Реклама, донаты, служебные объявления — не сигнал.</summary>
    public bool IsJunk { get; set; }
    /// <summary>Информационное сообщение (учения, новости, итоги) — показывается в ленте, но не на карте.</summary>
    public bool IsInformational { get; set; }
    public string? JunkReason { get; set; }
    /// <summary>«…и близлежащие», «окрестности», «прилегающие» — опасность распространяется на соседние территории.</summary>
    public bool Nearby { get; set; }
    /// <summary>Подсказки для маршрута: коридоры (трасса, ж/д, река, берег) и место пуска.</summary>
    public RouteHints Route { get; set; } = new();
    /// <summary>
    /// Место пуска на стороне противника, если сообщение — о пуске, но в РФ уже названо место тревоги
    /// («Пуски УАБ от Изюма в направлении Белгородской области»): тревога по региону + отдельная отметка пуска.
    /// </summary>
    public ParsedPlace? LaunchSite { get; set; }
    public AlertLevel LaunchLevel { get; set; } = AlertLevel.Alarm;
    /// <summary>Регионы РФ, названные источником («от Харьковской области») и потому снятые с тревоги.</summary>
    public List<string> OriginRegions { get; } = [];
    /// <summary>
    /// Регионы с уровнем ниже общего, явно указанным в своей строке («Ростовская область — опасность.
    /// Белгородская область — внимание»): движок выделяет их в отдельную тревогу этого уровня.
    /// </summary>
    public Dictionary<string, AlertLevel> RegionLevels { get; } = new(StringComparer.Ordinal);
    /// <summary>Часть разделённого сообщения (ключ для дедупликации), null — сообщение целиком.</summary>
    public string? Part { get; init; }
    /// <summary>Сводка блоками с разным статусом (тревога и отбой в одном сообщении): разобранные блоки по порядку.</summary>
    public List<ParsedMessage> Blocks { get; } = [];
}

/// <summary>Эвристический разбор текстов тревог из Telegram-каналов.</summary>
public static partial class MessageParser
{
    // разбор вызывает статические Regex.IsMatch/Replace с десятками разных шаблонов: в кеш по умолчанию (15) они не
    // помещались и перестраивались на каждое сообщение — до трети времени разбора архива
    static MessageParser() { if (Regex.CacheSize < 256) Regex.CacheSize = 256; }

    private static readonly string[] UavStems =
    [
        "бпла", "беспилот", "дрон", "шахед", "герань", "герани", "лютый", "лютых", "fpv", "фпв", "хорнет", "дартс", "шарк",
        "фламинго", "ланцет", "баба яга", "бабе яге", "бабы яги", "мавик", "крыло", "матка", "мрш", "мвш", "камикадзе", "ударный", "фвп", "пфв", "барражир",
    ];
    private static readonly string[] MissileStems = ["ракет", "баллист", "крылат", "хаймарс", "himars", "атакмс", "atacms", "шторм", "нептун", "пкр", "кинжал", "циркон", "калибр", "х-101", "х101", "х-59", "х-22"];
    private static readonly string[] AviationStems = ["авиацион", "авиабомб", "каб", "уаб", "авиаци", "самолет", "самолёт", "f-16", "ф-16", "f16", "ф16", "су-24", "су-27", "миг-29", "вертолет", "вертолёт"];
    private static readonly string[] NavalStems = ["бэк", "катер", "безэкипаж"];
    private static readonly string[] ArtilleryStems = ["обстрел", "артиллер", "рсзо", "минометн", "миномет", "дистанционное минирование", "минирован", "кассет"];

    private static readonly string[] AllClearStems = ["отбой"];
    private static readonly string[] AirDefenseStems = ["пво", "сбит", "уничтож", "перехват", "подавлен"];
    private static readonly string[] ImpactStems = ["взрыв", "прилет", "попадан", "детонац", "пожар", "поражен"];

    private static readonly HashSet<string> TargetMarkers = new(StringComparer.Ordinal) { "направлении", "направление", "сторону", "на", "к", "курсом", "далее" };
    private static readonly HashSet<string> OriginMarkers = new(StringComparer.Ordinal) { "от", "из", "со", "стороны" };

    // слова, после удаления которых строка может оказаться названием населённого пункта
    private static readonly string[] ServiceStems =
    [
        "тревог", "опасн", "отбой", "бпла", "беспилот", "ракет", "фиксац", "сохраня", "сохранен", "внимани", "повторн", "режим",
        "соблюда", "меры", "безопасн", "пво", "работа", "област", "край", "края", "республик", "массов", "продолжа",
        "направлени", "сторон", "тыл", "предельн", "укрыт", "http", "подписат", "канал", "радар", "спасибо", "поддерж",
        "угроз", "атак", "дрон", "взрыв", "прилет", "катер", "авиац", "особое", "моря", "юга", "севера", "запада", "востока",
        "акватори", "выделенн", "участк", "все", "районы", "нпз", "электротяг", "реактив", "группы", "групп",
        "близлежащ", "ближайш", "окрестност", "прилегающ", "далее", "между", "стык", "граница", "границ", "возможн", "сброс",
        "принять", "повтор", "множествен", "угроза", "округов",
        // направления и служебные слова, которые каналы пишут отдельной строкой («Северо-Запад», «Обстрел», «Ложная»)
        "северо-", "юго-", "обстрел", "ложн", "думайте", "коллег", "действует", "предположит", "убедител", "зарядит",
        "телефон", "водител", "максимальн", "вражеск", "барражир", "береговая", "трасс", "сутки", "завтра", "итого",
    ];

    /// <summary>
    /// Срезает «подвал» канала — хвостовые строки с названием и ссылкой на канал/бота («❗️Радар Москва - @radar_moscoww»,
    /// «🌐 Обход белых списков - @…», «наблюдатель.рф / подписаться»). Иначе название канала («Радар Самара») читается
    /// как упоминание региона в каждом сообщении.
    /// </summary>
    public static string StripFooter(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        int removed = 0;
        // «📲 Наш канал в МАХ -» + ссылка отдельной строкой
        if (lines.Count > 2 && MaxFooterRx().IsMatch(lines[^1].Trim()) && IsFooter(lines[^2].Trim()))
        {
            lines.RemoveAt(lines.Count - 1);
            removed++;
        }
        while (lines.Count > 1 && removed < 5 && IsFooter(lines[^1].Trim()))
        {
            lines.RemoveAt(lines.Count - 1);
            removed++;
            while (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        }
        return removed == 0 ? text.Trim() : string.Join('\n', lines).Trim();
    }

    /// <summary>
    /// Текст для показа: без промо-строк и в середине («подписывайтесь на канал в MAX …», «❗️Радар Москва - @…»).
    /// Разбор идёт по полному тексту — промо помогает распознать рекламные посты как мусор.
    /// </summary>
    public static string StripPromo(string text)
    {
        var lines = text.Split('\n').ToList();
        int before = lines.Count;
        lines.RemoveAll(l => l.Trim() is { Length: > 0 } t && (IsFooter(t) || PromoLine().IsMatch(t)) && !FooterStatusWords().IsMatch(Gazetteer.Normalize(t)));
        if (lines.Count == before || lines.All(string.IsNullOrWhiteSpace)) return text;
        return BlankLinesRx().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"подпис(?:ывайтесь|ыватесь|аться|ывайся|ка на|ись)|обход белых списков|рекомендуем подписаться|^уважаемые подписчики|канал(?:е|ы)? в (?:max|мах)\b|^[^\p{L}\d]*(?:https?://)?max\.ru/\S*$", RegexOptions.IgnoreCase)]
    private static partial Regex PromoLine();

    // строка с формулировкой тревоги никогда не считается подписью, даже если в ней есть @канал
    private static bool IsFooter(string line) => line.Length <= 300 && FooterLine().IsMatch(line) && !FooterStatusWords().IsMatch(Gazetteer.Normalize(line));

    /// <summary>Строка — подпись или промо канала («‼️Подписывайся на Радар ВРВ», «📡Локатор России - @locatorru»).</summary>
    internal static bool IsFooterLine(string line) =>
        IsFooter(line) || PromoLine().IsMatch(line) && !FooterStatusWords().IsMatch(Gazetteer.Normalize(line));

    [GeneratedRegex(@"опасност|тревог|отбой|пво|сбит|фиксац|взрыв|прил[её]т|угроз|укрыт|атак|пуск|летит|летят|обстрел")]
    private static partial Regex FooterStatusWords();

    // Строка-подпись: ссылка на канал/бота (без текста тревоги) либо известные служебные хвосты
    [GeneratedRegex(@"^(?:[^\p{L}\d]*(?:[\p{L}\d .,«»""'|•—–-]{0,60}?)(?:[—–-]\s*)?@[A-Za-z0-9_]{4,}[^\p{L}]*|[^\p{L}\d]*(?:https?://)?t\.me/\S+|наблюдатель\.рф(?:\s*[|•/—–-]\s*подписаться)?|подписаться|[^\p{L}\d]*радар самара (?:в max|подпишись!?)|[^\p{L}\d]*📲.*\b(?:max|мах)\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FooterLine();

    public static ParsedMessage Parse(RawMessage raw, IReadOnlyList<string> channelRegions)
    {
        var whole = ParseCore(raw, channelRegions, null, null);
        if (whole.IsJunk || whole.IsInformational) return whole;
        // «Азов, Таганрог ⏎ Опасность по БПЛА ⏎ Курская … Ярославская область ⏎ отбой опасности по БПЛА» — блоки со своим статусом
        var blocks = StatusBlocks(StripFooter(raw.Text));
        if (blocks.Count > 1)
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = ParseCore(raw, channelRegions, blocks[i], "blk:" + i);
                if (!b.IsJunk && (b.Regions.Count > 0 || b.Places.Count > 0)) whole.Blocks.Add(b);
            }
        if (whole.Blocks.Count < 2 || whole.Blocks.All(b => b.Status == whole.Blocks[0].Status)) whole.Blocks.Clear();
        if (whole.Status == AlertStatus.AllClear && whole.Blocks.Count == 0 && ClearThenAlert(StripFooter(raw.Text), whole) is var (clearText, alertText, where))
        {
            var c = ParseCore(raw, channelRegions, clearText, "blk:0");
            var a = ParseCore(raw, channelRegions, alertText, "blk:1");
            // в части тревоги нет своих мест («Тревога по реактивным БПЛА») — места те же, что у отбоя
            if (a.Regions.Count + a.Places.Count == 0 && where.Length > 0) a = ParseCore(raw, channelRegions, alertText + "\n" + where, "blk:1");
            if (!c.IsJunk && !a.IsJunk && c.Status == AlertStatus.AllClear && a.Status != AlertStatus.AllClear &&
                a.Threat != ThreatType.None && a.Regions.Count + a.Places.Count > 0)
            {
                whole.Blocks.Add(c);
                whole.Blocks.Add(a);
            }
        }
        return whole;
    }

    /// <summary>
    /// «Брянская область отбой ракетной опасности ⏎ Тревога по реактивным БПЛА», «Отбой по всем ранее объявленным
    /// Херсонская область ⏎ Внимание по БПЛА на юге»: после отбоя объявлена новая угроза. Возвращает текст отбоя
    /// (до строки отбоя включительно), текст тревоги (всё после неё) и места отбоя — для тревоги без своих мест.
    /// «… БПЛА сохраняется / в действии», «переходим в режим внимания» — не новая тревога, а пояснение к отбою.
    /// </summary>
    private static (string Clear, string Alert, string Where)? ClearThenAlert(string text, ParsedMessage whole)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        int ci = lines.FindIndex(l => Regex.IsMatch(Gazetteer.Normalize(l), @"\b(?:отбой|отмена)\b"));
        if (ci < 0) return null;
        for (int ai = ci + 1; ai < lines.Count; ai++)
        {
            var n = Gazetteer.Normalize(lines[ai]);
            if (!Regex.IsMatch(n, @"^[^\p{L}\d]*(?:тревога|опасность|внимание|угроза)\b") || !Regex.IsMatch(n, @"бпла|беспилот|дрон|ракет|авиац|баллист|уаб|\bкаб"))
                continue;
            // пояснение к отбою: угроза названа как сохраняющаяся (в этой строке или следующей)
            var tail = n + " " + (ai + 1 < lines.Count ? Gazetteer.Normalize(lines[ai + 1]) : "");
            if (Regex.IsMatch(tail, @"\bотбой\b|сохраня|в действии|действует|оста[её]тся|оставля|продолжа|режим")) return null;
            // места отбоя — строки до него и строка отбоя без самой фразы («Брянская область отбой …» → «Брянская область»)
            var where = lines.Take(ci).Append(Regex.Replace(lines[ci], @"(?i)\b(?:отбой|отмена)\b.*$", "").Trim())
                             .Where(l => l.Length > 0).ToList();
            if (where.Count == 0) where = whole.Regions.Select(r => Gazetteer.Get(r)?.Name ?? r).ToList();
            return (string.Join('\n', lines.Take(ci + 1)), string.Join('\n', lines.Skip(ci + 1)), string.Join('\n', where));
        }
        return null;
    }

    /// <summary>
    /// Блоки сводки: строки мест, завершённые строкой статуса («Опасность по БПЛА», «Отбой …»). Делится, только если
    /// есть и тревога, и отбой, и в каждом блоке перед строкой статуса есть строки мест.
    /// </summary>
    private static List<string> StatusBlocks(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var res = new List<string>();
        var cur = new List<string>();
        bool alert = false, clear = false;
        foreach (var l in lines)
        {
            var n = Gazetteer.Normalize(l);
            bool isClear = Regex.IsMatch(n, @"^[^\p{L}\d]*отбой\b");
            bool isAlert = Regex.IsMatch(n, @"^[^\p{L}\d]*(?:опасность|тревога|угроза|внимание)\b") && Regex.IsMatch(n, @"бпла|беспилот|ракет|авиац|дрон");
            if (isClear || isAlert)
            {
                if (cur.Count == 0) return [];
                cur.Add(l);
                res.Add(string.Join('\n', cur));
                cur = [];
                if (isClear) clear = true; else alert = true;
            }
            else cur.Add(l);
        }
        // хвост без строки статуса (режим, подпись) — к последнему блоку
        if (cur.Count > 0 && res.Count > 0) res[^1] += "\n" + string.Join('\n', cur);
        return alert && clear ? res : [];
    }

    private static ParsedMessage ParseCore(RawMessage raw, IReadOnlyList<string> channelRegions, string? blockText, string? part)
    {
        // «Славянск -на-Кубани», «Ростов- на-Дону» — пробел у дефиса внутри составного названия
        // «Славянск -на-» → «Славянск-на-», «Ростов- на-» → «Ростов-на-» (дефис уже есть — пробел просто убирается)
        var body = HyphenGapRx().Replace(FixKraiTypos(blockText ?? StripFooter(raw.Text)), m => m.Value.Contains('-') ? "-" : "");
        var msg = new ParsedMessage { Raw = raw, CleanText = body, Part = part };
        var text = WeatherStorm().Replace(SplitHyphenPairs(body), " ");
        var norm = Gazetteer.Normalize(text);
        var tokens = Gazetteer.Tokenize(text);

        msg.JunkReason = DetectJunk(norm, tokens);
        msg.IsJunk = msg.JunkReason != null;
        if (msg.IsJunk) return msg;

        msg.Threat = DetectThreat(tokens, norm);
        msg.Status = DetectStatus(tokens, norm);
        msg.Heading = DetectHeading(norm);
        msg.Count = DetectCount(tokens, norm);
        msg.IsContinuation = norm.Contains("сохраня") || norm.Contains("повторно") || norm.Contains("продолжа");

        // Регионы, явно упомянутые в тексте
        var mentioned = new List<(string Region, int Index)>();
        for (int i = 0; i < tokens.Count; i++)
        {
            // «Республика Алтай», «Горный Алтай» — республика, а не Алтайский край
            if (tokens[i] is "алтай" or "алтая" or "алтае" && i > 0 && tokens[i - 1] is "республика" or "республики" or "республике" or "горный" or "горного" or "горном")
            {
                mentioned.Add(("altai_rep", i));
                continue;
            }
            // регионы, однословное название которых совпадает с частыми топонимами: только «… край»
            if (i + 1 < tokens.Count && tokens[i + 1] is "край" or "края" or "крае" or "краю")
            {
                var krai = tokens[i] switch
                {
                    "приморский" or "приморского" or "приморском" or "приморскому" => "primorye",
                    "камчатский" or "камчатского" or "камчатском" or "камчатскому" => "kamchatka",
                    _ => null,
                };
                if (krai != null) { mentioned.Add((krai, i)); continue; }
            }
            // «Московской трассы», «Харьковское шоссе» — дорога, а не регион
            if (i + 1 < tokens.Count && Regex.IsMatch(tokens[i + 1], @"^(?:трасс[аыеуо]й?|шосс[еи])$") && Regex.IsMatch(tokens[i], @"(?:ск|цк)(?:ая|ой|ую|ое|ого|ом)$"))
                continue;
            // станицы Кубани с названиями областей («Ленинградская, Кущевская», «Полтавская, Славянск-на-Кубани»):
            // без «область» рядом и в сообщении о Краснодарском крае — это станица, а не регион
            if (KubanStanitsa(tokens, i, norm)) continue;
            // «Станица Луганская», «в сторону Станицы Луганской» — населённый пункт, а не ЛНР
            // но «станиц Краснодарского края», «в станицах Ростовской области» — регион
            if (i > 0 && (tokens[i - 1].StartsWith("станиц", StringComparison.Ordinal) || tokens[i - 1] == "ст") &&
                !(i + 1 < tokens.Count && tokens[i + 1] is "область" or "области" or "обл" or "край" or "края" or "крае" or "республика" or "республики"))
                continue;
            // «Ивановская Лисица», «Брянские Липяги» (Белгородская обл.) — обжитой пункт из двух слов с прилагательным-регионом
            if (Gazetteer.RegionsForWord(tokens[i]).Count > 0 && RegionWordInPlaceName(tokens, i, channelRegions)) continue;
            foreach (var r in Gazetteer.RegionsForWord(tokens[i]))
                mentioned.Add((r, i));
        }
        foreach (var (r, _) in mentioned)
            if (!msg.Regions.Contains(r)) msg.Regions.Add(r);

        var context = new HashSet<string>(msg.Regions, StringComparer.Ordinal);
        foreach (var r in channelRegions) context.Add(r);

        // Известные населённые пункты
        var rawTokens0 = Gazetteer.TokenizeRaw(text);
        var matches = Gazetteer.FindPlaces(tokens).ToList();
        var used = new HashSet<int>();
        var byName = matches.GroupBy(m => (m.Index, m.Place.Name));
        // при совпадении начала — сначала более длинное название («Донецк Ростовский» раньше «Донецк»)
        foreach (var g in byName.OrderBy(g => g.Key.Index).ThenByDescending(g => g.Max(m => m.Length)))
        {
            // один пункт может совпасть несколькими формами алиаса («донецк ростовский», «донецк рост») — это один вариант
            var options = g.Select(m => m.Place).Distinct().ToList();
            // то же слово под другим названием («Калач» — и воронежский Калач, и Калач-на-Дону): регион из сообщения решает
            int glen = g.Max(m => m.Length);
            var sameSpot = matches.Where(m => m.Index == g.Key.Index && m.Length == glen && m.Place.Name != g.Key.Name).Select(m => m.Place).ToList();
            var pick = options.FirstOrDefault(p => msg.Regions.Contains(p.RegionId))
                       ?? (sameSpot.Any(p => msg.Regions.Contains(p.RegionId)) ? null : options.FirstOrDefault(p => context.Contains(p.RegionId)))
                       ?? (sameSpot.Any(p => msg.Regions.Contains(p.RegionId) || context.Contains(p.RegionId)) ? null
                           : options.Count == 1 && !options[0].Ambiguous ? options[0] : null);
            if (pick == null) continue;
            var (idx, len) = (g.First().Index, g.First().Length);
            if (Enumerable.Range(idx, len).Any(used.Contains)) continue;
            // «Красногвардейском МО», «Курский район» — это район, а не одноимённый пункт
            int after = idx + len;
            bool adjForm = tokens[idx].EndsWith("ский") || tokens[idx].EndsWith("ском") || tokens[idx].EndsWith("ского") || tokens[idx].EndsWith("ской") || tokens[idx].EndsWith("цкий");
            if (adjForm && after < tokens.Count && IsAdminSuffix(tokens[after])) continue;
            if (IsAdjListItem(tokens, idx)) continue;
            if (IsCompassWord(tokens, idx)) continue;
            // «на Донецком направлении», «по Черноморскому побережью» — прилагательное от названия, а не сам пункт
            if (len == 1 && rawTokens0.Count == tokens.Count && AdjectiveOfNoun(rawTokens0, idx, [.. context]) && Gazetteer.Normalize(pick.Name) != tokens[idx]) continue;
            if (len == 1 && NamedAfter(tokens, idx, pick.Name)) continue;
            // «Новую Каховку», «Малого Полпино», «новой Таволжанки»: слово перед пунктом — часть более длинного названия
            // (другой пункт того же региона), а не определение к нему
            string pickName = pick.Name, pickRegion = pick.RegionId;
            GeoPoint pickPos = pick.Pos;
            if (idx > 0 && !used.Contains(idx - 1) && tokens[idx - 1].Length >= 4 && Regex.IsMatch(tokens[idx - 1], @"(?:ая|ую|ой|ое|ого|ом|ий|ый|ие|ые|ых|их|ья|ью|ье|ьей)$") &&
                PlaceIndex.Find(tokens[idx - 1] + " " + string.Join(' ', tokens.Skip(idx).Take(len)), context.Count > 0 ? [.. context] : Frontline) is
                    { Kind: 'P' } longer && longer.Name.EndsWith(pick.Name, StringComparison.OrdinalIgnoreCase) && longer.Name.Length > pick.Name.Length + 2)
            {
                (pickName, pickRegion, pickPos) = (longer.Name, longer.RegionId, longer.Pos);
                idx--; len++;
            }
            for (int k = idx; k < idx + len; k++) used.Add(k);
            // слово-регион («Орёл», «Севастополь») не должно дублироваться регионом
            var role = RoleAt(tokens, idx);
            if (role == 1 && LocativeNa(tokens, idx, pickName)) role = 0;
            AddPlace(msg, new ParsedPlace
            {
                Label = pickName, RegionId = pickRegion, Pos = pickPos,
                IsTarget = role == 1, IsOrigin = role == -1, IsVia = IsViaAt(tokens, idx),
            });
        }

        // слова, обозначающие сам регион, и «область/край/республика» рядом с ними — не населённые пункты
        foreach (var (_, idx) in mentioned)
        {
            // «Курский район», «Крымский район» — это район, а не регион: слово остаётся доступным для поиска района
            if (idx + 1 < tokens.Count && IsAdminSuffix(tokens[idx + 1])) continue;
            used.Add(idx);
            if (idx + 1 < tokens.Count && tokens[idx + 1] is "область" or "области" or "край" or "края" or "крае" or "рф" or "республика" or "республики") used.Add(idx + 1);
            if (idx > 0 && tokens[idx - 1] is "республика" or "республики" or "республике" or "горный" or "горного" or "горном") used.Add(idx - 1);
        }
        // «Таганрогский залив», «Керченский пролив», «Азовское море» — акватории, не пункты
        for (int i = 0; i + 1 < tokens.Count; i++)
            // «Раздольненский район, ⏎ залив Красноперекопск» — «район» не часть названия залива
            if (tokens[i + 1] is "залив" or "залива" or "заливе" or "пролив" or "пролива" or "проливе" or "море" or "моря" or "коса" or "косы" or "полуостров" or "полуострова"
                && !IsAdminSuffix(tokens[i]))
                { used.Add(i); }

        // Перечни районов/округов (официальные сообщения РСЧС и оперштабов):
        //   «Абинский, Крымский, Темрюкский районы», «в Волоконовском, Валуйском ... МО»,
        //   «на территории муниципальных образований: Брюховецкий, Ейский, Каневской, ...»
        int listStart = norm.IndexOf("муниципальных образовани", StringComparison.Ordinal);
        if (listStart < 0) listStart = norm.IndexOf("муниципальных округов", StringComparison.Ordinal);
        for (int i = 0; i < tokens.Count; i++)
        {
            bool tailMarker = tokens[i] is "районы" or "районов" or "округа" or "округов" or "мо" or "го" or "районах" or "округах"
                or "районам" or "округам" or "районами" or "округами";
            // «Рыльский, Хомутовский, Глушковский район», «Кантемировский ⏎ Богучарский район»: перечень перед одиночным «район»
            if (!tailMarker && tokens[i] is "район" or "округ" && i >= 2 && IsAdjToken(tokens[i - 1]) && (IsAdjToken(tokens[i - 2]) || (i >= 3 && tokens[i - 2] == "и" && IsAdjToken(tokens[i - 3]))))
                tailMarker = true;
            // «Обоянский, Солнцевский, Курский и далее на север Курская область»: перечень из 2+ прилагательных,
            // сразу за которым идёт служебное продолжение — районы названного дальше региона
            if (!tailMarker && i >= 2 && IsAdjToken(tokens[i - 1]) && IsAdjToken(tokens[i - 2]) && tokens[i] is "и" or "далее" or "тревога" or "опасность"
                && !Gazetteer.IsRegionWord(tokens[i - 1]))
                tailMarker = true;
            // «В границах муниципальных округов: Краснодонский, Лутугинский … и городов …» — перечень до «и городов»
            if (!tailMarker && listStart >= 0 && tokens[i] is "городов" or "города" && i >= 1 && tokens[i - 1] == "и") tailMarker = true;
            if (!tailMarker && !(listStart >= 0 && i == tokens.Count - 1)) continue;
            var ctx = msg.Regions.Concat(channelRegions).Distinct().ToList();
            if (ctx.Count == 0) ctx = [.. Frontline];
            for (int k = i - (tailMarker ? 1 : 0); k >= 0 && k >= i - 40; k--)
            {
                if (used.Contains(k)) continue;
                var t = tokens[k];
                bool adj = t.EndsWith("ский") || t.EndsWith("цкий") || t.EndsWith("ской") || t.EndsWith("ный") || t.EndsWith("ском") || t.EndsWith("ского")
                           || t.EndsWith("цком") || t.EndsWith("ном") || t.EndsWith("скому") || t.EndsWith("цкому") || t.EndsWith("ским") || t.EndsWith("цким");
                if (!adj)
                {
                    // служебные слова внутри перечня («муниципальный округ», «г», названия городов) пропускаем, остальное — конец перечня
                    if (t is "муниципальный" or "муниципальных" or "городской" or "городских" or "округ" or "район" or "г" or "и" or "в") continue;
                    if (listStart >= 0 && k > 0) continue;
                    break;
                }
                // «по Приморскому, Бердянскому районам», «Ивнянским районом» → именительный падеж
                if (t.EndsWith("ому")) t = t[..^3] + "ий";
                else if (t.EndsWith("им") && t.Length > 5) t = t[..^2] + "ий";
                else if (t.EndsWith("ского") || t.EndsWith("цкого")) t = t[..^3] + "ий";
                else if (t.EndsWith("ском") || t.EndsWith("цком")) t = t[..^2] + "ий";
                var hit = ctx.Count > 0 ? PlaceIndex.Find(t + " район", ctx) : PlaceIndex.FindUnique(t + " район", Frontline);
                // «Рыльский, Хомутовский, Глушковский район, стык с Брянской областью»: соседний регион в тексте — не регион
                // района; берём однозначный в прифронтовых, если в сообщении уже есть пункт его региона (Глушковский район)
                // — иначе «Троицкий Губкинский ГО» стал бы Троицким районом ЛНР, а «Кировский район» Донецка — крымским
                if (hit == null && !ctx.SequenceEqual(Frontline) && PlaceIndex.FindUnique(t + " район", Frontline) is { } fh &&
                    (msg.Places.Any(p => p.RegionId == fh.RegionId && p.Pos is { } pp && Geo.DistanceKm(pp, fh.Pos) < 120) ||
                     ListNeighbour(tokens, k, i, fh)))
                    hit = fh;
                // тыловые каналы («Радар по всей России»): район, однозначный по всей РФ
                if (hit == null && ctx.SequenceEqual(Frontline)) hit = PlaceIndex.FindUnique(t + " район", AllRu.Value);
                if (hit == null) continue;
                used.Add(k);
                // «далее в направлении Советский, Кировский район» — цели; «от Рыльского, Глушковского района» — откуда
                var dRole = RoleAt(tokens, k);
                // «через Суджанский и Большесолдатский районы» — промежуточные районы маршрута
                AddPlace(msg, new ParsedPlace { Label = DistrictLabel(hit), RegionId = hit.RegionId, Pos = hit.Pos, IsArea = true, IsTarget = dRole == 1, IsOrigin = dRole == -1, IsVia = IsViaAt(tokens, k) });
            }
        }

        var rawTokens = Gazetteer.TokenizeRaw(text);
        // Сокращения регионов в сообщениях: «Бгд» — Белгородская область, «Брк» — Брянская и т. п.
        foreach (var (abbr, region) in RegionAbbr)
            if (tokens.Contains(abbr) && !msg.Regions.Contains(region)) msg.Regions.Add(region);

        // Именованные зоны (трассы, полуострова, проливы, ТЭС)
        var zoneSpans = new List<(int Start, int End)>();
        foreach (var z in Zones)
            if (z.Pattern.IsMatch(norm))
            {
                if (z.Label == CoastZones.AzovLabel && CoastZones.Azov.Count > 0)
                {
                    AddAzovCoast(msg, tokens, used, channelRegions);
                    continue;
                }
                // «Причерноморье» без Кубани — побережье названного региона («Херсонская область РФ ⏎ Причерноморье»)
                // или регионального канала (Крым): не Кубань
                if (z.Label == "Черноморское побережье Кубани" && !KubanCoastApplies(norm, msg.Regions, channelRegions)) continue;
                // «залив Сиваш», «Сасык-Сиваш»: слово внутри названия водоёма — не одноимённое село в другом регионе.
                // «залив Красноперекопск» — город на берегу остаётся: лишняя тревога безопаснее пропущенной
                if (z.Label is "Залив Сиваш" || z.Label.StartsWith("Озеро ", StringComparison.Ordinal))
                    foreach (Match zm in z.Pattern.Matches(norm)) zoneSpans.Add((zm.Index, zm.Index + zm.Length));
                // одноимённый пункт справочника («Арабатская стрелка») заменяется зоной с сохранением роли
                var same = msg.Places.FirstOrDefault(x => x.ZoneRadiusKm == null && Gazetteer.Normalize(x.Label) == Gazetteer.Normalize(z.Label));
                if (same != null) msg.Places.Remove(same);
                // акватория у нескольких регионов (Таганрогский залив — Ростовская обл. и Краснодарский край):
                // регион — названный в сообщении, иначе канала, иначе основной
                bool water = WaterZones.TryGetValue(z.Label, out var shores);
                if (!water) SharedZones.TryGetValue(z.Label, out shores);
                var zr = shores == null ? z.Region
                    : shores.FirstOrDefault(msg.Regions.Contains) ?? shores.FirstOrDefault(channelRegions.Contains) ?? z.Region;
                // слова зоны не должны стать отдельными пунктами; роль («в сторону Белосарайской косы») — по первому слову
                var m = z.Pattern.Match(norm);
                var zt = Gazetteer.Tokenize(m.Value);
                int zFirst = -1;
                for (int ti = 0; ti + zt.Count <= tokens.Count; ti++)
                    if (zt.Count > 0 && Enumerable.Range(0, zt.Count).All(q => tokens[ti + q] == zt[q] || tokens[ti + q].StartsWith(zt[q][..Math.Min(5, zt[q].Length)])))
                    {
                        if (zFirst < 0) zFirst = ti;
                        for (int q = 0; q < zt.Count; q++) used.Add(ti + q);
                    }
                // роль по тексту («в направлении Таврической ТЭС», «далее в направлении Донузлав») — у наземных зон;
                // акватории и обширные зоны (побережье Кубани, Приазовье, трассы) — области тревоги
                // «Побережье от Тарханкут до Севастополя» — описание района, а не место пуска: у прочих зон только роль цели
                int zRole = same != null ? (same.IsTarget ? 1 : same.IsOrigin ? -1 : 0)
                    : zFirst < 0 ? 0
                    : z.Label.EndsWith(" коса", StringComparison.Ordinal) ? (RoleAt(tokens, zFirst) is var kr && kr == 1 && LocativeAfterNa(tokens, zFirst, zt.Count) ? 0 : kr)
                    : !water && z.RadiusKm <= 30 && RoleAt(tokens, zFirst) == 1 && !LocativeAfterNa(tokens, zFirst, zt.Count) ? 1 : 0;
                AddPlace(msg, new ParsedPlace
                {
                    Label = z.Label, RegionId = zr, Pos = new GeoPoint(z.Lat, z.Lon), IsArea = true, ZoneRadiusKm = z.RadiusKm,
                    SeaId = water ? RouteNetwork.SeaAt(new GeoPoint(z.Lat, z.Lon)) : null,
                    IsTarget = zRole == 1, IsOrigin = zRole == -1,
                });
            }
        // пункт, найденный только внутри названия водоёма («залив Сиваш», «Сасык-Сиваш»), — не отдельный пункт;
        // названный ещё раз отдельно — остаётся
        if (zoneSpans.Count > 0)
            msg.Places.RemoveAll(p => p.ZoneRadiusKm == null && OnlyInsideSpans(norm, Gazetteer.Normalize(p.Label), zoneSpans));

        // Офлайн-справочник топонимов: последовательности слов с заглавной буквы (+ «район», «МО», «ГО»)
        if (rawTokens.Count == tokens.Count)
        {
            var ctxRegions = msg.Regions.Concat(channelRegions).Where(r => !Gazetteer.IsForeign(r)).Distinct().ToList();
            foreach (var r in msg.Regions.Where(Gazetteer.IsForeign)) ctxRegions.Add(r);
            bool voted = false;
            if (ctxRegions.Count == 0) { ctxRegions = VoteRegions(rawTokens, used); voted = ctxRegions.Count > 1; }
            for (int i = 0; i < rawTokens.Count; i++)
            {
                if (used.Contains(i) || !IsNameStart(rawTokens[i]) || IsCompassWord(tokens, i)) continue;
                // «Н. Мельницы», «Н Мельницы», «Б. Утриш» — сокращённое прилагательное перед названием
                if (ctxRegions.Count > 0 && !voted && AbbrevPlace(rawTokens, i, used, ctxRegions) is { } ab)
                {
                    for (int k = i - 1; k < i + ab.Len; k++) used.Add(k);
                    var abRole = RoleAt(tokens, i - 1);
                    AddPlace(msg, new ParsedPlace
                    {
                        Label = ab.Hit.Name, RegionId = ab.Hit.RegionId, Pos = ab.Hit.Pos,
                        IsTarget = abRole == 1, IsOrigin = abRole == -1, IsVia = IsViaAt(tokens, i - 1),
                    });
                    i += ab.Len - 1;
                    continue;
                }
                // самая длинная подходящая последовательность (до 3 слов), не пересекающая разделители
                for (int len = Math.Min(4, rawTokens.Count - i); len >= 1; len--)
                {
                    if (Enumerable.Range(i, len).Any(used.Contains)) continue;
                    bool ok = true;
                    for (int k = 1; k < len && ok; k++)
                        ok = !rawTokens[i + k].Break && (IsNameStart(rawTokens[i + k]) || IsAdminSuffix(rawTokens[i + k].Lower)
                             || (k == 1 && len == 2 && rawTokens[i + 1].Lower.Length > 3 && rawTokens[i + 1].Orig.All(char.IsLetter)
                                 && !NeverPlace.Contains(rawTokens[i + 1].Lower) && !IsService(rawTokens[i + 1].Orig)));
                    if (!ok) continue;
                    var phrase = string.Join(' ', rawTokens.Skip(i).Take(len).Select(t => t.Orig));
                    // однословное название, совпадающее с обычным словом, принимаем, только если оно стоит отдельной строкой/элементом списка
                    bool standalone = rawTokens[i].Break && (i + 1 >= rawTokens.Count || rawTokens[i + 1].Break);
                    if (len == 1 && rawTokens[i].Lower.Length < 4) continue;
                    if (len == 1 && StopNames.Contains(rawTokens[i].Lower) && !(standalone && !NeverPlace.Contains(rawTokens[i].Lower))) continue;
                    if (IsService(phrase) && !IsAdminSuffix(rawTokens[i + len - 1].Lower)) continue;
                    var hit = ctxRegions.Count > 0 ? PlaceIndex.Find(phrase, ctxRegions) : PlaceIndex.FindUnique(phrase, Frontline);
                    // пункт у границы соседнего региона («Чонгар» — Херсонская обл., в сообщении про Крым)
                    if (hit == null && ctxRegions.Count > 0 && phrase.Length >= 5)
                    {
                        var near = ctxRegions.SelectMany(Neighbours).Where(r => !ctxRegions.Contains(r)).Distinct().ToList();
                        var nh = near.Count > 0 ? PlaceIndex.FindUnique(phrase, near) : null;
                        // районы — по уникальности среди всей прифронтовой зоны (каналы пишут и о соседних областях)
                        if (nh == null && hitIsAdminPhrase(phrase)) nh = PlaceIndex.FindUnique(phrase, FrontlineRu);
                        if (nh != null && ctxRegions.Any(r => Gazetteer.Get(r) is { } reg && Geo.DistanceKm(reg.Center, nh.Pos) < 350)) hit = nh;
                    }
                    if (hit == null && ctxRegions.Count == 0 && hitIsAdminPhrase(phrase)) hit = PlaceIndex.FindUnique(phrase, AllRu.Value);
                    // «Саратовский район», «Луховицкий район» — округ вокруг города (в справочнике районов его нет): точка — город
                    bool cityDistrict = false;
                    if (hit == null && hitIsAdminPhrase(phrase) &&
                        PlaceIndex.FindDistrictByCity(phrase, ctxRegions.Count > 0 ? ctxRegions : AllRu.Value) is { } dc)
                    { hit = dc; cityDistrict = true; }
                    // тыловые каналы без региона: «Зеленоград», «Кубинка», «Шуя» — единственный крупный город с таким названием
                    if (hit == null && ctxRegions.Count == 0 && len <= 2 && !StopNames.Contains(rawTokens[i].Lower) && !NeverPlace.Contains(rawTokens[i].Lower) &&
                        !IsService(phrase) && (msg.Threat != ThreatType.None || msg.Status == AlertStatus.AllClear))
                        hit = PlaceIndex.FindDominant(phrase, AllRu.Value);
                    if (hit == null && len == 1 && ctxRegions.Count > 0)
                        foreach (var cr in ctxRegions) { hit = PlaceIndex.FindPrefix(phrase, cr); if (hit != null) break; }
                    // опечатка («Краснадар», «Анастасиевкая») — отдельной строкой/элементом перечня, в регионах контекста
                    // прилагательное («Молочанский», «Кущевский») — скорее район, чем искажённый пункт: не угадываем
                    if (hit == null && len == 1 && ctxRegions.Count > 0 && !voted && standalone &&
                        !Regex.IsMatch(rawTokens[i].Lower, @"(?:ский|цкий|ской|ское|ская)$") &&
                        !StopNames.Contains(rawTokens[i].Lower) && !NeverPlace.Contains(rawTokens[i].Lower))
                        hit = PlaceIndex.FindTypo(phrase, ctxRegions);
                    // составное название с опечаткой («МО Серебрянные пруды») — отдельной строкой целиком
                    if (hit == null && len is 2 or 3 && ctxRegions.Count > 0 && !voted &&
                        (rawTokens[i].Break || i > 0 && rawTokens[i - 1].Break && IsAdminSuffix(rawTokens[i - 1].Lower)) &&
                        (i + len >= rawTokens.Count || rawTokens[i + len].Break || IsAdminSuffix(rawTokens[i + len].Lower)) &&
                        Enumerable.Range(i, len).All(k => !IsAdminSuffix(rawTokens[k].Lower) && !StopNames.Contains(rawTokens[k].Lower)))
                        hit = PlaceIndex.FindTypo(phrase, ctxRegions);
                    if (hit == null) continue;
                    // прилагательное в косвенном падеже перед нарицательным словом — определение, а не пункт:
                    // «Луганскую Народную Республику», «Большую часть», «Ставропольскую нефтебазу»
                    // в любом падеже, если следом — слово, к которому оно относится («Большое количество», «Мирный житель»,
                    // «Красного уровня», «Черноморского флота», «Азовское побережье», «Примерное направление»)
                    if (len == 1 && AdjectiveOfNoun(rawTokens, i, ctxRegions)) continue;
                    if (len == 1 && NamedAfter(tokens, i, hit.Name)) continue;
                    // «ЛДНР» даёт в контекст обе республики: одноимённый пункт есть в обеих — берём ближайший к остальным пунктам
                    if (hit.RegionId is "lnr" or "dnr" && ctxRegions.Contains(hit.RegionId == "lnr" ? "dnr" : "lnr") &&
                        PlaceIndex.Find(phrase, [hit.RegionId == "lnr" ? "dnr" : "lnr"]) is { } twin &&
                        msg.Places.Where(p => p.Pos != null && !p.IsTarget && !p.IsOrigin).Select(p => p.Pos!.Value).ToList() is { Count: > 0 } others &&
                        others.Min(o => Geo.DistanceKm(o, twin.Pos)) < others.Min(o => Geo.DistanceKm(o, hit.Pos)))
                        hit = twin;
                    if (voted)
                        foreach (var cr in ctxRegions.Where(r => r != hit.RegionId))
                            if (PlaceIndex.Find(phrase, [cr]) is { } alt)
                                AddPlace(msg, new ParsedPlace { Label = alt.Kind == 'A' ? DistrictLabel(alt) : alt.Name, RegionId = alt.RegionId, Pos = alt.Pos, IsArea = alt.Kind == 'A', IsTarget = RoleAt(tokens, i) == 1, IsOrigin = RoleAt(tokens, i) == -1 });
                    for (int k = i; k < i + len; k++) used.Add(k);
                    var role = RoleAt(tokens, i);
                    if (role == 1 && LocativeNa(tokens, i, hit.Name)) role = 0;
                    bool isArea = hit.Kind == 'A' || cityDistrict;
                    AddPlace(msg, new ParsedPlace
                    {
                        Label = cityDistrict ? DistrictLabel(new IndexedPlace(CapitalizeAdmin(phrase), hit.RegionId, hit.Pos, 'A', 0)) : isArea ? DistrictLabel(hit) : hit.Name,
                        RegionId = hit.RegionId, Pos = hit.Pos,
                        IsTarget = role == 1, IsOrigin = role == -1, IsArea = isArea, IsVia = IsViaAt(tokens, i),
                    });
                    i += len - 1;
                    break;
                }
            }
        }

        if (msg.Status != AlertStatus.AllClear) ApplyRegionDirections(msg, tokens, rawTokens, mentioned);
        ApplyCompassQualifiers(msg, tokens, rawTokens.Count == tokens.Count ? rawTokens : null, mentioned, channelRegions);
        if (msg.Status != AlertStatus.AllClear) ApplyBorderAndSea(msg, norm, channelRegions);
        ResolveNamesakes(msg, channelRegions);
        msg.Route = RouteHintParser.Parse(msg.CleanText.Length > 0 ? msg.CleanText : text, msg);

        // Неизвестные названия → геокодирование
        string? geoRegion = msg.Regions.FirstOrDefault() ?? channelRegions.FirstOrDefault();
        if (geoRegion != null)
        {
            foreach (var cand in ExtractCandidates(text))
            {
                var candTokens = Gazetteer.Tokenize(cand.Name);
                if (candTokens.Count == 0) continue;
                if (Gazetteer.FindPlaces(candTokens).Any()) continue;
                if (candTokens.All(Gazetteer.IsRegionWord)) continue;
                if (msg.Places.Any(p => Gazetteer.Normalize(p.Label) == Gazetteer.Normalize(cand.Name))) continue;
                var candNorm = Gazetteer.Normalize(cand.Name);
                if (msg.Places.Any(p => candNorm.Contains(Gazetteer.Normalize(p.Label)) || Gazetteer.Normalize(p.Label).Contains(candNorm))) continue;
                // то же, что уже найдено, в другом написании: «Приморско Ахтарск» / «Приморско-Ахтарск», «Лискинском районах» / «Лискинский район»
                static string Key(string s) => Regex.Replace(Gazetteer.Normalize(s), @"[\s-]+", "");
                var ck = Key(cand.Name);
                var c0 = candNorm.Split(' ')[0];
                if (msg.Places.Any(p => Key(p.Label) == ck || p.IsArea && c0.Length >= 7 && Regex.IsMatch(candNorm, @"район|округ|\bмо\b|\bго\b") &&
                                        Gazetteer.Normalize(p.Label).StartsWith(c0[..^3], StringComparison.Ordinal))) continue;
                if (StopNames.Contains(candNorm)) continue;
                if (candTokens.Any(t => StopNames.Contains(t) || Gazetteer.IsRegionWord(t)) && !IsAdminSuffix(candTokens[^1])) continue;
                if (candTokens.Any(t => t.Length <= 2 && t is not ("на" or "де" or "ла"))) continue;
                if (candTokens.Count == 1 && candTokens[0].Length < 4) continue;
                if (CandidateNoise().IsMatch(candNorm) || ArticleNoise().IsMatch(candNorm)) continue;
                // кандидат уже покрыт офлайн-разбором (те же слова)
                if (candTokens.All(ct => tokens.Select((t, ix) => (t, ix)).Any(x => x.t == ct && used.Contains(x.ix)) || IsAdminSuffix(ct) ||
                                         ct is "районам" or "округам" or "районами" or "округами" or "районы" or "округа")) continue;
                if (ResolveMismatch(msg, cand.Name, cand.Role, geoRegion, channelRegions)) continue;
                msg.Places.Add(new ParsedPlace
                {
                    Label = cand.Name, RegionId = geoRegion, IsTarget = cand.Role == 1, IsOrigin = cand.Role == -1,
                    GeocodeQuery = ExpandAbbreviations(cand.Name),
                });
            }
        }

        // «Все приграничные регионы» — перечень приграничных субъектов
        if (BorderAllRx().IsMatch(norm))
            foreach (var r in Borderline) if (!msg.Regions.Contains(r)) msg.Regions.Add(r);

        // «по всем ранее объявленным» — относится к уже действующим тревогам (движок применит к ним)
        msg.RefersToPrevious = RefersPrevRx().IsMatch(norm);

        // Длинные сводки о последствиях/инструктажи — информационные (в ленту, не на карту), если в них нет призыва укрыться
        if (msg.Threat != ThreatType.None && (tokens.Count > 30 || Instruction().IsMatch(norm)) && Informational().IsMatch(norm) && !Shelter().IsMatch(norm) && !ActiveAlert().IsMatch(norm))
            msg.IsInformational = true;
        if (msg.Threat == ThreatType.None && tokens.Count > 120)
            msg.IsInformational = true;
        // памятка «напоминаем уровни тревоги: внимание — …, опасность — …» — описание шкалы, а не тревога
        if (LevelsNoteRx().IsMatch(norm) && tokens.Count > 25)
            msg.IsInformational = true;

        // Каналы тревог часто публикуют только перечень пунктов/направление без слова «БПЛА» (тип ясен из контекста,
        // карта во вложении). Принцип безопасности: сообщение с локацией считается сигналом, если оно не информационное.
        if (msg.Threat == ThreatType.None && msg.Status != AlertStatus.AllClear && (msg.Regions.Count > 0 || msg.Places.Count > 0))
        {
            if (Informational().IsMatch(norm) && !Shelter().IsMatch(norm))
                msg.IsInformational = true;
            else
                msg.Threat = UavCues().IsMatch(norm) ? ThreatType.Uav : ThreatType.General;
        }
        else if (msg.Threat == ThreatType.None && Shelter().IsMatch(norm))
        {
            msg.Threat = ThreatType.General;
        }
        // Короткие оперативные реплики региональных каналов без типа угрозы: «Ещё одно сбитие!», «Декабристов фиксация»,
        // «Приготовиться к работе ПВО». Сводки «тихо / фиксаций нет» сигналом не считаются.
        else if (msg.Threat == ThreatType.None && msg.Status != AlertStatus.AllClear && tokens.Count <= 15 &&
                 !CalmNote().IsMatch(norm) && !Informational().IsMatch(norm) &&
                 (msg.Status is AlertStatus.AirDefense or AlertStatus.Impact || LoneCue().IsMatch(norm)))
        {
            msg.Threat = UavCues().IsMatch(norm) ? ThreatType.Uav : ThreatType.General;
        }

        FinishKinds(msg, norm);
        DetectLaunch(msg, norm);
        DetectRegionLevels(msg, text);

        // «Пока по обстановке тихо, в случае появления угрозы сообщим», «угрозы нет» — сводка об отсутствии угрозы.
        // Без названных пунктов и без конкретики (тип, ПВО, отбой) это не тревога по региону канала.
        if (msg.Threat != ThreatType.None && msg.Status != AlertStatus.AllClear && msg.Places.Count == 0 && msg.Status != AlertStatus.Launch &&
            CalmStatus().IsMatch(norm) && !CalmOverride().IsMatch(norm))
            msg.IsInformational = true;

        if (msg.Status == AlertStatus.AllClear) DropClearNarrativeTargets(msg, text, mentioned);
        // «Отбой ракетной опасности и опасности БПЛА», «отбой ракетной и БПЛА опасности»: отбой по обеим угрозам —
        // снимает любую тревогу. Только если БПЛА не названы как сохраняющиеся («… БПЛА сохраняется», «осталась»)
        if (msg.Status == AlertStatus.AllClear && msg.Threat == ThreatType.Missile && CombinedClear().IsMatch(norm) && !UavRemains().IsMatch(norm))
            msg.Threat = ThreatType.General;
        // «Ракетная опасность ⏎ Отбой опасности по БПЛА»: отбой относится к БПЛА, ракетная опасность названа отдельно и остаётся
        // «Отбой ракетной опасности, БПЛА летят»: отбой касается ракет, а БПЛА названы как продолжающаяся угроза
        else if (msg.Status == AlertStatus.AllClear && msg.Threat == ThreatType.Missile && MissileClear().IsMatch(norm) && UavFlying().IsMatch(norm))
        {
            msg.Status = AlertStatus.Alert;
            msg.Threat = ThreatType.Uav;
            msg.Kind = ThreatCatalog.Primary(msg.Kinds, ThreatType.Uav);
        }
        else if (msg.Status == AlertStatus.AllClear && msg.Threat == ThreatType.Missile && UavOnlyClear().IsMatch(norm) && !MissileClear().IsMatch(norm))
        {
            msg.Threat = ThreatType.Uav;
            msg.Kind = ThreatCatalog.Primary(msg.Kinds, ThreatType.Uav);
        }
        ApplyCityAndRegion(msg, norm);

        // «Весь Юг ⏎ Ракетная опасность», «Отбой ракетной опасности весь Юг» — макрорегион общих каналов.
        // Только без названных регионов и пунктов и у канала без своего региона: «Юг ⏎ Московская область» — юг области,
        // «Юг» в канале Ростовской области — юг Ростовской области
        if (msg.Regions.Count == 0 && msg.Places.Count == 0 && channelRegions.Count == 0 && !msg.IsInformational &&
            !msg.IsJunk && tokens.Count <= 40 && SouthWord().IsMatch(norm))
        {
            foreach (var r in SouthMacro) msg.Regions.Add(r);
            // «Весь юг!» без типа — общая опасность, как любое сообщение с местом без слова «БПЛА»
            if (msg.Threat == ThreatType.None && msg.Status != AlertStatus.AllClear) msg.Threat = ThreatType.General;
        }

        // Без названных мест — регион(ы) канала. После уточнения типа: «Работа ПВО по КР» тоже получает регион.
        if (msg.Regions.Count == 0 && msg.Places.Count == 0 && channelRegions.Count >= 1 && (msg.Threat != ThreatType.None || msg.Status == AlertStatus.AllClear))
            foreach (var r in channelRegions) msg.Regions.Add(r);
        return msg;
    }

    /// <summary>
    /// «Приморский край» в перечне районов прифронтовой области («… Бердянский район, Приморский край, Запорожская
    /// область») — опечатка вместо «Приморский район»: дальневосточный регион в таком сообщении не имеется в виду.
    /// </summary>
    private static string FixKraiTypos(string text)
    {
        if (!KraiTypo().IsMatch(text)) return text;
        var n = Gazetteer.Normalize(text);
        bool western = Gazetteer.Tokenize(n).SelectMany(Gazetteer.RegionsForWord).Any(r => Gazetteer.Get(r) is { } reg && reg.Center.Lon < 60);
        return western ? KraiTypo().Replace(text, m => m.Groups[1].Value + " район") : text;
    }

    [GeneratedRegex(@"\b(Приморский|Камчатский)\s+край\b", RegexOptions.IgnoreCase)]
    private static partial Regex KraiTypo();

    /// <summary>
    /// «Пуски Штормов от Изюма», «Массовый запуск БПЛА от Черниговской области», «Выходы РСЗО», «Подготовка к пускам» —
    /// сообщение о пуске: отдельная категория. Место пуска — на стороне противника; если в сообщении есть и места тревоги
    /// в РФ (пункты, регионы без роли «от»), это обычная тревога с указанием источника, а не пуск.
    /// </summary>
    private static void DetectLaunch(ParsedMessage msg, string norm)
    {
        if (msg.Status != AlertStatus.Alert || msg.Threat == ThreatType.None) return;
        if (!LaunchWords().IsMatch(norm) || LaunchNotes().IsMatch(norm)) return;
        // в РФ тревога уже объявлена (пункты или регионы как место тревоги) — это тревога, а пуск — её источник
        // пункт на стороне противника («Чугуев ещё выходы», «Пуски от Изюма») — место пуска, а не место тревоги
        bool Enemy(ParsedPlace p) => p.Pos is { } pos && Radar.Services.Borderline.IsEnemySide(pos, p.RegionId);
        var sites = msg.Places.Where(p => !p.IsTarget && Enemy(p)).ToList();
        var siteRegions = sites.Select(p => p.RegionId).ToHashSet();
        bool ruAlert = msg.Places.Any(p => !p.IsTarget && !Enemy(p) && p.RegionId is { } r && !Gazetteer.IsForeign(r))
                       || msg.Regions.Any(r => !Gazetteer.IsForeign(r) && !siteRegions.Contains(r));
        var prepLevel = LaunchPrep().IsMatch(norm) ? AlertLevel.Attention : AlertLevel.Alarm;
        if (ruAlert)
        {
            // тревога в РФ остаётся тревогой; названное место пуска — отдельной отметкой
            if (sites.Count > 0) { msg.LaunchSite = sites[0]; msg.LaunchLevel = prepLevel; }
            // «запуск БПЛА от Днепропетровской области … в направлении ЛНР. Ростовская область — опасность» — место пуска
            // названо областью противника: отметка пуска в типичной точке пусков, сама область — не место тревоги в РФ
            else if (msg.Route.Launch is { Explicit: true } lh && msg.Regions.FirstOrDefault(Gazetteer.IsForeign) is { } fr)
            {
                msg.LaunchSite = new ParsedPlace { Label = lh.Label, RegionId = fr, Pos = lh.Pos, IsOrigin = true, IsArea = true };
                msg.LaunchLevel = prepLevel;
                foreach (var r in msg.Regions.Where(Gazetteer.IsForeign).ToList()) { msg.Regions.Remove(r); msg.OriginRegions.Add(r); }
            }
            return;
        }
        // место пуска должно быть названо (пункт или область на стороне противника); иначе «Возможна подготовка
        // к пускам Штормов» в региональном канале — предупреждение для региона канала, остаётся тревогой
        bool site = sites.Count > 0 || msg.Regions.Any(Gazetteer.IsForeign) || msg.OriginRegions.Any(r => r == "kharkov");
        if (!site) return;
        msg.Status = AlertStatus.Launch;
        // «подготовка/готовит/возможны пуски» — внимание; факт пуска — тревога
        msg.Level = prepLevel;
    }

    /// <summary>
    /// Уровни по строкам: регион получает уровень ниже общего, только если во всех строках, где он назван, явно
    /// указан этот уровень. Регион без уровня в строке или с пунктами в сообщении остаётся на общем уровне.
    /// </summary>
    private static void DetectRegionLevels(ParsedMessage msg, string text)
    {
        if (msg.Status != AlertStatus.Alert || msg.Threat == ThreatType.None || msg.Regions.Count < 2) return;
        var levels = new Dictionary<string, AlertLevel>(StringComparer.Ordinal);
        // точка — конец фразы, только если за ней новая фраза (заглавная) или конец; «обл.», «г.» фразу не рвут
        foreach (var seg in Regex.Split(text, @"[\n;!]+|\.(?=\s*(?:[А-ЯЁA-Z\d]|$))"))
        {
            var sn = Gazetteer.Normalize(seg);
            // «Над ЛНР фиксации БПЛА, …, внимание» — объект уже там: строка не понижает уровень
            bool present = PresentRx().IsMatch(sn);
            AlertLevel? lvl = present ? null : AlarmWordRx().IsMatch(sn) ? AlertLevel.Alarm
                : DangerWordRx().IsMatch(sn) ? AlertLevel.Danger
                : AttentionWordRx().IsMatch(sn) ? AlertLevel.Attention : null;
            foreach (var r in Gazetteer.Tokenize(seg).SelectMany(Gazetteer.RegionsForWord).Distinct())
            {
                if (!msg.Regions.Contains(r)) continue;
                var l = lvl ?? msg.Level;
                levels[r] = levels.TryGetValue(r, out var prev) && prev > l ? prev : l;
            }
        }
        foreach (var (r, l) in levels)
            if (l < msg.Level && msg.Places.All(p => p.RegionId != r)) msg.RegionLevels[r] = l;
        // все регионы ниже общего уровня — делить нечего
        if (msg.Regions.All(msg.RegionLevels.ContainsKey)) msg.RegionLevels.Clear();
    }

    /// <summary>Делит сообщение с разными уровнями по регионам на части: основную и по одной на каждый пониженный уровень.</summary>
    public static List<ParsedMessage> SplitByLevel(ParsedMessage msg)
    {
        if (msg.RegionLevels.Count == 0) return [msg];
        var result = new List<ParsedMessage>();
        foreach (var g in msg.RegionLevels.GroupBy(kv => kv.Value))
        {
            var part = new ParsedMessage
            {
                Raw = msg.Raw, Threat = msg.Threat, Kind = msg.Kind, Kinds = [.. msg.Kinds], Level = g.Key, Status = msg.Status,
                Count = msg.Count, CleanText = msg.CleanText, Nearby = msg.Nearby, Part = "lvl:" + g.Key,
            };
            foreach (var kv in g) { part.Regions.Add(kv.Key); msg.Regions.Remove(kv.Key); }
            result.Add(part);
        }
        msg.RegionLevels.Clear();
        result.Insert(0, msg);
        return result;
    }

    [GeneratedRegex(@"\b(?:пуск(?:и|ов|ам|ами|у|а)?|запуск(?:и|ов|ам|у|а)?|выход(?:ы|ов)\b(?! в\b| на\b)|выход (?:бпла|рсзо|ракет))")]
    private static partial Regex LaunchWords();

    [GeneratedRegex(@"подготов|готовит|готовность|возможн|вероятн|ожида|приготов|угроза пуск")]
    private static partial Regex LaunchPrep();

    // справки и пояснения («уровни тревоги», «при пусках сообщим») — не пуск
    [GeneratedRegex(@"напоминаем|уровни тревоги|сообщим|сообщений о запусках не|инсценировк|пуско-зарядн")]
    private static partial Regex LaunchNotes();

    // отбой, где ракетная опасность и БПЛА перечислены через «и» в одной фразе отбоя
    // или двумя отбоями подряд через запятую («отбой ракетной опасности, отбой опасности по БПЛА»)
    [GeneratedRegex(@"(?:отбой|отмена)[^\n.!]{0,40}ракетн[^\n.!]{0,40}(?:\bи\b|,\s*(?:отбой|отмена)\b)[^\n.!]{0,30}(?:бпла|беспилот|дрон)")]
    private static partial Regex CombinedClear();

    [GeneratedRegex(@"(?:отбой|отмена)[^\n.!]{0,30}(?:бпла|беспилот|дрон)")]
    private static partial Regex UavOnlyClear();

    [GeneratedRegex(@"(?:отбой|отмена)[^\n.!]{0,40}(?:ракетн|авиацион)")]
    private static partial Regex MissileClear();

    [GeneratedRegex(@"(?:бпла|беспилот|дрон)[^\n.!]{0,20}\b(?:летят|летит|в воздухе|над)\b|\b(?:летят|летит)\b[^\n.!]{0,20}(?:бпла|беспилот|дрон)")]
    private static partial Regex UavFlying();

    [GeneratedRegex(@"(?:бпла|беспилот|дрон)[^\n.!]{0,30}(?:сохраня|остал|продолжа|действует)|(?:сохраня|остал)[^\n.!]{0,30}(?:бпла|беспилот|дрон)|опасность по (?:обычным )?бпла сохраня")]
    private static partial Regex UavRemains();

    // частые шаблоны разбора (на каждое сообщение) — генерируются при сборке
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRx();
    [GeneratedRegex(@"^(?:https?://)?max\.ru/\S+$")]
    private static partial Regex MaxFooterRx();
    [GeneratedRegex(@"(?:вс[еяю]|все) приграничн\w* (?:регион|област|район)|приграничн\w* (?:регион|район|област)|^вс[еи] регион|(?:вс[еяю]|все) приграничье")]
    private static partial Regex BorderAllRx();
    [GeneratedRegex(@"ранее объявлен|ранее обьявлен|раннее объявлен|ранее анонсирован|указанн\w* направлени|данным направлени|вышеуказанн")]
    private static partial Regex RefersPrevRx();
    [GeneratedRegex(@"напоминаем уровни тревог|уровни тревоги:")]
    private static partial Regex LevelsNoteRx();
    [GeneratedRegex(@"фиксаци|фиксир|летит|летят|\bидут\b|\bшли\b|пролет|курсом|сбит|пво|взрыв")]
    private static partial Regex PresentRx();
    [GeneratedRegex(@"\bтревог")]
    private static partial Regex AlarmWordRx();
    [GeneratedRegex(@"опасност|угроз")]
    private static partial Regex DangerWordRx();
    [GeneratedRegex(@"\bвнимани")]
    private static partial Regex AttentionWordRx();
    [GeneratedRegex(@"\b(?:су|миг|ту|f|ф)[\s-]?(?:24|25|27|30|34|35|16|29|22[mм]3|95)\b")]
    private static partial Regex AircraftModelRx();
    [GeneratedRegex(@"\bвыход(?:ы|ов)?\b|\bпуск(?:и|ов)?\b|\bзапуск(?:и|ов)?\b")]
    private static partial Regex LaunchGeneralRx();
    [GeneratedRegex(@"\bотмена\b[^\n]{0,40}опасност")]
    private static partial Regex CancelDangerRx();
    [GeneratedRegex(@"(?<!\p{L})отбо\w*\s+(?:пока\s+)?(?:не|нет|дадим|будет)\b[^\n]*|(?<!\p{L})(?:не|нет|без)\s+(?:будет\s+)?отбо\w*|(?<!\p{L})до\s+(?:официального\s+)?отбо\w*[^\n]*|(?<!\p{L})(?:дадим|будет|ждите|ожидайте|ожидаем)\s+отбо\w*[^\n]*")]
    private static partial Regex NegatedClearRx();
    [GeneratedRegex(@"(?<=\p{L})\s+-(?=на-)|(?<=\p{L}-)\s+(?=на-)")]
    private static partial Regex HyphenGapRx();

    private static void FinishKinds(ParsedMessage msg, string norm)
    {
        var kinds = ThreatCatalog.Detect(norm);
        // группа уточняется по типу, если в тексте только «опасность»/«тревога» без группы (аэростаты, ДРГ, РСЗО)
        if (msg.Threat is ThreatType.General or ThreatType.None && kinds.Count > 0 && msg.Status != AlertStatus.AllClear || msg.Threat == ThreatType.General && kinds.Count > 0)
            msg.Threat = ThreatCatalog.Info(kinds[0]).Group;
        // «Опасность по БПЛА и аэростатам» — БПЛА; «МВШ со сбросами» — аэростаты (не БПЛА)
        if (kinds.Contains(ThreatKind.Balloon) && msg.Threat == ThreatType.Uav && !Regex.IsMatch(norm, @"бпла|беспилот|дрон|фпв|fpv"))
            msg.Threat = ThreatType.Balloon;
        msg.Kinds = kinds;
        msg.Kind = ThreatCatalog.Primary(kinds, msg.Threat);
        msg.Level = ThreatCatalog.DetectLevel(norm, msg.Status);
        msg.Nearby = NearbyWords().IsMatch(norm);
    }

    // Сводки «всё спокойно» и служебные просьбы — не тревога, даже если в них есть «фиксаций», «угрозы»
    [GeneratedRegex(@"не наблюда|фиксаций нет|нет фиксаци|фиксаций (?:к нам )?больше нет|больше нет|по обстановке (?:тихо|вс[её] спокойно|спокойно|также)|вс[её] спокойно|без (?:прямой )?угрозы|миновало|пишите|сообщайте|в личные сообщения|в случае (?:возникновения|появления) угрозы|показал|дезинформ")]
    private static partial Regex CalmNote();

    [GeneratedRegex(@"по обстановке (?:пока )?(?:тихо|вс[её] спокойно|спокойно)|^пока тихо|(?:угроз[аы]?|опасност[иь]) (?:пока )?нет\b|нет (?:никакой )?угрозы|угроз не (?:наблюда|зафиксир)")]
    private static partial Regex CalmStatus();

    [GeneratedRegex(@"бпла|беспилот|дрон|шахед|герань|ракет|баллист|уаб|каб\b|авиац|пво|сбит|взрыв|прил[её]т|фиксац|летит|летят|укрыт|атак|тревог[ауи]? (?:по|в|объявл)|опасность по|отбой|внимание")]
    private static partial Regex CalmOverride();

    // Оперативные слова, означающие активную угрозу даже без названного типа
    [GeneratedRegex(@"фиксац|летит|летят|пусков|\bпуск|сбити|сбит|работа пво|работе пво|отработке пво|готовность к|приготов")]
    private static partial Regex LoneCue();

    [GeneratedRegex(@"близлежащ|окрестност|прилегающ|\bи ближайшие\b|\bближайшие (?:населен|пункт|район|сел|территор)")]
    private static partial Regex NearbyWords();

    // Лексика сигнала: если хоть что-то из этого есть в коротком сообщении — оно НЕ может быть отброшено как мусор.
    [GeneratedRegex(@"бпла|беспилот|дрон|шахед|герань|хорнет|ланцет|ракет|баллист|шторм|нептун|хаймарс|атакмс|уаб|каб\b|бомб|авиац|f-16|су-\d|миг|опасност|тревог|отбой|угроз|фиксац|пво|сбит|взрыв|прил[её]т|пуск|запуск|рсзо|обстрел|катер|бэк|безэкипаж|сброс|укрыт")]
    private static partial Regex SignalWords();

    [GeneratedRegex(@"канал(?:е|ы|ов)? (?:тревог )?в max|max\.ru/|t\.me/|vk\.(?:com|ru)/|чат-?бот|резервн\w* (?:канал|бот)")]
    private static partial Regex ChannelPromo();

    [GeneratedRegex(@"упомянул администратор|\bvip\b|приватн\w* канал|закрыт\w* канал|пригласил|\bкэфф?\b|коэффиц|экспресс на \d|букмекер|1xbet|одна ставка|подключить ускоритель|ускорител\w* интернет|обход белых списков|\bпдд\b|без прав\b|брендов|парфюм|финансов\w* грамотност|поддержите любой суммой|требуется ваша поддержка|платим \d|открываем доступ|доступ на \d+ минут")]
    private static partial Regex Spam();

    [GeneratedRegex(@"(?:набор|приглашаем|присоединяйся|ведется набор|объявля\w* набор)[^\n]{0,80}(?:военн\w* служб|по контракту|подразделени|сотрудник|операторов|омон|росгварди)|контракт на \d+ (?:год|мес)|без перевода в пехоту|переводов нет|денежн\w* довольстви|единовременн\w* выплат|выплат\w* от [\d. ]{4,}|полный социальный пакет|принимаем граждан")]
    private static partial Regex Recruitment();

    [GeneratedRegex(@"европарламент|конгресс\w* сша|палата представителей|госсекретар|сенат\w* сша|белый дом|резолюци|проголосова|переговор\w* (?:в|с|о)|саммит|санкци")]
    private static partial Regex PoliticsNews();

    [GeneratedRegex(@"cloudtips|pay\.|boosty|donat|донат|сбер|карт[ау]\s+\d|реквизит")]
    private static partial Regex DonationWords();

    [GeneratedRegex(@"наш (?:основной )?(?:открытый )?канал (?:тревог )?в max|канал(?:е|ы)? (?:тревог )?в max|max\.ru/|реклам|подпис(?:ывайтесь|аться|ка на)|переходите|перейти в канал|наш (?:информационн|рекламн|резервн)|чат-бот|чат бот|бот[ауе]? (?:при[её]ма|для)|опрос|розыгрыш|производ(?:им|ство|им)|устройств|сиз\b|партию|заказ|приобрести|купить|скидк|вакансi|ваканси|max\.ru|vk\.com|в вк\b|пересылка в max|сбоями|штатном режиме|не имеем отношени|поддержк[уиае] (?:канала|нашей)|спасибо|благодар")]
    private static partial Regex PromoWords();

    /// <summary>
    /// Отсев мусора. Принцип безопасности: сообщение отбрасывается, только если в нём НЕТ признаков сигнала
    /// (или это длинный рекламный текст, где «опасность» встречается в рассуждениях). Пропустить сигнал хуже, чем показать лишнее.
    /// </summary>
    private static string? DetectJunk(string norm, List<string> tokens)
    {
        bool signal = SignalWords().IsMatch(norm);
        bool hasLocation = tokens.Any(t => Gazetteer.IsRegionWord(t)) || Gazetteer.FindPlaces(tokens).Any();
        bool donation = DonationWords().IsMatch(norm);
        bool promo = PromoWords().IsMatch(norm);
        int lines = norm.Count(c => c == '\n') + 1;

        // откровенный спам (ставки, «закрытые каналы», VPN-«ускорители», сборы): мусор, если это не короткая сводка с локацией
        if (Spam().IsMatch(norm) && !(signal && hasLocation && tokens.Count <= 40 && !Regex.IsMatch(norm, @"упомянул|кэфф?\b|ставк"))) return "спам";
        // реклама собственного канала («Наш канал тревог в MAX») — слово «тревог» здесь не означает тревогу
        if (ChannelPromo().IsMatch(norm) && !hasLocation && !Regex.IsMatch(norm, @"бпла|ракетн|опасност|укрыти|отбой")) return "реклама канала";
        // перечень собственных каналов со ссылками («Наши каналы тревог: 1. Крым … https://max.ru/…») — регионы здесь не тревога
        if (Regex.Matches(norm, @"max\.ru/|t\.me/|vk\.(?:com|ru)/").Count >= 3 && Regex.IsMatch(norm, @"канал|подписыва|ресурс")) return "реклама канала";
        // сборы на оборудование, отчёты о помощи, напоминание уровней тревоги — служебные тексты канала, не сигнал
        if (Regex.IsMatch(norm, @"итого:?\s*[\d\s.,]+\s*(?:руб|₽)|принять участие в сборе|заявк\w* на мог|с помощью нашего с вами оборудован|перспективн\w* разработ|маскировочн\w* сет|желающие помочь|на видео (?:дальнобойн|работа)"))
            return "сбор/служебное";
        // набор на военную службу/в ОМОН («операторов БПЛА, РЭБ, ПВО» — слова тревоги здесь не означают тревогу)
        if (Recruitment().IsMatch(norm) && !ActiveAlert().IsMatch(Regex.Replace(norm, @"операторов? (?:бпла|fpv|фпв|рэб|пво)|войск\w* беспилотн\w*|беспилотн\w* систем", " ")))
            return "набор на службу";
        // политические новости («Европарламент…», «Конгресс США проголосовал…») без перечня мест и признаков налёта
        if (PoliticsNews().IsMatch(norm) && !hasLocation && !ActiveAlert().IsMatch(norm)) return "новости";
        if (donation && !signal) return "донаты";
        // ссылка на донат + благодарность без перечня пунктов/регионов — даже если в тексте есть «тревог», «БПЛА»
        if (donation && !hasLocation && (promo || tokens.Count > 25)) return "донаты";
        if (promo && !signal) return "реклама/служебное";
        // длинные статьи и рекламные тексты: много слов, нет перечня пунктов/регионов в начале
        if (tokens.Count > 70 && (promo || donation) && !hasLocation) return "статья/реклама";
        if (!signal && !hasLocation && tokens.Count > 0 && tokens.All(t => t.StartsWith("http") || t.Length <= 2)) return "ссылки";
        _ = lines;
        return null;
    }

    // Фразы, которые не являются топонимами, но выглядят как название с заглавной буквы
    [GeneratedRegex(@"^(?:уйти|уйдите|займите|пройти|пройдите|потенциальн|выходы|выход|пуски|запуски|налет|налёт|летит|летят|видим|уточняем|возможно|ориентировочно|предварительно|на территории|мониторим|готовность|берегите|идут|сбити|сбиты|сбили|согласно|сегодня|доброй|громкие|низко|маркетплейс|три человек|погиб|московск\w* регион|причерноморь|крымский полуостров|черного мор|ямало|ханты|хмао|(?:северо-|юго-)?(?:север|юж|запад|восточ)н(?:ее|ая част|ой част)|ещ[её]|очень|много|массов|единичн|работа|звуки|меры|при |в том числе|далее|повторно|вс[еяю] |весь )|рсзо|бпла|фпв|бэк|уаб|каб\b|мвш|самол[её]т|вертол[её]т|ракет|сброс|оптик|электротяг")]
    private static partial Regex CandidateNoise();

    // Слова статей, объявлений и памяток: первое слово фразы с заглавной буквы — не название
    [GeneratedRegex(@"^(?:прогноз|транспортн\w* средств|машина поврежд|пострадавш|один человек|два человека|родным|без паники|без изменений|если услышите|гражданские суда|квадрокоптер|проверка систем|небо чистое|на данный момент|заранее оповестим|прислать новость|удачной охоты|ожидаем|будут? сбити|получить консультац|набор |денежн|полный социальн|управление|цукс|ограничени|указанн\w* район|прибрежн\w* зон|также|отмена|сирен|срочно|высоко|объясн|хочется|хотим|надеемся|естественно|уверен|приготов|артобстрел|большое количество|противником|ограничьте|смотрим|наматыва|ищет|ид[её]т |одно сообщение|пока |это |цели|примерно|кто-то|уважаем|извинен|комментир|представ|основные|складывается|включите|услышьте|дождаться|ощущаете|нам |вконтакте|(?:юго|северо|север|юг)-(?:восток|запад)|подстанци|электрическ|газо|топливо|автоцистерн|стоянк|военн|морские суда|суда\b|корабли|нефтебаз|вдоль |улиц|наш |ваш |все |просим|внимание|напоминаем|обращаем|сообщаем|информац|будьте|соблюдайте)")]
    private static partial Regex ArticleNoise();

    // Признаки движения БПЛА в «безымянных» сообщениях агрегаторов
    [GeneratedRegex(@"в направлени|далее|в сторону|электротяг|с моря|с азовского|с черного|пролет|пролёт|фиксац|курсом|на (?:север|юг|запад|восток)|с (?:севера|юга|запада|востока)|в тыл|летит|летят|низко")]
    private static partial Regex UavCues();

    // Памятки и инструкции населению
    [GeneratedRegex(@"^(?:при |напоминаем|рекомендуем|просим|памятка|уважаемые)")]
    private static partial Regex Instruction();

    // Формулировки действующей тревоги — сообщение с ними никогда не считается информационным
    [GeneratedRegex(@"(?:тревога|опасность|угроза)(?: атаки)? по\b|(?:беспилотная|ракетная|авиационная[^\n]{0,20}) опасность|угроза (?:повторной )?атаки|экстренная информация|фиксаци|\bшли\b|\bидут\b|летят|летит|проходят|в направлении|будет осуществлять|запуск|пуск|приготовиться|готовит")]
    private static partial Regex ActiveAlert();

    // Призыв к укрытию — всегда сигнал, даже без типа угрозы
    [GeneratedRegex(@"укрыти|укройтесь|в укрытие|к окнам не подход|уйти с улиц|уйдите|уходить с открыт|не покидать|стоп движени|ограничьте передвиж|будьте (?:предельно )?внимательн|быть предельно|летит|летят")]
    private static partial Regex Shelter();

    // Информационные сообщения с упоминанием региона: не тревога
    [GeneratedRegex(@"учени[яй]|проверк[аи] (?:работоспособности|систем|сирен|громкоговорител)|громкоговорител|движение автотранспорта|крымскому мосту возобновлено|погиб|соболезнован|ранен|больниц|пункт временного размещения|госпитализ|пострадал|пострадавш|по данным минобороны|минобороны россии|уничтожено \d+|(?:уничтожен|перехвачен|сбит)\w* (?:и уничтожен\w* )?\d+ (?:украинск|бпла|беспилот|ракет)|в течение (?:дня|ночи|суток)|за сутки|за минувш|за прошедш|повреждены|поврежден[оаы]? |напоминаем|рекомендуем|уровни тревоги|наша деятельность|мы ведем|граберов?|произвели|снимать запрещено|осколк[иа] (?:могут|сбитых)|отч[её]т|сбор[уа]? на|стоп сбор|получению|получили оборудование|поздравля|с днем|с днём|с праздником")]
    private static partial Regex Informational();

    private static readonly (string Abbr, string Region)[] RegionAbbr =
        [("бгд", "belgorod"), ("бел", "belgorod"), ("белг", "belgorod"), ("крс", "kursk"), ("брн", "bryansk"), ("врн", "voronezh"), ("рнд", "rostov")];

    private static readonly string[] Borderline =
        ["bryansk", "kursk", "belgorod", "voronezh", "rostov", "lnr", "dnr", "zaporozhye", "kherson", "crimea", "sevastopol", "krasnodar"];

    /// <summary>«Весь Юг» у общих каналов (LPRalarm, «Радар по всей России», «Купол»): южные регионы под ракетной/авиационной угрозой.</summary>
    private static readonly string[] SouthMacro =
        ["crimea", "sevastopol", "krasnodar", "rostov", "zaporozhye", "kherson", "dnr", "lnr"];

    private static readonly string[] Frontline =
    [
        "belgorod", "kursk", "bryansk", "orel", "voronezh", "rostov", "krasnodar", "crimea", "sevastopol", "kherson",
        "zaporozhye", "lnr", "dnr", "kharkov", "ua_odesa", "ua_mykolaiv", "ua_dnipro", "ua_sumy", "ua_chernihiv", "ua_poltava",
    ];

    /// <summary>
    /// Именованные зоны, которые не являются населёнными пунктами, но регулярно встречаются в тревогах.
    /// Точка — ориентировочный центр зоны; радиус — её охват.
    /// </summary>
    private static readonly (Regex Pattern, string Label, string Region, double Lat, double Lon, double RadiusKm)[] Zones =
    [
        (new(@"р-?\s?280|трасс\w* новороссия|трасса новороссия", RegexOptions.Compiled), "Трасса Р-280 «Новороссия»", "zaporozhye", 46.85, 35.9, 60),
        (new(@"трасс\w* таврида", RegexOptions.Compiled), "Трасса «Таврида»", "crimea", 45.05, 34.6, 50),
        (new(@"керченск\w* полуостров", RegexOptions.Compiled), "Керченский полуостров", "crimea", 45.3, 35.95, 30),
        (new(@"керченск\w* пролив|крымск\w* мост", RegexOptions.Compiled), "Керченский пролив", "crimea", 45.3, 36.55, 15),
        (new(@"таманск\w* полуостров", RegexOptions.Compiled), "Таманский полуостров", "krasnodar", 45.2, 36.95, 25),
        (new(@"ара?батск\w* (?:стрелк|залив)", RegexOptions.Compiled), "Арабатская стрелка", "kherson", 45.85, 35.05, 30),
        (new(@"казантипск\w* залив", RegexOptions.Compiled), "Казантипский залив", "crimea", 45.45, 35.7, 15),
        (new(@"\bюбк\b|южн\w* берег\w* крыма", RegexOptions.Compiled), "Южный берег Крыма", "crimea", 44.55, 34.2, 35),
        (new(@"тарханкут", RegexOptions.Compiled), "Тарханкут", "crimea", 45.45, 32.75, 20),
        (new(@"донузлав", RegexOptions.Compiled), "Донузлав", "crimea", 45.4, 33.1, 12),
        (new(@"балаклавск\w* тэ[сц]", RegexOptions.Compiled), "Балаклавская ТЭС", "sevastopol", 44.52, 33.6, 5),
        (new(@"таврическ\w* тэ[сц]", RegexOptions.Compiled), "Таврическая ТЭС", "crimea", 44.96, 34.02, 5),
        (new(@"сакск\w* тэ[сц]", RegexOptions.Compiled), "Сакская ТЭС", "crimea", 45.13, 33.59, 5),
        (new(@"бухт\w* севастопольск|севастопольск\w* бухт", RegexOptions.Compiled), "Севастопольская бухта", "sevastopol", 44.62, 33.55, 5),
        // «Причерноморье» у каналов тревог — Черноморское побережье Кубани (Анапа — Сочи); см. KubanCoastApplies
        (new(@"причерноморь|побережье краснодарск|черноморск\w* побережь\w* (?:кубан|краснодар)", RegexOptions.Compiled), "Черноморское побережье Кубани", "krasnodar", 44.4, 38.6, 80),
        (new(@"приазовье", RegexOptions.Compiled), "Приазовье", "zaporozhye", 46.75, 36.4, 60),
        (new(@"^азовское море|\bнад азовским морем|акватори\w* азовского", RegexOptions.Compiled), "Азовское море", "zaporozhye", 46.2, 36.7, 90),
        (new(@"таганрогск\w* залив", RegexOptions.Compiled), "Таганрогский залив", "rostov", 47.05, 38.45, 40),
        // северная (российская) часть Каспия у Дагестана, Калмыкии и Астраханской области — без пункта сообщение было бы без места
        (new(@"акватори\w* каспийск\w*|\bнад каспи\w*|^каспийск\w* мор", RegexOptions.Multiline | RegexOptions.Compiled), "Каспийское море", "dagestan", 44.3, 48.6, 120),
        (new(@"таманск\w* залив", RegexOptions.Compiled), "Таманский залив", "krasnodar", 45.28, 36.85, 10),
        (new(@"феодосийск\w* залив", RegexOptions.Compiled), "Феодосийский залив", "crimea", 44.95, 35.55, 18),
        // «Сиваш» без уточнения — и село в Херсонской области («Чонгар, Сиваш, Медведевка»): залив — только явные формы
        (new(@"залив\w* сиваш|(?<!сасык[\s-])сиваши\b|(?:через|от|над|по)\s+(?:залив\w*\s+)?сиваш[ау]?\b", RegexOptions.Compiled), "Залив Сиваш", "crimea", 45.74, 34.92, 30),
        (new(@"залив\w* красноперекопск\w*|перекопск\w* залив\w*|залив\w* перекопск\w*", RegexOptions.Compiled), "Залив Красноперекопск", "crimea", 46.177, 33.894, 15),
        // косы: зона — полоса вдоль береговой линии (см. AlertEngine.CoastZones)
        (new(@"кинбурнск\w* кос", RegexOptions.Compiled), "Кинбурнская коса", "kherson", 46.52, 31.75, 20),
        (new(@"тендровск\w* кос", RegexOptions.Compiled), "Тендровская коса", "kherson", 46.24, 31.80, 25),
        (new(@"бакальск\w* кос", RegexOptions.Compiled), "Бакальская коса", "crimea", 45.75, 33.19, 6),
        (new(@"белосарайск\w* кос", RegexOptions.Compiled), "Белосарайская коса", "dnr", 46.89, 37.33, 6),
        (new(@"обиточн\w* кос", RegexOptions.Compiled), "Обиточная коса", "zaporozhye", 46.62, 36.20, 12),
        // озеро у Евпатории — не село Сиваш в Херсонской области
        (new(@"сасык[\s-]?сиваш\w*", RegexOptions.Compiled), "Озеро Сасык-Сиваш", "crimea", 45.17, 33.53, 10),
    ];

    /// <summary>Зоны над водой: рисуются частью акватории (без суши); регионы берегов — в порядке приоритета.</summary>
    private static readonly Dictionary<string, string[]> WaterZones = new(StringComparer.Ordinal)
    {
        ["Азовское море"] = ["zaporozhye", "kherson", "dnr", "rostov", "krasnodar", "crimea"],
        ["Таганрогский залив"] = ["rostov", "krasnodar", "dnr"],
        ["Каспийское море"] = ["dagestan", "astrakhan", "kalmykia"],
        ["Керченский пролив"] = ["crimea", "krasnodar"],
        ["Таманский залив"] = ["krasnodar"],
        ["Феодосийский залив"] = ["crimea"],
        ["Казантипский залив"] = ["crimea"],
        ["Залив Сиваш"] = ["crimea", "kherson"],
        ["Залив Красноперекопск"] = ["crimea"],
    };

    /// <summary>Зоны на суше у нескольких регионов: Арабатская стрелка — север в Херсонской области, юг в Крыму.</summary>
    private static readonly Dictionary<string, string[]> SharedZones = new(StringComparer.Ordinal)
    {
        ["Арабатская стрелка"] = ["kherson", "crimea"],
    };

    /// <summary>Слова с заглавной буквы, которые не являются топонимами, хотя такие пункты в справочнике бывают.</summary>
    private static readonly HashSet<string> StopNames = new(StringComparer.Ordinal)
    {
        "тревога", "опасность", "отбой", "внимание", "повторно", "сохраняется", "ещё", "еще", "все", "всем", "особое", "принять",
        "самолет", "самолёт", "вертолет", "вертолёт",
        "меры", "безопасности", "работа", "пво", "бпла", "фпв", "ракетная", "авиационная", "возможно", "противник", "противника",
        "продолжаются", "массовые", "фиксации", "фиксация", "пуски", "запуски", "выходы", "между", "стык", "граница", "трасса",
        "мост", "море", "моря", "залив", "пролив", "берег", "порт", "пристань", "станция", "поселок", "посёлок", "село", "город",
        "район", "округ", "область", "край", "республика", "дорогие", "уважаемые", "спасибо", "огромное", "наши", "наш", "мы",
        "вы", "они", "ударный", "хорнет", "дартс", "шарк", "ланцет", "майя", "одновременно", "молния", "матка", "крыло", "баба", "яга", "мавик",
        "звезда", "победа", "заря", "мир", "свобода", "дружба", "восход", "рассвет", "новая", "новый", "старая", "старый",
        "красная", "красный", "большая", "малая", "верхний", "нижний", "лесной", "садовый", "центральный", "северный", "южный",
        "западный", "восточный", "степной", "речной", "луч", "маяк", "волна", "вперед", "надежда", "искра", "заречный",
        "сбросы", "сбросами", "сброс", "укрытие", "укрытия", "внимательными", "стоп", "движение", "ракеты", "шторм", "штормов",
        "нептун", "уаб", "каб", "рсзо", "самолеты", "самолёты", "вертолеты", "разведка", "дрг", "минирование", "лднр", "рф",
        "мо", "го", "сво", "всу", "нпз", "тэс", "аэс", "гэс", "пкр", "мрш", "реактивный", "реактивным", "реактивных",
        "оперштаб", "дорога", "север", "юг", "запад", "восток", "много", "очень", "напоминаем", "врио", "губернатор", "губернатора",
        "экстренная", "информация", "рсчс", "высокая", "активность", "вся", "весь", "федеральная", "грэс", "титан", "карло",
        "шбк", "юбк", "дмг", "мчс", "администрация", "глава", "оставайтесь", "спуститесь", "единичные", "фиксаций", "ракета",
        "крым", "кубань", "донбасс", "приграничье", "приграничные", "тыловые", "регионы", "области", "районы", "населенные",
        "пункты", "близлежащие", "часть", "летящих", "следующая", "волна", "внимание", "пролет", "пролёт", "дальнейшее",
        // служебные слова сводок, встречающиеся с заглавной буквы (валидация архива)
        "учения", "баллистика", "бомбовая", "кассеты", "арта", "осторожно", "корректировка", "чисто", "уничтожен", "сбит",
        "объявлена", "повышенная", "выясняем", "сообщают", "пошел", "пошёл", "вылеты", "центр", "сервер", "требования", "ставки",
        "штормам", "нептунам", "ваканси", "вакансии", "воздушная", "северо", "юго", "пострадавших", "пострадавшему", "пострадавшим",
        // обычные слова, совпадающие с названиями сёл (по архиву: «Сирены воздушной тревоги», «Второй УАБ», «Пожар
        // локализован», «Начало атаки», «Сигнал опасности»); отдельной строкой (список пунктов) — по-прежнему пункт
        "сирена", "сирены", "второй", "третий", "первый", "пожар", "пожары", "начало", "сигнал", "почта", "бригада", "примерный",
        "головка", "элеватор", "аэропорт", "аэродром", "хутор", "фонтан", "фонтаны", "начались",
    };

    /// <summary>
    /// Сообщение без явного региона (каналы-агрегаторы): определяем регион по большинству топонимов.
    /// Возвращает регионы-победители (несколько — при равенстве голосов).
    /// </summary>
    private static List<string> VoteRegions(List<Gazetteer.RawToken> raw, HashSet<int> used)
    {
        var votes = new Dictionary<string, double>(StringComparer.Ordinal);
        int names = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            if (used.Contains(i) || !IsNameStart(raw[i]) || StopNames.Contains(raw[i].Lower)) continue;
            int len = 1;
            if (i + 1 < raw.Count && !raw[i + 1].Break && (IsNameStart(raw[i + 1]) || IsAdminSuffix(raw[i + 1].Lower))) len = 2;
            var phrase = string.Join(' ', raw.Skip(i).Take(len).Select(t => t.Orig));
            var regs = PlaceIndex.RegionsOf(phrase, FrontlineRu);
            if (regs.Count == 0 && len == 2) regs = PlaceIndex.RegionsOf(raw[i].Orig, FrontlineRu);
            if (regs.Count == 0) continue;
            names++;
            // уникальное совпадение весит больше, чем название, встречающееся во многих регионах
            foreach (var r in regs) votes[r] = votes.GetValueOrDefault(r) + 1.0 / regs.Count;
            i += len - 1;
        }
        if (votes.Count == 0) return VoteAllRegions(raw, used);
        double best = votes.Values.Max();
        var winners = votes.Where(v => v.Value >= best - 1e-9).Select(v => v.Key).ToList();
        // уверенный перевес — один регион; иначе (одно неоднозначное название) — все кандидаты, если их немного:
        // в системе оповещения лишнее предупреждение безопаснее пропущенного
        if (best >= 0.99 || names >= 2) return winners;
        return winners.Count <= 3 ? winners : [];
    }

    /// <summary>
    /// «Сельцо-Брянск», «Валуйки-Пятницкое» — два пункта через дефис (маршрут/участок дороги), а не одно название.
    /// Разделяем, только если составное слово неизвестно, а обе части — известные топонимы.
    /// </summary>
    // «Сильный шторм», «шторм на море», «штормовое предупреждение» — погода, а не ракеты Storm Shadow
    [GeneratedRegex(@"(?:сильн\w*|небольш\w*|морск\w*)\s+шторм\w*|шторм\w*\s+(?:на|в)\s+(?:море|черном|азовском|каспи\w*)|штормов\w*\s+(?:предупрежд\w*|ветер|волн\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex WeatherStorm();

    private static string SplitHyphenPairs(string text) => SplitHyphenOnce(text);

    private static bool KnownName(string w) => Gazetteer.FindPlaces(Gazetteer.Tokenize(w)).Any() || PlaceIndex.RegionsOf(w, Frontline).Count > 0;

    private static string SplitHyphenOnce(string text)
    {
        // цепочка «А-Б-В»: если хотя бы два звена — известные топонимы, это маршрут
        text = HyphenChain().Replace(text, m =>
        {
            var parts = m.Value.Split('-');
            if (KnownName(m.Value)) return m.Value;
            return parts.Count(KnownName) >= 2 ? string.Join(", ", parts) : m.Value;
        });
        return SplitPairs(text);
    }

    [GeneratedRegex(@"[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]{2,}){2,}")]
    private static partial Regex HyphenChain();

    private static string SplitPairs(string text) => HyphenPair().Replace(text, m =>
    {
        var whole = m.Value;
        var a = m.Groups[1].Value;
        var b = m.Groups[2].Value;
        if (Gazetteer.FindPlaces(Gazetteer.Tokenize(whole)).Any() || PlaceIndex.RegionsOf(whole, Frontline).Count > 0) return whole;
        // «Зубово-Полянский район», «Матвеево-Курганский район» — составное название района, а не два пункта
        if (AdminAfter().IsMatch(text.AsSpan(m.Index + m.Length)) && PlaceIndex.RegionsOf(whole + " район", AllRu.Value).Count > 0) return whole;
        bool ka = Gazetteer.FindPlaces(Gazetteer.Tokenize(a)).Any() || PlaceIndex.RegionsOf(a, Frontline).Count > 0;
        bool kb = Gazetteer.FindPlaces(Gazetteer.Tokenize(b)).Any() || PlaceIndex.RegionsOf(b, Frontline).Count > 0;
        return ka && kb ? $"{a}, {b}" : whole;
    });

    [GeneratedRegex(@"\b([А-ЯЁ][а-яё]{2,})-([А-ЯЁ][а-яё]{2,})(?=\b)")]
    private static partial Regex HyphenPair();

    [GeneratedRegex(@"^\s+(?:район|округ|мо\b|го\b|муниципальн|городск)", RegexOptions.IgnoreCase)]
    private static partial Regex AdminAfter();

    /// <summary>В том же перечне (до хвоста <paramref name="tail"/>) есть другой район региона <paramref name="hit"/>, рядом с ним.</summary>
    private static bool ListNeighbour(List<string> tokens, int k, int tail, IndexedPlace hit)
    {
        for (int j = k + 1; j < tail; j++)
        {
            if (!IsAdjToken(tokens[j])) continue;
            if (PlaceIndex.Find(tokens[j] + " район", [hit.RegionId]) is { } n && Geo.DistanceKm(n.Pos, hit.Pos) < 120) return true;
        }
        return false;
    }

    private static bool IsAdjToken(string t) => t.Length > 5 && (t.EndsWith("ский") || t.EndsWith("цкий") || t.EndsWith("ской"));

    /// <summary>Слово — элемент перечня прилагательных, завершающегося «районы/МО/округа» («в Валуйском, Красногвардейском МО»).</summary>
    private static bool IsAdjListItem(List<string> tokens, int idx)
    {
        static bool Adj(string t) => t.EndsWith("ский") || t.EndsWith("цкий") || t.EndsWith("ской") || t.EndsWith("ском") || t.EndsWith("ского") || t.EndsWith("цком");
        if (!Adj(tokens[idx])) return false;
        for (int k = idx + 1; k < tokens.Count && k <= idx + 30; k++)
        {
            if (tokens[k] is "районы" or "районов" or "округа" or "округов" or "мо" or "го" or "районах" or "округах") return true;
            if (!Adj(tokens[k]) && tokens[k] is not ("и" or "г")) return false;
        }
        return false;
    }

    /// <summary>Все вхождения названия (по основе, с падежными окончаниями) лежат внутри указанных фрагментов текста.</summary>
    private static bool OnlyInsideSpans(string norm, string label, List<(int Start, int End)> spans)
    {
        if (label.Length < 4) return false;
        var stem = label.Length > 6 ? label[..^2] : label;
        bool any = false;
        for (int i = norm.IndexOf(stem, StringComparison.Ordinal); i >= 0; i = norm.IndexOf(stem, i + 1, StringComparison.Ordinal))
        {
            if (i > 0 && char.IsLetter(norm[i - 1])) continue;
            any = true;
            if (!spans.Any(s => i >= s.Start && i < s.End)) return false;
        }
        return any;
    }

    /// <summary>
    /// Одноимённые пункты: «Ялта, Мангуш, Урзуф» — это Ялта в ДНР, а не в Крыму; «Калиновка, Хомутовка» — курская
    /// Калиновка у Хомутовки, а не на востоке области. Если выбранный пункт лежит далеко (> 120 км) от всех остальных
    /// пунктов сообщения, а одноимённый — рядом с ними (< 45 км), берём ближний. Регион, который сообщение задаёт
    /// только этим пунктом, заменяется; явно названные регионы и регионы канала не трогаются.
    /// </summary>
    private static void ResolveNamesakes(ParsedMessage msg, IReadOnlyList<string> channelRegions)
    {
        if (msg.Places.Count(p => p.Pos != null && p.SeaId == null && p.BorderId == null && p.PartRegion == null) < 2) return;
        // регионы, названные словом в тексте или заданные каналом: пункт из такого региона переносится только в такой же
        // или в регион остальных пунктов (краснодарский канал пишет и про Крым: «Советский, Нижнегорский, Кировский район»)
        var stated = new HashSet<string>(channelRegions, StringComparer.Ordinal);
        foreach (var r in msg.Regions) if (RegionNamedInText(msg, r)) stated.Add(r);
        // два прохода: «Константиновка, Дзержинск, Горловка» — после переноса Константиновки и Дзержинск оказывается рядом
        for (int pass = 0; pass < 2; pass++)
        for (int i = 0; i < msg.Places.Count; i++)
        {
            var located = msg.Places.Where(p => p.Pos != null && p.SeaId == null && p.BorderId == null && p.PartRegion == null).ToList();
            var x = msg.Places[i];
            if (x.Pos is not { } pos || x.SeaId != null || x.BorderId != null || x.PartRegion != null || x.ZoneRadiusKm != null) continue;
            // «Куйбышево (Бильмак)» — прежнее и нынешнее название одного места: другое название рядом подтверждает выбор,
            // а тёзка в том же месте, что и другое название сообщения, — и есть этот пункт
            var aliasOf = located.Where(o => o != x && Gazetteer.Normalize(o.Label) != Gazetteer.Normalize(x.Label)).ToList();
            if (aliasOf.Any(o => Geo.DistanceKm(o.Pos!.Value, pos) < 2)) continue;
            if (!x.IsArea && aliasOf.Count > 0 && aliasOf.Min(o => Geo.DistanceKm(o.Pos!.Value, pos)) > 40 && PlaceIndex.ByPrefix(Gazetteer.Normalize(x.Label))
                    .FirstOrDefault(c => Gazetteer.Normalize(c.Name) == Gazetteer.Normalize(x.Label) && c.Kind == 'P' &&
                                         aliasOf.Any(o => Geo.DistanceKm(o.Pos!.Value, c.Pos) < 0.5)) is { } same)
            {
                Move(i, x, same);
                continue;
            }
            // город встроенного справочника: цель («на Алексеевку — Воронеж») — это он; областной центр («Орёл/Калуга») — всегда он,
            // а не одноимённое село. Остальные пункты переносим (Ялта у Мариуполя, Калиновка у Хомутовки)
            bool builtin = Gazetteer.Places.Any(g => g.Name == x.Label && g.RegionId == x.RegionId && Geo.DistanceKm(g.Pos, pos) < 1);
            // регион пункта не назван и не канала, а тёзка — в регионе канала/текста вплотную к остальным пунктам:
            // «Новороссийск… Гайдук, Борисовка и далее на порт» в краснодарском канале — не белгородская Борисовка,
            // «по трассе с Васильевки в сторону Бугаевки, Погарский район» в брянском — не запорожская Васильевка
            if (!stated.Contains(x.RegionId!) && stated.Count > 0 && !x.IsArea)
            {
                var near = located.Where(o => o != x && Gazetteer.Normalize(o.Label) != Gazetteer.Normalize(x.Label)).ToList();
                if (near.Count > 0 && near.Min(o => Geo.DistanceKm(o.Pos!.Value, pos)) > 100)
                {
                    var key0 = Gazetteer.Normalize(x.Label);
                    // город справочника («г. Алексеевка … на Воронеж» в воронежском канале — белгородский город) уступает
                    // только при регионе тёзки, названном в тексте, или при двух и более соседних пунктах рядом с тёзкой
                    bool strong = builtin || PlaceIndex.ByPrefix(key0, [x.RegionId!]).Any(c => Geo.DistanceKm(c.Pos, pos) < 15 && c.Population >= 10_000);
                    var local = PlaceIndex.ByPrefix(key0, stated).Where(c => Gazetteer.Normalize(c.Name) == key0 && c.Kind == 'P')
                        .Select(c => (c, d: near.Min(o => Geo.DistanceKm(o.Pos!.Value, c.Pos)))).Where(t => t.d < 40)
                        .Where(t => !strong || RegionNamedInText(msg, t.c.RegionId) || near.Count(o => Geo.DistanceKm(o.Pos!.Value, t.c.Pos) < 30) >= 2 ||
                                    near.Min(o => Geo.DistanceKm(o.Pos!.Value, pos)) > 300)
                        .OrderBy(t => t.d).FirstOrDefault();
                    if (local.c != null)
                    {
                        Move(i, x, local.c);
                        continue;
                    }
                }
            }
            if (builtin && x.IsTarget) continue;
            var pop = PlaceIndex.ByPrefix(Gazetteer.Normalize(x.Label), [x.RegionId!])
                .Where(c => Geo.DistanceKm(c.Pos, pos) < 15).Select(c => c.Population).DefaultIfEmpty(0).Max();
            bool large = pop >= 50_000;
            // сравниваем с пунктами той же роли: цель («в направлении Воронежа») может быть далеко от места фиксации
            var sameRole = located.Where(o => o != x && o.IsTarget == x.IsTarget && o.IsOrigin == x.IsOrigin &&
                                              Gazetteer.Normalize(o.Label) != Gazetteer.Normalize(x.Label)).ToList();
            var anyRole = located.Where(o => o != x && Gazetteer.Normalize(o.Label) != Gazetteer.Normalize(x.Label)).ToList();
            if (anyRole.Count == 0) continue;
            var name = Gazetteer.Normalize(x.Label);
            bool area = x.IsArea;
            IndexedPlace? best = null;
            double bestKm = 0;
            // сначала — по пунктам той же роли; если их нет или они сами ошибочны (два тёзки далеко друг от друга:
            // «от Прохоровка, Андреевка в направлении Бойцовское ДНР»), — по всем пунктам сообщения («от Угловое в направлении Орловка»),
            // но строже: только тёзка вплотную или в названном регионе, город справочника — только в названном регионе
            for (int round = 0; round < 2 && best == null; round++)
            {
            bool fallback = round == 1;
            var others = fallback ? anyRole : sameRole;
            if (others.Count == 0) continue;
            double curKm = others.Min(o => Geo.DistanceKm(o.Pos!.Value, pos));
            // 60–120 км — переносим только к тёзке вплотную у остальных пунктов
            // («на Тишкин/Урывский/Глубокий М4 (Каменский район)» — ростовский Глубокий, а не хутор в ЛНР)
            if (curKm <= 60) continue;
            double nearOnly = curKm <= 120 ? 20 : 45;
            var otherRegions = others.Select(o => o.RegionId).Where(r => r != null).ToHashSet(StringComparer.Ordinal);
            bestKm = nearOnly;
            // пункт — только точное имя (падежные «варианты» дают другие названия: «Приморск» → «Приморский»); район — его формы
            foreach (var key in area ? PlaceIndex.Variants(name) : [name])
                foreach (var c in PlaceIndex.ByPrefix(key).Where(c => Gazetteer.Normalize(c.Name) == key && c.RegionId != x.RegionId &&
                                                                       (c.Kind == 'A') == area && !Gazetteer.IsForeign(c.RegionId) &&
                                                                       (!stated.Contains(x.RegionId!) || stated.Any(s => SameArea(s, c.RegionId)) || otherRegions.Contains(c.RegionId))))
                {
                    double km = others.Min(o => Geo.DistanceKm(o.Pos!.Value, c.Pos));
                    // крупный город («Калуга», «Ялта») уступает тёзке, только если рядом с тёзкой не меньше двух пунктов сообщения
                    // (любой роли: «Ялта, Юрьевка и далее на Мариуполь») или регион тёзки назван в тексте, а регион города — нет
                    // или город совсем далеко (> 300 км) от всего остального («Горловка, Дзержинск» — не нижегородский)
                    if (large && located.Count(o => o != x && Geo.DistanceKm(o.Pos!.Value, c.Pos) < 45) < 2 &&
                        !(stated.Contains(c.RegionId) && !stated.Contains(x.RegionId!)) &&
                        !(others.Min(o => Geo.DistanceKm(o.Pos!.Value, pos)) > 300 && km < 30))
                        continue;
                    // запасной раунд — только для сёл: город справочника или крупный (Алексеевка, Россошь) остаётся собой
                    if (fallback && (builtin || pop >= 10_000 || km > 25 && !stated.Contains(c.RegionId))) continue;
                    // и не уводит из явно названного региона в неназванный
                    if (fallback && stated.Contains(x.RegionId!) && !stated.Any(s => SameArea(s, c.RegionId))) continue;
                    if (km < bestKm) { bestKm = km; best = c; }
                }
            }
            // «Советский и далее на Сельцо, Жуковка» — район города (Советский район Брянска), а не одноимённый посёлок за 100 км:
            // городской район того же имени рядом с остальными пунктами сообщения
            if (best == null && !area && !builtin && pop < 10_000 && anyRole.Min(o => Geo.DistanceKm(o.Pos!.Value, pos)) > 60 &&
                PlaceIndex.Find(x.Label + " район", [x.RegionId!]) is { Kind: 'P' } cityDistrict &&
                anyRole.Min(o => Geo.DistanceKm(o.Pos!.Value, cityDistrict.Pos)) < 30)
            {
                Move(i, x, cityDistrict);
                continue;
            }
            if (best == null)
            {
                if (sameRole.Count == 0) continue;
                double sameKm = sameRole.Min(o => Geo.DistanceKm(o.Pos!.Value, pos));
                if (sameKm <= 60) continue;
                bestKm = sameKm <= 120 ? 20 : 45;
            }
            // «Первомайский район, Первомайск» в крымском канале — это Первомайское (райцентр), а не Первомайск в ЛНР:
            // близкое по написанию название в явно названном регионе, рядом с остальными пунктами
            // Город встроенного справочника — только при явном регионе тёзки и рядом с районом этого имени
            // («Волчанск, Давыдовка … Запорожская область» — это харьковский Волчанск, а не село Волчанское)
            if (best == null && !large && !stated.Contains(x.RegionId!) && stated.Count > 0 && !area && name.Length >= 6)
                foreach (var c in PlaceIndex.ByPrefix(name, stated).Where(c => c.Kind == 'P' && Gazetteer.Normalize(c.Name).Length <= name.Length + 3 &&
                                                                            (!builtin || located.Any(o => o.IsArea && o.RegionId == c.RegionId &&
                                                                                Gazetteer.Normalize(o.Label).StartsWith(name[..^1], StringComparison.Ordinal)))))
                {
                    double km = sameRole.Min(o => Geo.DistanceKm(o.Pos!.Value, c.Pos));
                    if (km < Math.Min(bestKm, 30)) { bestKm = km; best = c; }
                }
            if (best == null) continue;
            Move(i, x, best);
        }

        void Move(int i, ParsedPlace x, IndexedPlace best)
        {
            var oldRegion = x.RegionId;
            msg.Places[i] = new ParsedPlace
            {
                Label = x.Label, RegionId = best.RegionId, Pos = best.Pos, IsTarget = x.IsTarget, IsOrigin = x.IsOrigin, IsVia = x.IsVia,
                IsArea = x.IsArea, GeocodeQuery = x.GeocodeQuery,
            };
            if (!msg.Regions.Contains(best.RegionId)) msg.Regions.Add(best.RegionId);
            // регион остался без пунктов и не назван в тексте явно — он появился только из-за ошибочного пункта
            if (oldRegion != null && !channelRegions.Contains(oldRegion) && msg.Places.All(p => p.RegionId != oldRegion) &&
                !RegionNamedInText(msg, oldRegion))
                msg.Regions.Remove(oldRegion);
        }
    }

    /// <summary>
    /// Название не нашлось в регионе сообщения (несостыковка «пункт не из своего региона»):
    ///   1) искажение рядом с остальными пунктами («Дятьково, Антрацит» в ЛНР — Дьяково у Антрацита);
    ///   2) «Знаменский район» в регионе, где района нет, — его центр (ЗАТО Знаменск);
    ///   3) район с уникальным во всей РФ названием («Кантемировский район / Астраханская область» у агрегатора) — добавляем
    ///      в его регионе; сам регион сообщения без пунктов остаётся под тревогой целиком: лишняя тревога безопаснее пропуска.
    /// Иначе — как прежде, онлайн-геокодер.
    /// </summary>
    private static bool ResolveMismatch(ParsedMessage msg, string name, int role, string region, IReadOnlyList<string> channelRegions)
    {
        var ctx = msg.Regions.Concat(channelRegions).Append(region).Distinct().ToList();
        var near = msg.Places.Where(p => p.Pos != null && p.SeaId == null && p.ZoneRadiusKm is null or < 40).Select(p => p.Pos!.Value).ToList();
        var norm = Gazetteer.Normalize(name);
        bool district = Regex.IsMatch(norm, @"(?:ский|цкий|кий) (?:район|округ|мо|го|р-н)$");
        IndexedPlace? hit = null;
        bool area = false;
        // опечатка в районе: «Шебекенский район», «Борисовкий р-н», «Рякитянском» (в перечне районов)
        if (district || Regex.IsMatch(norm, @"^\S+(?:ском|ского|скому|ским|цком)$"))
        {
            hit = PlaceIndex.FindDistrictTypo(norm.Split(' ')[0], ctx);
            area = hit != null;
        }
        if (hit == null && !district) hit = PlaceIndex.FindNearTypo(name, ctx, near);
        // одна правка без соседей («Кореневои близлежащие») — как в офлайн-разборе: только регионы контекста, единственный кандидат
        if (hit == null && !district && !norm.Contains(' ') && !Regex.IsMatch(norm, @"(?:ский|цкий|ская|ское)$")) hit = PlaceIndex.FindTypo(name, ctx);
        if (hit == null && district && Regex.IsMatch(norm, @"(?:ский|цкий) (?:район|округ|мо|го)$"))
        {
            var stem = Regex.Replace(norm, @"(?:ий|ой) (?:район|округ|мо|го)$", "");
            hit = PlaceIndex.ByPrefix(stem, ctx).Where(c => c.Kind == 'P' && Gazetteer.Normalize(c.Name) == stem).MaxBy(c => c.Population);
            // район соседнего региона (не дальше 400 км от региона сообщения): «Богучанский район / Воронежская область» —
            // опечатка «Богучарский», а не район в Красноярском крае
            // район прифронтового региона с уникальным названием — где угодно (агрегатор перепутал подпись региона:
            // «Кантемировский район / Астраханская область»); лишняя тревога в Воронежской области безопаснее пропуска
            if (hit == null && PlaceIndex.FindUnique(name, AllRu.Value) is { Kind: 'A' } d && !ctx.Contains(d.RegionId) &&
                (FrontlineRu.Contains(d.RegionId) || ctx.Any(r => Gazetteer.Get(r) is { } rg && Geo.DistanceKm(rg.Center, d.Pos) < 400)))
            {
                hit = d;
                area = true;
            }
        }
        if (hit == null) return false;
        AddPlace(msg, new ParsedPlace
        {
            Label = area ? DistrictLabel(hit) : hit.Name, RegionId = hit.RegionId, Pos = hit.Pos, IsArea = area,
            IsTarget = role == 1, IsOrigin = role == -1,
        });
        if (area && !msg.Regions.Contains(hit.RegionId)) msg.Regions.Add(hit.RegionId);
        return true;
    }

    /// <summary>Один регион для сводок: Крым и Севастополь («Республика Крым … Орловка» — севастопольская Орловка).</summary>
    private static bool SameArea(string a, string b) => a == b || a is "crimea" or "sevastopol" && b is "crimea" or "sevastopol";

    /// <summary>Регион назван словом в тексте («Ростовская область», «ДНР»); слово-город («Орёл») регион не называет.</summary>
    private static bool RegionNamedInText(ParsedMessage msg, string regionId)
    {
        var labels = msg.Places.Select(p => Gazetteer.Normalize(p.Label)).ToHashSet(StringComparer.Ordinal);
        foreach (var t in Gazetteer.Tokenize(msg.CleanText.Length > 0 ? msg.CleanText : msg.Raw.Text))
            if (!labels.Contains(t) && Gazetteer.RegionsForWord(t).Contains(regionId)) return true;
        return false;
    }

    /// <summary>Добавляет пункт без дублей (одно и то же место из разных правил разбора).</summary>
    /// <summary>
    /// Станицы и сёла с названиями областей: «Ленинградская, Кущевская» (Кубань), «Николаевская, Ростовская область»,
    /// «Курская, далее на Моздок» (Ставрополье). Слово — регион-«источник», значение — регион, где есть такой пункт.
    /// </summary>
    /// <summary>
    /// «Белгород и область», «Курск и район» — город вместе с областью (районом): тревога/отбой по всему региону, а не по
    /// городу. Иначе «Белгород и область — отбой» снимал только Белгород, а тревоги по Шебекино и области оставались.
    /// </summary>
    private static void ApplyCityAndRegion(ParsedMessage msg, string norm)
    {
        foreach (Match m in CityAndRegion().Matches(norm))
        {
            var city = msg.Places.FirstOrDefault(p => !p.IsTarget && !p.IsArea && p.RegionId != null && p.Pos != null &&
                                                      Gazetteer.Normalize(p.Label) == m.Groups["c"].Value);
            if (city == null) continue;
            bool district = m.Groups["k"].Value.StartsWith("район", StringComparison.Ordinal);
            if (district && CityDistrictPhrases(city.Label).Select(ph => PlaceIndex.Find(ph, [city.RegionId!])).FirstOrDefault(x => x is { Kind: 'A' }) is { } d)
            {
                msg.Places.Add(new ParsedPlace { Label = DistrictLabel(d), RegionId = d.RegionId, Pos = d.Pos, IsArea = true });
                continue;
            }
            // область (или район, которого нет в справочнике) — весь регион: пункт снимается, регион остаётся целиком
            msg.Places.Remove(city);
            if (!msg.Regions.Contains(city.RegionId!)) msg.Regions.Add(city.RegionId!);
        }
    }

    /// <summary>
    /// Варианты названия района города для поиска: «Курск» → «Курский район», «Орёл» → «Орловский район»,
    /// «Липецк» → «Липецкий район», «Тула» → «Тульский район», «Калуга» → «Калужский район».
    /// </summary>
    private static IEnumerable<string> CityDistrictPhrases(string city)
    {
        var c = Gazetteer.Normalize(city);
        if (c.EndsWith("цк", StringComparison.Ordinal)) yield return c + "ий район";
        if (c.EndsWith("ск", StringComparison.Ordinal) || c.EndsWith("цк", StringComparison.Ordinal)) yield return c[..^2] + "ский район";
        // беглая гласная: «Орёл» → «Орловский», «Козельск» не затрагивается (оканчивается на «ск»)
        if (c.Length > 3 && c[^2] is 'е' or 'о' && !"аеиоуыэюя".Contains(c[^1])) yield return c[..^2] + c[^1] + "овский район";
        if (c.EndsWith("га", StringComparison.Ordinal)) yield return c[..^2] + "жский район";
        if (c.Length > 3 && c[^1] is 'а' or 'я') yield return c[..^1] + "ьский район";
        yield return c + "ский район";
        yield return c + "овский район";
    }

    [GeneratedRegex(@"(?<![\p{L}-])(?<c>[\p{L}-]{3,}) и (?:вся |всей |весь )?(?<k>область|области|район|района|край|края)(?!\p{L})")]
    private static partial Regex CityAndRegion();

    private static readonly Dictionary<string, string> RegionNamedSettlements = new(StringComparer.Ordinal)
    {
        ["ленинградская"] = "krasnodar", ["полтавская"] = "krasnodar", ["воронежская"] = "krasnodar", ["калужская"] = "krasnodar",
        ["архангельская"] = "krasnodar", ["ярославская"] = "krasnodar", ["смоленская"] = "krasnodar", ["владимирская"] = "krasnodar",
        ["костромская"] = "krasnodar", ["тверская"] = "krasnodar", ["рязанская"] = "krasnodar", ["саратовская"] = "krasnodar",
        ["ивановская"] = "krasnodar", ["запорожская"] = "krasnodar", ["черниговская"] = "krasnodar", ["нижегородская"] = "krasnodar",
        ["николаевская"] = "rostov", ["кировская"] = "rostov", ["курская"] = "stavropol",
    };

    private static readonly Dictionary<string, string> HomeContext = new(StringComparer.Ordinal)
    {
        ["krasnodar"] = @"краснодарск|кубан|\bкк\b|ейск|ахтарск|тимашевск|кущевск|тихорецк|славянск-на-кубани|староминск|крыловск|павловск|каневск|брюховецк|кореновск|динск|кучугур|лабинск|белореченск",
        ["rostov"] = @"ростовск|волгодонск|цимлянск|сальск",
        ["stavropol"] = @"ставропол|буденновск|будённовск|моздок|минеральн",
    };

    /// <summary>
    /// Слово-регион — часть обжитого двусловного пункта региона канала или прифронтовой зоны: первое («Ивановская Лисица»)
    /// или второе («Старый Крым»).
    /// </summary>
    private static bool RegionWordInPlaceName(List<string> tokens, int i, IReadOnlyList<string> channelRegions)
    {
        IndexedPlace? Hit(string phrase) =>
            channelRegions.Count > 0 ? PlaceIndex.Find(phrase, channelRegions) : PlaceIndex.FindUnique(phrase, Frontline);
        static bool Lived(IndexedPlace? h) => h is { Kind: 'P', Population: > 0 } && h.Name.Contains(' ');
        if (i > 0 && !IsAdminSuffix(tokens[i - 1]) && tokens[i - 1] is not ("республика" or "республики" or "в" or "на" or "и" or "по" or "над") &&
            Lived(Hit(tokens[i - 1] + " " + tokens[i])))
            return true;
        if (i + 1 >= tokens.Count || IsAdminSuffix(tokens[i + 1]) ||
            tokens[i + 1] is "область" or "области" or "областей" or "обл" or "край" or "края" or "крае" or "республика" or "республики" or "и") return false;
        return Lived(Hit(tokens[i] + " " + tokens[i + 1]));
    }

    /// <summary>
    /// «на Украине», «на Октябрьском», «на Бессарабке», «на запорожском направлении» — предложный падеж: где находится,
    /// а не куда летит («на Октябрьское», «на Бессарабку»). Несклоняемые и совпадающие с названием формы («на Туапсе»,
    /// «на Джанкой») — цель, как и раньше.
    /// </summary>
    private static bool LocativeNa(List<string> tokens, int idx, string name)
    {
        if (idx == 0 || tokens[idx - 1] != "на") return false;
        var w = tokens[idx];
        var n = Gazetteer.Normalize(name).Split(' ', '-')[0];
        if (w == n) return false;
        return w.EndsWith('е') || w.EndsWith("ом", StringComparison.Ordinal) || w.EndsWith("ой", StringComparison.Ordinal) ||
               w.EndsWith("ей", StringComparison.Ordinal) || w.EndsWith("ах", StringComparison.Ordinal) || w.EndsWith("ях", StringComparison.Ordinal);
    }

    /// <summary>
    /// «посёлок Победы», «микрорайон Строителей», «улица Прогресса» — родительный падеж после нарицательного слова:
    /// часть города, названная в честь, а не одноимённый пункт («посёлок Победа» — пункт).
    /// </summary>
    private static bool NamedAfter(List<string> tokens, int idx, string name)
    {
        if (idx == 0 || tokens[idx - 1] is not ("поселок" or "пос" or "микрорайон" or "мкр" or "квартал" or "улица" or "ул" or "проспект" or "пр" or "площадь" or "парк" or "шахта" or "шахты"))
            return false;
        var w = tokens[idx];
        var n = Gazetteer.Normalize(name).Split(' ', '-')[0];
        return w != n && (w.EndsWith('ы') || w.EndsWith('и') || w.EndsWith("ей", StringComparison.Ordinal) || w.EndsWith("ов", StringComparison.Ordinal) || w.EndsWith('а'));
    }

    /// <summary>«на Тарханкуте», «на Керченском полуострове» — предложный падеж: где находится, а не куда летит.</summary>
    private static bool LocativeAfterNa(List<string> tokens, int start, int count)
    {
        if (start == 0 || tokens[start - 1] != "на") return false;
        var first = tokens[start];
        var last = tokens[Math.Min(tokens.Count, start + count) - 1];
        return first.EndsWith("ом", StringComparison.Ordinal) || first.EndsWith("ой", StringComparison.Ordinal) && count > 1 ||
               last.EndsWith('е');
    }

    private static bool KubanStanitsa(List<string> tokens, int i, string norm)
    {
        var t = tokens[i];
        // только именительный падеж («Ленинградская»), падежи области («Ленинградской области») — регион
        if (!RegionNamedSettlements.TryGetValue(t, out var home)) return false;
        if (i + 1 < tokens.Count && tokens[i + 1] is "область" or "обл" or "области" or "областей") return false;
        if (i > 0 && tokens[i - 1] is "область" or "обл") return false;
        // перечень областей («Курская / Орловская / Воронежская области, Краснодарский край») — регионы;
        // единственное «область» в строке относится к соседнему слову («Николаевская, Ростовская область»)
        foreach (Match lm in Regex.Matches(norm, $@"[^\n]*(?<!\p{{L}}){t}(?!\p{{L}})[^\n]*"))
            if (Regex.IsMatch(lm.Value, @"(?<!\p{L})(?:области|областей)(?!\p{L})")) return false;
        // в сообщении о регионе пункта: назван регион или его пункты
        if (!Regex.IsMatch(norm, HomeContext[home])) return false;
        return PlaceIndex.Find(t, [home]) != null;
    }

    private static bool KubanCoastApplies(string norm, IReadOnlyCollection<string> regions, IReadOnlyList<string> channelRegions)
    {
        if (regions.Contains("krasnodar") || Regex.IsMatch(norm, @"краснодарск|кубан|\bкк\b")) return true;
        if (regions.Any(r => r is "crimea" or "sevastopol" or "kherson" or "zaporozhye" or "ua_odesa" or "ua_mykolaiv")) return false;
        return channelRegions.Count == 0 || channelRegions.Contains("krasnodar");
    }

    /// <summary>
    /// «Приазовье» — участки Азовского побережья. Названы регионы побережья («Запорожская область РФ ⏎ Приазовье»,
    /// «Приазовье Краснодарского края») или канал региональный — только их участки, иначе всё побережье.
    /// </summary>
    private static void AddAzovCoast(ParsedMessage msg, List<string> tokens, HashSet<int> used, IReadOnlyList<string> channelRegions)
    {
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].StartsWith("приазов", StringComparison.Ordinal) && !IsAdminSuffix(i + 1 < tokens.Count ? tokens[i + 1] : "")) used.Add(i);
        var scope = CoastZones.AzovRegions.Where(msg.Regions.Contains).ToList();
        if (scope.Count == 0) scope = [.. CoastZones.AzovRegions.Where(channelRegions.Contains)];
        if (scope.Count == 0) scope = [.. CoastZones.AzovRegions];
        // участки одного названия — напрямую: AddPlace свёл бы их в один по совпадению названия
        msg.Places.RemoveAll(p => p.Label == CoastZones.AzovLabel);
        foreach (var s in CoastZones.Azov.Where(s => scope.Contains(s.Region)))
        {
            msg.Places.Add(new ParsedPlace { Label = CoastZones.AzovLabel, RegionId = s.Region, Pos = s.Mid, IsArea = true, ZoneRadiusKm = 30 });
            if (!msg.Regions.Contains(s.Region)) msg.Regions.Add(s.Region);
        }
    }

    private static void AddPlace(ParsedMessage msg, ParsedPlace p)
    {
        // одно место — одна запись: роль «цель» важнее нейтральной, нейтральная важнее «источника»
        // дубль — то же место (совпадение координат) или то же название
        // (одно название — одно место: варианты-тёзки из других регионов сюда не добавляются, их выбор — по контексту раньше)
        if (p.Pos is { } pos && msg.Places.FirstOrDefault(x => x.Pos is { } xp && (Geo.DistanceKm(xp, pos) < 0.3 || Gazetteer.Normalize(x.Label) == Gazetteer.Normalize(p.Label))) is { } dup)
        {
            if (p.IsTarget && !dup.IsTarget || !p.IsTarget && !p.IsOrigin && dup.IsOrigin)
            {
                // роль меняется, место (и регион) — то же
                msg.Places[msg.Places.IndexOf(dup)] = p;
            }
            return;
        }
        msg.Places.Add(p);
        if (p.RegionId != null && !msg.Regions.Contains(p.RegionId)) msg.Regions.Add(p.RegionId);
    }

    /// <summary>«Валуйский Район» / «Старооскольский городской округ» → «Валуйский район» / «Старооскольский округ».</summary>
    private static string DistrictLabel(IndexedPlace hit)
    {
        var n = hit.Name;
        var lower = n.ToLowerInvariant();
        if (lower.EndsWith(" муниципальный округ") || lower.EndsWith(" городской округ")) return n[..n.LastIndexOf(' ', n.Length - 7)] + " округ";
        if (lower.EndsWith(" район")) return n[..^6] + " район";
        return n;
    }

    /// <summary>Слова, которые не бывают топонимами даже как отдельный элемент списка.</summary>
    private static readonly HashSet<string> NeverPlace = new(StringComparer.Ordinal)
    {
        "тревога", "опасность", "отбой", "внимание", "повторно", "сохраняется", "принять", "меры", "безопасности", "бпла", "фпв",
        // «Самолёт отработал по ЛБС» — деревня Самолёт есть в Брянской области
        "самолет", "самолёт", "вертолет", "вертолёт",
        "ракетная", "авиационная", "возможно", "противник", "противника", "продолжаются", "массовые", "фиксации", "фиксация",
        "пуски", "запуски", "выходы", "между", "трасса", "мост", "море", "дорогие", "уважаемые", "спасибо", "огромное", "ударный",
        "хорнет", "дартс", "шарк", "ланцет", "майя", "укрытие", "укрытия", "стоп", "движение", "ракеты", "уаб", "каб", "рсзо", "лднр",
        "рф", "мо", "го", "всу", "нпз", "пкр", "оперштаб", "одновременно", "много", "очень", "напоминаем", "экстренная", "информация", "рсчс",
        "крым", "кубань", "приграничные", "тыловые", "регионы", "области", "районы", "близлежащие", "ещё", "еще", "все", "всем",
        "особое", "работа", "пво", "север", "юг", "запад", "восток", "дорога", "вся", "весь", "высокая", "активность", "единичные",
        "северный", "южный", "западный", "восточный", "центральный",
    };

    private static readonly Dictionary<string, string[]> NeighbourMap = new(StringComparer.Ordinal)
    {
        ["crimea"] = ["kherson", "sevastopol", "krasnodar"], ["sevastopol"] = ["crimea"], ["kherson"] = ["crimea", "zaporozhye"],
        ["zaporozhye"] = ["kherson", "dnr"], ["dnr"] = ["lnr", "zaporozhye", "rostov"], ["lnr"] = ["dnr", "rostov", "voronezh", "belgorod", "kharkov"],
        ["rostov"] = ["lnr", "dnr", "krasnodar", "voronezh"], ["krasnodar"] = ["crimea", "rostov", "adygea"], ["belgorod"] = ["kursk", "voronezh", "kharkov"],
        ["kursk"] = ["belgorod", "bryansk", "orel"], ["bryansk"] = ["kursk", "orel", "kaluga", "smolensk"], ["voronezh"] = ["belgorod", "rostov", "lnr", "lipetsk"],
        ["kharkov"] = ["belgorod", "lnr"], ["orel"] = ["kursk", "bryansk"],
    };

    /// <summary>«Саратовском районе», «Луховицкого МО» → «Саратовский район», «Луховицкий район».</summary>
    private static string CapitalizeAdmin(string phrase)
    {
        var adj = Gazetteer.Normalize(phrase).Split(' ')[0];
        // «-ской» — и косвенный падеж («в Саратовской районе» — редко), и именительный у кубанских районов («Каневской район»):
        // именительный не меняем, если фраза и так в нём («… район»)
        bool nominative = Gazetteer.Normalize(phrase).EndsWith(" район", StringComparison.Ordinal);
        foreach (var (end, repl) in new[] { ("ского", "ский"), ("скому", "ский"), ("ском", "ский"), ("ским", "ский"), ("ской", "ский"),
                                            ("цкого", "цкий"), ("цкому", "цкий"), ("цком", "цкий"), ("цким", "цкий"), ("цкой", "цкий") })
            if (adj.EndsWith(end, StringComparison.Ordinal))
            {
                if (!(nominative && end is "ской" or "цкой")) adj = adj[..^end.Length] + repl;
                break;
            }
        return char.ToUpperInvariant(adj[0]) + adj[1..] + " район";
    }

    private static bool hitIsAdminPhrase(string phrase)
    {
        var l = Gazetteer.Normalize(phrase);
        return l.EndsWith(" район") || l.EndsWith(" района") || l.EndsWith(" мо") || l.EndsWith(" го") || l.EndsWith(" округ");
    }

    private static IEnumerable<string> Neighbours(string r) => NeighbourMap.TryGetValue(r, out var n) ? n : [];

    private static readonly Lazy<string[]> AllRu = new(() => Gazetteer.Regions.Keys.Where(r => !Gazetteer.IsForeign(r)).ToArray());

    private static readonly string[] FrontlineRu = Frontline.Where(r => !r.StartsWith("ua_")).ToArray();

    /// <summary>Тыловые регионы: принимаем, только если минимум два названия однозначно указывают на один регион.</summary>
    private static List<string> VoteAllRegions(List<Gazetteer.RawToken> raw, HashSet<int> used)
    {
        var pool = Gazetteer.Regions.Keys.Where(r => !Gazetteer.IsForeign(r)).ToHashSet(StringComparer.Ordinal);
        // голос региона = сумма 1/N по названиям (N — в скольких регионах встречается название);
        // нужно ≥2 названия, совместимых с регионом, и хотя бы одно однозначное
        var score = new Dictionary<string, double>(StringComparer.Ordinal);
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Count; i++)
        {
            if (used.Contains(i) || !IsNameStart(raw[i]) || StopNames.Contains(raw[i].Lower)) continue;
            var regs = PlaceIndex.RegionsOf(raw[i].Orig, pool);
            if (regs.Count == 0 || regs.Count > 6) continue;
            foreach (var r in regs)
            {
                score[r] = score.GetValueOrDefault(r) + 1.0 / regs.Count;
                hits[r] = hits.GetValueOrDefault(r) + 1;
            }
            if (regs.Count == 1) unique.Add(regs.First());
        }
        var best = score.Where(v => hits[v.Key] >= 2 && unique.Contains(v.Key)).OrderByDescending(v => v.Value).ToList();
        return best.Count == 1 || (best.Count > 1 && best[0].Value > best[1].Value + 0.5) ? [best[0].Key] : [];
    }

    /// <summary>«Южная часть», «Северной части», «восточнее» — сторона света, а не пункт («Южная», «Северное»).</summary>
    private static bool IsCompassWord(List<string> tokens, int i) =>
        CompassAdverbs.Any(c => c.Word == tokens[i]) ||
        i + 1 < tokens.Count && tokens[i + 1].StartsWith("част", StringComparison.Ordinal) && CompassStems.Any(c => tokens[i].StartsWith(c.Stem, StringComparison.Ordinal));

    // «ОСИПЕНКО», «ЧЕРВОНОЕ ПОЛЕ» — названия заглавными (от 4 букв: короче — аббревиатуры «ДНР», «ПВО»);
    // латинская «ë» в кириллическом слове («Ивайтëнки») — опечатка раскладки, не латиница
    private static bool IsNameStart(Gazetteer.RawToken t) =>
        t.Orig.Length > 0 && char.IsUpper(t.Orig[0]) && (t.Orig.Any(char.IsLower) || t.Orig.Length >= 4 && t.Orig.All(c => char.IsUpper(c) || c == '-')) &&
        !t.Orig.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' && c != 'ë');

    // основа прилагательного (+ окончания) или целое слово (заканчивается пробелом)
    private static readonly Dictionary<string, string[]> AbbrevAdj = new(StringComparer.Ordinal)
    {
        ["Н"] = ["Нижн", "Нов"], ["Нижн"] = ["Нижн"], ["Нов"] = ["Нов"], ["Верх"] = ["Верхн"], ["Б"] = ["Больш", "Бел"], ["Бол"] = ["Больш"],
        ["М"] = ["Мал"], ["Мал"] = ["Мал"], ["Кр"] = ["Красн"], ["Ст"] = ["Стар", "Станица "], ["Яс"] = ["Ясн"], ["Им"] = ["Имени "],
        ["Наб"] = ["Набережн"],
        // «В»/«С» — обычно предлоги («В Белгороде», «С Нового года»): только с точкой («В. Копани», «С. Маячка»)
        ["В"] = ["Верхн", "Велик"], ["С"] = ["Стар"],
    };

    /// <summary>«Н. Мельницы» → «Нижние Мельницы», «Б. Утриш» → «Большой Утриш» (слово перед <paramref name="i"/> — сокращение).</summary>
    private static (IndexedPlace Hit, int Len)? AbbrevPlace(List<Gazetteer.RawToken> raw, int i, HashSet<int> used, IReadOnlyList<string> regions)
    {
        if (i == 0 || used.Contains(i - 1)) return null;
        var ab = raw[i - 1].Orig;
        // прочие сокращения с точкой («М. Пристань» → «Маслова Пристань», «Н. Березово» → «Нижнее Березово-Второе»):
        // по справочнику, только однозначно в регионах контекста
        // «Соц. Город» → «Соцгород» (→ «Соцгородок»)
        (IndexedPlace, int)? Fallback() =>
            !raw[i].Hard || ab.Length > 3 || !char.IsUpper(ab[0]) || !ab.Skip(1).All(char.IsLower) ? null
            : PlaceIndex.FindByAbbrev(ab, raw[i].Orig, regions) is { } fb ? (fb, 1)
            : ab.Length == 3 && PlaceIndex.Find(ab + raw[i].Lower, regions) is { Kind: 'P' } glued ? (glued, 1)
            : null;
        if (!AbbrevAdj.TryGetValue(ab, out var stems)) return Fallback();
        if (ab is "В" or "С" && !raw[i].Hard) return null;
        for (int len = Math.Min(2, raw.Count - i); len >= 1; len--)
        {
            if (Enumerable.Range(i, len).Any(used.Contains) || Enumerable.Range(i + 1, len - 1).Any(k => raw[k].Break || !IsNameStart(raw[k]))) continue;
            var phrase = string.Join(' ', raw.Skip(i).Take(len).Select(t => t.Orig));
            foreach (var stem in stems)
            {
                // «-их/-ых»: «От Н. Мельниц» → «Нижних Мельниц»
                string[] ends = stem.EndsWith(' ') ? [""]
                    : stem is "Больш" ? ["ой", "ая", "ое", "ие", "их"]
                    : stem is "Нижн" or "Верхн" ? ["ий", "яя", "ее", "ие", "их"]
                    : stem is "Велик" ? ["ий", "ая", "ое", "ие", "их"]
                    : ["ый", "ая", "ое", "ые", "ых"];
                foreach (var e in ends)
                    if (PlaceIndex.Find((stem + e).TrimEnd() + " " + phrase, regions) is { Kind: 'P' } hit &&
                        hit.Name.StartsWith(stem.TrimEnd()[..Math.Min(3, stem.TrimEnd().Length)], StringComparison.OrdinalIgnoreCase))
                        return (hit, len);
            }
        }
        return Fallback();
    }

    private static bool IsAdminSuffix(string lower) =>
        lower is "район" or "района" or "районе" or "мо" or "го" or "округ" or "округа" or "округе" or "р-н" or "р-не" or "р-на"
            or "муниципальный" or "муниципальном" or "муниципального" or "городской" or "городском" or "городского";

    private static readonly (string Word, double Deg)[] Compass =
    [
        ("северо-восток", 45), ("северо-запад", 315), ("юго-восток", 135), ("юго-запад", 225),
        ("север", 0), ("восток", 90), ("юг", 180), ("запад", 270),
    ];

    // шаблоны курса по сторонам света — один раз (Regex.Matches со строкой собирал их на каждое сообщение:
    // 16 шаблонов не помещались в кеш Regex и перестраивались, это была треть времени разбора)
    private static readonly (Regex To, Regex From, double Deg)[] CompassRx = [.. Compass.Select(c => (
        new Regex($@"(?<!\p{{L}})(?:на|курсом на)\s+{c.Word}\b(?<next>[ \t]+[^\s,.;)]+)?|(?<!\p{{L}})в сторону\s+{c.Word}(?:а|у)?\b(?<next>[ \t]+[^\s,.;)]+)?", RegexOptions.Compiled),
        new Regex($@"(?<!\p{{L}})(?:с|со стороны)\s+{c.Word}(?:а|е|у)?\b(?<next>[ \t]+[^\s,.;)]+)?", RegexOptions.Compiled),
        c.Deg))];

    /// <summary>Курс словами в исходном тексте сообщения (для аудита).</summary>
    internal static double? DetectHeadingText(string text) => DetectHeading(Gazetteer.Normalize(text));

    private static double? DetectHeading(string norm)
    {
        // «на северо-восток», «курсом на юг» — направление движения;
        // «с юга», «с моря», «со стороны запада» — откуда пришёл объект (курс противоположный)
        foreach (var (to, from, deg) in CompassRx)
        {
            // «на юг», «в сторону юга»; «на юге области» — место, а не курс; предлоги — отдельными словами («она», «курс»)
            // «на юг Московской области» — часть региона как цель, а не курс
            foreach (Match m in to.Matches(norm))
            {
                var next = m.Groups["next"].Value.Trim();
                if (next.Length > 0 && (next.StartsWith("республик") || next.StartsWith("област") || next.StartsWith("кра") ||
                                        Gazetteer.RegionsForWord(next).Count > 0))
                    continue;
                return deg;
            }
            // «с юга ЛНР», «с юга республики», «с севера Курской области» — из части региона, а не курс
            foreach (Match m in from.Matches(norm))
            {
                var next = m.Groups["next"].Value.Trim();
                if (next.Length > 0 && (next.StartsWith("республик") || next.StartsWith("област") || next.StartsWith("кра") ||
                                        Gazetteer.RegionsForWord(next).Count > 0))
                    continue;
                return (deg + 180) % 360;
            }
        }
        // «и на северо» в конце строки — оборванное «на северо-…»: общая часть — север/юг
        if (Regex.Match(norm, @"(?<!\p{L})на\s+(северо|юго)\s*(?:$|\n|[.,;!])") is { Success: true } cut)
            return cut.Groups[1].Value == "северо" ? 0 : 180;
        return null;
    }

    private static int DetectCount(List<string> tokens, string norm)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (int.TryParse(tokens[i], out var n) && n is > 0 and < 200 && (tokens[i + 1].StartsWith("бпла") || tokens[i + 1].StartsWith("беспилот") || tokens[i + 1].StartsWith("ракет") || tokens[i + 1].StartsWith("шт")))
                return n;
        if (norm.Contains("массир") || norm.Contains("массов") || norm.Contains("множеств")) return 10;
        if (norm.Contains("групп")) return 3;
        return 1;
    }

    /// <summary>1 — цель/направление, -1 — точка отсчёта, 0 — нейтрально.</summary>
    /// <summary>
    /// Регионы-направления (формат «Локатора России» и агрегаторов):
    ///   «…, Волгоградская область — пролёт БПЛА в сторону Саратовской области» — Саратовская область только цель;
    ///   «Волгоградская область — опасность по БПЛА от Ростовской и Воронежской областей» — откуда летят.
    /// Такие регионы не закрашиваются целиком и не попадают в регионы тревоги (иначе отбой в регионе-цели
    /// погасил бы тревогу там, где объект сейчас). Цель — конечная точка траектории, источник — курс.
    /// Если других мест в сообщении нет, регион остаётся регионом тревоги: лишнее предупреждение безопаснее.
    /// </summary>
    private static void ApplyRegionDirections(ParsedMessage msg, List<string> tokens, List<Gazetteer.RawToken> raw,
        List<(string Region, int Index)> mentioned)
    {
        if (mentioned.Count == 0) return;
        var idxRole = new Dictionary<int, int>();
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (r, idx) in mentioned.OrderBy(m => m.Index))
        {
            if (idx + 1 < tokens.Count && IsAdminSuffix(tokens[idx + 1])) continue; // «Курский район» — не регион
            int role = RegionRole(tokens, raw, idx, idxRole);
            idxRole[idx] = role;
            // регион, упомянутый и как место тревоги, и как направление, — место тревоги
            roles[r] = roles.TryGetValue(r, out var prev) && prev != role ? 0 : role;
        }
        // пункт-источник на стороне противника («от Краматорска в направлении ЛДНР») не делает регион местом тревоги
        bool NoPlaces(string r) => msg.Places.All(p => p.RegionId != r ||
            p.IsOrigin && p.Pos is { } op && Radar.Services.Borderline.IsEnemySide(op, p.RegionId));
        var targets = roles.Where(kv => kv.Value == 1 && NoPlaces(kv.Key)).Select(kv => kv.Key).ToList();
        var origins = roles.Where(kv => kv.Value == -1 && NoPlaces(kv.Key) && !Gazetteer.IsForeign(kv.Key)).Select(kv => kv.Key).ToList();
        // пункты-источники на стороне противника местом тревоги не являются: «от Краматорска в направлении ЛДНР» —
        // тревога в ЛДНР (цели остаются регионами тревоги)
        bool hasPlace = msg.Places.Any(p => p.Pos is { } pp && !(p.IsOrigin && Radar.Services.Borderline.IsEnemySide(pp, p.RegionId)));
        bool hasAlertRegion = roles.Any(kv => kv.Value == 0);

        bool pureTargets = hasPlace || hasAlertRegion || origins.Count > 0;
        foreach (var r in targets)
        {
            var reg = Gazetteer.Get(r)!;
            // единственное место в сообщении («БПЛА в сторону Саратовской области») — регион остаётся под тревогой
            AddPlace(msg, new ParsedPlace { Label = reg.Name, RegionId = r, Pos = reg.Center, IsTarget = true, IsArea = true, ZoneRadiusKm = 60 });
            if (pureTargets) msg.Regions.Remove(r);
        }
        if (!pureTargets) targets.Clear();

        if (origins.Count > 0 && (hasPlace || hasAlertRegion || targets.Count > 0))
        {
            // точка, к которой относится курс: первый пункт тревоги, иначе центр региона тревоги, иначе цель
            GeoPoint? at = msg.Places.FirstOrDefault(p => !p.IsTarget && p.Pos != null)?.Pos
                           ?? roles.Where(kv => kv.Value == 0).Select(kv => Gazetteer.Get(kv.Key)?.Center).FirstOrDefault(c => c != null)
                           ?? msg.Places.FirstOrDefault(p => p.IsTarget && p.Pos != null)?.Pos;
            // «от ЛДНР» — источник обе республики: курс от середины между ними
            var from = new GeoPoint(origins.Average(o => Gazetteer.Get(o)!.Center.Lat), origins.Average(o => Gazetteer.Get(o)!.Center.Lon));
            if (msg.Heading == null && at is { } a && Geo.DistanceKm(from, a) > 30) msg.Heading = Geo.Bearing(from, a);
            foreach (var r in origins) { msg.Regions.Remove(r); msg.OriginRegions.Add(r); }
        }
    }

    /// <summary>
    /// Ориентиры без населённых пунктов:
    ///   «по границе Воронежской и Ростовской области», «по границе с Белгородской областью» (второй регион — канала) —
    ///   зона вдоль общей границы регионов вместо заливки обоих регионов целиком;
    ///   «с выходом в Азовское море», «в направлении Азовского моря» — цель над водой у ближайшего берега от пункта
    ///   сообщения (прогноз ведётся в море, а не в условный центр).
    /// </summary>
    private static void ApplyBorderAndSea(ParsedMessage msg, string norm, IReadOnlyList<string> channelRegions)
    {
        if (BorderBetween().Match(norm) is { Success: true } bm)
        {
            string? a = RegionOfWord(bm.Groups["a"].Value), b = RegionOfWord(bm.Groups["b"].Value);
            // «по границе с Белгородской областью» — вторая сторона: регион канала (или единственный другой регион сообщения)
            if (bm.Groups["with"].Success)
                a = channelRegions.FirstOrDefault(r => r != b && !Gazetteer.IsForeign(r))
                    ?? msg.Regions.FirstOrDefault(r => r != b && !Gazetteer.IsForeign(r));
            if (a != null && b != null && a != b && RouteNetwork.BorderBetween(a, b) is { } line &&
                line.Parts.MaxBy(p => p.Length) is { Length: >= 2 } part)
            {
                var mid = Geo.Along(part, Geo.PathLength(part) / 2);
                string Short(string r) => Gazetteer.Get(r)?.Name.Replace(" область", "").Replace(" край", "") ?? r;
                string ra = Gazetteer.IsForeign(a) ? b : a;
                AddPlace(msg, new ParsedPlace
                {
                    Label = $"Граница: {Short(a)} — {Short(b)}", RegionId = ra, Pos = mid, IsArea = true, ZoneRadiusKm = 30,
                    BorderId = RouteNetwork.BorderKey(a, b),
                });
                foreach (var r in new[] { a, b })
                    if (!Gazetteer.IsForeign(r) && !msg.Regions.Contains(r)) msg.Regions.Add(r);
            }
        }

        // «в направлении стыка Смоленской и Калужской областей» — цель: точка их общей границы, ближайшая к месту фиксации,
        // а не центры обоих регионов
        if (JunctionTarget().Match(norm) is { Success: true } jm &&
            RegionOfWord(jm.Groups["a"].Value) is { } ja && RegionOfWord(jm.Groups["b"].Value) is { } jb && ja != jb &&
            RouteNetwork.BorderBetween(ja, jb) is { } jl && jl.Parts.SelectMany(p => p).Any())
        {
            var from = msg.Places.LastOrDefault(p => p.Pos != null && !p.IsTarget && p.RegionId != ja && p.RegionId != jb)?.Pos
                       ?? channelRegions.Concat(msg.Regions).Where(r => r != ja && r != jb).Select(r => Gazetteer.Get(r)?.Center).FirstOrDefault(c => c != null);
            if (string.CompareOrdinal(ja, jb) > 0) (ja, jb) = (jb, ja);
            var part = jl.Parts.MaxBy(p => p.Length)!;
            var at = from is { } f ? jl.Parts.SelectMany(p => p).MinBy(p => Geo.DistanceKm(p, f)) : Geo.Along(part, Geo.PathLength(part) / 2);
            string Short(string r) => Gazetteer.Get(r)?.Name.Replace(" область", "").Replace(" край", "") ?? r;
            msg.Places.RemoveAll(p => p.IsTarget && p.IsArea && p.RegionId is { } pr && (pr == ja || pr == jb) && p.Label == Gazetteer.Get(pr)?.Name);
            AddPlace(msg, new ParsedPlace
            {
                Label = $"Стык: {Short(ja)} — {Short(jb)}", RegionId = ja, Pos = at, IsArea = true, ZoneRadiusKm = 30, IsTarget = true,
            });
            // регионы стыка — направление полёта; место тревоги — пункты сообщения
            if (msg.Places.Any(p => !p.IsTarget && p.Pos != null)) { msg.Regions.Remove(ja); msg.Regions.Remove(jb); }
        }

        if (SeaExit().Match(norm) is { Success: true } sm && !msg.Places.Any(p => p.IsTarget && p.SeaId == null))
        {
            bool taganrog = sm.Value.Contains("таганрог");
            string seaId = taganrog ? "sea:taganrog" : "sea:azov";
            var from = msg.Places.LastOrDefault(p => p.Pos != null && !p.IsTarget && p.SeaId == null && p.BorderId == null);
            if (from?.Pos is { } fp && RouteNetwork.SeaPointNear(seaId, fp) is { } sp)
            {
                // пункт у самой воды (центр зоны «Арабатская стрелка» лежит в море): цель — дальше в сторону открытой акватории
                if (Geo.DistanceKm(fp, sp) < 10)
                {
                    var open = taganrog ? new GeoPoint(47.05, 38.4) : new GeoPoint(46.3, 36.6);
                    var q = Geo.Destination(fp, Geo.Bearing(fp, open), Math.Min(25, Geo.DistanceKm(fp, open)));
                    sp = RouteNetwork.SeaAt(q) != null ? q : sp;
                }
                AddPlace(msg, new ParsedPlace
                {
                    Label = taganrog ? "Таганрогский залив" : "Азовское море", RegionId = from.RegionId, Pos = sp,
                    IsTarget = true, IsArea = true, ZoneRadiusKm = 25, SeaId = seaId,
                });
            }
        }

        static string? RegionOfWord(string w) =>
            Gazetteer.RegionsForWord(w).FirstOrDefault(r => !r.StartsWith("ua_", StringComparison.Ordinal)) ?? Gazetteer.RegionsForWord(w).FirstOrDefault();
    }

    // «по границе Воронежской и Ростовской области», «вдоль границы Курской и Брянской областей», «по границе с Тамбовской областью»
    [GeneratedRegex(@"(?:по|вдоль)\s+(?:линии\s+)?границ[еыи]\s+(?:(?<a>\p{L}+(?:ской|цкой))\s+и\s+(?<b>\p{L}+(?:ской|цкой))|(?<with>с)\s+(?<b>\p{L}+(?:ской|цкой)))\s+(?:област|кра|республик)")]
    private static partial Regex BorderBetween();

    /// <summary>
    /// Слово с заглавной — прилагательное, а следом (в той же строке, через пробел, а не запятую) идёт нарицательное
    /// существительное, к которому оно относится: «Большое количество», «Мирный житель», «Красного уровня»,
    /// «Черноморского флота», «Азовское побережье», «Чёрного морей», «Примерное направление». Это не пункт.
    /// </summary>
    private static bool AdjectiveOfNoun(List<Gazetteer.RawToken> raw, int i, IReadOnlyList<string> regions)
    {
        if (i + 1 >= raw.Count || raw[i + 1].Break) return false;
        var adj = raw[i].Lower;
        if (!AdjectiveEnding().IsMatch(adj)) return false;
        var next = raw[i + 1];
        // следующее слово с заглавной — обычно часть названия («Новая Таволжанка»), но «Народную Республику» в названии
        // республики — то же определение к нарицательному слову; «Северного Кавказа» — часть названия края, а не пункт
        if (next.Orig.Length == 0) return false;
        if (next.Lower.StartsWith("кавказ", StringComparison.Ordinal) && adj.StartsWith("северн", StringComparison.Ordinal)) return true;
        if (char.IsUpper(next.Orig[0]) && !next.Lower.StartsWith("республик", StringComparison.Ordinal)) return false;
        if (!AdjectiveHeadNoun().IsMatch(next.Lower)) return false;
        // само сочетание — известный пункт или зона («Северная сторона» Севастополя, «Пятницкое кольцо», «Троицкий пост»)
        var pair = adj + " " + next.Lower;
        if (Gazetteer.FindPlaces([adj, next.Lower]).Any(m => m.Length == 2)) return false;
        if (PlaceIndex.Find(pair, regions.Count > 0 ? regions : Frontline) is { } pp && Gazetteer.Normalize(pp.Name).Contains(' ')) return false;
        return true;
    }

    [GeneratedRegex(@"(?:ая|яя|ое|ее|ий|ый|ой|ую|юю|ого|его|ому|ему|ом|ем|ей|ые|ие|ых|их|ым|им|ыми|ими)$")]
    private static partial Regex AdjectiveEnding();

    [GeneratedRegex(@"^(?:част[ьиюей]|республик\p{L}*|нефтебаз\p{L}*|баз[аыуе]|лини[яиюей]|дорог[аиуе]|улиц[аыуе]|трасс[аыуе]|территори\p{L}*|пехот\p{L}*|подстанци\p{L}*|станци[яиюей]|окраин[аыуе]|област\p{L}*|кра[яйюе]|сторон[аыуе]|школ[аыуе]|больниц[аыуе]|зон[аыуе]|границ\p{L}*|набережн\p{L}*|площад\p{L}*|аэродром\p{L}*|количеств\p{L}*|житель|жители|жителей|жителя|жителю|уров\p{L}*|флот\p{L}*|побережь\p{L}*|мор[еяюи]|морей|морям|морю|направлени\p{L}*|охот\p{L}*|канал\p{L}*|регион\p{L}*|время|времени|число|числа|масштаб\p{L}*|пост|поста|посту|кольц\p{L}*|шоссе|рын\p{L}*|вокзал\p{L}*|порт\p{L}*|аэропорт\p{L}*|завод\p{L}*|мост\p{L}*|переправ\p{L}*|фронт\p{L}*|участ\p{L}*|рубеж\p{L}*|отряд\p{L}*|бригад\p{L}*|войск\p{L}*|армии|армия|групп\p{L}*|волн\p{L}*|атак\p{L}*|удар\p{L}*|режим\p{L}*|сигнал\p{L}*|тревог\p{L}*|опасност\p{L}*|угроз\p{L}*|внимани\p{L}*|меры|мер|ночь|ночью|день|утро|вечер|сутки|суток|неделю|недели)$")]
    private static partial Regex AdjectiveHeadNoun();

    [GeneratedRegex(@"(?:направлени[еия]|сторону|далее\s+на|далее\s+в|(?<!\p{L})на|(?<!\p{L})в|(?<!\p{L})к)\s+(?:месту\s+)?стык[ау]?\s+(?:границ\s+)?(?<a>\p{L}+(?:ской|цкой))\s+и\s+(?<b>\p{L}+(?:ской|цкой))\s+(?:област|кра|республик)")]
    private static partial Regex JunctionTarget();

    // «с выходом в Азовское море», «далее в Азовское море», «в направлении Азовского моря», «в сторону Таганрогского залива»
    [GeneratedRegex(@"(?:выход\w*|далее|дальше|пролет\w*|пролёт\w*)\s+(?:\p{L}+\s+){0,2}?в\s+(?:азовское\s+море|таганрогский\s+залив)|(?:в\s+направлении|в\s+сторону|курсом\s+на)\s+(?:азовского\s+моря|таганрогского\s+залива)")]
    private static partial Regex SeaExit();

    // Стороны света: основа прилагательного/наречия → (азимут; 0 — центр)
    private static readonly (string Stem, double? Deg)[] CompassStems =
    [
        ("северо-восточн", 45), ("северо-западн", 315), ("юго-восточн", 135), ("юго-западн", 225),
        ("северн", 0), ("южн", 180), ("западн", 270), ("восточн", 90), ("центральн", null),
    ];
    private static readonly (string Word, double Deg)[] CompassAdverbs =
    [
        ("северо-восточнее", 45), ("северо-западнее", 315), ("юго-восточнее", 135), ("юго-западнее", 225),
        ("севернее", 0), ("южнее", 180), ("западнее", 270), ("восточнее", 90),
    ];
    private static readonly (string Word, double Deg)[] CompassSides =
    [
        ("северо-востоке", 45), ("северо-западе", 315), ("юго-востоке", 135), ("юго-западе", 225),
        ("севере", 0), ("юге", 180), ("западе", 270), ("востоке", 90),
    ];

    /// <summary>
    /// Компасные уточнения позиции:
    ///   «северо-восточнее Дивеево», «южнее Джанкой» — точка смещается от пункта (~12 км, для районов ~25 км);
    ///   «южная часть Ростовской области», «на юге области», «Северная часть» (регион канала) — вместо всего
    ///   региона подсвечивается зона в этой части (регион перестаёт быть «целиком»).
    /// </summary>
    private static void ApplyCompassQualifiers(ParsedMessage msg, List<string> tokens, List<Gazetteer.RawToken>? raw,
        List<(string Region, int Index)> mentioned, IReadOnlyList<string> channelRegions)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            // «северо-восточнее Дивеево»
            var adv = CompassAdverbs.FirstOrDefault(c => c.Word == tokens[i]);
            if (adv.Word != null)
            {
                var next = tokens[i + 1];
                if (next is "пункта" or "г" or "п" or "с" or "пгт" or "города" && i + 2 < tokens.Count) next = tokens[i + 2];
                var stem = next.Length > 5 ? next[..5] : next;
                var idx = msg.Places.FindIndex(p => p.Pos != null && !p.IsTarget && !p.IsOrigin && Gazetteer.Normalize(p.Label).StartsWith(stem, StringComparison.Ordinal));
                if (idx >= 0 && stem.Length >= 3)
                {
                    var p = msg.Places[idx];
                    double km = p.IsArea ? 25 : 12;
                    msg.Places[idx] = new ParsedPlace
                    {
                        Label = p.Label, RegionId = p.RegionId, Pos = Geo.Destination(p.Pos!.Value, adv.Deg, km),
                        IsTarget = p.IsTarget, IsOrigin = p.IsOrigin, IsArea = p.IsArea, ZoneRadiusKm = p.ZoneRadiusKm ?? (p.IsArea ? 20 : 10),
                    };
                }
                continue;
            }

            // «южная часть Ростовской области», «в северной части ЛНР», «на юге Ульяновской области»
            double? deg;
            int after;
            if (tokens[i + 1].StartsWith("част", StringComparison.Ordinal) && CompassStems.FirstOrDefault(c => tokens[i].StartsWith(c.Stem, StringComparison.Ordinal)) is { Stem: not null } cs)
                (deg, after) = (cs.Deg, i + 2);
            else if (tokens[i] is "на" or "в" && CompassSides.FirstOrDefault(c => c.Word == tokens[i + 1]) is { Word: not null } side)
                (deg, after) = (side.Deg, i + 2);
            else continue;
            // «от западной части Крыма», «в сторону северной части» — направление, а не место угрозы
            if (i > 0 && tokens[i - 1] is "от" or "из" or "со" or "с" or "сторону" or "направлении" or "на" or "к" or "далее") continue;
            if (i > 1 && tokens[i - 1] is "по" or "в" && tokens[i - 2] is "от" or "сторону" or "направлении") continue;
            // «Брянская область восточная часть ⏎ Курская область западная часть»: регион перед уточнением в той же строке
            // важнее региона на следующей строке
            bool SameLine(int from, int to) => raw == null || Enumerable.Range(from + 1, Math.Max(0, to - from)).All(k => k >= raw.Count || !raw[k].Break);
            string? before = mentioned.Where(m => m.Index < i && m.Index >= i - 3 && SameLine(m.Index, i)).Select(m => m.Region).LastOrDefault();
            string? following = mentioned.Where(m => m.Index >= after && m.Index <= after + 2 && SameLine(i, m.Index)).Select(m => m.Region).FirstOrDefault();
            // «Южная часть» у города («южная часть Белгорода») — это не регион
            string? region = following ?? before;
            if (region == null)
            {
                bool generic = after < tokens.Count && tokens[after] is "области" or "региона" or "края" or "республики" or "округа";
                // «Северная часть ⏎ Опасность по БПЛА» — часть региона из контекста
                bool lineEnd = after >= tokens.Count || Regex.IsMatch(tokens[after], "^(?:тревог|опасн|внимани|фиксац|бпла|ракетн|угроз|работа|пво|отбой|сбит|пролет|пролёт|группа|возможн)");
                if (!generic && !lineEnd) continue;
                var pool = msg.Regions.Where(r => !Gazetteer.IsForeign(r)).ToList();
                if (pool.Count == 0) pool = channelRegions.ToList();
                if (pool.Count != 1) continue;
                region = pool[0];
            }
            if (!msg.Regions.Contains(region) && !channelRegions.Contains(region)) continue; // регион-направление
            if (msg.Places.Any(p => p.RegionId == region && !p.IsTarget)) continue; // названы конкретные пункты
            if (Gazetteer.Get(region) is not { } reg || Gazetteer.IsForeign(region)) continue;

            double kmLat = reg.HalfLat * 111, kmLon = reg.HalfLon * 111 * Math.Cos(reg.Center.Lat * Math.PI / 180);
            var center = reg.Center;
            if (deg is { } d)
            {
                double rad = d * Math.PI / 180;
                center = new GeoPoint(reg.Center.Lat + Math.Cos(rad) * reg.HalfLat * 0.5, reg.Center.Lon + Math.Sin(rad) * reg.HalfLon * 0.5);
            }
            var name = (deg switch { 0 => "Север", 180 => "Юг", 270 => "Запад", 90 => "Восток", 45 => "Северо-восток", 315 => "Северо-запад",
                135 => "Юго-восток", 225 => "Юго-запад", _ => "Центр" }) + " — " + reg.Name;
            if (msg.Places.Any(p => p.Label == name)) continue;
            msg.Places.Add(new ParsedPlace
            {
                Label = name, RegionId = region, Pos = center, IsArea = true, PartRegion = region, PartBearing = deg,
                ZoneRadiusKm = Math.Round(Math.Clamp(Math.Min(kmLat, kmLon) * 0.55, 20, 140)),
            });
            if (!msg.Regions.Contains(region)) msg.Regions.Add(region);
        }
    }

    // Слова, которые могут стоять между указателем направления и названием региона
    private static readonly HashSet<string> RegionRoleFiller = new(StringComparer.Ordinal)
    {
        "на", "в", "к", "и", "или", "а", "также", "части", "южной", "северной", "западной", "восточной", "центральной",
        "юго-западной", "юго-восточной", "северо-западной", "северо-восточной", "стороны", "область", "области", "областей",
        "край", "края", "краю", "республики", "республику", "республика", "рф",
    };

    private static int RegionRole(List<string> tokens, List<Gazetteer.RawToken> raw, int idx, Dictionary<int, int> idxRole)
    {
        bool rawOk = raw.Count == tokens.Count;
        for (int k = idx - 1; k >= Math.Max(0, idx - 5); k--)
        {
            // перечень регионов продолжает роль предыдущего: «от Ростовской и Воронежской», «на Волгоградскую область, Саратовскую»
            // но не через конец строки/предложения: «…от ЛНР и Белгородской области.\nЛДНР ожидаем…» — новый пункт сводки
            if (rawOk && raw[k + 1].Hard) return 0;
            if (idxRole.TryGetValue(k, out var inherited)) return inherited;
            var t = tokens[k];
            if (rawOk && raw[k + 1].Break && t is not ("область" or "области" or "областей" or "край" or "края")) return 0;
            if (t is "сторону" or "направлении" or "направление" or "направления" or "курсом" or "далее" or "дальше") return 1;
            if (t is "от" or "из" or "со" or "с") return -1;
            if (!RegionRoleFiller.Contains(t)) return 0;
        }
        return 0;
    }

    /// <summary>«через Литвиновку», «через Беловодский район, …» — пункт, через который пролегает маршрут.</summary>
    private static bool IsViaAt(List<string> tokens, int index)
    {
        for (int k = index - 1; k >= Math.Max(0, index - 4); k--)
        {
            if (tokens[k] == "через") return true;
            if (TargetMarkers.Contains(tokens[k]) || OriginMarkers.Contains(tokens[k])) return false;
        }
        return false;
    }

    private static int RoleAt(List<string> tokens, int index)
    {
        // «от Каховки до Чаплынки», «от Береговое до Фруктовое»: начало участка — «откуда» (как раньше), а конец
        // («до …») — место тревоги, а не второе «откуда»
        if (AfterDo(tokens, index)) return 0;
        for (int k = index - 1; k >= Math.Max(0, index - 3); k--)
        {
            // «на территории Севского района», «на трассе», «на участке» — место, а не цель;
            // «от МО над Курском сбито» — над пунктом: место, а не откуда
            // «далее вдоль побережья: Анапа, Новороссийск, …» — пункты по пути, а не цель
            if (tokens[k] is "территории" or "трассе" or "участке" or "границе" or "окраине" or "въезде" or "выезде" or "над" or "вдоль") return 0;
            // «с направления Климово», «со стороны направления …» — откуда, а не куда
            if (tokens[k].StartsWith("напр", StringComparison.Ordinal) && k > 0 && tokens[k - 1] is "с" or "со") return -1;
            if (TargetMarkers.Contains(tokens[k]) || tokens[k].StartsWith("напр", StringComparison.Ordinal) || tokens[k] is "примерно" && k > 0 && tokens[k - 1].StartsWith("напр")) return 1;
            if (OriginMarkers.Contains(tokens[k])) return -1;
            if (tokens[k] == "и" || tokens[k] == "через") return 0;
        }
        return 0;
    }

    /// <summary>Пункт — конец участка «от A до B» (после «до» через 0–1 слово, без нового предлога).</summary>
    private static bool AfterDo(List<string> tokens, int index)
    {
        for (int k = index - 1; k >= Math.Max(0, index - 2); k--)
        {
            if (tokens[k] == "до") return true;
            if (TargetMarkers.Contains(tokens[k]) || OriginMarkers.Contains(tokens[k]) || tokens[k] is "и" or "через") return false;
        }
        return false;
    }

    private static bool Has(List<string> tokens, string norm, string[] stems)
    {
        foreach (var s in stems)
        {
            if (s.Contains(' '))
            {
                if (norm.Contains(s, StringComparison.Ordinal)) return true;
            }
            else if (s.Length <= 3)
            {
                if (tokens.Any(t => ShortStemMatch(t, s))) return true;
            }
            // по началу слова: «шторм» ≠ «Штормовое», «ракет» = «ракетная»
            else if (tokens.Any(t => StemMatch(t, s))) return true;
        }
        return false;
    }

    /// <summary>
    /// Короткая основа (аббревиатура): само слово или его падежная форма («каба», «кабы», «бэки»),
    /// а не любое слово с этим началом («кабан», «кабель», «бэкап»).
    /// </summary>
    private static bool ShortStemMatch(string token, string stem) =>
        token == stem || token.StartsWith(stem, StringComparison.Ordinal) && token.Length <= stem.Length + 2 &&
        token[stem.Length..] is "а" or "ы" or "у" or "ом" or "ов" or "ам" or "ах" or "и" or "ми" or "ой" or "е" or "-" || token.StartsWith(stem + "-", StringComparison.Ordinal);

    private static bool StemMatch(string token, string stem) =>
        token.StartsWith(stem, StringComparison.Ordinal) && !IsToponymLike(token, stem) &&
        // «крыло» (БПЛА-«крыло») — только само слово и его формы, не «Крыловская»/«Крылово»
        !(stem == "крыло" && token.Length > 6);

    /// <summary>Слово начинается с основы угрозы, но является топонимом («Штормовое», «Ракетное»).</summary>
    private static bool IsToponymLike(string token, string stem) =>
        token.Length > stem.Length + 2 && (token.EndsWith("ое") || token.EndsWith("ово") || token.EndsWith("ино") || token.EndsWith("ка") && !token.EndsWith("ракетка"))
        && stem is "шторм" or "ракет" or "нептун" or "катер" or "крылат" or "миномет";

    // Явные указания на тип: в сообщении «Реактивный БПЛА или ПКР Нептун» основная угроза — БПЛА, ракета — вариант.
    private static ThreatType DetectThreat(List<string> tokens, string norm)
    {
        if (norm.Contains("бомбов") || norm.Contains("авиабомб") || Has(tokens, norm, ["уаб", "каб"])) return ThreatType.Aviation;

        bool missile = Has(tokens, norm, MissileStems);
        bool uav = Has(tokens, norm, UavStems);
        if (missile && uav)
        {
            int mi = FirstIndex(tokens, MissileStems), ui = FirstIndex(tokens, UavStems);
            // «реактивный БПЛА» — это БПЛА; «ракетная опасность ... БПЛА» — ракета
            return ui < mi ? ThreatType.Uav : ThreatType.Missile;
        }
        if (missile) return ThreatType.Missile;
        if (uav) return ThreatType.Uav;
        if (Has(tokens, norm, NavalStems)) return ThreatType.Naval;
        if (Has(tokens, norm, AviationStems) || AircraftModelRx().IsMatch(norm)) return ThreatType.Aviation;
        // «Чугуев ещё выходы», «выходы РСЗО» — пуски с указанного направления
        if (LaunchGeneralRx().IsMatch(norm)) return ThreatType.General;
        // «Горловка арта», «Арта Кременная» — артобстрел (сленг)
        if (Has(tokens, norm, ArtilleryStems) || tokens.Any(t => t is "арта" or "арты" or "арту" or "артой")) return ThreatType.Artillery;
        if (norm.Contains("тревог") || norm.Contains("опасност")) return ThreatType.General;
        return ThreatType.None;
    }

    private static int FirstIndex(List<string> tokens, string[] stems)
    {
        for (int i = 0; i < tokens.Count; i++)
            foreach (var s in stems)
            {
                // составная основа («баба яга») — по первым словам подряд
                if (s.Contains(' '))
                {
                    var parts = s.Split(' ');
                    if (i + parts.Length <= tokens.Count && parts.Select((p, k) => tokens[i + k].StartsWith(p, StringComparison.Ordinal)).All(x => x)) return i;
                }
                else if (s.Length <= 3 ? ShortStemMatch(tokens[i], s) : StemMatch(tokens[i], s))
                    return i;
            }
        return int.MaxValue;
    }

    /// <summary>
    /// Отбой с пояснением, куда ушли объекты: «Воронежская область — отбой … Все БПЛА ушли на Котовск», «ракеты сбиты,
    /// летели на Шебекино, отбой», «Отбой … Были на Волчанск». Цель в части фразы без слова «отбой» — рассказ, а не место
    /// отбоя: иначе отбой по региону превращался в отбой по этому пункту и тревога оставалась. Регион, появившийся
    /// только из такой цели (Тамбовская обл. от Котовска), из отбоя тоже убирается. «Отбой … на Крупецкую зону» не трогается.
    /// </summary>
    private static void DropClearNarrativeTargets(ParsedMessage msg, string text, List<(string Region, int Index)> mentioned)
    {
        var clauses = Regex.Split(Gazetteer.Normalize(text), @"[.,;!?\n]+");
        var drop = new List<ParsedPlace>();
        foreach (var p in msg.Places.Where(p => p.IsTarget))
        {
            var first = Gazetteer.Tokenize(p.Label).FirstOrDefault();
            if (first == null || first.Length < 4) continue;
            var stem = first[..Math.Max(4, first.Length - 3)];
            var hit = clauses.Where(c => Gazetteer.Tokenize(c).Any(t => t.StartsWith(stem, StringComparison.Ordinal))).ToList();
            if (hit.Count > 0 && hit.All(c => !c.Contains("отбо", StringComparison.Ordinal))) drop.Add(p);
        }
        if (drop.Count == 0) return;
        foreach (var p in drop) msg.Places.Remove(p);
        var explicitRegions = mentioned.Select(m => m.Region).ToHashSet(StringComparer.Ordinal);
        foreach (var r in drop.Select(p => p.RegionId).OfType<string>().Distinct())
            if (!explicitRegions.Contains(r) && msg.Places.All(p => p.RegionId != r)) msg.Regions.Remove(r);
    }

    private static AlertStatus DetectStatus(List<string> tokens, string norm)
    {
        // «отбой», «отмена ... опасности»; но не «Отбой не даём», «Отбоя не было», «сохраняется до отбоя»
        // «Отбой дадим от служб», «как фиксации прекратятся, дадим отбой», «ожидайте отбой» — обещание, а не отбой
        // частицы — отдельными словами: «В Белгородском районе отбой» — не «не отбой»
        var negated = NegatedClearRx().Replace(norm, " ");
        if (negated != norm)
        {
            var t2 = Gazetteer.Tokenize(negated);
            if (!(Has(t2, negated, AllClearStems) || CancelDangerRx().IsMatch(negated)))
                return Has(t2, negated, ImpactStems) ? AlertStatus.Impact : Has(t2, negated, AirDefenseStems) ? AlertStatus.AirDefense : AlertStatus.Alert;
        }
        if (Has(tokens, norm, AllClearStems) || CancelDangerRx().IsMatch(norm)) return AlertStatus.AllClear;
        if (Has(tokens, norm, ImpactStems)) return AlertStatus.Impact;
        if (Has(tokens, norm, AirDefenseStems)) return AlertStatus.AirDefense;
        return AlertStatus.Alert;
    }

    private static IEnumerable<(string Name, int Role)> ExtractCandidates(string text)
    {
        // длинный текст (статья, памятка, обращение): названия — только в коротких строках-перечнях и после «в направлении»;
        // слова с заглавной буквы в начале предложений длинного абзаца — не топонимы
        bool article = text.Length > 400;
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine;
            bool prose = article && rawLine.Length > 100;
            int role = 0;
            var lower = Gazetteer.Normalize(line);
            if (lower.StartsWith("от ") || lower.StartsWith("из ")) { role = -1; line = line[3..]; }
            else if (lower.StartsWith("через ")) line = line[6..];

            var dir = Direction().Match(line);
            if (dir.Success)
            {
                var target = Tail().Replace(line[(dir.Index + dir.Length)..].Split(',')[0], "").Trim(' ', '.', ',', '(', ')');
                if (target.Length >= 3 && char.IsUpper(target[0]) && target.Split(' ').Length <= 3 && !IsService(target))
                    yield return (target, 1);
                line = line[..dir.Index];
            }
            if (prose) continue;

            foreach (var piece0 in Splitter().Split(line))
            {
                var piece = Tail().Replace(piece0, "").Trim(' ', '.', '-', '–', '—', ':', ';', '!');
                if (piece.Length < 3 || piece.Length > 40) continue;
                if (!char.IsUpper(piece[0])) continue;
                var words = piece.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 4) continue;
                if (IsService(piece)) continue;
                if (words.Any(w => w.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '@' or '/'))) continue;
                yield return (piece, role);
            }
        }
    }

    /// <summary>Фраза содержит служебное слово (проверка по началу слов, а не подстроке: «Крайнее» ≠ «край»).</summary>
    private static bool IsService(string piece)
    {
        var pn = Gazetteer.Normalize(piece);
        if (pn.EndsWith("район") || pn.EndsWith("района")) return false;
        var words = Gazetteer.Tokenize(pn);
        // «Северо-Задонск», «Северо-Гундоровский» — пункт, а не направление «северо-запад»
        if (words.Count == 1 && words[0].StartsWith("северо-", StringComparison.Ordinal) && CompassStems.All(c => !words[0].StartsWith(c.Stem, StringComparison.Ordinal)) &&
            !Regex.IsMatch(words[0], @"^северо-(?:восток|запад)\p{L}*$"))
            return false;
        foreach (var st in ServiceStems)
        {
            if (st.Length <= 4) { if (words.Any(w => w == st || (w.StartsWith(st, StringComparison.Ordinal) && w.Length <= st.Length + 2))) return true; }
            else if (words.Any(w => w.StartsWith(st, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static string ExpandAbbreviations(string name)
    {
        var s = Regex.Replace(name, @"\bМО\b", "муниципальный округ");
        s = Regex.Replace(s, @"\bГО\b", "городской округ");
        s = Regex.Replace(s, @"\bр-н\b|\bр-на\b", "район");
        s = Regex.Replace(s, @"\bрайона\b", "район");
        s = Regex.Replace(s, @"\bгор\.\s*", "");
        return s.Trim();
    }

    [GeneratedRegex(@"(?:^|(?<=\s))(?:и\s+)?(?:далее\s+)?(?:в\s+направлени[еи]|в\s+сторону|курсом\s+на)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Direction();

    [GeneratedRegex(@",|\s+и\s+(?=[А-ЯЁ])|\s-\s|\s–\s")]
    private static partial Regex Splitter();

    [GeneratedRegex(@"\s+(и\s+)?(близлежащ\w*|ближайш\w*|окрестност\w*|прилегающ\w*|район\w*\s+порта|повторно|далее|в\s+направлени\w*|в\s+сторон\w*|тревога\w*|опасност\w*)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tail();

    // «Весь Юг», «Юг» отдельной строкой, «… весь Юг», «по югу» — но не «юго-восток», «на юг», «с юга», «юг области»
    [GeneratedRegex(@"(?:^|\n)[ \t]*(?:весь[ \t]+)?юг[ \t]*[!.]?[ \t]*(?=\n|$)|(?<!\p{L})(?:весь|всему|по)[ \t]+юг[уе]?(?!\p{L})|(?:^|\n)[ \t]*юг[ \t]+отбой|отбо\w*[ \t]+юг(?!\p{L})")]
    private static partial Regex SouthWord();
}
