using System.IO.Compression;
using System.Globalization;
using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

public sealed record IndexedPlace(string Name, string RegionId, GeoPoint Pos, char Kind, int Population);

/// <summary>
/// Офлайн-справочник топонимов (GeoNames, ~170 тыс. названий по регионам угрозы).
/// Поиск — по нормализованному названию с учётом падежных форм и сокращений; неоднозначность
/// разрешается по региону из контекста сообщения.
/// </summary>
public static partial class PlaceIndex
{
    private static readonly Lazy<Dictionary<string, List<IndexedPlace>>> _index = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string[]> _sortedKeys = new(() => _index.Value.Keys.Order(StringComparer.Ordinal).ToArray());

    /// <summary>Ключи районов и округов («шебекинский район») с выделенным прилагательным — для поиска опечаток без перебора всех ключей.</summary>
    private static readonly Lazy<(string Adj, string Key)[]> _districtKeys = new(() =>
        [.. _sortedKeys.Value.Where(k => k.IndexOf(' ') > 0 && (k.EndsWith(" район", StringComparison.Ordinal) || k.EndsWith(" округ", StringComparison.Ordinal)))
            .Select(k => (k[..k.IndexOf(' ')], k))]);

    /// <summary>Названия, начинающиеся с префикса (для усечённых/искажённых написаний: «Красногвардейск» → «Красногвардейское»).</summary>
    public static IEnumerable<IndexedPlace> ByPrefix(string prefix) => ByPrefix(prefix, null);

    /// <summary>
    /// Записи, чьё название начинается с <paramref name="prefix"/>. Лимит считается по подходящим записям (регион из
    /// <paramref name="regions"/>), а не по всем ключам: иначе одноимённые пункты дальних регионов вытесняли бы
    /// нужные из окна и результат зависел бы от наполнения справочника.
    /// </summary>
    public static IEnumerable<IndexedPlace> ByPrefix(string prefix, IReadOnlyCollection<string>? regions)
    {
        var keys = _sortedKeys.Value;
        int i = Array.BinarySearch(keys, prefix, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        for (int n = 0; i < keys.Length && keys[i].StartsWith(prefix, StringComparison.Ordinal) && n < 40; i++)
        {
            bool any = false;
            foreach (var p in _index.Value[keys[i]])
                if (regions == null || regions.Contains(p.RegionId)) { any = true; yield return p; }
            if (any) n++;
        }
    }

    public static void Preload() => Task.Run(() => _index.Value);

    /// <summary>
    /// Поиск для выбора зон наблюдения: населённые пункты РФ по началу названия (ключа индекса),
    /// точное совпадение и крупные — первыми. Украинские регионы не включаются.
    /// </summary>
    public static List<IndexedPlace> SearchPlaces(string query, int limit = 30)
    {
        var q = Gazetteer.Normalize(query.Trim());
        if (q.Length < 2) return [];
        var res = new List<IndexedPlace>();
        var seen = new HashSet<(string, string)>();
        var keys = _sortedKeys.Value;
        int i = Array.BinarySearch(keys, q, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        // все совпадения по началу (отсечка до сортировки по населению теряла бы крупные города с «поздним» ключом);
        // предел — от совсем общих префиксов
        for (; i < keys.Length && keys[i].StartsWith(q, StringComparison.Ordinal) && res.Count < 20_000; i++)
            foreach (var p in _index.Value[keys[i]])
                if (p.Kind == 'P' && !Gazetteer.IsForeign(p.RegionId) && seen.Add((p.Name, p.RegionId))) res.Add(p);
        return [.. res.OrderByDescending(p => Gazetteer.Normalize(p.Name) == q)
            .ThenByDescending(p => p.Population).ThenBy(p => p.Name.Length).Take(limit)];
    }

    /// <summary>Ближайший к точке населённый пункт (не дальше <paramref name="maxKm"/>); крупные — предпочтительнее при равной близости.</summary>
    public static IndexedPlace? NearestPlace(GeoPoint p, double maxKm = 30)
    {
        IndexedPlace? best = null;
        double bestScore = double.MaxValue;
        double dLat = maxKm / 111.0, dLon = maxKm / (111.0 * Math.Max(0.2, Math.Cos(p.Lat * Math.PI / 180)));
        foreach (var list in _index.Value.Values)
            foreach (var x in list)
            {
                if (x.Kind != 'P' || Math.Abs(x.Pos.Lat - p.Lat) > dLat || Math.Abs(x.Pos.Lon - p.Lon) > dLon) continue;
                double km = Geo.DistanceKm(p, x.Pos);
                if (km > maxKm) continue;
                // посёлок в 2 км и город в 6 км — ориентиром удобнее город
                double score = km / (1 + Math.Log10(Math.Max(1, x.Population)) / 3);
                if (score < bestScore) { bestScore = score; best = x; }
            }
        return best;
    }

    // хвост составного названия → полные ключи: «пристань» → «маслова пристань», «березово» → «нижнее березово-второе»
    private static readonly Lazy<Dictionary<string, List<string>>> _tails = new(() =>
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var k in _index.Value.Keys)
        {
            int sp = k.IndexOf(' ');
            if (sp <= 0) continue;
            var tail = k[(sp + 1)..];
            foreach (var t in new[] { tail, tail.Split('-', ' ')[0] }.Distinct())
            {
                if (!d.TryGetValue(t, out var l)) d[t] = l = [];
                l.Add(k);
            }
        }
        return d;
    });

    /// <summary>
    /// «М. Пристань» → «Маслова Пристань», «Н. Березово» → «Нижнее Березово-Второе»: первое слово названия начинается
    /// с сокращения <paramref name="abbr"/>, остальное — <paramref name="rest"/>. Только в регионах контекста и только однозначно.
    /// </summary>
    public static IndexedPlace? FindByAbbrev(string abbr, string rest, IReadOnlyList<string> regions)
    {
        var a = Gazetteer.Normalize(abbr);
        if (a.Length == 0 || regions.Count == 0) return null;
        var found = new List<IndexedPlace>();
        foreach (var v in Variants(rest).Distinct())
            if (_tails.Value.TryGetValue(v, out var keys))
                foreach (var k in keys)
                    if (k.StartsWith(a, StringComparison.Ordinal) && k.IndexOf(' ') > a.Length)
                        foreach (var x in _index.Value[k])
                            if (x.Kind == 'P' && regions.Contains(x.RegionId) && !found.Any(f => f.Name == x.Name && f.RegionId == x.RegionId))
                                found.Add(x);
        // «Н. Березово-Второе» и «Нижнее Березово второе» — один пункт под двумя записями
        if (found.Count > 1 && found.All(f => Geo.DistanceKm(f.Pos, found[0].Pos) < 3)) return found.MaxBy(f => f.Population);
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Ближайший город с населением от <paramref name="minPopulation"/> не дальше <paramref name="maxKm"/>.</summary>
    public static IndexedPlace? NearestCity(GeoPoint p, double maxKm, int minPopulation)
    {
        IndexedPlace? best = null;
        double bestKm = maxKm;
        // города по убыванию населения, меньше порога — дальше не смотрим (трекер зовёт это на каждую отметку,
        // обход всего справочника в ~170 тыс. названий был заметной долей времени); грубый отсев по широте/долготе
        if (minPopulation < 10_000) throw new ArgumentOutOfRangeException(nameof(minPopulation), "список городов — от 10 тыс. жителей");
        double dLat = maxKm / 111.0, dLon = maxKm / (111.0 * Math.Max(0.2, Math.Cos(p.Lat * Math.PI / 180)));
        foreach (var x in Cities.Value)
        {
            if (x.Population < minPopulation) break;
            if (Math.Abs(x.Pos.Lat - p.Lat) > dLat || Math.Abs(x.Pos.Lon - p.Lon) > dLon) continue;
            double km = Geo.DistanceKm(p, x.Pos);
            if (km <= bestKm) { bestKm = km; best = x; }
        }
        return best;
    }

    /// <summary>Населённые пункты от 10 тыс. жителей, по убыванию населения (без повторов записей одного пункта).</summary>
    private static readonly Lazy<IndexedPlace[]> Cities = new(() =>
        [.. _index.Value.Values.SelectMany(l => l).Where(x => x.Kind == 'P' && x.Population >= 10_000).Distinct()
            .OrderByDescending(x => x.Population)]);

    public static int Count => _index.Value.Count;

    private static Dictionary<string, List<IndexedPlace>> Load()
    {
        var dict = new Dictionary<string, List<IndexedPlace>>(200_000, StringComparer.Ordinal);
        using var res = typeof(PlaceIndex).Assembly.GetManifestResourceStream("Radar.places.tsv.gz");
        if (res == null) return dict;
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var rd = new StreamReader(gz);
        while (rd.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 7) continue;
            if (p[4].Length == 0 || !double.TryParse(p[2], CultureInfo.InvariantCulture, out var plat) ||
                !double.TryParse(p[3], CultureInfo.InvariantCulture, out var plon) || !int.TryParse(p[5], CultureInfo.InvariantCulture, out var pop)) continue;
            var place = new IndexedPlace(p[6], p[1], new GeoPoint(plat, plon), p[4][0], pop);
            if (!dict.TryGetValue(p[0], out var list)) dict[p[0]] = list = new List<IndexedPlace>(1);
            list.Add(place);
        }
        AddRussianSpellings(dict);
        AddBasemapDistricts(dict);
        AddExtraDistricts(dict);
        return dict;
    }

    private static readonly HashSet<string> UkSpellingRegions = new(StringComparer.Ordinal)
        { "lnr", "dnr", "zaporozhye", "kherson", "crimea", "sevastopol", "rostov", "krasnodar", "belgorod", "kursk", "bryansk", "voronezh" };

    /// <summary>
    /// Пункты прифронтовых регионов, записанные в справочнике по-украински («Красний Лиман», «Дудчани», «Зелений Под»):
    /// каналы пишут их по-русски — добавляем русское написание тем же пунктом (если в регионе его ещё нет).
    /// </summary>
    private static void AddRussianSpellings(Dictionary<string, List<IndexedPlace>> dict)
    {
        // пункты с русским написанием по ячейкам ~5 км: украинская запись рядом с почти тёзкой — дубль («Дякове» у «Дьяково»)
        static (int, int) Cell(GeoPoint p) => ((int)Math.Floor(p.Lat * 20), (int)Math.Floor(p.Lon * 20));
        var cells = new Dictionary<(string, int, int), List<(string Key, IndexedPlace Place)>>();
        foreach (var (k, list) in dict)
            foreach (var x in list)
                if (x.Kind == 'P' && UkSpellingRegions.Contains(x.RegionId) && UkToRu(x.Name) == x.Name)
                {
                    var (cy, cx) = Cell(x.Pos);
                    if (!cells.TryGetValue((x.RegionId, cy, cx), out var c)) cells[(x.RegionId, cy, cx)] = c = [];
                    c.Add((k, x));
                }
        List<(string Key, IndexedPlace Place)> Near(IndexedPlace x)
        {
            var (cy, cx) = Cell(x.Pos);
            var res = new List<(string, IndexedPlace)>();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (cells.TryGetValue((x.RegionId, cy + dy, cx + dx), out var c)) res.AddRange(c);
            return res;
        }
        var add = new List<(string Key, IndexedPlace Place)>();
        foreach (var (_, list) in dict)
            foreach (var x in list)
            {
                // крупные записи («Донецьке», «Правий Берег») — районы городов, а не пункты
                if (x.Kind != 'P' || !UkSpellingRegions.Contains(x.RegionId) || x.Population > 100_000) continue;
                var ru = UkToRu(x.Name);
                if (ru == x.Name) continue;
                var key = Gazetteer.Normalize(ru);
                // «Молотовський» — районы городов; «Московське», «Донецьке» — совпадают с прилагательными-регионами
                // «Приморський район» (Мариуполь) — район города, записанный пунктом; «Петровського» — «имени …»: совпадает
                // с названиями улиц («на Петровского, 11»)
                if (key.Length < 5 || CityDistrictAdj().IsMatch(key) || Gazetteer.IsRegionWord(key.Split(' ', '-')[0]) ||
                    key.EndsWith(" район", StringComparison.Ordinal) || key.EndsWith("ого", StringComparison.Ordinal)) continue;
                if (dict.TryGetValue(key, out var ex) && ex.Any(e => e.RegionId == x.RegionId)) continue;
                if (IsRussianDuplicate(Near(x), key, x)) continue;
                add.Add((key, x with { Name = ru }));
            }
        foreach (var (key, place) in add)
        {
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<IndexedPlace>(1);
            if (!list.Any(e => e.RegionId == place.RegionId)) list.Add(place);
        }
    }

    /// <summary>
    /// Дубль: рядом (до 5 км) есть пункт того же региона, чьё название почти совпадает с русским написанием
    /// («Дякове» у «Дьяково»). Иначе запись — самостоятельный пункт («Красний Лиман» среди соседей с другими названиями).
    /// </summary>
    private static bool IsRussianDuplicate(List<(string Key, IndexedPlace Place)> near, string key, IndexedPlace x)
    {
        foreach (var (k, e) in near)
            if (e.RegionId == x.RegionId && Math.Abs(k.Length - key.Length) <= 2 && k[0] == key[0] &&
                Geo.DistanceKm(e.Pos, x.Pos) < 5 && Levenshtein(k, key) <= 2)
                return true;
        return false;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    /// <summary>Украинское написание названия → русское («Красний Лиман» → «Красный Лиман», «Чорнухине» → «Чернухино»).</summary>
    internal static string UkToRu(string name)
    {
        if (!UkMarker().IsMatch(name)) return name;
        var w = UkAdjective().Replace(name, m =>
        {
            var ru = UkAdjectives[m.Value.ToLowerInvariant()];
            return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(ru[0]) + ru[1..] : ru;
        });
        w = UkAdjEnding().Replace(w, m => m.Groups[1].Value + "к" + (m.Groups[2].Value == "е" ? "ое" : "ая"));
        w = UkPluralEnding().Replace(w, "ы");
        w = UkNeuterEnding().Replace(w, "о");
        return w.Replace("ськ", "ск").Replace("цьк", "цк").Replace("ї", "йи").Replace("Ї", "Йи").Replace("є", "е").Replace("Є", "Е")
                .Replace("і", "и").Replace("І", "И").Replace("ґ", "г").Replace("Ґ", "Г").Replace("Чорн", "Черн").Replace("чорн", "черн");
    }

    private static readonly Dictionary<string, string> UkAdjectives = new(StringComparer.Ordinal)
    {
        ["красний"] = "красный", ["новий"] = "новый", ["старий"] = "старый", ["малий"] = "малый", ["правий"] = "правый",
        ["лівий"] = "левый", ["зелений"] = "зеленый", ["чорний"] = "черный", ["білий"] = "белый", ["нижній"] = "нижний",
        ["верхній"] = "верхний", ["станиця"] = "станица",
    };

    [GeneratedRegex(@"[іїєґІЇЄҐ]|ськ|цьк|(?:^|[ -])(?:[Кк]расний|[Нн]овий|[Сс]тарий|[Мм]алий|[Пп]равий|[Лл]івий|[Зз]елений|[Чч]орний|[Бб]ілий|[Нн]ижній|[Вв]ерхній|[Сс]таниця)(?:$|[ -])|[бвгджзклмнпрстфхцчшщ][аи]ни$|(?:ин|ов|ев)е$")]
    private static partial Regex UkMarker();
    [GeneratedRegex(@"(?<=^|[ -])(?:[Кк]расний|[Нн]овий|[Сс]тарий|[Мм]алий|[Пп]равий|[Лл]івий|[Зз]елений|[Чч]орний|[Бб]ілий|[Нн]ижній|[Вв]ерхній|[Сс]таниця)(?=$|[ -])")]
    private static partial Regex UkAdjective();
    [GeneratedRegex(@"(с|ц)ьк([еа])(?=$|[ -])")]
    private static partial Regex UkAdjEnding();
    [GeneratedRegex(@"(?<=[бвгджзклмнпрстфхцчшщ][аи]н)и$")]
    private static partial Regex UkPluralEnding();
    [GeneratedRegex(@"(?<=ин|ов|ев)е$")]
    private static partial Regex UkNeuterEnding();
    [GeneratedRegex(@"(?:ск|цк)ий$")]
    private static partial Regex CityDistrictAdj();

    /// <summary>
    /// Районы, которых нет в GeoNames или которые там под другим именем: прежнее деление ЛНР/ДНР и Херсонской области
    /// (каналы и РСЧС называют муниципальные округа по старым районам) и русские написания украинских названий.
    /// Центры — по OpenStreetMap.
    /// </summary>
    private static void AddExtraDistricts(Dictionary<string, List<IndexedPlace>> dict)
    {
        (string Name, string Region, double Lat, double Lon)[] extra =
        [
            ("Краснодонский район", "lnr", 48.372, 39.652), ("Лутугинский район", "lnr", 48.380, 39.172),
            ("Славяносербский район", "lnr", 48.642, 39.015), ("Антрацитовский район", "lnr", 48.119, 39.276),
            ("Перевальский район", "lnr", 48.396, 38.731), ("Свердловский район", "lnr", 48.090, 39.660),
            ("Станично-Луганский район", "lnr", 48.700, 39.560), ("Новоайдарский район", "lnr", 48.899, 38.939),
            ("Беловодский район", "lnr", 49.181, 39.654), ("Белокуракинский район", "lnr", 49.639, 38.678),
            ("Марковский район", "lnr", 49.546, 39.606), ("Меловской район", "lnr", 49.400, 40.150),
            ("Новопсковский район", "lnr", 49.614, 39.180), ("Сватовский район", "lnr", 49.608, 38.424),
            ("Старобельский район", "lnr", 49.433, 39.463), ("Троицкий район", "lnr", 49.823, 38.316),
            ("Кременский район", "lnr", 49.125, 38.386), ("Попаснянский район", "lnr", 48.640, 38.380),
            ("Тельмановский район", "dnr", 47.439, 38.133), ("Шахтерский район", "dnr", 47.969, 38.544),
            ("Амвросиевский район", "dnr", 47.791, 38.468), ("Волновахский район", "dnr", 47.716, 37.158),
            ("Голопристанский район", "kherson", 46.328, 32.117), ("Новотроицкий район", "kherson", 46.398, 34.327),
            ("Ивановский район", "kherson", 46.675, 34.455), ("Нижнесерогозский район", "kherson", 46.893, 34.468),
            ("Горностаевский район", "kherson", 46.912, 33.904), ("Верхнерогачикский район", "kherson", 47.288, 34.169),
            ("Алешкинский район", "kherson", 46.620, 32.720),
        ];
        foreach (var (name, region, lat, lon) in extra)
        {
            var key = Gazetteer.Normalize(name);
            if (dict.TryGetValue(key, out var ex) && ex.Any(x => x.RegionId == region && x.Kind == 'A')) continue;
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<IndexedPlace>(1);
            list.Add(new IndexedPlace(name, region, new GeoPoint(lat, lon), 'A', 0));
        }
        // районы областных центров («Советский и далее на Сельцо» — Советский район Брянска): как пункт внутри города
        // (у каждого района одна запись — уточнённая по OSM, где она есть). Тёзки районов области различаются регионом
        // и проверкой ниже: район города не добавляется, если в области есть одноимённый административный район
        (string Name, string Region, double Lat, double Lon)[] cityDistricts =
        [
            ("Центральный район", "kursk", 51.767, 36.146), ("Сеймский район", "kursk", 51.700, 36.205), ("Железнодорожный район", "kursk", 51.730, 36.224),
            ("Центральный район", "voronezh", 51.733, 39.213), ("Ленинский район", "voronezh", 51.641, 39.184), ("Коминтерновский район", "voronezh", 51.720, 39.146),
            ("Советский район", "voronezh", 51.628, 39.114), ("Левобережный район", "voronezh", 51.580, 39.265), ("Железнодорожный район", "voronezh", 51.766, 39.349),
            ("Железнодорожный район", "rostov", 47.200, 39.672), ("Кировский район", "rostov", 47.204, 39.752), ("Ленинский район", "rostov", 47.210, 39.713),
            ("Первомайский район", "rostov", 47.290, 39.771), ("Западный район", "krasnodar", 45.033, 38.916), ("Карасунский район", "krasnodar", 45.030, 39.050),
            ("Прикубанский район", "krasnodar", 45.109, 38.956), ("Железнодорожный район", "orel", 52.968, 36.118), ("Заводской район", "orel", 52.933, 36.046),
            ("Северный район", "orel", 53.003, 36.114), ("Советский район", "orel", 52.982, 36.059),
            ("Ворошиловский район", "dnr", 48.000, 37.800), ("Калининский район", "dnr", 48.020, 37.840),
            ("Киевский район", "dnr", 48.030, 37.790), ("Кировский район", "dnr", 47.980, 37.700),
            ("Куйбышевский район", "dnr", 48.010, 37.740), ("Ленинский район", "dnr", 47.970, 37.780),
            ("Петровский район", "dnr", 47.930, 37.630), ("Пролетарский район", "dnr", 47.960, 37.940),
            ("Будённовский район", "dnr", 47.970, 37.830),
            ("Артёмовский район", "lnr", 48.580, 39.300), ("Каменнобродский район", "lnr", 48.570, 39.270),
            ("Ленинский район", "lnr", 48.565, 39.330), ("Октябрьский район", "lnr", 48.590, 39.370),
            ("Железнодорожный район", "crimea", 44.960, 34.080), ("Киевский район", "crimea", 44.950, 34.130),
            ("Центральный район", "crimea", 44.950, 34.100),
            // округа Тулы, Курска, Белгорода (OSM, центроиды границ)
            ("Зареченский район", "tula", 54.291, 37.548), ("Пролетарский район", "tula", 54.235, 37.755),
            ("Советский район", "tula", 54.186, 37.590), ("Центральный район", "tula", 54.127, 37.663),
            ("Привокзальный район", "tula", 54.170, 37.580),
                                    ("Западный округ", "belgorod", 50.596, 36.543), ("Восточный округ", "belgorod", 50.580, 36.620),
            // районы крупных тыловых городов (тыловые каналы: «Кировский район, Волгоград»); центры — Nominatim/OSM
            ("Кировский район", "volgograd", 48.607, 44.482), ("Дзержинский район", "volgograd", 48.756, 44.472), ("Тракторозаводский район", "volgograd", 48.837, 44.601),
            ("Ворошиловский район", "volgograd", 48.698, 44.447), ("Красноармейский район", "volgograd", 48.482, 44.631), ("Центральный район", "volgograd", 48.722, 44.532),
            ("Краснооктябрьский район", "volgograd", 48.775, 44.553),
            ("Заводской район", "saratov", 51.469, 45.946), ("Кировский район", "saratov", 51.586, 46.040), ("Ленинский район", "saratov", 51.613, 45.927),
            ("Октябрьский район", "saratov", 51.526, 45.977), ("Фрунзенский район", "saratov", 51.540, 45.978), ("Волжский район", "saratov", 51.556, 46.123),
            ("Заволжский район", "yaroslavl", 57.663, 39.914), ("Фрунзенский район", "yaroslavl", 57.573, 39.896), ("Дзержинский район", "yaroslavl", 57.674, 39.812),
            ("Кировский район", "yaroslavl", 57.632, 39.811), ("Ленинский район", "yaroslavl", 57.639, 39.854), ("Красноперекопский район", "yaroslavl", 57.579, 39.818),
                                    ("Октябрьский район", "lipetsk", 53.161, 39.442),
            ("Пролетарский район", "tver", 56.837, 35.814), ("Московский район", "tver", 56.822, 35.940), ("Заволжский район", "tver", 56.872, 35.900),
            ("Центральный район", "tver", 56.850, 35.904),
            ("Индустриальный район", "perm", 57.931, 56.150), ("Кировский район", "perm", 58.022, 55.953), ("Свердловский район", "perm", 57.969, 56.279),
            ("Мотовилихинский район", "perm", 58.031, 56.405), ("Дзержинский район", "perm", 58.047, 56.123), ("Ленинский район", "perm", 58.047, 56.225),
            ("Орджоникидзевский район", "perm", 58.118, 56.390),
            ("Железнодорожный район", "penza", 53.202, 45.061), ("Ленинский район", "penza", 53.194, 44.979), ("Октябрьский район", "penza", 53.250, 44.934),
            ("Первомайский район", "penza", 53.149, 44.989),
            ("Железнодорожный район", "ryazan", 54.585, 39.690), ("Московский район", "ryazan", 54.658, 39.621), ("Октябрьский район", "ryazan", 54.576, 39.798),
            ("Советский район", "ryazan", 54.714, 39.848),
            ("Заднепровский район", "smolensk", 54.807, 31.994), ("Ленинский район", "smolensk", 54.759, 32.027), ("Промышленный район", "smolensk", 54.757, 32.081),
            ("Бежицкий район", "bryansk", 53.324, 34.281), ("Володарский район", "bryansk", 53.270, 34.414), ("Советский район", "bryansk", 53.250, 34.360),
            ("Фокинский район", "bryansk", 53.213, 34.415),
                                    ("Ворошиловский район", "rostov", 47.306, 39.700),             ("Октябрьский район", "rostov", 47.291, 39.666),             ("Пролетарский район", "rostov", 47.212, 39.780), ("Советский район", "rostov", 47.214, 39.608),
            ("Центральный район", "krasnodar", 45.018, 39.001),
            ("Кировский район", "samara", 53.235, 50.269), ("Красноглинский район", "samara", 53.333, 50.215), ("Куйбышевский район", "samara", 53.117, 50.076),
            ("Промышленный район", "samara", 53.229, 50.201), ("Самарский район", "samara", 53.184, 50.090),
            ("Автозаводский район", "nizhny", 56.234, 43.828), ("Канавинский район", "nizhny", 56.316, 43.937), ("Ленинский район", "nizhny", 56.279, 43.929),
            ("Московский район", "nizhny", 56.331, 43.858), ("Советский район", "nizhny", 56.283, 44.017), ("Сормовский район", "nizhny", 56.372, 43.806),
            ("Авиастроительный район", "tatarstan", 55.879, 49.103), ("Вахитовский район", "tatarstan", 55.778, 49.100), ("Кировский район", "tatarstan", 55.823, 48.947),
            ("Московский район", "tatarstan", 55.863, 48.977), ("Ново-Савиновский район", "tatarstan", 55.828, 49.134), ("Приволжский район", "tatarstan", 55.690, 49.081),
            ("Советский район", "tatarstan", 55.823, 49.264),
        ];
        foreach (var (name, region, lat, lon) in cityDistricts)
        {
            var key = Gazetteer.Normalize(name);
            if (dict.TryGetValue(key, out var ex) && ex.Any(x => x.RegionId == region)) continue;
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<IndexedPlace>(1);
            list.Add(new IndexedPlace(name, region, new GeoPoint(lat, lon), 'P', 0));
        }
        // написания: «Чаплынский» (укр. Чаплинка), «Выгонический», «Бердянкий» (опечатка канала)
        (string Alias, string Of)[] spellings =
        [
            ("чаплынский район", "чаплинский район"), ("выгонический район", "выгоничский район"),
            ("бердянкий район", "бердянский район"), ("шахтёрский район", "шахтерский район"),
            ("тракторный район", "тракторозаводский район"), ("красноармеский район", "красноармейский район"),
            // частые написания каналов (валидация архива)
            ("измайловский район", "измалковский район"), ("матвеево-кургански район", "матвеево-курганский район"),
            ("приморско-ахатрский район", "приморско-ахтарский район"), ("чернышевский район", "чернышковский район"),
            ("щербинский район", "щербиновский район"), ("бабынский район", "бабынинский район"), ("плюссовский район", "плюсский район"),
        ];
        foreach (var (alias, of) in spellings)
        {
            if (!dict.TryGetValue(of, out var src)) continue;
            // «Чернышевский район» есть и в Забайкалье — добавляем к нему волгоградский Чернышковский, регион решает контекст
            dict[alias] = dict.TryGetValue(alias, out var own) ? [.. own, .. src.Where(x => !own.Any(o => o.RegionId == x.RegionId))] : src;
        }
    }

    /// <summary>
    /// В GeoNames есть не все районы (в Пензенской — 14 из 27), а контуры районов OSM лежат в подложке:
    /// недостающие районы добавляются в справочник по центру контура.
    /// </summary>
    private static readonly HashSet<string> FrontlineRegions = new(StringComparer.Ordinal)
    {
        "belgorod", "kursk", "bryansk", "voronezh", "rostov", "krasnodar", "crimea", "sevastopol",
        "kherson", "zaporozhye", "lnr", "dnr", "kharkov",
    };

    private static void AddBasemapDistricts(Dictionary<string, List<IndexedPlace>> dict)
    {
        // Формат RBM2 (см. Services/BasemapFormat.cs) читается здесь без зависимости от UI: kind, lod, id, name, label, rank, контуры.
        using var res = typeof(PlaceIndex).Assembly.GetManifestResourceStream("Radar.basemap.bin.gz");
        if (res == null) return;
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var d = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (d.Length < 4 || System.Text.Encoding.ASCII.GetString(d[..4]) != BasemapFormat.Magic) return;
        const byte DistrictKind = 8;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pos = 4;
        while (pos < d.Length)
        {
            byte kind = d[pos++];
            if (kind == 255) break;
            pos++; // lod
            var id = Str(d, ref pos);
            Str(d, ref pos); // name
            float lat = BitConverter.ToSingle(d.Slice(pos, 4)), lon = BitConverter.ToSingle(d.Slice(pos + 4, 4));
            pos += 12; // label + rank
            uint parts = BitConverter.ToUInt32(d.Slice(pos, 4));
            pos += 4;
            for (int p = 0; p < parts; p++)
            {
                int n = (int)BitConverter.ToUInt32(d.Slice(pos, 4));
                pos += 4;
                BasemapFormat.SkipPart(d, ref pos, n);
            }
            if (kind != DistrictKind || !seen.Add(id)) continue;

            int bar = id.IndexOf('|');
            if (bar <= 0) continue;
            var region = id[..bar];
            // прифронтовые районы полностью есть в GeoNames (в т. ч. под украинскими регионами) — дубли сделали бы их неоднозначными
            if (FrontlineRegions.Contains(region)) continue;
            var stem = id[(bar + 1)..];
            if (stem.Length < 4 || !(stem.EndsWith("ский") || stem.EndsWith("цкий") || stem.EndsWith("ской") || stem.EndsWith("ный"))) continue;
            bool known = false;
            foreach (var suf in new[] { " район", " городской округ", " муниципальный округ" })
                if (dict.TryGetValue(stem + suf, out var ex) && ex.Any(x => x.RegionId == region && x.Kind == 'A')) known = true;
            if (known) continue;
            var key = stem + " район";
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<IndexedPlace>(1);
            var name = char.ToUpperInvariant(stem[0]) + stem[1..] + " район";
            list.Add(new IndexedPlace(name, region, new GeoPoint(lat, lon), 'A', 0));
        }

        static string Str(Span<byte> d, ref int pos)
        {
            int len = BitConverter.ToUInt16(d.Slice(pos, 2));
            var s = System.Text.Encoding.UTF8.GetString(d.Slice(pos + 2, len));
            pos += 2 + len;
            return s;
        }
    }

    /// <summary>
    /// Ищет топоним. <paramref name="regions"/> — допустимые регионы в порядке приоритета;
    /// если пусто — только однозначные совпадения.
    /// </summary>
    public static IndexedPlace? Find(string name, IReadOnlyList<string> regions)
    {
        IndexedPlace? hamlet = null;
        int hamletRegion = 0;
        foreach (var key in Variants(name))
        {
            if (!_index.Value.TryGetValue(key, out var list)) continue;
            if (hamlet != null)
            {
                // крупный пункт по начальной форме слова в том же или более приоритетном регионе
                var (alt, _) = BestIn(list, regions, hamletRegion + 1);
                if (alt is { Kind: 'P', Population: >= 1000 }) return alt;
                continue;
            }
            var (hit, ri) = BestIn(list, regions, regions.Count);
            if (hit == null)
            {
                if (regions.Count == 0 && list.Count == 1) return list[0];
                continue;
            }
            // хутор без жителей по точной форме слова («до Каменки» → хутор «Каменки» вместо Каменки, 9 тыс.):
            // сначала проверяем начальные формы
            if (hit.Kind == 'P' && hit.Population < SmallHamlet) { (hamlet, hamletRegion) = (hit, ri); continue; }
            return hit;
        }
        return hamlet;
    }

    private const int SmallHamlet = 100;

    /// <summary>
    /// Лучшее совпадение среди первых <paramref name="upTo"/> регионов контекста: сначала населённые пункты во всех этих
    /// регионах, потом прочие объекты (станции, урочища, реки) — «Успенская» [rostov, krasnodar] — станица в Краснодарском
    /// крае, а не ростовская станция. Из одноимённых пунктов региона берётся самый крупный.
    /// </summary>
    private static (IndexedPlace? Hit, int Region) BestIn(List<IndexedPlace> list, IReadOnlyList<string> regions, int upTo)
    {
        for (int pass = 0; pass < 2; pass++)
            for (int ri = 0; ri < upTo && ri < regions.Count; ri++)
            {
                IndexedPlace? hit = null;
                foreach (var x in list)
                    if (x.RegionId == regions[ri] && (x.Kind == 'P') == (pass == 0) && (hit == null || x.Population > hit.Population)) hit = x;
                if (hit != null) return (hit, ri);
            }
        return (null, -1);
    }

    /// <summary>Регионы, в которых встречается название (для «голосования» по контексту сообщения).</summary>
    public static HashSet<string> RegionsOf(string name, IReadOnlyCollection<string> pool)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Variants(name))
            if (_index.Value.TryGetValue(key, out var list))
                foreach (var x in list)
                    if (pool.Contains(x.RegionId)) set.Add(x.RegionId);
        if (set.Count == 0)
        {
            // усечённое написание: только для длинных названий, иначе слишком много ложных совпадений
            var n = Gazetteer.Normalize(name);
            if (n.Length >= 8 && !n.Contains(' '))
                foreach (var x in ByPrefix(n, pool))
                    if (x.Kind == 'P') set.Add(x.RegionId);
        }
        return set;
    }

    /// <summary>Поиск по префиксу в заданном регионе (усечённые написания).</summary>
    public static IndexedPlace? FindPrefix(string name, string region)
    {
        var n = Gazetteer.Normalize(name);
        if (n.Length < 8 || n.Contains(' ')) return null;
        var hits = ByPrefix(n, [region]).Where(x => x.Kind == 'P').ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// Опечатка в названии («Краснадар», «Анастасиевкая», «Карачаев», «Каригнская»): одна замена, вставка, пропуск
    /// или перестановка соседних букв — в регионах контекста. Только для слов от 7 букв и только при единственном кандидате
    /// (и не короче 7 букв сам кандидат): лучше оставить пункт неразобранным, чем привязать к чужому.
    /// </summary>
    public static IndexedPlace? FindTypo(string name, IReadOnlyCollection<string> regions)
    {
        var n = Gazetteer.Normalize(name);
        // составное название («Серебрянные пруды») — опечатка только в одном слове, число слов то же
        if (n.Length < 7 || n.Count(c => c == ' ') > 2 || regions.Count == 0) return null;
        int spaces = n.Count(c => c == ' ');
        IndexedPlace? found = null;
        // первые две буквы опечатка почти не затрагивает — кандидаты по префиксу из 2 букв
        var keys = _sortedKeys.Value;
        for (int ki = Lower(n[..2]); ki < keys.Length; ki++)
        {
            var key = keys[ki];
            if (!key.StartsWith(n[..2], StringComparison.Ordinal)) break;
            if (key.Length < 7 || Math.Abs(key.Length - n.Length) > 1 || key == n || key.Count(c => c == ' ') != spaces || !WithinOne(n, key)) continue;
            foreach (var p in _index.Value[key])
            {
                if (!regions.Contains(p.RegionId) || p.Kind != 'P') continue;
                // опечатка правдоподобна у заметного пункта; хутор в 30 жителей — скорее другое слово («Войково» → «Волково»)
                if (p.Population < 1000 && n.Length < 9) continue;
                if (found != null && found.Name != p.Name) return null;
                if (found == null || p.Population > found.Population) found = p;
            }
        }
        return found;
    }

    /// <summary>
    /// Искажённое название рядом с остальными пунктами сообщения («Дятьково, Антрацит, Петровское» — это Дьяково у Антрацита):
    /// до двух правок, только пункты не дальше <paramref name="radiusKm"/> от <paramref name="near"/> и единственный кандидат.
    /// Близость к другим пунктам сообщения — главное подтверждение, без неё две правки угадывать нельзя.
    /// </summary>
    public static IndexedPlace? FindNearTypo(string name, IReadOnlyCollection<string> regions, IReadOnlyList<GeoPoint> near, double radiusKm = 40)
    {
        var n = Gazetteer.Normalize(name);
        if (n.Length < 6 || n.Contains(' ') || near.Count == 0) return null;
        IndexedPlace? found = null;
        int bestDist = int.MaxValue;
        var keys = _sortedKeys.Value;
        for (int ki = Lower(n[..1]); ki < keys.Length && keys[ki][0] == n[0]; ki++)
        {
            var key = keys[ki];
            if (key.Length < 5 || Math.Abs(key.Length - n.Length) > 2 || key == n) continue;
            // длинное название: до 3 правок («дятьково» → «дьяково»: пропуск + перестановка через букву), но только вплотную к соседям
            int dist = Distance(n, key, 3);
            if (dist > 3 || dist == 3 && n.Length < 7) continue;
            // две и более правок — только с тем же окончанием (форма названия: «-ово», «-овка»), иначе это другое слово
            // («Бановка» ≠ «Башоск», «Шевченко» ≠ «Шевченково»); прилагательные («Ленинский») — районы, не угадываем
            if (dist >= 2 && (n[^3..] != key[^3..] || System.Text.RegularExpressions.Regex.IsMatch(n, @"(?:ский|цкий|ская|ское|ный|ная|ное)$"))) continue;
            double r = dist == 3 ? Math.Min(radiusKm, 25) : dist == 2 ? Math.Min(radiusKm, 30) : radiusKm;
            foreach (var p in _index.Value[key])
            {
                if (!regions.Contains(p.RegionId) || p.Kind != 'P' || near.Min(q => Geo.DistanceKm(q, p.Pos)) > r) continue;
                // тот же пункт под русским и украинским названием («Дьяково» / «Дякове») — один кандидат
                if (found != null && Geo.DistanceKm(found.Pos, p.Pos) > 2) return null;
                if (found == null || dist < bestDist || p.Population > found.Population) { found = p; bestDist = Math.Min(bestDist, dist); }
            }
        }
        return found;
    }

    /// <summary>Расстояние Дамерау — Левенштейна (с отсечкой: больше <paramref name="max"/> — возвращает max + 1).</summary>
    private static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        // три строки матрицы на стеке вместо массива на каждый ключ: функция вызывается для тысяч ключей подряд
        int w = b.Length + 1;
        Span<int> buf = w * 3 <= 768 ? stackalloc int[w * 3] : new int[w * 3];
        Span<int> pp = buf[..w], prev = buf.Slice(w, w), cur = buf.Slice(2 * w, w);
        for (int j = 0; j < w; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int c = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + c);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, pp[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > max) return max + 1;
            var t = pp; pp = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Район с опечаткой в прилагательном («Шебекенский», «Борисовкий», «Рякитянском»): одна правка, только районы/округа
    /// регионов контекста, единственный кандидат.
    /// </summary>
    public static IndexedPlace? FindDistrictTypo(string adjective, IReadOnlyCollection<string> regions)
    {
        var a = Gazetteer.Normalize(adjective);
        foreach (var (end, repl) in new[] { ("ского", "ский"), ("скому", "ский"), ("ском", "ский"), ("ским", "ский"), ("цкого", "цкий"), ("цком", "цкий") })
            if (a.EndsWith(end, StringComparison.Ordinal)) { a = a[..^end.Length] + repl; break; }
        if (a.Length < 7 || regions.Count == 0) return null;
        IndexedPlace? found = null;
        foreach (var (adj, key) in _districtKeys.Value)
        {
            if (adj[0] != a[0]) continue;
            if (adj == a || Math.Abs(adj.Length - a.Length) > 1 || !WithinOne(a, adj)) continue;
            foreach (var p in _index.Value[key])
            {
                if (p.Kind != 'A' || !regions.Contains(p.RegionId)) continue;
                if (found != null && Geo.DistanceKm(found.Pos, p.Pos) > 5) return null;
                found ??= p;
            }
        }
        return found;
    }

    private static int Lower(string prefix)
    {
        int i = Array.BinarySearch(_sortedKeys.Value, prefix, StringComparer.Ordinal);
        return i < 0 ? ~i : i;
    }

    /// <summary>Расстояние Дамерау — Левенштейна не больше 1.</summary>
    private static bool WithinOne(string a, string b)
    {
        if (a.Length == b.Length)
        {
            int i = 0;
            while (i < a.Length && a[i] == b[i]) i++;
            if (i == a.Length) return true;
            if (a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1))) return true; // замена
            return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2)); // перестановка
        }
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        int k = 0;
        while (k < s.Length && s[k] == l[k]) k++;
        return s.AsSpan(k).SequenceEqual(l.AsSpan(k + 1)); // вставка/пропуск
    }

    /// <summary>Поиск без контекста региона: принимается, только если во всех <paramref name="pool"/> ровно один кандидат.</summary>
    public static IndexedPlace? FindUnique(string name, IReadOnlyCollection<string> pool)
    {
        foreach (var key in Variants(name))
        {
            if (!_index.Value.TryGetValue(key, out var list)) continue;
            var hits = list.Where(x => pool.Contains(x.RegionId)).ToList();
            // одноимённая гора или станция не делает пункт неоднозначным («Кременная» в ЛНР и гора в Крыму)
            if (hits.Count > 1 && hits.Any(x => x.Kind is 'P' or 'A')) hits = [.. hits.Where(x => x.Kind is 'P' or 'A')];
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) return null; // неоднозначно — лучше не угадывать
        }
        return null;
    }

    /// <summary>
    /// Город без контекста региона (тыловые каналы: «Зеленоград», «Кубинка», «Шуя»): единственный крупный пункт с таким
    /// названием по всей РФ — от <paramref name="minPop"/> жителей и в 10 раз крупнее любого одноимённого.
    /// </summary>
    public static IndexedPlace? FindDominant(string name, IReadOnlyCollection<string> pool, int minPop = 15000)
    {
        // прилагательные («Красногвардейский», «Калининский», «Белая») — чаще часть названия района/реки, чем город
        if (System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(name), @"(?:ий|ый|ой|ая|яя|ие|ые)$")) return null;
        foreach (var key in Variants(name))
        {
            if (!_index.Value.TryGetValue(key, out var list)) continue;
            var hits = list.Where(x => x.Kind == 'P' && pool.Contains(x.RegionId)).OrderByDescending(x => x.Population).ToList();
            if (hits.Count == 0) continue;
            if (hits[0].Population < minPop) return null;
            var rival = hits.Skip(1).FirstOrDefault(x => Geo.DistanceKm(x.Pos, hits[0].Pos) > 3);
            return rival == null || hits[0].Population >= 10 * Math.Max(1, rival.Population) ? hits[0] : null;
        }
        return null;
    }

    /// <summary>
    /// «Саратовский район», «Луховицкий район», «Унеченский район» — округ вокруг города, которого нет среди районов справочника:
    /// крупный пункт региона, чьё название начинается с основы прилагательного (единственный).
    /// </summary>
    public static IndexedPlace? FindDistrictByCity(string phrase, IReadOnlyCollection<string> regions)
    {
        var n = Gazetteer.Normalize(phrase).Trim();
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^([а-я-]+?)(?:ск|цк)(?:ий|ого|ому|ом|ой|им)\s+(?:район\w*|округ\w*|мо|го|муниципальн\w* округ\w*|городск\w* округ\w*)$");
        if (!m.Success) return null;
        var stem = m.Groups[1].Value;
        // основа прилагательного → основа города: «унечен» (Унеча), «выксун» (Выкса), «калуж» (Калуга), «туль» (Тула), «лухови» (Луховицы)
        var stems = new HashSet<string>(StringComparer.Ordinal) { stem, stem.TrimEnd('ь') };
        if (stem.EndsWith("ен", StringComparison.Ordinal)) stems.Add(stem[..^2]);
        if (stem.EndsWith("ун", StringComparison.Ordinal)) stems.Add(stem[..^2]);
        if (stem.EndsWith("ж", StringComparison.Ordinal)) stems.Add(stem[..^1] + "г");
        stems.RemoveWhere(s => s.Length < 3);
        if (stems.Count == 0) return null;
        var head = stem[..3];
        IndexedPlace? found = null;
        var keys = _sortedKeys.Value;
        for (int i = Lower(head); i < keys.Length && keys[i].StartsWith(head, StringComparison.Ordinal); i++)
        {
            var k = keys[i];
            if (k.Contains(' ')) continue;
            var b = k.TrimEnd('а', 'ы', 'и', 'я', 'о', 'е');
            if (!stems.Any(s => b == s || b.StartsWith(s, StringComparison.Ordinal) && b.Length - s.Length <= 1)) continue;
            foreach (var x in _index.Value[k])
            {
                if (x.Kind != 'P' || x.Population < 5000 || !regions.Contains(x.RegionId)) continue;
                // «Луховицы» / «Луховици» — варианты написания одного пункта
                if (found != null && Geo.DistanceKm(found.Pos, x.Pos) > 3) return null;
                if (found == null || x.Name.Length < found.Name.Length) found = x;
            }
        }
        return found;
    }

    /// <summary>Разговорные и сокращённые названия → официальные.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["вешки"] = "вешенская", ["вешек"] = "вешенская", ["вешках"] = "вешенская",
        ["артемовск"] = "бахмут", ["красноармейск"] = "покровск", ["дзержинск"] = "торецк", ["кадиевка"] = "стаханов",
        ["жп"] = "железный порт", ["аэропорт"] = "аэропорт",
        ["соц город"] = "соцгородок", ["соцгород"] = "соцгородок",
    };

    /// <summary>Нормальная форма и вероятные начальные формы для падежей («Гремячки» → «гремячка»).</summary>
    private static readonly string[] AdminSuffixes = [" районе", " района", " районы", " район", " р-не", " р-на", " р-н", " мо", " го",
        " муниципальном округе", " муниципального округа", " муниципальный округ",
        " городском округе", " городского округа", " городской округ", " округе", " округа", " округ"];

    public static IEnumerable<string> Variants(string name)
    {
        var n = Gazetteer.Normalize(name).Replace('’', ' ').Replace("'", "").Trim();
        n = string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (n.Length < 3) yield break;
        yield return n;
        if (Aliases.TryGetValue(n, out var alias)) yield return alias;

        // районы и округа: «Белгородский район», «Губкинский ГО», «Ивнянский МО»
        foreach (var suf in AdminSuffixes)
            if (n.EndsWith(suf, StringComparison.Ordinal))
            {
                var stem = n[..^suf.Length].Trim();
                foreach (var adj in AdminAdjLemmas(stem))
                {
                    yield return adj + " район";
                    yield return adj + " городской округ";
                    yield return adj + " муниципальный округ";
                }
                yield break;
            }

        // составные через дефис: «Ростова-на-Дону», «Славянске-на-Кубани» — склоняется только первая часть;
        // «Каменку-Днепровскую», «Дмитриева-Льговского» — обе части (существительное и прилагательное)
        if (!n.Contains(' ') && n.Split('-') is { Length: >= 2 } hp)
        {
            if (hp.Length >= 3 && hp[1] is "на" or "над" or "под" or "де")
            {
                foreach (var f in Lemmas(hp[0])) yield return f + "-" + string.Join('-', hp[1..]);
                if (hp[0].EndsWith('е') || hp[0].EndsWith('у') || hp[0].EndsWith('а')) yield return hp[0][..^1] + "-" + string.Join('-', hp[1..]);
            }
            else if (hp.Length == 2)
                foreach (var f in Lemmas(hp[0]).Prepend(hp[0]).Append(hp[0].EndsWith('а') ? hp[0][..^1] : hp[0]))
                    foreach (var a in AdjLemmas(hp[1]).Append(hp[1]))
                        yield return f + "-" + a;
        }

        var words = n.Split(' ');
        var last = words[^1];
        var prefix = words.Length > 1 ? string.Join(' ', words[..^1]) + " " : "";
        foreach (var f in Lemmas(last)) yield return prefix + f;
        if (words.Length > 1)
        {
            // «Красной Яруги» → «Красная Яруга»: склоняем оба слова
            var first = words[0];
            // короткие слова («Яра», «Лога») правила окончаний не берут (слишком коротко) — отбрасываем окончание падежа
            var lasts = Lemmas(last).Prepend(last);
            if (last.Length <= 4 && last[^1] is 'а' or 'у' or 'е') lasts = lasts.Append(last[..^1]);
            // родительный множественного с нулевым окончанием: «Нижних Мельниц», «Больших Солдат», «Верхних Лубянок»
            if (first.EndsWith("их", StringComparison.Ordinal) || first.EndsWith("ых", StringComparison.Ordinal))
            {
                lasts = lasts.Append(last + "ы").Append(last + "и");
                if (last.Length > 4 && (last.EndsWith("ок", StringComparison.Ordinal) || last.EndsWith("ек", StringComparison.Ordinal)))
                    lasts = lasts.Append(last[..^2] + "ки");
            }
            foreach (var a in AdjLemmas(first))
                foreach (var l in lasts)
                    yield return string.Join(' ', words.Skip(1).SkipLast(1).Prepend(a).Append(l));
            // «Станицы Луганской» → «Станица Луганская»: существительное, затем прилагательное
            if (words.Length == 2)
                foreach (var f in Lemmas(first))
                    foreach (var a in AdjLemmas(last))
                        yield return f + " " + a;
        }
    }

    private static readonly (string, string[])[] LemmaRules =
    [
        ("кой", ["ка"]), ("гой", ["га"]), ("ой", ["ая", "ое", "ой", "а"]), ("ей", ["я", "ь"]), ("ого", ["ое", "ий", "ой", "ый"]), ("ому", ["ое", "ий", "ый"]), ("ом", ["", "о", "ое"]),
        ("ки", ["ка"]), ("ке", ["ка"]), ("ку", ["ка"]), ("ги", ["га"]), ("ге", ["га"]), ("хи", ["ха"]),
        ("ы", ["а", ""]), ("и", ["а", "я", "ь", "и"]), ("е", ["", "а", "о", "е", "я"]), ("у", ["", "а"]), ("а", ["", "о", "ы"]),
        ("ую", ["ая"]), ("юю", ["яя"]), ("ю", ["я", "ь"]), ("я", ["ь", "й", "е"]), ("ей", ["ь", "и"]), ("ов", ["ы", "о"]), ("ах", ["ы", "и", "а"]), ("ям", ["и"]),
        ("ска", ["ск"]), ("ске", ["ск"]), ("ого", ["ое"]), ("ино", ["ино"]), ("ово", ["ово"]),
        // родительный множественного: «Комарич» → «Комаричи», «Днепрян» → «Днепряны»
        ("ич", ["ичи"]), ("ян", ["яны"]),
        // творительный после шипящих и «ц»: «Мельницей» → «Мельница»
        ("цей", ["ца"]), ("шей", ["ша"]), ("жей", ["жа"]), ("чей", ["ча"]), ("щей", ["ща"]),
    ];

    private static IEnumerable<string> Lemmas(string w)
    {
        foreach (var (end, repl) in LemmaRules)
            if (w.EndsWith(end, StringComparison.Ordinal) && w.Length > end.Length + 2)
                foreach (var r in repl)
                    yield return w[..^end.Length] + r;
    }

    /// <summary>«шебекинском»/«шебекинского»/«шебекинский» → «шебекинский» (и «-ой»/«-ый» варианты).</summary>
    private static IEnumerable<string> AdminAdjLemmas(string w)
    {
        yield return w;
        string[] ends = ["ского", "скому", "ском", "ским", "ский", "ской", "цкого", "цком", "цкий", "ного", "ном", "ный", "ого", "ом", "ий", "ый", "ой"];
        foreach (var e in ends)
            if (w.EndsWith(e, StringComparison.Ordinal) && w.Length > e.Length + 2)
            {
                var stem = w[..^e.Length];
                if (e.StartsWith("ск") || e.StartsWith("цк")) { stem += e[..2]; yield return stem + "ий"; yield return stem + "ой"; }
                else { yield return stem + "ий"; yield return stem + "ый"; yield return stem + "ой"; }
                yield break;
            }
    }

    private static IEnumerable<string> AdjLemmas(string w)
    {
        if (w.EndsWith("ую")) { yield return w[..^2] + "ая"; }
        if (w.EndsWith("юю")) { yield return w[..^2] + "яя"; }
        // «Казачью Локню» → «Казачья Локня»
        if (w.EndsWith("ью")) { yield return w[..^2] + "ья"; }
        if (w.EndsWith("ой")) { yield return w[..^2] + "ая"; yield return w[..^2] + "ое"; yield return w[..^2] + "ой"; yield return w[..^2] + "ый"; }
        if (w.EndsWith("ого")) { yield return w[..^3] + "ое"; yield return w[..^3] + "ий"; yield return w[..^3] + "ый"; yield return w[..^3] + "ой"; }
        if (w.EndsWith("ом")) { yield return w[..^2] + "ое"; yield return w[..^2] + "ий"; yield return w[..^2] + "ый"; yield return w[..^2] + "ой"; }
        if (w.EndsWith("ей")) { yield return w[..^2] + "яя"; yield return w[..^2] + "ья"; }
        if (w.EndsWith("ьей")) yield return w[..^3] + "ья";
        if (w.EndsWith("их") || w.EndsWith("ых")) { yield return w[..^2] + "ие"; yield return w[..^2] + "ые"; }
    }
}
