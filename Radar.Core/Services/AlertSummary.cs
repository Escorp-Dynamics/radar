using System.Text;
using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Текст тревоги для карточки и подсказки без того, что уже показано заголовком и плашками:
/// регионы события («Ярославская область»), уровень и тип угрозы («Опасность по МВШ», «Тревога по БПЛА»),
/// дежурные призывы («Срочно примите меры безопасности»). Остаются пункты, направления и подробности.
/// </summary>
public static partial class AlertSummary
{
    // уровень/статус, тип угрозы, связки — их наличие уже видно по плашкам
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "по", "при", "на", "над", "в", "во", "с", "со", "и", "для", "за", "о", "об", "у", "а",
        "еще", "мер", "меры", "срочно", "принять", "примите", "вниманию", "внимание", "внимания", "повторно", "остается", "действии",
        "ранее", "районам", "районах",
        "соблюдайте", "соблюдаем", "соблюдать", "рсчс", "региона", "регионе", "всей", "вся", "особое", "займите", "места",
        "работа", "работы", "атака", "атаки", "атаку",
        // «и близлежащие» без пункта (пункт был регионом события) — пустая добавка
        "близлежащие", "близлежащих", "ближайшие",
        "пво", "территории", "территория",
        "область", "области", "край", "края", "крае", "республика", "республики", "республике", "рф", "народная", "народной",
    };

    private static readonly string[] Prefixes =
    [
        "опасн", "тревог", "угроз", "фиксац", "отбо", "предосторожн", "безопасн", "сохраня", "продолжа",
        "объявл", "пуск", "противник", "возможн", "потенциальн", "применени",
    ];

    /// <summary>В отбое: «режим (повышенного) внимания сохраняется», «всё хорошо» — уровень после отбоя и междометия.</summary>
    private static readonly HashSet<string> AllClearExact = new(StringComparer.Ordinal)
    {
        "режим", "режиме", "повышенного", "переходим", "сохраняем", "оставляем", "все", "хорошо", "спокойно", "тихо",
    };

    /// <summary>
    /// Слова типа угрозы: дубль плашки, только если это тип события. «Ракетная опасность» в тревоге по БПЛА —
    /// другая угроза, её не убираем. «Ракетно-бомбовая» — авиационная опасность.
    /// </summary>
    private static readonly (string Word, bool Prefix, ThreatType[] Groups)[] TypeWords =
    [
        ("бпла", false, [ThreatType.Uav]), ("бпл", false, [ThreatType.Uav]), ("фпв", false, [ThreatType.Uav]), ("fpv", false, [ThreatType.Uav]),
        ("хорнет", false, [ThreatType.Uav]), ("дрон", false, [ThreatType.Uav]), ("дроны", false, [ThreatType.Uav]), ("дронов", false, [ThreatType.Uav]),
        ("дронам", false, [ThreatType.Uav]), ("дронами", false, [ThreatType.Uav]), ("беспилотн", true, [ThreatType.Uav]),
        ("ударн", true, [ThreatType.Uav]), ("реактивн", true, [ThreatType.Uav]), ("барражир", true, [ThreatType.Uav]), ("боеприпас", true, [ThreatType.Uav]),
        ("мвш", false, [ThreatType.Balloon]), ("мрш", false, [ThreatType.Balloon]), ("шар", false, [ThreatType.Balloon]),
        ("шары", false, [ThreatType.Balloon]), ("шаров", false, [ThreatType.Balloon]), ("аэростат", true, [ThreatType.Balloon]),
        ("ракет", true, [ThreatType.Missile, ThreatType.Aviation]), ("крылат", true, [ThreatType.Missile]), ("баллист", true, [ThreatType.Missile]),
        ("пкр", false, [ThreatType.Missile]), ("нептун", false, [ThreatType.Missile]),
        // «авиационная ракетная опасность» — каналы так пишут и ракетную, и авиационную; «по авиации» — только авиация
        ("авиационн", true, [ThreatType.Aviation, ThreatType.Missile]), ("авиац", true, [ThreatType.Aviation]), ("бомбов", true, [ThreatType.Aviation]), ("уаб", false, [ThreatType.Aviation]), ("каб", false, [ThreatType.Aviation]),
        ("бэк", false, [ThreatType.Naval]),
    ];

    /// <summary>Слово — тип угрозы: null — нет; true — тип события (дубль), false — другой тип (оставить).</summary>
    private static bool? TypeWord(string t, IReadOnlySet<ThreatType> groups)
    {
        foreach (var (w, prefix, g) in TypeWords)
            if (prefix ? t.StartsWith(w, StringComparison.Ordinal) : t == w)
                return g.Any(groups.Contains);
        return null;
    }

    /// <summary>Текст без дублей заголовка и плашек; пустая строка — показывать нечего.</summary>
    /// <param name="text">Текст сообщения (без промо).</param>
    /// <param name="regionIds">Регионы события — они в заголовке.</param>
    /// <param name="allClear">Событие — отбой (тогда «отбой …» тоже дубль).</param>
    /// <param name="groups">Группы угроз события (по плашке и перечню типов).</param>
    public static string Clean(string text, IEnumerable<string> regionIds, bool allClear, IEnumerable<ThreatType> groups)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var ids = regionIds as IReadOnlySet<string> ?? regionIds.ToHashSet(StringComparer.Ordinal);
        var g = groups.ToHashSet();
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = CleanLine(raw.Trim(), ids, allClear, g);
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append(".!?:;,".Contains(sb[^1]) ? " " : ", ");
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static string CleanLine(string line, IReadOnlySet<string> ids, bool allClear, IReadOnlySet<ThreatType> groups)
    {
        if (line.Length == 0 || Noise().IsMatch(line)) return "";
        // частичный отбой внутри действующей тревоги — строка целиком: где снято, важнее формы
        if (!allClear && Word().Matches(line).Any(m => Gazetteer.Normalize(m.Value).StartsWith("отбо", StringComparison.Ordinal))) return line;
        // «Курск и близлежащие опасность по БПЛА», «Горловка ДНР и близлежащие» — статус и регион внутри строки;
        // фраза с другим типом угрозы («ракетная опасность» в тревоге по БПЛА) остаётся
        var stripped = StatusPhrase().Replace(line, m =>
            Word().Matches(m.Value).Any(w => TypeWord(Gazetteer.Normalize(w.Value), groups) == false) ? m.Value : " ");
        // «Фиксация от 3 БПЛА» → «от 3 шт», «Сбитие БПЛА» → пусто: тип и статус уже на плашках, остаётся количество
        stripped = CountPhrase().Replace(stripped, m =>
        {
            if (m.Groups["type"].Success && TypeWord(Gazetteer.Normalize(m.Groups["type"].Value), groups) == false) return m.Value;
            if (m.Groups["n"].Success) return $" {(m.Groups["from"].Success ? "от " : "")}{m.Groups["n"].Value} шт ";
            if (m.Groups["group"].Success) return " группа ";
            if (m.Groups["many"].Success) return " много ";
            return " ";
        });
        if (ids.Contains("dnr") || ids.Contains("lnr")) stripped = InlineRegion().Replace(stripped, " ");
        // «Орёл и Орловская область …» — регион события внутри строки; «в сторону Курской области» — направление, остаётся
        stripped = RegionPhrase().Replace(stripped, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!IsEventRegion(Gazetteer.Normalize(name), ids)) return m.Value;
            var before = Word().Matches(stripped[..m.Index]).LastOrDefault()?.Value is { } pw ? Gazetteer.Normalize(pw) : "";
            return Directional.Contains(before) ? m.Value : " ";
        });
        stripped = Spaces().Replace(stripped, " ").Trim();
        if (stripped != line)
        {
            stripped = TrimDanglingTail(DanglingHead().Replace(stripped, "")).Trim();
            if (stripped.Length == 0) return "";
            line = SpaceBeforePunct().Replace(stripped, "$1");
        }
        // части строки с разделителями между ними: чётные — текст, нечётные — разделитель
        var parts = Separator().Split(line);
        var kept = new List<(string Sep, string Seg)>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            var sep = i > 0 ? parts[i - 1] : "";
            if (!Redundant(parts[i], ids, groups, allClear)) kept.Add((sep, parts[i].Trim()));
        }
        if (kept.Count == 0) return "";
        // все части на месте — строка как есть (с исходной пунктуацией)
        if (kept.Count * 2 - 1 == parts.Length) return line;
        var sb = new StringBuilder(kept[0].Seg);
        for (int i = 1; i < kept.Count; i++) sb.Append(kept[i].Sep).Append(kept[i].Seg);
        return sb.ToString().Trim().TrimEnd(',', ';', '-', '–', '—', ':').Trim();
    }

    private static bool Redundant(string segment, IReadOnlySet<string> ids, IReadOnlySet<ThreatType> groups, bool allClear)
    {
        var tokens = Word().Matches(segment).Select(m => Gazetteer.Normalize(m.Value)).ToList();
        if (tokens.Count == 0) return true;
        foreach (var t in tokens)
        {
            if (Exact.Contains(t)) continue;
            // в отбое дата/время («8.03 Отбой …») — уже в строке времени карточки
            if (allClear && (AllClearExact.Contains(t) || t.All(char.IsDigit))) continue;
            if (TypeWord(t, groups) is { } same) { if (same) continue; return false; }
            if (Prefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal))) continue;
            if (IsEventRegion(t, ids)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Слово — название региона события («Курской», «Крыму», «ЛДНР»). По основе имени региона, а не по любому
    /// слову, узнающему регион: «Орёл» — город, хотя по нему тоже определяется область.
    /// </summary>
    private static bool IsEventRegion(string t, IReadOnlySet<string> ids)
    {
        // слово из названия региона события, как в заголовке или официально: «Чувашская Республика», «Марий Эл», «Севастополь»
        foreach (var id in ids)
            if (NameWords(id).Contains(t)) return true;
        foreach (var id in Gazetteer.RegionsForWord(t))
        {
            if (!ids.Contains(id)) continue;
            // аббревиатура (без гласных): ЛДНР, СПб
            if (!t.Any(c => "аеиоуыэюя".Contains(c))) return true;
            var stem = t[..Math.Min(t.Length, Math.Max(4, t.Length - 2))];
            if (Gazetteer.Get(id) is { } r && Word().Matches(r.Name).Any(m => Gazetteer.Normalize(m.Value).StartsWith(stem, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> _nameWords = new(StringComparer.Ordinal);
    private static HashSet<string> NameWords(string id) => _nameWords.GetOrAdd(id, i =>
    {
        var r = Gazetteer.Get(i);
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in (string?[])[r?.Name, r?.PolygonQuery])
            if (s != null)
                foreach (Match m in Word().Matches(s)) words.Add(Gazetteer.Normalize(m.Value));
        return words;
    });

    /// <summary>Подписи каналов: первые два и «ещё N» — длинный перечень не растягивает карточку.</summary>
    public static string Sources(IEnumerable<string> channels, int show = 2)
    {
        var l = channels.Distinct(StringComparer.Ordinal).ToList();
        if (l.Count <= show + 1) return string.Join(", ", l.Select(c => "@" + c));
        return string.Join(", ", l.Take(show).Select(c => "@" + c)) + $" и ещё {l.Count - show}";
    }

    [GeneratedRegex(@"(\s*[,;]\s*|\s+[-–—]\s+|(?<=\p{L}|\d|\))[.!?:]+(?:\s+|$)|\s*[!?]+\s*)")]
    private static partial Regex Separator();

    [GeneratedRegex(@"[\p{L}\d][\p{L}\d-]*")]
    private static partial Regex Word();

    // «(ракетная) опасность/тревога/угроза/фиксации по (возможным/реактивным) БПЛА (и ФПВ) (сохраняется/повторно)»,
    // «меры безопасности при БПЛА»
    [GeneratedRegex(@"(?<![\p{L}\d])(?:отбо\w+\s+)?(?:работ\w*\s+(?:пво|вкс)(?:\s+по\s+(?:бпла|бпл|дрон\w*|ракет\w*))?(?![\p{L}\d])|(?:(?:ракетн|авиационн|беспилотн|бомбов)\w*[\s(),-]+)+(?:опасност|тревог)\w*(?:\s+сохраняется)?(?![\p{L}\d])(?!\s+по\s)|(?:(?:ракетн|авиационн)\w*\s+)?(?:(?:опасност|тревог|угроз|фиксаци)\w*|(?:соблюда\w+\s+)?меры\s+(?:безопасност|предосторожност)\w*)\s+(?:по|при)\s+(?:(?:возможн|реактивн|ударн)\w*\s+)?(?:бпла|бпл|мвш|мрш|фпв|дрон\w*)(?:\s+(?:и|,)\s+(?:фпв|мвш|мрш|аэростат\w*|барражирующ\w*\s+боеприпас\w*))?(?:\s*,?\s+(?:хорнет|сохраняется|повторно|остаётся\s+в\s+действии|остается\s+в\s+действии))*(?![\p{L}\d]))", RegexOptions.IgnoreCase)]
    private static partial Regex StatusPhrase();

    // ДНР/ЛНР после пункта — регион уже в заголовке
    [GeneratedRegex(@"(?<=(?<![\p{L}\d-])\p{Lu}[\p{L}\d-]*\s+)(?:Л?ДНР|ЛНР)(?:\s+РФ)?(?![\p{L}\d])")]
    private static partial Regex InlineRegion();

    private static readonly HashSet<string> Directional = new(StringComparer.Ordinal)
    {
        "направлении", "сторону", "сторона", "на", "к", "ко", "от", "из", "с", "со", "в", "во", "до", "через", "над", "границе", "курсом", "стороны",
    };

    // «и Орловская область», «, Курской области РФ», «Республика Крым»
    [GeneratedRegex(@"(?:\s*,\s*|\s+и\s+)?(?<![\p{L}\d-])(?:(\p{L}+)\s+(?:област[ьиюей]|кра[йяюе]|республик[аиеу])|(?:республик[аиеу])\s+(\p{L}+))(?:\s+РФ)?(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex RegionPhrase();

    // «(ещё) фиксация/фиксации/сбитие/сбития (от 3 ед.|группы|множественные) (БПЛА)»
    [GeneratedRegex(@"(?<![\p{L}\d])(?:(?:ещ[её]|продолжаются|продолжается)\s+)?(?:(?<many>множествен\w*|многочислен\w*|массов\w*|единичн\w*)\s+)?(?:фиксаци[яи]|фиксаций|сбити[яе]|сбитий)(?:\s+(?:(?<from>от)\s+)?(?<n>\d+)(?:\s*(?:ед\.?|шт\.?))?|\s+(?<group>групп\w*))?(?:\s+(?<type>бпла|бпл|дрон\w*|ракет\w*|фпв|мвш|мрш))?(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex CountPhrase();

    // подписи и возгласы, не попавшие в промо: «❗️Наблюдаем за всей Россией — наблюдатель.рф», «ПВО — герои!»
    [GeneratedRegex(@"^[^\p{L}]*(?:наблюдаем за всей россией\b.*|пво\s*[-–—]?\s*(?:герои|боги|молодцы)\b.*|вс[её] хорошо[^\p{L}]*|спасибо пво\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?:[\s,;:.!?–—-]|и\s+близлежащ\w*|и\s+ближайш\w*)+", RegexOptions.IgnoreCase)]
    private static partial Regex DanglingHead();

    /// <summary>Отбрасывает с конца знаки препинания, пробелы и союзы «и/а/в/на» — линейным проходом (регулярка с якорем $ квадратична на длинных пробелах).</summary>
    private static string TrimDanglingTail(string s)
    {
        int end = s.Length;
        while (end > 0)
        {
            char c = s[end - 1];
            if (char.IsWhiteSpace(c) || ",;:.!–—-".Contains(c)) { end--; continue; }
            // союз отдельным словом: перед ним пробел
            int start = end;
            while (start > 0 && char.IsLetter(s[start - 1])) start--;
            var w = s[start..end];
            if (start > 0 && char.IsWhiteSpace(s[start - 1]) && (w.Equals("и", StringComparison.OrdinalIgnoreCase) || w.Equals("а", StringComparison.OrdinalIgnoreCase)
                || w.Equals("в", StringComparison.OrdinalIgnoreCase) || w.Equals("на", StringComparison.OrdinalIgnoreCase))) { end = start; continue; }
            break;
        }
        return s[..end];
    }

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunct();
}
