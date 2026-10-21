using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Геокодирование неизвестных названий через Nominatim (OpenStreetMap) с ограничением
/// 1 запрос/с (требование политики сервиса) и постоянным кэшем на диске.
/// Результат ограничивается окрестностью региона, чтобы не путать одноимённые сёла.
/// </summary>
public sealed class Geocoder(HttpClient http, NominatimThrottle throttle)
{
    // общий для всех экземпляров (живой движок, воспроизведение, статистика): файл читается один раз,
    // дописывания из разных экземпляров не перемешиваются
    private static readonly Lazy<ConcurrentDictionary<string, GeoPoint?>> SharedCache = new(Load);
    private static readonly Lock FileLock = new();
    private readonly ConcurrentDictionary<string, GeoPoint?> _cache = SharedCache.Value;

    public bool TryGetCached(string query, string regionId, out GeoPoint? pos) => _cache.TryGetValue(Key(query, regionId), out pos);

    public async Task<GeoPoint?> GeocodeAsync(string query, string regionId, CancellationToken ct)
    {
        var key = Key(query, regionId);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var region = Gazetteer.Get(regionId);
        if (region == null) return null;

        // сервер ограничил частоту или недоступен — пока пауза, не шлём запросов и не копим очередь
        if (throttle.Blocked) return null;
        await throttle.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;
            if (throttle.Blocked) return null;

            var (minLat, minLon, maxLat, maxLon) = region.DefaultBox;
            var vb = string.Create(CultureInfo.InvariantCulture, $"{minLon},{maxLat},{maxLon},{minLat}");
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&accept-language=ru&countrycodes=ru,ua" +
                      $"&bounded=1&viewbox={vb}&q={Uri.EscapeDataString(query)}";
            GeoPoint? result = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    // 429 и 5xx — отступаем на минуту; остальные ошибки не кэшируем и не блокируем
                    if ((int)resp.StatusCode is 429 or >= 500) throttle.Block(TimeSpan.FromSeconds(60));
                    return null;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var e = doc.RootElement[0];
                    // неожиданный формат ответа — как временная ошибка (не кешируем)
                    if (!e.TryGetProperty("lat", out var la) || !e.TryGetProperty("lon", out var lo) ||
                        !double.TryParse(la.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                        !double.TryParse(lo.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                        return null;
                    result = new GeoPoint(lat, lon);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }

            _cache[key] = result;
            Append(key, result);
            return result;
        }
        finally
        {
            throttle.Exit();
        }
    }

    private static string Key(string query, string regionId) => regionId + "|" + Gazetteer.Normalize(query);

    private static ConcurrentDictionary<string, GeoPoint?> Load()
    {
        var dict = new ConcurrentDictionary<string, GeoPoint?>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(AppPaths.GeocodeCache)) return dict;
            foreach (var line in File.ReadLines(AppPaths.GeocodeCache))
            {
                var parts = line.Split('\t');
                if (parts.Length != 3) continue;
                if (parts[1].Length == 0) { dict[parts[0]] = null; continue; }
                // битая (оборванная) строка пропускается, а не роняет запуск
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    dict[parts[0]] = new GeoPoint(lat, lon);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return dict;
    }

    private void Append(string key, GeoPoint? p)
    {
        var line = p is { } g
            ? string.Create(CultureInfo.InvariantCulture, $"{key}\t{g.Lat}\t{g.Lon}\n")
            : $"{key}\t\t\n";
        lock (FileLock)
        {
            try { MessageCache.AppendLines(AppPaths.GeocodeCache, [line.TrimEnd('\n')]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Общий ограничитель: не более одного запроса к Nominatim в секунду.</summary>
public sealed class NominatimThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public async Task EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var wait = _last.AddMilliseconds(1100) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch { _gate.Release(); throw; }
        }
    }

    private long _blockedUntilTicks;

    /// <summary>Идёт пауза после ответа 429/5xx.</summary>
    public bool Blocked => DateTime.UtcNow.Ticks < Interlocked.Read(ref _blockedUntilTicks);

    public void Block(TimeSpan span) => Interlocked.Exchange(ref _blockedUntilTicks, DateTime.UtcNow.Add(span).Ticks);

    public void Exit()
    {
        _last = DateTime.UtcNow;
        _gate.Release();
    }
}

public static class UserAgent
{
    public const string Value = "Radar/1.0 (desktop alert map; +https://github.com/)";
}
