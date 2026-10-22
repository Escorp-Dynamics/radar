using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Services;

/// <summary>Готовые итоги месяца в релизе «archive».</summary>
public sealed class StatsPackEntry
{
    public string Month { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>Отпечаток архива и движка (<see cref="ArchiveStats.Stamp"/>), с которым посчитан месяц.</summary>
    public long Stamp { get; set; }
    /// <summary>SHA-256 несжатого кеша.</summary>
    public string Sha256 { get; set; } = "";
}

public sealed class StatsPackManifest
{
    public int Format { get; set; } = StatsPack.FormatVersion;
    public DateTimeOffset Created { get; set; }
    public List<StatsPackEntry> Months { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatsPackManifest))]
internal sealed partial class StatsJsonContext : JsonSerializerContext;

/// <summary>
/// Статистика архива, посчитанная в CI: кеш каждого месяца (<c>{архив}/.stats/YYYY-MM.tsv</c>) выкладывается в релиз «archive»
/// как <c>stats-YYYY-MM.{sha}.tsv.gz</c> со списком <c>stats.json</c>. Клиент берёт месяц, только если его отпечаток
/// (пакеты архива + код движка) совпадает со своим — то есть итоги те же, что он посчитал бы сам, но без прогона движка.
/// </summary>
public static class StatsPack
{
    public const int FormatVersion = 1;
    public const string ManifestName = "stats.json";

    private static DateTimeOffset? ParseMonth(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var m)
            ? new DateTimeOffset(m, TimeSpan.Zero) : null;

    public static StatsPackManifest? ReadManifest(byte[] json)
    {
        try { return JsonSerializer.Deserialize(json, StatsJsonContext.Default.StatsPackManifest) is { Format: FormatVersion } m ? m : null; }
        catch (JsonException) { return null; }
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)) z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Принимает месяц пакета, если он подходит этому архиву и этой сборке. Возвращает, принят ли.</summary>
    private static bool Accept(string archiveDir, StatsPackEntry e, byte[] gz)
    {
        if (ParseMonth(e.Month) is not { } month || e.Stamp != ArchiveStats.Stamp(archiveDir, month)) return false;
        byte[] body;
        try { body = Gunzip(gz); }
        catch (InvalidDataException) { return false; }
        if (Convert.ToHexStringLower(SHA256.HashData(body)) != e.Sha256) return false;
        return ArchiveStats.Import(archiveDir, month, body);
    }

    /// <summary>Месяцы, которые ещё не посчитаны и для которых в пакете есть итоги с тем же отпечатком.</summary>
    private static IEnumerable<StatsPackEntry> Wanted(string archiveDir, StatsPackManifest m) =>
        m.Months.Where(e => Path.GetFileName(e.File) == e.File && ParseMonth(e.Month) is { } month
                            && e.Stamp == ArchiveStats.Stamp(archiveDir, month) && !ArchiveStats.IsReady(archiveDir, month));

    /// <summary>Готовые итоги из каталога прежнего пакета (CI: посчитанные вчера месяцы не пересчитываются).</summary>
    public static int ImportDir(string archiveDir, string packDir)
    {
        var path = Path.Combine(packDir, ManifestName);
        if (!System.IO.File.Exists(path) || ReadManifest(System.IO.File.ReadAllBytes(path)) is not { } m) return 0;
        int n = 0;
        foreach (var e in Wanted(archiveDir, m).ToList())
        {
            var f = Path.Combine(packDir, e.File);
            if (System.IO.File.Exists(f) && Accept(archiveDir, e, System.IO.File.ReadAllBytes(f))) n++;
        }
        return n;
    }

    /// <summary>
    /// Считает все месяцы архива (недостающие — параллельно) и пишет пакет статистики в <paramref name="outDir"/>:
    /// файлы месяцев и <see cref="ManifestName"/> (последним).
    /// </summary>
    public static StatsPackManifest Pack(string archiveDir, string outDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(outDir);
        var months = ArchiveFetcher.Months(archiveDir);
        if (months.Count == 0) throw new InvalidDataException($"в «{archiveDir}» нет месяцев архива");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = new System.Collections.Concurrent.ConcurrentBag<StatsPackEntry>();
        Parallel.ForEach(months.OrderByDescending(m => m), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, month =>
        {
            bool ready = ArchiveStats.IsReady(archiveDir, month);
            var body = ArchiveStats.Export(archiveDir, month) ?? throw new IOException($"{month:yyyy-MM}: не удалось записать кеш статистики");
            var sha = Convert.ToHexStringLower(SHA256.HashData(body));
            var name = $"stats-{month:yyyy-MM}.{sha[..8]}.tsv.gz";
            var path = Path.Combine(outDir, name);
            if (!System.IO.File.Exists(path))
            {
                using var fs = System.IO.File.Create(path + ".tmp");
                using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize)) gz.Write(body);
                System.IO.File.Move(path + ".tmp", path, true);
            }
            entries.Add(new StatsPackEntry { Month = month.ToString("yyyy-MM", CultureInfo.InvariantCulture), File = name, Stamp = ArchiveStats.Stamp(archiveDir, month), Sha256 = sha });
            log?.Invoke($"{month:yyyy-MM}: {(ready ? "готов" : "посчитан")} · {sw.Elapsed:m\\:ss}");
        });
        var manifest = new StatsPackManifest { Created = DateTimeOffset.UtcNow, Months = [.. entries.OrderBy(e => e.Month, StringComparer.Ordinal)] };
        var live = manifest.Months.Select(e => e.File).ToHashSet(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(outDir, "stats-*.tsv.gz"))
            if (!live.Contains(Path.GetFileName(f))) System.IO.File.Delete(f);
        var mpath = Path.Combine(outDir, ManifestName);
        System.IO.File.WriteAllBytes(mpath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(manifest, StatsJsonContext.Default.StatsPackManifest));
        System.IO.File.Move(mpath + ".tmp", mpath, true);
        return manifest;
    }

    /// <summary>
    /// Загрузка готовых итогов из релиза: только месяцы, которых нет локально и чей отпечаток совпадает со своим.
    /// Пакета нет (ещё не опубликован) — 0, без ошибки. Возвращает число принятых месяцев.
    /// </summary>
    public static async Task<int> SyncAsync(HttpClient http, string archiveDir, string baseUrl, CancellationToken ct)
    {
        byte[] json;
        try { json = await ArchiveSync.GetAsync(http, baseUrl + ManifestName, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return 0; }
        if (ReadManifest(json) is not { } m) return 0;
        var todo = await Task.Run(() => Wanted(archiveDir, m).ToList(), ct).ConfigureAwait(false);
        int n = 0;
        foreach (var e in todo)
        {
            ct.ThrowIfCancellationRequested();
            var gz = await ArchiveSync.GetAsync(http, baseUrl + Uri.EscapeDataString(e.File), ct).ConfigureAwait(false);
            if (await Task.Run(() => Accept(archiveDir, e, gz), ct).ConfigureAwait(false)) n++;
        }
        return n;
    }
}
