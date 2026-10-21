using System.Globalization;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Полный архив каналов (--archive-fetch dir): все сообщения за всё время через веб-превью t.me/s,
/// по файлу на месяц: <c>dir/{канал}/YYYY-MM.tsv</c> (формат кеша сообщений). Докачка: состояние листания
/// хранится в <c>dir/{канал}/.state</c>, повторный запуск продолжает с места остановки и дописывает новые сообщения.
/// </summary>
public static class ArchiveFetcher
{
    public static async Task RunAsync(string dir, IReadOnlyList<string>? only, int parallel)
    {
        Directory.CreateDirectory(dir);
        // каналы из аргументов — как есть (свой канал вне списка по умолчанию тоже качается)
        var channels = only is { Count: > 0 } ? only.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : AppSettings.DefaultChannels().Select(c => c.Name).ToList();
        using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(40) };
        var tg = new TelegramSource(http);
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(channels.Select(async ch =>
        {
            await gate.WaitAsync();
            try { await FetchChannelAsync(tg, Path.Combine(dir, ch), ch, sw); }
            catch (Exception ex) { Log($"{ch}: ошибка {ex.Message}"); }
            finally { gate.Release(); }
        }));
        Log($"готово за {Elapsed(sw)}");
    }

    private static readonly Lock LogLock = new();
    private static string Elapsed(System.Diagnostics.Stopwatch sw) => $"{(int)sw.Elapsed.TotalHours}:{sw.Elapsed:mm\\:ss}";
    private static void Log(string s) { lock (LogLock) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); }

    /// <summary>Сообщение ещё не сохранено: вглубь — ниже oldest; сверху — выше newest или ниже oldest (догон после обрыва).</summary>
    internal static bool IsFresh(long id, bool deep, long oldest, long newest) =>
        deep ? id < oldest : id > newest || id < oldest;

    private static async Task FetchChannelAsync(TelegramSource tg, string dir, string ch, System.Diagnostics.Stopwatch sw)
    {
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, ".state");
        // oldest — самый старый id, до которого докачано (0 — листание вглубь ещё не начиналось), done — дошли до начала канала,
        // newest — самый новый сохранённый id (с него дописываются новые сообщения)
        long oldest = 0, newest = 0;
        bool done = false;
        if (File.Exists(statePath))
            foreach (var line in File.ReadAllLines(statePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "oldest") long.TryParse(kv[1], out oldest);
                else if (kv[0] == "newest") long.TryParse(kv[1], out newest);
                else if (kv[0] == "done") done = kv[1] == "1";
            }
        // атомарно: оборванная запись не превращает oldest=1234 в oldest=12 (дыра в архиве)
        void SaveState()
        {
            var tmp = statePath + ".tmp";
            File.WriteAllText(tmp, $"oldest={oldest}\nnewest={newest}\ndone={(done ? 1 : 0)}\n");
            File.Move(tmp, statePath, true);
        }

        int saved = 0, pages = 0;
        // прерванная докачка (ошибка, Ctrl+C) могла дописать сообщения до сохранения состояния — повторы убираются всегда
        try
        {
        // 1) новые сообщения сверху — до уже сохранённого newest; 2) вглубь — от oldest до начала канала
        long top = 0;
        long? before = null;
        bool deep = false;
        while (true)
        {
            if (deep && done) break;
            if (deep) before = oldest;
            var (msgs, ids) = await PageAsync(tg, ch, before);
            pages++;
            if (ids.Count == 0) { if (deep) { done = true; SaveState(); } break; }
            long pageMin = ids.Min(), pageMax = ids.Max();
            if (top == 0) top = pageMax;
            // в догоне страница может уйти ниже oldest (прошлый запуск оборвался в начале листания вглубь): сообщения
            // ниже oldest тоже пишутся, иначе после сдвига oldest вниз они остались бы дырой навсегда
            var fresh = msgs.Where(m => IsFresh(m.Id, deep, oldest, newest)).ToList();
            saved += Write(dir, fresh);
            if (!deep)
            {
                if (oldest == 0) oldest = pageMin;
                if (pageMin <= newest + 1 || newest == 0)
                {
                    // догнали сохранённое (или первый запуск: верх канала получен) — дальше вглубь
                    newest = Math.Max(newest, top);
                    if (oldest == 0 || pageMin < oldest) oldest = pageMin;
                    SaveState();
                    deep = true;
                    continue;
                }
                before = pageMin;
            }
            else
            {
                if (pageMin >= oldest) { done = true; SaveState(); break; }
                oldest = pageMin;
                if (oldest <= 1) done = true;
                SaveState();
            }
            if (pages % 50 == 0)
                Log($"{ch}: страниц {pages}, сохранено {saved}, до id {oldest} ({msgs.Select(m => m.Time).DefaultIfEmpty().Min():yyyy-MM-dd}) · {Elapsed(sw)}");
            await Task.Delay(350);
        }
        Log($"{ch}: готово — страниц {pages}, новых сообщений {saved}, канал с id {oldest}");
        }
        finally { Normalize(dir); }
    }

    private static async Task<(List<RawMessage> Msgs, List<long> Ids)> PageAsync(TelegramSource tg, string ch, long? before)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await tg.FetchPageAsync(ch, before, CancellationToken.None); }
            // канал не найден — повторять бессмысленно; страница без ленты (закрыт, заглушка) — пара повторов, не восемь
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { throw; }
            catch (InvalidDataException) when (attempt >= 2) { throw; }
            catch (Exception ex) when (attempt < 8)
            {
                // 429 и сетевые сбои — пауза с нарастанием и разбросом (Retry-After, если сервер его указал)
                var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempt)) * (0.85 + Random.Shared.NextDouble() * 0.3));
                if (ex is RetryAfterException ra && ra.Delay > wait) wait = ra.Delay;
                Log($"{ch}: {ex.Message} — повтор через {wait.TotalSeconds:0} с");
                await Task.Delay(wait);
            }
        }
    }

    private static int Write(string dir, List<RawMessage> msgs)
    {
        foreach (var g in msgs.GroupBy(m => m.Time.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            // как в кеше: оборванная прошлая запись (без перевода строки) не склеивается с новой
            MessageCache.AppendLines(Path.Combine(dir, g.Key + ".tsv"), g.Select(MessageCache.Format));
        return msgs.Count;
    }

    /// <summary>Каждый месячный файл — без повторов и по времени.</summary>
    private static void Normalize(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "????-??.tsv"))
        {
            var list = File.ReadLines(f).Select(MessageCache.Parse).OfType<RawMessage>()
                .GroupBy(m => m.Id).Select(g => g.First()).OrderBy(m => m.Time).ThenBy(m => m.Id).ToList();
            var tmp = f + ".tmp";
            File.WriteAllLines(tmp, list.Select(MessageCache.Format), new UTF8Encoding(false));
            File.Move(tmp, f, true);
        }
    }

    /// <summary>Каталоги каналов архива (служебные — «.stats» и т. п. — пропускаются).</summary>
    public static IEnumerable<string> ChannelDirs(string dir) =>
        Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal) : [];

    /// <summary>Все сообщения архива (все каналы) в интервале — по времени. Пакетный архив (manifest.json) или каталоги каналов.</summary>
    public static List<RawMessage> Load(string dir, DateTimeOffset from, DateTimeOffset to)
    {
        var res = new List<RawMessage>();
        if (!Directory.Exists(dir)) return res;
        if (ArchivePack.ReadManifest(dir) is { } manifest)
        {
            foreach (var e in manifest.Months)
            {
                if (DateTimeOffset.FromUnixTimeSeconds(e.To) < from || DateTimeOffset.FromUnixTimeSeconds(e.From) > to) continue;
                foreach (var m in ArchivePack.ReadMonth(dir, e))
                    if (m.Time >= from && m.Time <= to) res.Add(m);
            }
            res.Sort(ByTime);
            return res;
        }
        foreach (var chDir in ChannelDirs(dir))
            foreach (var f in Directory.GetFiles(chDir, "????-??.tsv"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!DateTime.TryParseExact(name, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month)) continue;
                var mStart = new DateTimeOffset(month, TimeSpan.Zero);
                if (mStart.AddMonths(1) <= from || mStart > to) continue;
                foreach (var line in File.ReadLines(f))
                    if (MessageCache.Parse(line) is { } m && m.Time >= from && m.Time <= to) res.Add(m);
            }
        // повторы (прерванная докачка) — как в пакетном формате: одно сообщение на (канал, id)
        res = [.. res.DistinctBy(m => (m.Channel, m.Id))];
        res.Sort(ByTime);
        return res;
    }

    // порядок одновременных сообщений не зависит от формата архива и раскладки файлов
    private static int ByTime(RawMessage a, RawMessage b)
    {
        int c = a.Time.CompareTo(b.Time);
        if (c == 0) c = string.CompareOrdinal(a.Channel, b.Channel);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    /// <summary>Месяцы, за которые есть архив (по всем каналам).</summary>
    public static List<DateTimeOffset> Months(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        if (ArchivePack.ReadManifest(dir) is { } manifest)
            return [.. manifest.Months
                .Select(e => DateTime.TryParseExact(e.Month, "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var m) ? new DateTimeOffset(m, TimeSpan.Zero) : (DateTimeOffset?)null)
                .OfType<DateTimeOffset>().Distinct().Order()];
        return ChannelDirs(dir).SelectMany(d => Directory.GetFiles(d, "????-??.tsv"))
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var m) ? new DateTimeOffset(m, TimeSpan.Zero) : (DateTimeOffset?)null)
            .OfType<DateTimeOffset>().Distinct().Order().ToList();
    }
}
