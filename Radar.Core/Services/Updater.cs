using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Services;

/// <summary>Файл релиза в манифесте обновления: имя, SHA-256 и размер.</summary>
public sealed class UpdateAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Манифест обновления (update.json в релизе): версия и файлы, подписан ключом сборки (update.json.sig).</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("assets")] public List<UpdateAsset> Assets { get; set; } = [];
}

[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

/// <summary>Чем закончилась установка: файлы уже заменены (новая версия — после перезапуска) или ждём подтверждения системы (Android).</summary>
public enum UpdateInstall { RestartToApply, AwaitingUser }

/// <summary>Установщик скачанного пакета для своей платформы.</summary>
public interface IUpdateInstaller
{
    /// <summary>Почему автоустановка сейчас невозможна (например, нет прав на папку программы); null — можно.</summary>
    string? Unavailable { get; }
    /// <summary>Можно ставить в фоне, не открывая системных окон (Android: разрешение на установку уже выдано).</summary>
    bool CanInstallUnattended { get; }
    Task<UpdateInstall> InstallAsync(string package, CancellationToken ct);
}

/// <summary>Что и откуда ставить: проверенный манифестом файл релиза.</summary>
public sealed record UpdatePlan(string Tag, string Url, UpdateAsset Asset);

/// <summary>
/// Автообновление: манифест релиза проверяется по подписи (ECDSA P-256, открытый ключ зашит в приложение), файл — по SHA-256
/// и размеру из манифеста. Запросы к GitHub при сбое идут через прокси — без подписи подменённый файл ставился бы как родной.
/// </summary>
public static class Updater
{
    public const string ManifestName = "update.json";
    public const string SignatureName = "update.json.sig";
    /// <summary>macOS-сборки дописываются в релиз позже — своим манифестом: общий не перезаписывается на глазах у клиентов.</summary>
    public const string MacManifestName = "update-osx.json";
    public const string MacSignatureName = "update-osx.json.sig";

    /// <summary>Открытый ключ подписи релизов (SubjectPublicKeyInfo, DER, base64). Закрытый — секрет UPDATE_SIGNING_KEY в CI.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEinJK2517xz7ZoCKUYHhUVZr9wJKdNPyL11+Aty1PWtmZ5dHua8E/+2s116S9fAsmvbj23ae0q885DR9/Z8pjbw==";

    /// <summary>Сборка из CI (-p:RadarRelease=true): только она обновляет сама себя, локальная сборка разработчика — никогда.</summary>
    public static bool IsReleaseBuild =>
#if RADAR_RELEASE
        true;
#else
        false;
#endif

    public static Version CurrentVersion => typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Установщик платформы; null — только ссылка на скачивание.</summary>
    public static IUpdateInstaller? Installer { get; set; }
    /// <summary>Перезапуск в новую версию (десктоп).</summary>
    public static Action? Restart { get; set; }
    /// <summary>Перезапуск, если окно скрыто в трее (пользователь ничего не заметит); true — перезапуск начат.</summary>
    public static Func<bool>? RestartIfIdle { get; set; }

    /// <summary>Сообщение установщика платформы (Android: итог установки приходит позже, из системы).</summary>
    public static event Action<string, bool>? ExternalStatus;
    public static void Report(string text, bool failed) => ExternalStatus?.Invoke(text, failed);

    public static string DownloadDir => Path.Combine(AppPaths.Root, "update");

    /// <summary>Манифест, если подпись верна; иначе null.</summary>
    public static UpdateManifest? Verify(byte[] json, byte[] signature, string publicKey = PublicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(json, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
        try { return JsonSerializer.Deserialize(json, UpdateJsonContext.Default.UpdateManifest); }
        catch (JsonException) { return null; }
    }

    /// <summary>Файл из манифеста той же версии, что и релиз: старый подписанный манифест не выдать за новый.</summary>
    public static UpdateAsset? Select(UpdateManifest m, string tag, string assetName)
    {
        if (UpdateChecker.Parse(tag) is not { } v || UpdateChecker.Parse(m.Version) != v) return null;
        var a = m.Assets.FirstOrDefault(x => x.Name == assetName);
        return a is { Size: > 0 } && a.Sha256.Length == 64 ? a : null;
    }

    /// <summary>
    /// План установки для этой платформы; null — в релизе нет подписанного манифеста или файла под платформу (ещё не собран,
    /// как macOS, дописываемая позже). Неверная подпись или манифест другой версии — исключение: это подмена, а не нехватка.
    /// </summary>
    public static async Task<UpdatePlan?> PlanAsync(HttpClient http, GitHubRelease rel, CancellationToken ct)
    {
        if (UpdateChecker.AssetFor(rel) is not { } asset) return null;
        bool mac = asset.Name.Contains("-osx-", StringComparison.Ordinal);
        var m = rel.Assets.FirstOrDefault(a => a.Name == (mac ? MacManifestName : ManifestName));
        var s = rel.Assets.FirstOrDefault(a => a.Name == (mac ? MacSignatureName : SignatureName));
        if (m == null || s == null) return null;
        var json = await GetBytesAsync(http, m.Url, 256 * 1024, ct).ConfigureAwait(false);
        var sig = await GetBytesAsync(http, s.Url, 4096, ct).ConfigureAwait(false);
        var man = Verify(json, sig) ?? throw new InvalidDataException("подпись обновления не прошла проверку");
        if (UpdateChecker.Parse(man.Version) != UpdateChecker.Parse(rel.Tag)) throw new InvalidDataException($"манифест {man.Version} не от релиза {rel.Tag}");
        return Select(man, rel.Tag, asset.Name) is { } entry ? new UpdatePlan(rel.Tag, asset.Url, entry) : null;
    }

    private static Task<byte[]> GetBytesAsync(HttpClient http, string url, int max, CancellationToken ct) =>
        TelegramRoute.SendAsync(http, async (c, t) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            using var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, t).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(t).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, t).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > max) throw new InvalidDataException($"ответ больше {max} байт");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }, ct);

    /// <summary>Клиент для загрузки пакета: десятки мегабайт на медленной связи — с запасом по времени.</summary>
    public static HttpClient CreateDownloadClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        return http;
    }

    /// <summary>Скачать файл плана в <see cref="DownloadDir"/>: размер и SHA-256 сверяются с манифестом, иначе файл удаляется.</summary>
    public static Task<string> DownloadAsync(HttpClient http, UpdatePlan plan, IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(http, plan, DownloadDir, progress, ct);

    public static Task<string> DownloadAsync(HttpClient http, UpdatePlan plan, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(plan.Asset.Name));
        var part = dest + ".part";
        return TelegramRoute.SendAsync(http, async (c, t) =>
        {
            try
            {
                using var resp = await c.GetAsync(plan.Url, HttpCompletionOption.ResponseHeadersRead, t).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using (var src = await resp.Content.ReadAsStreamAsync(t).ConfigureAwait(false))
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, t).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > plan.Asset.Size) throw new InvalidDataException("файл обновления больше заявленного");
                        sha.AppendData(buf, 0, n);
                        await fs.WriteAsync(buf.AsMemory(0, n), t).ConfigureAwait(false);
                        progress?.Report((double)total / plan.Asset.Size);
                    }
                    if (total != plan.Asset.Size) throw new InvalidDataException("файл обновления скачан не полностью");
                    if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), plan.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("контрольная сумма обновления не совпала");
                }
                File.Move(part, dest, overwrite: true);
                return dest;
            }
            catch
            {
                try { File.Delete(part); } catch (IOException) { }
                throw;
            }
        }, TimeSpan.FromMinutes(20), ct);
    }

    /// <summary>Скачанные пакеты больше не нужны (установлена последняя версия).</summary>
    public static void ClearDownloads()
    {
        try { if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
