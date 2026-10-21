using System.Collections.Concurrent;
using System.Net;
using Atom.Net.Proxies;
using Atom.Web.Proxies;
using Atom.Web.Proxies.Services;

namespace Radar.Services;

/// <summary>Как ходить в Telegram: сам решает (напрямую, а при блокировке — через прокси), всегда через прокси, только напрямую.</summary>
public enum TelegramAccess { Auto = 0, Proxy = 1, Direct = 2 }

/// <summary>
/// Маршрут запросов к t.me. Там, где Telegram заблокирован (нет VPN), ленты каналов читаются через бесплатные прокси:
/// ротатор Atom собирает списки из открытых источников, кандидаты проверяются запросом к настоящей ленте канала,
/// рабочие держатся в пуле и чередуются между каналами; отказавший прокси выбывает, пул пополняется в фоне.
/// Проверенные адреса сохраняются — следующий запуск начинает с них, не дожидаясь поиска.
/// </summary>
public static class TelegramRoute
{
    private const string ProbeUrl = "https://t.me/s/telegram";
    private const int PoolTarget = 6;
    private static readonly TimeSpan DirectRecheck = TimeSpan.FromMinutes(10);

    public static TelegramAccess Mode { get; set; }

    /// <summary>Состояние для настроек: «напрямую», «через прокси …», «поиск прокси…».</summary>
    public static string Status { get; private set; } = "";
    public static event Action? StatusChanged;

    private static readonly List<Route> Pool = [];
    private static readonly ConcurrentDictionary<string, DateTime> Banned = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static int _next;
    private static DateTime _directBlockedUntil;
    private static Task? _search;
    private static bool _loaded;

    private sealed class Route(string address, HttpClient client)
    {
        public string Address { get; } = address;
        public HttpClient Client { get; } = client;
        public int Failures;
    }

    private static string StorePath => Path.Combine(AppPaths.Root, "proxies.txt");

    /// <summary>HTML страницы канала. Исключения — те же, что у прямого запроса (сеть, 429, «лента недоступна»).</summary>
    public static async Task<string> GetHtmlAsync(HttpClient direct, string url, Func<HttpClient, string, CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        var mode = Mode;
        if (mode == TelegramAccess.Direct || (mode == TelegramAccess.Auto && DateTime.UtcNow >= _directBlockedUntil))
        {
            try
            {
                var html = await fetch(direct, url, ct).ConfigureAwait(false);
                if (mode == TelegramAccess.Auto) SetStatus("напрямую");
                return html;
            }
            catch (Exception ex) when (mode == TelegramAccess.Auto && IsBlockage(ex, ct))
            {
                // один закрытый или удалённый канал тоже отдаёт страницу без ленты — блокировку подтверждает эталонный канал
                if (await DirectWorksAsync(direct, fetch, ct).ConfigureAwait(false)) throw;
                // t.me не отвечает или отдаёт заглушку — дальше через прокси; прямой путь перепроверяется раз в 10 минут
                _directBlockedUntil = DateTime.UtcNow + DirectRecheck;
            }
        }
        return await ViaProxyAsync(url, fetch, ct).ConfigureAwait(false);
    }

    private static DateTime _directCheckedAt;
    private static bool _directOk;

    private static async Task<bool> DirectWorksAsync(HttpClient direct, Func<HttpClient, string, CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        if (DateTime.UtcNow - _directCheckedAt < TimeSpan.FromMinutes(1)) return _directOk;
        try { await fetch(direct, ProbeUrl, ct).ConfigureAwait(false); _directOk = true; }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { _directOk = false; }
        _directCheckedAt = DateTime.UtcNow;
        return _directOk;
    }

    /// <summary>
    /// Запрос к другим серверам (GitHub: обновления и архив) с запасным путём: при сетевой ошибке напрямую —
    /// повтор через рабочие прокси того же пула (они проверены на TLS-туннель, годятся не только для t.me).
    /// Ответ сервера с кодом (404, 403…) — не сбой сети, прокси не пробуется.
    /// </summary>
    public static async Task<T> SendAsync<T>(HttpClient direct, Func<HttpClient, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        if (Mode != TelegramAccess.Proxy)
        {
            try { return await op(direct, ct).ConfigureAwait(false); }
            catch (Exception ex) when (Mode == TelegramAccess.Auto && IsNetworkFailure(ex, ct)) { }
        }
        EnsureLoaded();
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var route = Take();
            if (route == null)
            {
                await WaitForRouteAsync(ct).ConfigureAwait(false);
                route = Take();
                if (route == null) throw last ?? new HttpRequestException("сервер недоступен ни напрямую, ни через прокси");
            }
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromMinutes(2));
                return await op(route.Client, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct)) { last = ex; if (++route.Failures >= 2) Drop(route); }
        }
        throw last!;
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException { StatusCode: null } => true,
        IOException => true,
        _ => false,
    };

    private static bool IsBlockage(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.UnavailableForLegalReasons } => true,
        InvalidDataException => true,
        _ => false,
    };

    private static async Task<string> ViaProxyAsync(string url, Func<HttpClient, string, CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        EnsureLoaded();
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var route = Take();
            if (route == null)
            {
                SetStatus("поиск рабочих прокси…");
                await WaitForRouteAsync(ct).ConfigureAwait(false);
                route = Take();
                if (route == null) throw last ?? new HttpRequestException("нет доступа к Telegram: рабочие прокси не найдены");
            }
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(20));
                var html = await fetch(route.Client, url, limit.Token).ConfigureAwait(false);
                route.Failures = 0;
                SetStatus($"через прокси · рабочих {PoolCount}");
                return html;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                // прокси проверен на настоящей ленте: страница без ленты — это сам канал (закрыт, удалён), а не прокси
                if (ex is InvalidDataException) throw;
                // 429 — ограничение на адрес прокси: этот канал пока опрашиваем через другой
                if (ex is RetryAfterException) continue;
                if (++route.Failures >= 2) Drop(route);
            }
        }
        throw last!;
    }

    private static int PoolCount { get { lock (Gate) return Pool.Count; } }

    private static Route? Take()
    {
        lock (Gate)
        {
            if (Pool.Count < PoolTarget / 2) StartSearch();
            if (Pool.Count == 0) return null;
            return Pool[(int)((uint)Interlocked.Increment(ref _next) % (uint)Pool.Count)];
        }
    }

    private static void Drop(Route route)
    {
        lock (Gate)
        {
            if (!Pool.Remove(route)) return;
            Banned[route.Address] = DateTime.UtcNow;
            route.Client.Dispose();
            Save();
        }
    }

    private static async Task WaitForRouteAsync(CancellationToken ct)
    {
        Task? search;
        lock (Gate) { StartSearch(); search = _search; }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            lock (Gate) if (Pool.Count > 0) return;
            if (search is { IsCompleted: true }) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static void StartSearch()
    {
        if (_search is { IsCompleted: false }) return;
        _search = Task.Run(SearchAsync);
    }

    /// <summary>Кандидаты из ротатора проверяются параллельно настоящим запросом к ленте; поиск прекращается, как только пул полон.</summary>
    private static async Task SearchAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            using var factory = new ProxyFactory { RefreshInterval = TimeSpan.FromHours(1), RotationStrategy = ProxyRotationStrategy.Random };
            factory.Use(new ProxiflyProxyListProvider());
            factory.Use(new IplocateProxyListProvider());
            factory.Use(new OpenProxyListProvider());
            factory.Use(new VakhovProxyListProvider());
            factory.Use(new R00teeProxyListProvider());
            factory.Use(new ProxyScrapeProvider());
            var candidates = (await factory.GetAsync(1500, cts.Token).ConfigureAwait(false))
                .Select(ToAddress).Where(a => a != null).Select(a => a!)
                .Where(a => !Banned.TryGetValue(a, out var t) || DateTime.UtcNow - t > TimeSpan.FromHours(1))
                // HTTP(S)-прокси на практике чаще пропускают TLS к t.me, чем SOCKS из открытых списков
                .OrderBy(a => a.StartsWith("http", StringComparison.Ordinal) ? 0 : 1)
                .Distinct(StringComparer.Ordinal).ToArray();
            lock (Gate) candidates = candidates.Where(a => Pool.All(r => r.Address != a)).ToArray();

            using var limiter = new SemaphoreSlim(48);
            await Task.WhenAll(candidates.Select(async address =>
            {
                try { await limiter.WaitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    lock (Gate) if (Pool.Count >= PoolTarget) { cts.Cancel(); return; }
                    if (await ProbeAsync(address, cts.Token).ConfigureAwait(false) is { } route) Add(route);
                }
                finally { limiter.Release(); }
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("поиск прокси: " + ex.Message); }
        lock (Gate) if (Pool.Count == 0) SetStatus("рабочие прокси не найдены — повтор при следующем опросе");
    }

    private static string? ToAddress(ServiceProxy p)
    {
        if (string.IsNullOrEmpty(p.Host) || p.Port <= 0) return null;
        var scheme = p.Type switch
        {
            ProxyType.Socks4 => "socks4",
            ProxyType.Socks5 => "socks5",
            ProxyType.Http or ProxyType.Https => "http",
            _ => null,
        };
        return scheme == null ? null : $"{scheme}://{p.Host}:{p.Port}";
    }

    private static HttpClient CreateClient(string address) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri(address)),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
        }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<Route?> ProbeAsync(string address, CancellationToken ct)
    {
        var client = CreateClient(address);
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(12));
            using var req = new HttpRequestMessage(HttpMethod.Get, ProbeUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", TelegramSource.BrowserAgent);
            using var resp = await client.SendAsync(req, probe.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false);
                // заглушки и подменяющие прокси отдают 200 без ленты — такие не годятся
                if (html.Contains("tgme_channel_history", StringComparison.Ordinal)) return new Route(address, client);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or NotSupportedException) { }
        client.Dispose();
        return null;
    }

    private static void Add(Route route)
    {
        lock (Gate)
        {
            if (Pool.Count >= PoolTarget || Pool.Any(r => r.Address == route.Address)) { route.Client.Dispose(); return; }
            Pool.Add(route);
            Save();
        }
    }

    /// <summary>Сохранённые с прошлого запуска адреса — в пул сразу; отказавшие выбудут при первом же запросе.</summary>
    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(StorePath)) return;
                foreach (var line in File.ReadAllLines(StorePath).Select(l => l.Trim()).Where(l => l.Length > 0).Take(PoolTarget))
                    if (Uri.TryCreate(line, UriKind.Absolute, out _)) Pool.Add(new Route(line, CreateClient(line)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Save()
    {
        try { File.WriteAllLines(StorePath, Pool.Select(r => r.Address)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SetStatus(string s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke();
    }

    /// <summary>Смена режима: прямой путь снова пробуется сразу.</summary>
    public static void Reset()
    {
        _directBlockedUntil = default;
        SetStatus(Mode == TelegramAccess.Direct ? "напрямую" : "");
    }
}
