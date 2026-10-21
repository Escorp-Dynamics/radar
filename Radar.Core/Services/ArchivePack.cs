using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radar.Models;

namespace Radar.Services;

/// <summary>Описание месячного пакета в манифесте.</summary>
public sealed class ArchivePackEntry
{
    public string Month { get; set; } = "";
    public string File { get; set; } = "";
    public int Messages { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public Dictionary<string, int> Channels { get; set; } = [];
    /// <summary>Время первого и последнего сообщения (unix, UTC).</summary>
    public long From { get; set; }
    public long To { get; set; }
}

/// <summary>Манифест пакетного архива: версия формата и список месяцев.</summary>
public sealed class ArchiveManifest
{
    public int Format { get; set; } = ArchivePack.FormatVersion;
    public DateTimeOffset Created { get; set; }
    public List<ArchivePackEntry> Months { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArchiveManifest))]
internal sealed partial class ArchiveJsonContext : JsonSerializerContext;

/// <summary>
/// Продакшн-формат архива: по пакету на месяц <c>{архив}/YYYY-MM.tsv.gz</c> (все каналы, по времени; строки — формат
/// кеша сообщений) и <c>{архив}/manifest.json</c> с числом сообщений, каналами и SHA-256 каждого пакета.
/// Рабочий формат (каталоги каналов с помесячными TSV, куда пишет докачка) упаковывается командой <c>--archive-pack</c>.
/// </summary>
public static class ArchivePack
{
    public const int FormatVersion = 1;
    public const string ManifestName = "manifest.json";

    public static string PackName(DateTimeOffset month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".tsv.gz";

    /// <summary>Манифест каталога или null — каталог не упакован (или манифест повреждён).</summary>
    public static ArchiveManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(path), ArchiveJsonContext.Default.ArchiveManifest);
            return m is { Format: FormatVersion } ? m : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Сообщения пакета месяца (уже отсортированы по времени). Пакет, которого нет или который оборван (сообщений меньше,
    /// чем в манифесте), — <see cref="InvalidDataException"/>: неполные данные иначе молча попали бы в статистику и её кеш.
    /// </summary>
    public static IEnumerable<RawMessage> ReadMonth(string dir, ArchivePackEntry e)
    {
        var path = Path.Combine(dir, e.File);
        if (!System.IO.File.Exists(path)) throw new InvalidDataException($"нет пакета архива «{e.File}»");
        int n = 0;
        using var fs = System.IO.File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz, new UTF8Encoding(false));
        while (r.ReadLine() is { } line)
            if (MessageCache.Parse(line) is { } m) { n++; yield return m; }
        if (e.Messages > 0 && n != e.Messages)
            throw new InvalidDataException($"пакет «{e.File}» повреждён: {n} сообщений вместо {e.Messages}");
    }

    /// <summary>
    /// Упаковка рабочего архива <paramref name="src"/> в <paramref name="dst"/>: пакеты месяцев и манифест.
    /// Неизменившиеся месяцы (тот же SHA-256 содержимого) не перезаписываются. Возвращает манифест.
    /// </summary>
    public static ArchiveManifest Pack(string src, string dst, Action<string>? log = null)
    {
        Directory.CreateDirectory(dst);
        var oldManifest = ReadManifest(dst);
        // манифест есть, но не читается — упаковка с пустым «прежним» списком потеряла бы месяцы, которых нет в источнике
        if (oldManifest is null && System.IO.File.Exists(Path.Combine(dst, ManifestName)))
            throw new InvalidDataException($"манифест «{Path.Combine(dst, ManifestName)}» повреждён или другой версии — упаковка остановлена, архив не тронут");
        var old = oldManifest?.Months.ToDictionary(m => m.Month, StringComparer.Ordinal) ?? [];
        var manifest = new ArchiveManifest { Created = DateTimeOffset.UtcNow };
        var byMonth = ArchiveFetcher.ChannelDirs(src)
            .SelectMany(d => Directory.GetFiles(d, "????-??.tsv"))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in byMonth)
        {
            if (!DateTime.TryParseExact(g.Key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var mdt))
                continue;
            var month = new DateTimeOffset(mdt, TimeSpan.Zero);
            // все каналы месяца — без повторов и по времени (как и при чтении каталогов)
            var src0 = g.SelectMany(f => System.IO.File.ReadLines(f).Select(MessageCache.Parse).OfType<RawMessage>());
            // в источнике месяц меньше, чем в архиве (неполная копия, нет каталога канала): старый пакет объединяется
            // с источником — упаковка только добавляет сообщения, но не теряет их
            if (old.TryGetValue(g.Key, out var before) && System.IO.File.Exists(Path.Combine(dst, before.File)))
            {
                var srcList = src0.ToList();
                if (srcList.DistinctBy(m => (m.Channel, m.Id)).Count() < before.Messages)
                {
                    log?.Invoke($"{g.Key}: в источнике меньше сообщений, чем в архиве ({before.Messages}) — объединено с прежним пакетом");
                    srcList.AddRange(ReadMonth(dst, before));
                }
                src0 = srcList;
            }
            var msgs = src0
                .GroupBy(m => (m.Channel, m.Id)).Select(x => x.First())
                .OrderBy(m => m.Time).ThenBy(m => m.Channel, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();
            if (msgs.Count == 0) continue;
            var body = new UTF8Encoding(false).GetBytes(string.Join('\n', msgs.Select(MessageCache.Format)) + "\n");
            var sha = Convert.ToHexStringLower(SHA256.HashData(body));
            var name = PackName(month);
            if (old.TryGetValue(g.Key, out var same) && same.Sha256 == sha && System.IO.File.Exists(Path.Combine(dst, same.File))) name = same.File;
            // новый пакет — под новым именем: прежний остаётся на месте, пока манифест на него ссылается (обрыв
            // упаковки посередине не делает архив нечитаемым)
            else name = month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + "." + sha[..8] + ".tsv.gz";
            var path = Path.Combine(dst, name);
            var entry = new ArchivePackEntry
            {
                Month = g.Key, File = name, Messages = msgs.Count, Sha256 = sha,
                Channels = msgs.GroupBy(m => m.Channel).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count()),
                From = msgs[0].Time.ToUnixTimeSeconds(), To = msgs[^1].Time.ToUnixTimeSeconds(),
            };
            if (old.TryGetValue(g.Key, out var prev) && prev.Sha256 == sha && System.IO.File.Exists(path))
            {
                entry.Bytes = new FileInfo(path).Length;
                manifest.Months.Add(entry);
                continue;
            }
            var tmp = path + ".tmp";
            using (var fs = System.IO.File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                gz.Write(body);
            System.IO.File.Move(tmp, path, true);
            entry.Bytes = new FileInfo(path).Length;
            manifest.Months.Add(entry);
            log?.Invoke($"{g.Key}: {msgs.Count} сообщений, {entry.Bytes / 1024} КБ");
        }
        // неверный источник (нет каталогов каналов) не должен «обнулить» готовый архив пустым манифестом
        if (manifest.Months.Count == 0) throw new InvalidDataException($"в «{src}» нет месяцев архива (ожидались каталоги каналов с YYYY-MM.tsv)");
        // месяцы, которых нет в источнике (неполная копия, удалённые старые файлы), остаются в архиве
        foreach (var (key, prev) in old)
            if (!manifest.Months.Any(m => m.Month == key) && System.IO.File.Exists(Path.Combine(dst, prev.File)))
            {
                manifest.Months.Add(prev);
                log?.Invoke($"{key}: нет в источнике — оставлен прежний пакет");
            }
        manifest.Months.Sort((a, b) => string.CompareOrdinal(a.Month, b.Month));
        WriteManifest(dst, manifest);
        // манифест уже ссылается на новые пакеты — прежние файлы, на которые больше никто не ссылается, удаляются
        var live = manifest.Months.Select(m => m.File).ToHashSet(StringComparer.Ordinal);
        foreach (var p in old.Values)
            if (!live.Contains(p.File))
                try { System.IO.File.Delete(Path.Combine(dst, p.File)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return manifest;
    }

    /// <summary>
    /// Обратно в рабочий формат (каталоги каналов) — для докачки на сервере сборки: пакеты релиза → <c>{канал}/YYYY-MM.tsv</c>
    /// и <c>.state</c> по сохранённым id (листание вглубь завершено, докачка идёт только сверху, от самого нового id).
    /// </summary>
    public static int Unpack(string src, string dst, Action<string>? log = null)
    {
        if (ReadManifest(src) is not { } manifest) throw new InvalidDataException($"в «{src}» нет манифеста архива");
        var range = new Dictionary<string, (long Min, long Max)>(StringComparer.Ordinal);
        int n = 0;
        foreach (var e in manifest.Months)
        {
            var writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
            try
            {
                foreach (var m in ReadMonth(src, e))
                {
                    if (!writers.TryGetValue(m.Channel, out var w))
                    {
                        var d = Path.Combine(dst, m.Channel);
                        Directory.CreateDirectory(d);
                        writers[m.Channel] = w = new StreamWriter(Path.Combine(d, e.Month + ".tsv"), false, new UTF8Encoding(false));
                    }
                    w.Write(MessageCache.Format(m));
                    w.Write('\n');
                    n++;
                    // правки (id со сдвигом) не настоящие id канала — в состояние листания не идут
                    if (m.Id >= AlertEngine.EditIdOffset) continue;
                    range[m.Channel] = range.TryGetValue(m.Channel, out var r) ? (Math.Min(r.Min, m.Id), Math.Max(r.Max, m.Id)) : (m.Id, m.Id);
                }
            }
            finally { foreach (var w in writers.Values) w.Dispose(); }
        }
        foreach (var (ch, r) in range)
            System.IO.File.WriteAllText(Path.Combine(dst, ch, ".state"), $"oldest={r.Min}\nnewest={r.Max}\ndone=1\n");
        log?.Invoke($"распаковано {n} сообщений, каналов {range.Count}");
        return n;
    }

    private static void WriteManifest(string dir, ArchiveManifest m)
    {
        var path = Path.Combine(dir, ManifestName);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(m, ArchiveJsonContext.Default.ArchiveManifest));
        System.IO.File.Move(tmp, path, true);
    }

    /// <summary>Проверка пакетов по манифесту: файл есть, SHA-256 и число сообщений совпадают. Возвращает список ошибок.</summary>
    public static List<string> Verify(string dir)
    {
        var errors = new List<string>();
        if (ReadManifest(dir) is not { } m) { errors.Add("манифест не найден или повреждён"); return errors; }
        foreach (var e in m.Months)
        {
            var path = Path.Combine(dir, e.File);
            if (!System.IO.File.Exists(path)) { errors.Add($"{e.Month}: нет файла {e.File}"); continue; }
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                var body = ms.ToArray();
                var sha = Convert.ToHexStringLower(SHA256.HashData(body));
                if (sha != e.Sha256) { errors.Add($"{e.Month}: SHA-256 не совпадает"); continue; }
                int n = Encoding.UTF8.GetString(body).Split('\n').Count(l => MessageCache.Parse(l) != null);
                if (n != e.Messages) errors.Add($"{e.Month}: сообщений {n}, в манифесте {e.Messages}");
            }
            catch (InvalidDataException) { errors.Add($"{e.Month}: пакет повреждён"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{e.Month}: не удалось прочитать ({ex.Message})"); }
        }
        return errors;
    }
}
