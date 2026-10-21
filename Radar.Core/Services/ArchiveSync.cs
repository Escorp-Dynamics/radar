using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Radar.Services;

/// <summary>
/// Автозагрузка архива сообщений из релиза «archive»: сравнивает удалённый манифест с локальным и докачивает только
/// новые и изменившиеся месяцы (SHA-256 содержимого). Манифест пишется последним — оборванная загрузка оставляет архив
/// в прежнем согласованном состоянии; лишние файлы прежних версий месяцев удаляются после записи манифеста.
/// </summary>
public static class ArchiveSync
{
    public readonly record struct Result(int Downloaded, int Total, bool Changed);

    /// <summary>Каталог автозагружаемого архива (в каталоге данных приложения).</summary>
    public static string LocalDir => Path.Combine(AppPaths.Root, "archive-pack");

    /// <summary>Загрузка с GitHub; при сетевом сбое — повтор через ротатор прокси (см. <see cref="TelegramRoute.SendAsync"/>).</summary>
    private static Task<byte[]> GetAsync(HttpClient http, string url, CancellationToken ct) =>
        TelegramRoute.SendAsync(http, async (c, t) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            using var resp = await c.SendAsync(req, t).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(t).ConfigureAwait(false);
        }, ct);

    public static async Task<Result> SyncAsync(HttpClient http, string dir, string baseUrl, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var remoteBytes = await GetAsync(http, baseUrl + ArchivePack.ManifestName, ct);
        var remote = JsonSerializer.Deserialize(remoteBytes, ArchiveJsonContext.Default.ArchiveManifest);
        if (remote is not { Format: ArchivePack.FormatVersion }) throw new InvalidDataException("манифест архива другой версии — обновите приложение");
        var local = ArchivePack.ReadManifest(dir)?.Months.ToDictionary(m => m.Month, StringComparer.Ordinal) ?? [];
        var need = remote.Months.Where(e => !(local.TryGetValue(e.Month, out var l) && l.Sha256 == e.Sha256 && l.Messages == e.Messages
            && File.Exists(Path.Combine(dir, l.File)))).ToList();
        int done = 0;
        progress?.Report((0, need.Count));
        foreach (var e in need)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(e.File) != e.File) throw new InvalidDataException($"недопустимое имя пакета «{e.File}»");
            var data = await GetAsync(http, baseUrl + Uri.EscapeDataString(e.File), ct);
            Check(data, e);
            var path = Path.Combine(dir, e.File);
            await File.WriteAllBytesAsync(path + ".part", data, ct);
            File.Move(path + ".part", path, true);
            progress?.Report((++done, need.Count));
        }
        bool changed = need.Count > 0 || local.Count != remote.Months.Count;
        if (changed)
        {
            var tmp = Path.Combine(dir, ArchivePack.ManifestName + ".part");
            await File.WriteAllBytesAsync(tmp, remoteBytes, ct);
            File.Move(tmp, Path.Combine(dir, ArchivePack.ManifestName), true);
            var keep = remote.Months.Select(m => m.File).ToHashSet(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(dir, "*.tsv.gz"))
                if (!keep.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new Result(need.Count, remote.Months.Count, changed);
    }

    /// <summary>Пакет совпадает с манифестом: SHA-256 распакованного содержимого.</summary>
    internal static void Check(byte[] gz, ArchivePackEntry e)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)) z.CopyTo(ms);
        if (Convert.ToHexStringLower(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length))) != e.Sha256)
            throw new InvalidDataException($"пакет «{e.File}» не совпадает с манифестом");
    }
}
