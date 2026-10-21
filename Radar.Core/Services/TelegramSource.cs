using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Читает публичные Telegram-каналы через веб-превью https://t.me/s/{channel}
/// (без авторизации и API-ключей).
/// </summary>
public sealed partial class TelegramSource(HttpClient http)
{
    /// <summary>
    /// История канала с конца до момента <paramref name="since"/> — постранично (<c>?before=</c>), не более <paramref name="maxPages"/> страниц.
    /// </summary>
    /// <remarks>Если задан <paramref name="afterId"/>, листание останавливается, дойдя до уже известного сообщения (дельта).</remarks>
    public async Task<List<RawMessage>> FetchHistoryAsync(string channel, DateTimeOffset since, int maxPages, CancellationToken ct, long afterId = 0, bool failOnPartial = false)
    {
        var all = new Dictionary<long, RawMessage>();
        long? before = null;
        for (int page = 0; page < maxPages; page++)
        {
            string html;
            try { html = await FetchHtmlAsync(channel, before, ct).ConfigureAwait(false); }
            catch (Exception ex) when (page > 0 && !failOnPartial && !ct.IsCancellationRequested && ex is not RetryAfterException) { break; } // глубже не получилось — берём что есть
            var msgs = ParseHtml(channel, html);
            foreach (var m in msgs) all.TryAdd(m.Id, m);
            // id всех постов страницы (включая медиа без текста) — для перехода на предыдущую
            var ids = MessageStart().Matches(html).Select(x => x.Groups["post"].Value)
                .Select(v => long.TryParse(v.AsSpan(v.LastIndexOf('/') + 1), out var id) ? id : 0).Where(id => id > 0).ToList();
            if (ids.Count == 0) break;
            long oldest = ids.Min();
            if (oldest <= 1 || oldest <= afterId || (before is { } b && oldest >= b)) break;
            if (msgs.Count > 0 && msgs.Min(m => m.Time) < since) break;
            before = oldest;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        return [.. all.Values.OrderBy(m => m.Id)];
    }

    /// <summary>Одна страница ленты (до 20 постов раньше <paramref name="before"/>): сообщения с текстом и id всех постов страницы.</summary>
    public async Task<(List<RawMessage> Msgs, List<long> Ids)> FetchPageAsync(string channel, long? before, CancellationToken ct)
    {
        var html = await FetchHtmlAsync(channel, before, ct).ConfigureAwait(false);
        var ids = MessageStart().Matches(html).Select(x => x.Groups["post"].Value)
            .Select(v => long.TryParse(v.AsSpan(v.LastIndexOf('/') + 1), out var id) ? id : 0).Where(id => id > 0).ToList();
        return (ParseHtml(channel, html), ids);
    }

    public static bool SimulateOffline { get; set; } =
        Environment.GetEnvironmentVariable("RADAR_SIMULATE_OFFLINE") == "1" || Environment.GetCommandLineArgs().Contains("--simulate-offline");

    public async Task<List<RawMessage>> FetchAsync(string channel, CancellationToken ct) =>
        ParseHtml(channel, await FetchHtmlAsync(channel, null, ct).ConfigureAwait(false));

    public const string BrowserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private Task<string> FetchHtmlAsync(string channel, long? before, CancellationToken ct)
    {
        var url = $"https://t.me/s/{Uri.EscapeDataString(channel)}" + (before is { } b ? $"?before={b}" : "");
        // эмуляция отсутствия сети для проверки интерфейса: --simulate-offline или RADAR_SIMULATE_OFFLINE=1
        if (SimulateOffline) throw new HttpRequestException("эмуляция: нет сети", new System.Net.Sockets.SocketException(101));
        // напрямую, а где t.me заблокирован — через проверенные прокси (см. TelegramRoute)
        return TelegramRoute.GetHtmlAsync(http, url, LoadAsync, ct);
    }

    private static async Task<string> LoadAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        // 429 без Retry-After тоже ограничение частоты: пауза по умолчанию, а не частые повторы, продлевающие блокировку
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RetryAfterException(resp.Headers.RetryAfter is { } ra
                ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60))
                : TimeSpan.FromSeconds(60));
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // ответ 200 без ленты канала: заглушка провайдера/Wi-Fi, блокировка, канал удалён или закрыт —
        // это не «новых сообщений нет», а отсутствие данных
        if (!html.Contains("tgme_channel_history", StringComparison.Ordinal))
            throw new InvalidDataException("лента канала недоступна");
        return html;
    }

    public static List<RawMessage> ParseHtml(string channel, string html)
    {
        var result = new List<RawMessage>();
        var starts = MessageStart().Matches(html);
        for (int i = 0; i < starts.Count; i++)
        {
            var m = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
            var block = html.AsSpan(m.Index, end - m.Index).ToString();

            var post = m.Groups["post"].Value;
            var slash = post.LastIndexOf('/');
            if (slash < 0 || !long.TryParse(post.AsSpan(slash + 1), out var id)) continue;

            var textMatch = TextBlock().Match(block);
            if (!textMatch.Success) continue;
            var text = HtmlToText(textMatch.Groups["t"].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // без времени пост не датировать «сейчас»: старое сообщение стало бы свежей тревогой
            var timeMatch = TimeAttr().Match(block);
            if (!timeMatch.Success || !DateTimeOffset.TryParse(timeMatch.Groups["d"].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) continue;

            result.Add(new RawMessage(channel, id, time, Gazetteer.StripMarks(text)));
        }
        return result;
    }

    private static string HtmlToText(string html)
    {
        var s = LineBreak().Replace(html, "\n");
        s = Tag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        var sb = new StringBuilder();
        foreach (var line in s.Split('\n'))
        {
            var l = Spaces().Replace(line, " ").Trim();
            if (l.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
        }
        return sb.ToString();
    }

    [GeneratedRegex("<div class=\"tgme_widget_message (?:text_not_supported_wrap )?js-widget_message\"[^>]*data-post=\"(?<post>[^\"]+)\"")]
    private static partial Regex MessageStart();

    [GeneratedRegex("<div class=\"tgme_widget_message_text js-message_text\"[^>]*>(?<t>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex TextBlock();

    [GeneratedRegex("<time[^>]*datetime=\"(?<d>[^\"]+)\"")]
    private static partial Regex TimeAttr();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();
}

/// <summary>429 с указанием сервера, через сколько повторять.</summary>
public sealed class RetryAfterException(TimeSpan delay)
    : HttpRequestException("Telegram ограничил частоту запросов", null, System.Net.HttpStatusCode.TooManyRequests)
{
    public TimeSpan Delay { get; } = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : delay;
}
