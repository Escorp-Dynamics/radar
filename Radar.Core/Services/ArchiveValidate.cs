using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Отладка (--archive-validate &lt;архив&gt; &lt;каталог отчёта&gt;): разбор всего архива и отчёты по слабым местам парсера —
/// сообщения без угрозы, без места, с нераспознанными пунктами, с неизвестным типом; шаблоны текста (числа и названия
/// свёрнуты) сгруппированы и упорядочены по частоте, чтобы видеть новые формулировки и типы угроз.
/// </summary>
public static partial class ArchiveValidate
{
    public static void Run(string archiveDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        FrontLineSource.LoadForAudit();
        var settings = new AppSettings();
        var regions = settings.Channels.ToDictionary(c => c.Name, c => (IReadOnlyList<string>)c.Regions, StringComparer.OrdinalIgnoreCase);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var msgs = ArchiveFetcher.Load(archiveDir, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Console.WriteLine($"сообщений {msgs.Count}, загрузка {sw.Elapsed.TotalSeconds:F0} с");

        var verdicts = new ConcurrentDictionary<(string Ch, string V), int>();
        var threats = new ConcurrentDictionary<(ThreatType, ThreatKind, AlertStatus), int>();
        // шаблон → (число, пример, каналы)
        var groups = new ConcurrentDictionary<string, ConcurrentDictionary<string, Bucket>>();
        var unresolved = new ConcurrentDictionary<string, Bucket>();
        var words = new ConcurrentDictionary<string, int>();      // слова в сообщениях без угрозы / с общей угрозой
        var signalWords = new ConcurrentDictionary<string, int>(); // слова в распознанных сообщениях (фон)
        // одинаковый текст из разных каналов (репосты) — один раз: первым считается самое раннее сообщение
        // (время, канал, id), а не то, что первым обработал поток — отчёты одинаковы от запуска к запуску
        var firstOfText = new Dictionary<string, RawMessage>(StringComparer.Ordinal);
        foreach (var m in msgs) firstOfText.TryAdd(Flat(m.Text), m);
        var textChannels = msgs.GroupBy(m => Flat(m.Text), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Channel).Distinct().ToList(), StringComparer.Ordinal);

        Parallel.ForEach(msgs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, m =>
        {
            var p = MessageParser.Parse(m, regions.GetValueOrDefault(m.Channel) ?? []);
            string v = p.IsJunk ? "junk" : p.IsInformational ? "info"
                : p.Threat == ThreatType.None && p.Status != AlertStatus.AllClear ? "skip"
                : p.Regions.Count == 0 && p.Places.Count == 0 ? "noloc" : "signal";
            verdicts.AddOrUpdate((m.Channel, v), 1, (_, c) => c + 1);
            if (v == "signal") threats.AddOrUpdate((p.Threat, p.Kind, p.Status), 1, (_, c) => c + 1);
            if (!ReferenceEquals(firstOfText[Flat(m.Text)], m)) return;
            var chans = textChannels[Flat(m.Text)];

            void Add(string cat, string key)
            {
                var g = groups.GetOrAdd(cat, _ => new());
                g.GetOrAdd(key, _ => new Bucket()).Add(m, chans);
            }
            var tpl = Template(p.CleanText.Length > 0 ? p.CleanText : m.Text, p);
            if (v is "skip" or "noloc") Add(v, tpl);
            if (v == "signal" && p.Threat == ThreatType.General) Add("general", tpl);
            if (v == "signal" && p.Kind == ThreatKind.Unknown && p.Threat is not (ThreatType.General or ThreatType.None) && p.Status != AlertStatus.AllClear)
                Add("unknown-kind", tpl);
            if (v == "info") Add("info", tpl);
            if (v == "junk") Add("junk:" + (p.JunkReason ?? ""), tpl);
            foreach (var x in p.Places.Where(x => x.Pos == null))
                unresolved.GetOrAdd(x.Label + (x.RegionId is { } r ? " [" + r + "]" : ""), _ => new Bucket()).Add(m, chans);
            var target = v == "skip" || p.Threat == ThreatType.General ? words : v == "signal" ? signalWords : null;
            if (target != null)
                foreach (Match w in Word().Matches(p.CleanText.ToLowerInvariant()))
                    target.AddOrUpdate(w.Value, 1, (_, c) => c + 1);
        });
        Console.WriteLine($"разбор {sw.Elapsed.TotalSeconds:F0} с, уникальных текстов {firstOfText.Count}");

        using (var w = Out(outDir, "summary.txt"))
        {
            w.WriteLine($"Сообщений: {msgs.Count}, уникальных текстов: {firstOfText.Count}");
            w.WriteLine();
            w.WriteLine("Вердикты по каналам (signal / skip / noloc / info / junk):");
            foreach (var ch in verdicts.Keys.Select(k => k.Ch).Distinct().Order())
            {
                int G(string v) => verdicts.GetValueOrDefault((ch, v));
                int tot = new[] { "signal", "skip", "noloc", "info", "junk" }.Sum(G);
                w.WriteLine($"  {ch,-24} {tot,7}  signal {G("signal"),7} ({100.0 * G("signal") / tot,4:0}%)  skip {G("skip"),6}  noloc {G("noloc"),6}  info {G("info"),6}  junk {G("junk"),6}");
            }
            w.WriteLine();
            w.WriteLine("Угрозы (распознанные сообщения): тип / подтип / статус");
            foreach (var ((t, k, s), c) in threats.OrderByDescending(x => x.Value))
                w.WriteLine($"  {c,8}  {t} / {k} / {s}");
            w.WriteLine();
            foreach (var (cat, g) in groups.OrderBy(x => x.Key))
                w.WriteLine($"Группа {cat}: шаблонов {g.Count}, уникальных текстов {g.Values.Sum(b => b.Count)}");
            w.WriteLine($"Нераспознанных пунктов: {unresolved.Count}, упоминаний {unresolved.Values.Sum(b => b.Count)}");
        }
        int groupNo = 0;
        foreach (var (cat, g) in groups)
        {
            var safe = new string(cat.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            using var w = Out(outDir, $"tpl-{++groupNo:000}-" + (safe.Length > 60 ? safe[..60] : safe) + ".tsv");
            w.WriteLine("count\tchannels\ttemplate\texample");
            foreach (var (tpl, b) in g.OrderByDescending(x => x.Value.Count).Take(3000))
                w.WriteLine($"{b.Count}\t{b.ChannelsText}\t{tpl}\t{b.Example}");
        }
        using (var w = Out(outDir, "unresolved-places.tsv"))
        {
            w.WriteLine("count\tchannels\tplace\texample");
            foreach (var (k, b) in unresolved.OrderByDescending(x => x.Value.Count).Take(3000))
                w.WriteLine($"{b.Count}\t{b.ChannelsText}\t{k}\t{b.Example}");
        }
        // слова, характерные для нераспознанного (часто там, редко в распознанном) — кандидаты в новые типы угроз и маркеры
        using (var w = Out(outDir, "words-unrecognized.tsv"))
        {
            long totU = Math.Max(1, words.Values.Sum()), totS = Math.Max(1, signalWords.Values.Sum());
            w.WriteLine("count_unrec\tcount_signal\tlift\tword");
            foreach (var (word, c) in words.Where(x => x.Value >= 5)
                         .Select(x => (x.Key, x.Value, lift: (x.Value / (double)totU) / ((signalWords.GetValueOrDefault(x.Key) + 1) / (double)totS)))
                         .OrderByDescending(x => x.lift).Take(1500).Select(x => (x.Key, x.Value)))
                w.WriteLine($"{c}\t{signalWords.GetValueOrDefault(word)}\t{(c / (double)totU) / ((signalWords.GetValueOrDefault(word) + 1) / (double)totS):0.0}\t{word}");
        }
        Console.WriteLine($"готово за {sw.Elapsed.TotalSeconds:F0} с → {outDir}");
    }

    private static StreamWriter Out(string dir, string name) => new(Path.Combine(dir, name), false, new UTF8Encoding(false));

    private sealed class Bucket
    {
        private int _count;
        private readonly ConcurrentDictionary<string, byte> _channels = new();
        private RawMessage? _first;
        private readonly Lock _sync = new();
        public int Count => _count;
        public void Add(RawMessage m, IEnumerable<string> channels)
        {
            Interlocked.Increment(ref _count);
            foreach (var c in channels) _channels.TryAdd(c, 0);
            // пример — самое раннее сообщение шаблона (не зависит от порядка потоков)
            lock (_sync)
                if (_first == null || (m.Time, m.Channel, m.Id).CompareTo((_first.Time, _first.Channel, _first.Id)) < 0) _first = m;
        }
        private string? _example => _first is { } f ? $"{f.Channel}/{f.Id} {f.Time:yyyy-MM-dd}: {Flat(f.Text)}" : null;
        public string ChannelsText => string.Join(",", _channels.Keys.Order().Take(4)) + (_channels.Count > 4 ? $" +{_channels.Count - 4}" : "");
        public string Example => _example is { Length: > 400 } e ? e[..400] + "…" : _example ?? "";
    }

    private static string Flat(string s) => s.Replace('\t', ' ').Replace("\n", " ⏎ ");

    /// <summary>Шаблон: числа → #, распознанные пункты → [п], ссылки и @-упоминания убраны.</summary>
    private static string Template(string text, ParsedMessage p)
    {
        var s = text.ToLowerInvariant();
        foreach (var x in p.Places.OrderByDescending(x => x.Label.Length))
            if (x.Label.Length >= 3) s = s.Replace(x.Label.ToLowerInvariant(), "[п]");
        s = Url().Replace(s, "");
        s = Num().Replace(s, "#");
        s = Spaces().Replace(s.Replace('\n', ' '), " ").Trim();
        return s.Length > 160 ? s[..160] : s;
    }

    [GeneratedRegex(@"\p{L}[\p{L}-]{2,}")]
    private static partial Regex Word();
    [GeneratedRegex(@"\d+")]
    private static partial Regex Num();
    [GeneratedRegex(@"https?://\S+|@\w+|t\.me/\S+")]
    private static partial Regex Url();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
