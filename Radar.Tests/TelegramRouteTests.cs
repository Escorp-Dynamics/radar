using Radar.Services;
using Xunit;

namespace Radar.Tests;

[Collection("TelegramRoute")]
public class TelegramRouteTests
{
    private static Func<HttpClient, string, CancellationToken, Task<string>> Fetch(Func<string, string> answer, List<string> calls) =>
        (_, url, _) => { lock (calls) calls.Add(url); return Task.FromResult(answer(url)); };

    [Fact]
    public async Task Auto_DirectWorks_NoProxy()
    {
        TelegramRoute.Mode = TelegramAccess.Auto;
        TelegramRoute.Reset();
        var calls = new List<string>();
        using var http = new HttpClient();
        var html = await TelegramRoute.GetHtmlAsync(http, "https://t.me/s/a", Fetch(_ => "ok", calls), CancellationToken.None);
        Assert.Equal("ok", html);
        Assert.Equal(["https://t.me/s/a"], calls);
        Assert.Equal("напрямую", TelegramRoute.Status);
    }

    [Fact]
    public async Task Auto_ClosedChannel_NotTreatedAsBlockage()
    {
        // страница одного канала без ленты, а эталонный канал открывается — значит, закрыт сам канал, прокси не нужен
        TelegramRoute.Mode = TelegramAccess.Auto;
        TelegramRoute.Reset();
        var calls = new List<string>();
        using var http = new HttpClient();
        var fetch = Fetch(url => url.EndsWith("/closed", StringComparison.Ordinal) ? throw new InvalidDataException("лента канала недоступна") : "ok", calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => TelegramRoute.GetHtmlAsync(http, "https://t.me/s/closed", fetch, CancellationToken.None));
        var html = await TelegramRoute.GetHtmlAsync(http, "https://t.me/s/b", fetch, CancellationToken.None);
        Assert.Equal("ok", html);
        Assert.Contains("https://t.me/s/b", calls);
    }

    [Fact]
    public async Task Direct_ErrorsPropagate_NoProxyFallback()
    {
        TelegramRoute.Mode = TelegramAccess.Direct;
        TelegramRoute.Reset();
        var calls = new List<string>();
        using var http = new HttpClient();
        var fetch = Fetch(_ => throw new HttpRequestException("нет связи"), calls);
        await Assert.ThrowsAsync<HttpRequestException>(() => TelegramRoute.GetHtmlAsync(http, "https://t.me/s/a", fetch, CancellationToken.None));
        Assert.Single(calls);
        TelegramRoute.Mode = TelegramAccess.Auto;
        TelegramRoute.Reset();
    }

    [Fact]
    public async Task RateLimit_IsNotBlockage()
    {
        // 429 — ограничение частоты, а не блокировка: в авто-режиме прокси не включается, пауза остаётся за движком
        TelegramRoute.Mode = TelegramAccess.Auto;
        TelegramRoute.Reset();
        var calls = new List<string>();
        using var http = new HttpClient();
        var fetch = Fetch(_ => throw new RetryAfterException(TimeSpan.FromSeconds(30)), calls);
        await Assert.ThrowsAsync<RetryAfterException>(() => TelegramRoute.GetHtmlAsync(http, "https://t.me/s/a", fetch, CancellationToken.None));
        Assert.Single(calls);
    }
}
