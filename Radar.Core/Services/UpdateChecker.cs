using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Services;

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
    [JsonPropertyName("html_url")] public string Page { get; set; } = "";
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;

/// <summary>Проверка новой версии по последнему релизу репозитория (тег vX.Y.Z).</summary>
public static class UpdateChecker
{
    public static Task<GitHubRelease?> LatestAsync(HttpClient http, CancellationToken ct) =>
        // при сетевом сбое — повтор через ротатор прокси
        TelegramRoute.SendAsync(http, async (c, t) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Distribution.LatestReleaseApi);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await c.SendAsync(req, t);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize(await resp.Content.ReadAsByteArrayAsync(t), ReleaseJsonContext.Default.GitHubRelease);
        }, ct);

    /// <summary>Номер версии из тега («v1.2.3» → 1.2.3); не версия — null.</summary>
    public static Version? Parse(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;

    public static bool IsNewer(string tag, Version current) =>
        Parse(tag) is { } v && v > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>Файл релиза для этой платформы: apk на Android, zip под Windows и macOS, tar.gz под Linux — под архитектуру процессора.</summary>
    public static GitHubAsset? AssetFor(GitHubRelease r)
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        string suffix = OperatingSystem.IsAndroid() ? ".apk" : OperatingSystem.IsWindows() ? $"win-{arch}.zip"
            : OperatingSystem.IsMacOS() ? $"osx-{arch}.zip" : OperatingSystem.IsLinux() ? $"linux-{arch}.tar.gz" : "";
        return suffix.Length == 0 ? null : r.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
