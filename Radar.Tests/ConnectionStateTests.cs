using System.Net;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Индикатор связи: «отключено», когда ни один канал не отдаёт ленту (нет сети, заглушка, блокировка).</summary>
public class ConnectionStateTests
{
    private const string Feed = """
        <section class="tgme_channel_history js-message_history">
        <div class="tgme_widget_message_wrap"><div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="a/10">
        <div class="tgme_widget_message_text js-message_text" dir="auto">Белгород — отбой</div>
        <time datetime="2026-09-01T12:00:00+00:00" class="time">12:00</time></div></div>
        </section>
        """;

    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Respond());
    }

    private static HttpResponseMessage Ok(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    private static (AlertEngine Engine, FakeHandler Handler, List<EngineStatus> Statuses) Make(Func<HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        var settings = new AppSettings { OnlineGeocoding = false, Channels = [new() { Name = "a" }, new() { Name = "b" }] };
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var statuses = new List<EngineStatus>();
        engine.StatusChanged += s => { lock (statuses) statuses.Add(s); };
        return (engine, handler, statuses);
    }

    [Fact]
    public void InitiallyConnecting()
    {
        var (engine, _, _) = Make(() => Ok(Feed));
        Assert.Equal(ConnectionState.Connecting, engine.Connection);
    }

    [Fact]
    public async Task NoNetworkIsOffline()
    {
        var (engine, _, statuses) = Make(() => throw new HttpRequestException("Name or service not known"));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Offline, engine.Connection);
        Assert.Equal(ConnectionState.Offline, statuses[^1].Connection);
        Assert.Null(statuses[^1].LastSuccess);
    }

    [Fact]
    public async Task CaptivePortalOrBlockPageIsOffline()
    {
        // HTTP 200, но вместо ленты канала — страница провайдера / Wi-Fi авторизации
        var (engine, _, _) = Make(() => Ok("<html><body>Доступ ограничен</body></html>"));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Offline, engine.Connection);
    }

    [Fact]
    public async Task RateLimitIsNotLossOfNetwork()
    {
        // 429: сервер ответил, сеть есть — это ограничение частоты, а не «ОТКЛЮЧЕНО»
        var (engine, _, _) = Make(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Online, engine.Connection);
    }

    [Fact]
    public async Task BadGatewayIsOffline()
    {
        var (engine, _, _) = Make(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Offline, engine.Connection);
    }

    [Fact]
    public async Task OnlineThenNetworkLostThenRestored()
    {
        var (engine, handler, statuses) = Make(() => Ok(Feed));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Online, engine.Connection);
        Assert.NotNull(statuses[^1].LastSuccess);

        handler.Respond = () => throw new HttpRequestException("Network is unreachable");
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Offline, engine.Connection);

        handler.Respond = () => Ok(Feed);
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Online, engine.Connection);
    }

    [Fact]
    public async Task OneChannelAliveIsStillOnline()
    {
        var (engine, handler, _) = Make(() => Ok(Feed));
        handler.Respond = () => throw new HttpRequestException("x");
        await engine.PollOnceAsync(CancellationToken.None);
        handler.Respond = () => Ok(Feed);
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Online, engine.Connection);
    }

    [Fact]
    public async Task InFlightReportedDuringFetch()
    {
        var (engine, _, statuses) = Make(() => Ok(Feed));
        await engine.PollOnceAsync(CancellationToken.None);
        Assert.Contains(statuses, s => s.InFlight > 0);
        Assert.Equal(0, statuses[^1].InFlight);
    }
}
