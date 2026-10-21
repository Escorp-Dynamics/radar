using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

/// <summary>Вид линейного ориентира, вдоль которого движется объект.</summary>
public enum CorridorKind { Road = 1, Rail = 2, River = 3, Coast = 4, Border = 5, Sea = 6 }

/// <summary>«Вдоль трассы Р-280», «по руслу Десны», «вдоль жд путей», «по берегу от Геническа до Таганрога».</summary>
public sealed record CorridorHint(CorridorKind Kind, string? Name, string Text);

/// <summary>Участок дороги между двумя пунктами: «по трассе Рыльск — Хомутовка», «дорога Токмак — Бердянск».</summary>
public sealed record RoadSegmentHint(GeoPoint From, string FromLabel, GeoPoint To, string ToLabel);

/// <summary>Откуда запущен объект: явное место пуска («пуски из Николаевской области») или сторона («с моря»).</summary>
public sealed record LaunchHint(GeoPoint Pos, string Label, bool Explicit);

/// <summary>Подсказки для построения маршрута, извлечённые из текста сообщения.</summary>
public sealed class RouteHints
{
    public List<CorridorHint> Corridors { get; } = [];
    /// <summary>Дороги между пунктами — путь строится по графу дорог.</summary>
    public List<RoadSegmentHint> Segments { get; } = [];
    public LaunchHint? Launch { get; set; }
    /// <summary>Объект пришёл с моря («фиксация с моря», «со стороны Чёрного моря»).</summary>
    public bool FromSea { get; set; }
    /// <summary>Объект уходит вглубь («через Погарский район в тыл»): движется от линии соприкосновения.</summary>
    public bool ToRear { get; set; }
    public bool IsEmpty => Corridors.Count == 0 && Segments.Count == 0 && Launch == null;
}

/// <summary>Разбор подсказок маршрута: коридоры (дороги, ж/д, реки, берег, граница) и места пусков.</summary>
public static partial class RouteHintParser
{
    public static RouteHints Parse(string text, ParsedMessage msg)
    {
        var h = new RouteHints();
        var norm = Gazetteer.Normalize(text);

        // ---- дороги: номер трассы («Р-280», «М4», «А 240») — в любом месте сообщения
        foreach (Match m in RoadRef().Matches(text))
        {
            var lg = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var letter = char.ToUpperInvariant(lg[0]) switch { 'M' => 'М', 'P' => 'Р', 'A' => 'А', var c => c };
            var refName = $"{letter}-{m.Groups[3].Value}";
            if (h.Corridors.All(c => c.Name != refName)) h.Corridors.Add(new CorridorHint(CorridorKind.Road, refName, m.Value));
        }
        // названия трасс
        foreach (Match m in RoadName().Matches(norm))
        {
            // падежи — одно имя («Тавриде», «Тавриду» → «таврида»)
            var name = m.Groups[1].Value.StartsWith("таврид", StringComparison.Ordinal) ? "таврида" : "новороссия";
            if (h.Corridors.All(c => c.Name != name)) h.Corridors.Add(new CorridorHint(CorridorKind.Road, name, m.Value));
        }
        // «вдоль трассы», «по трассе Рыльск — Хомутовка» без номера — коридор по дорогам между названными пунктами
        foreach (Match m in AlongRoad().Matches(norm))
            if (h.Corridors.All(c => c.Kind != CorridorKind.Road))
                h.Corridors.Add(new CorridorHint(CorridorKind.Road, null, m.Value));

        // ---- дороги между пунктами: «трасса Рыльск — Хомутовка», «по дороге Токмак-Бердянск», «по дороге на Геройское»
        foreach (Match m in RoadBetween().Matches(text))
        {
            var a = FindPlace(msg, m.Groups["a"].Value);
            var b = FindPlace(msg, m.Groups["b"].Value);
            if (a?.Pos is { } pa && b?.Pos is { } pb && Geo.DistanceKm(pa, pb) is > 3 and < 250 &&
                h.Segments.All(s => s.FromLabel != a.Label || s.ToLabel != b.Label))
                h.Segments.Add(new RoadSegmentHint(pa, a.Label, pb, b.Label));
        }
        foreach (Match m in RoadToward().Matches(text))
        {
            // «по дороге на Геройское» — от пункта сообщения (не цели) к названному
            var b = FindPlace(msg, m.Groups["b"].Value);
            var a = msg.Places.FirstOrDefault(p => p.Pos != null && !p.IsTarget && p != b && (b?.Pos is not { } bp || Geo.DistanceKm(p.Pos!.Value, bp) > 3));
            if (a?.Pos is { } pa && b?.Pos is { } pb && Geo.DistanceKm(pa, pb) < 250 && h.Segments.All(s => s.ToLabel != b.Label))
                h.Segments.Add(new RoadSegmentHint(pa, a.Label, pb, b.Label));
        }
        // цепочка «Рыльск — Льгов — Курчатов … особое внимание трасса»: участки дороги между соседними пунктами
        if (TrassaMention().IsMatch(norm))
        {
            var chain = PlaceChain().Matches(text);
            foreach (Match m in chain)
            {
                var parts = Regex.Split(m.Value, @"\s*[—–]\s*|\s+-\s+");
                for (int i = 0; i + 1 < parts.Length; i++)
                {
                    var a = FindPlace(msg, parts[i].Split(',')[^1]);
                    var b = FindPlace(msg, parts[i + 1].Split(',')[0]);
                    if (a?.Pos is { } pa && b?.Pos is { } pb && Geo.DistanceKm(pa, pb) is > 3 and < 150 &&
                        h.Segments.All(sg => sg.FromLabel != a.Label || sg.ToLabel != b.Label))
                        h.Segments.Add(new RoadSegmentHint(pa, a.Label, pb, b.Label));
                }
            }
        }
        // участки дороги между пунктами найдены — безымянное «вдоль трассы» ими и задано, отдельным коридором не нужно
        if (h.Segments.Count > 0)
            h.Corridors.RemoveAll(c => c.Kind == CorridorKind.Road && c.Name == null);

        // ---- ж/д
        if (AlongRail().Match(norm) is { Success: true } rm) h.Corridors.Add(new CorridorHint(CorridorKind.Rail, null, rm.Value));

        // ---- реки: «по руслу реки Десна», «вдоль Волги», «по Дону»
        foreach (Match m in AlongRiver().Matches(norm))
        {
            var name = RiverName(m.Groups["n"].Value.Trim());
            if (name.Length > 0 && h.Corridors.All(c => c.Kind != CorridorKind.River || c.Name != name)) h.Corridors.Add(new CorridorHint(CorridorKind.River, name, m.Value));
        }
        // ---- берег: «по берегу», «вдоль побережья», «по береговой линии»
        if (AlongCoast().Match(norm) is { Success: true } cm) h.Corridors.Add(new CorridorHint(CorridorKind.Coast, null, cm.Value));
        // ---- граница: «по границе Курской и Брянской областей», «вдоль границы»
        if (AlongBorder().Match(norm) is { Success: true } bm)
            h.Corridors.Add(new CorridorHint(CorridorKind.Border, msg.Places.FirstOrDefault(p => p.BorderId != null)?.BorderId, bm.Value));

        // ---- место пуска
        h.Launch = DetectLaunch(norm, msg);
        h.FromSea = FromSeaWords().IsMatch(norm);
        h.ToRear = ToRearWords().IsMatch(norm);
        return h;
    }

    /// <summary>Пункт сообщения, соответствующий слову из текста («Хомутовка», «Рыльска»): по основе названия.</summary>
    private static ParsedPlace? FindPlace(ParsedMessage msg, string word)
    {
        var w = Gazetteer.Normalize(word.Trim());
        if (w.Length < 3) return null;
        string stem = w.Length > 5 ? w[..^2] : w;
        return msg.Places.FirstOrDefault(p => p.Pos != null && Gazetteer.Normalize(p.Label) == w)
               ?? msg.Places.FirstOrDefault(p => p.Pos != null && Gazetteer.Normalize(p.Label).StartsWith(stem, StringComparison.Ordinal));
    }

    /// <summary>
    /// Место пуска: «пуски/запуски … из Николаевской области», «от Одессы», «со стороны Черниговской области» (если это
    /// регион Украины или акватория) — явное; «с моря», «с Азовского моря» — сторона, уточняется по региону сообщения.
    /// </summary>
    private static LaunchHint? DetectLaunch(string norm, ParsedMessage msg)
    {
        // явные регионы-источники за пределами РФ — по ролям мест («от Харькова», «из Сумской области»)
        var origin = msg.Places.FirstOrDefault(p => p.IsOrigin && p.Pos != null && p.RegionId != null && Gazetteer.IsForeign(p.RegionId));
        if (origin != null) return new LaunchHint(origin.Pos!.Value, origin.Label, true);
        // «от Сум», «со стороны Черниговской области», «из Харьковской области» — регион Украины как источник
        foreach (Match m in OriginRegion().Matches(norm))
            foreach (var w in Gazetteer.Tokenize(m.Groups["w"].Value))
                foreach (var r in Gazetteer.RegionsForWord(w))
                    if (Gazetteer.IsForeign(r) && Gazetteer.Get(r) is { } reg0)
                        return new LaunchHint(LaunchPoint(reg0), reg0.Name, true);
        foreach (Match m in LaunchFrom().Matches(norm))
        {
            var words = Gazetteer.Tokenize(m.Groups["w"].Value);
            foreach (var w in words)
                foreach (var r in Gazetteer.RegionsForWord(w))
                    if (Gazetteer.IsForeign(r) && Gazetteer.Get(r) is { } reg)
                        return new LaunchHint(LaunchPoint(reg), reg.Name, true);
        }
        // акватории: точка пуска/подлёта — ближайшая к пункту сообщения часть акватории, а не условный центр моря
        foreach (var (pattern, pos, label, seaId) in Seas)
            if (Regex.IsMatch(norm, pattern))
            {
                var near = msg.Places.FirstOrDefault(p => p.Pos != null && !p.IsTarget && p.SeaId == null)?.Pos;
                return new LaunchHint(near is { } n && RouteNetwork.SeaPointNear(seaId, n) is { } sp ? sp : pos, label, false);
            }
        // «от Харькова», «пуски из Харьковской области» — подконтрольная Украине часть (регион kharkov в справочнике — часть РФ)
        if (Regex.IsMatch(norm, @"(?:пуск\w*|запуск\w*)[^\n.]{0,40}?(?:из|с)\s+харьковск|(?:от|из|со стороны)\s+харьков(?:а|ской)"))
            return new LaunchHint(new GeoPoint(49.95, 36.3), "Харьков", true);
        return null;
    }

    /// <summary>Типичная точка пуска в регионе-источнике — ближе к границе РФ, а не центр области.</summary>
    private static GeoPoint LaunchPoint(Region reg) => LaunchSites.TryGetValue(reg.Id, out var p) ? p : reg.Center;

    // известные районы пусков (по открытым сводкам): точки ближе к линии фронта/побережью
    private static readonly Dictionary<string, GeoPoint> LaunchSites = new(StringComparer.Ordinal)
    {
        ["ua_sumy"] = new(51.0, 34.6), ["ua_chernihiv"] = new(51.7, 32.6),
        ["ua_poltava"] = new(49.6, 34.4), ["ua_dnipro"] = new(48.45, 35.1), ["ua_odesa"] = new(46.4, 30.7),
        ["ua_mykolaiv"] = new(46.95, 31.95),
    };

    /// <summary>Название реки в именительном падеже (одна река — один коридор при разных падежах).</summary>
    private static string RiverName(string w)
    {
        if (w.Contains("донц")) return w.StartsWith("северск") ? "северский донец" : "донец";
        foreach (var (stem, nom) in new[] { ("волг", "волга"), ("днепр", "днепр"), ("десн", "десна"), ("ок", "ока"), ("сейм", "сейм"),
                     ("хопр", "хопер"), ("медведиц", "медведица"), ("оскол", "оскол"), ("ипут", "ипуть"), ("болв", "болва"), ("сож", "сож"),
                     ("сур", "сура"), ("угр", "угра"), ("жиздр", "жиздра"), ("миус", "миус"), ("кубан", "кубань"), ("дон", "дон") })
            if (w.StartsWith(stem, StringComparison.Ordinal) && w.Length <= stem.Length + 1) return nom;
        return w;
    }

    private static readonly (string Pattern, GeoPoint Pos, string Label, string SeaId)[] Seas =
    [
        (@"(?:с|из|со стороны|от)\s+таганрогск\w*\s+залив", new(47.05, 38.4), "Таганрогский залив", "sea:taganrog"),
        (@"(?:с|из|со стороны|от)\s+азовского\s+моря|через\s+азовское\s+море", new(46.3, 36.6), "Азовское море", "sea:azov"),
        (@"(?:с|из|со стороны|от)\s+черного\s+моря|акватори\w+\s+черного", new(44.6, 32.4), "Чёрное море", "sea:black"),
        (@"(?:с|из|со стороны|от)\s+каспи", new(43.5, 49.5), "Каспийское море", "sea:caspian"),
    ];

    // «Р-280», «М4», «А 240», «м-4»; строчная без дефиса («а 3 летят», «м 5») — не трасса
    [GeneratedRegex(@"(?<![\p{L}\d])(?:([МРАMPA])\s?-?\s?|([мрамpa])\s?-\s?)(\d{1,3})(?![\d\p{L}])")]
    private static partial Regex RoadRef();

    // «Таврида» упоминается и без слова «трасса»
    [GeneratedRegex(@"(?<!\p{L})(таврида|тавриде|тавриду|тавриды|новороссия|новороссии)(?!\p{L})")]
    private static partial Regex RoadName();

    [GeneratedRegex(@"(?:вдоль|по)\s+(?:трасс\w*|автодорог\w*|дорог\w*|шосс\w*|автомагистрал\w*)")]
    private static partial Regex AlongRoad();

    // «трасса Рыльск — Хомутовка», «по дороге Токмак-Бердянск», «автодорога Шебекино – Белгород»
    [GeneratedRegex(@"(?:[Тт]расс\w*|[Аа]втодорог\w*|[Дд]орог\w*|[Шш]оссе)\s+(?<a>[А-ЯЁ][а-яё]+(?:\s[А-ЯЁ][а-яё]+)?)\s*[-–—]\s*(?<b>[А-ЯЁ][а-яё]+(?:\s[А-ЯЁ][а-яё]+)?)")]
    private static partial Regex RoadBetween();

    // упоминание трассы без номера: «особое внимание трасса», «вдоль трассы», «на трассе»
    [GeneratedRegex(@"(?:^|[^\p{L}])трасс[аеуы]?(?![\p{L}])")]
    private static partial Regex TrassaMention();

    // цепочка пунктов через тире: «Рыльск — Марьино, Ивановское — Льгов — Курчатов»
    [GeneratedRegex(@"[А-ЯЁ][а-яё-]+(?:,\s*[А-ЯЁ][а-яё-]+)*(?:\s*[—–]\s*|\s+-\s+)[А-ЯЁ][а-яё-]+(?:(?:,\s*|\s*[—–]\s*|\s+-\s+)[А-ЯЁ][а-яё-]+)*")]
    private static partial Regex PlaceChain();

    // «по дороге на Геройское», «вдоль трассы в сторону Феодосии»
    [GeneratedRegex(@"(?:[Пп]о|[Вв]доль)\s+(?:трасс\w*|дорог\w*|автодорог\w*)\s+(?:на|в сторону|в направлении)\s+(?<b>[А-ЯЁ][а-яё]+(?:\s[А-ЯЁ][а-яё]+)?)")]
    private static partial Regex RoadToward();

    [GeneratedRegex(@"(?:вдоль|по)\s+(?:ж/?д|ж\.\s?д\.?|железн\w*\s+дорог\w*|жд\s+пут\w*|ж/д\s+пут\w*|железнодорожн\w*)")]
    private static partial Regex AlongRail();

    // словоформы рек целиком: «по Волгоградской области» — не Волга, «по угрозе» — не Угра, «по океану» — не Ока;
    // «по Кубани» без слова «река» — регион
    [GeneratedRegex(@"(?<!\p{L})(?:вдоль|по)\s+(?:(?<r>руслу\s+(?:реки\s+)?|реке\s+|реки\s+)?(?<n>волг[аеиу]|дон[уае]?|днепр[уае]?|десн[аеуы]|ок[аеиу]|сейм[уае]?|донц[уаы]|северск\w*\s+донц[уаы]|хопр[уае]?|медведиц[аеуы]|оскол[уае]?|ипут[ьи]|болв[аеуы]|сож[уае]?|сур[аеуы]|угр[аеуы]|жиздр[аеуы]|миус[уае]?)|(?:руслу\s+(?:реки\s+)?|реке\s+|реки\s+)(?<n>кубан[ьи]))(?!\p{L})")]
    private static partial Regex AlongRiver();

    [GeneratedRegex(@"(?:вдоль|по)\s+(?:побережь\w*|берег\w*|береговой\s+лини\w*)")]
    private static partial Regex AlongCoast();

    [GeneratedRegex(@"(?:вдоль|по)\s+(?:линии\s+)?границ\w*")]
    private static partial Regex AlongBorder();

    [GeneratedRegex(@"(?:^|[^\p{L}])(?:с|со стороны)\s+(?:(?:черного|азовского|каспийского)\s+)?моря(?!\p{L})|(?:^|[^\p{L}])с\s+акватори")]
    private static partial Regex FromSeaWords();

    [GeneratedRegex(@"(?<!\p{L})(?:в|на|вглубь)\s+(?:тыл|тыловую\s+част\p{L}*)(?!\p{L})|(?<!\p{L})вглубь(?:\s+территории)?(?!\p{L})")]
    private static partial Regex ToRearWords();

    [GeneratedRegex(@"(?:^|[^\p{L}])(?:от|из|со стороны)\s+(?<w>\p{L}+(?:\s+\p{L}+)?)")]
    private static partial Regex OriginRegion();

    [GeneratedRegex(@"(?:пуск\w*|запуск\w*|взлет\w*|взлёт\w*)[^\n.]{0,40}?(?:из|с|со стороны)\s+(?<w>[^\n.,]{3,50})")]
    private static partial Regex LaunchFrom();
}
