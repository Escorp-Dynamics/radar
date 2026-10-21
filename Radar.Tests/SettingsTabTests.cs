using System.Net;
using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Вкладка «Настройки» и таблица источников: звуковые сигналы и включение/выключение каналов на ходу.</summary>
[Collection("Engine")]
public class SettingsTabTests
{
    [Theory]
    [InlineData(Chime.Alert, 0)]
    [InlineData(Chime.Urgent, 2)]
    [InlineData(Chime.Lost, 1)]
    [InlineData(Chime.Restored, 1)]
    public void ChimeIsValidPcmWav(Chime chime, int volume)
    {
        var wav = Notifier.Synthesize(chime, volume);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        int data = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, data);
        // длительность разумная (0.2…1 с при 22050 Гц, 16 бит моно)
        double seconds = data / 2.0 / 22050;
        Assert.InRange(seconds, 0.2, 1.0);
    }

    [Fact]
    public void LouderVolumeHasHigherPeak()
    {
        static int Peak(byte[] w) { int m = 0; for (int i = 44; i + 1 < w.Length; i += 2) m = Math.Max(m, Math.Abs((int)BitConverter.ToInt16(w, i))); return m; }
        Assert.True(Peak(Notifier.Synthesize(Chime.Alert, 2)) > Peak(Notifier.Synthesize(Chime.Alert, 0)));
    }

    [Theory]
    [InlineData(Chime.Alert)]
    [InlineData(Chime.Urgent)]
    [InlineData(Chime.Lost)]
    public void ChimeIsSoft(Chime chime)
    {
        // даже на максимальной громкости — не больше трети шкалы, без писка (переходов через ноль — как у тона ниже 1 кГц)
        var w = Notifier.Synthesize(chime, 2);
        int peak = 0, crossings = 0, prev = 0, n = 0;
        for (int i = 44; i + 1 < w.Length; i += 2, n++)
        {
            int v = BitConverter.ToInt16(w, i);
            peak = Math.Max(peak, Math.Abs(v));
            if (prev < 0 && v >= 0) crossings++;
            if (v != 0) prev = v;
        }
        Assert.InRange(peak, 1000, short.MaxValue / 3);
        Assert.True(crossings / (n / 22050.0) < 1000, $"{crossings / (n / 22050.0):0} Гц");
    }

    private const string Feed = """
        <section class="tgme_channel_history js-message_history">
        <div class="tgme_widget_message_wrap"><div class="tgme_widget_message text_not_supported_wrap js-widget_message" data-post="a/10">
        <div class="tgme_widget_message_text js-message_text" dir="auto">Белгород — отбой</div>
        <time datetime="2026-09-01T12:00:00+00:00" class="time">12:00</time></div></div>
        </section>
        """;

    private sealed class CountingHandler : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) });
        }
    }

    [Fact]
    public async Task DisabledChannelIsNotPolledAndReportedDisabled()
    {
        var handler = new CountingHandler();
        var http = new HttpClient(handler);
        var settings = new AppSettings { OnlineGeocoding = false, Channels = [new() { Name = "a" }, new() { Name = "b" }] };
        using var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var statuses = new List<EngineStatus>();
        engine.StatusChanged += s => { lock (statuses) statuses.Add(s); };

        engine.SetChannelEnabled("b", false);
        Assert.False(settings.Channels[1].Enabled);
        lock (statuses) Assert.False(statuses[^1].Channels.Single(c => c.Name == "b").Enabled);

        lock (handler.Urls) handler.Urls.Clear();
        await engine.PollOnceAsync(CancellationToken.None);
        lock (handler.Urls)
        {
            Assert.Contains(handler.Urls, u => u.Contains("/s/a", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Urls, u => u.Contains("/s/b", StringComparison.Ordinal));
        }
        // выключенный канал не портит индикатор связи
        Assert.Equal(ConnectionState.Online, engine.Connection);

        engine.SetChannelEnabled("b", true);
        Assert.True(settings.Channels[1].Enabled);
        lock (handler.Urls) handler.Urls.Clear();
        await engine.PollOnceAsync(CancellationToken.None);
        lock (handler.Urls) Assert.Contains(handler.Urls, u => u.Contains("/s/b", StringComparison.Ordinal));
    }

    [Fact]
    public void LifetimeChangeShiftsLiveAlerts()
    {
        var http = new HttpClient(new CountingHandler());
        var settings = new AppSettings { OnlineGeocoding = false, AlertLifetimeMinutes = 45, Channels = [new() { Name = "a", Regions = ["Белгородская область"] }] };
        using var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var t = DateTimeOffset.UtcNow.AddMinutes(-40);
        engine.ApplyBatch([new RawMessage("a", 1, t, "Белгородская область — угроза атаки БПЛА")]);
        var ev = Assert.Single(engine.Snapshot(), e => e.Status != AlertStatus.AllClear);
        Assert.Equal(t.AddMinutes(45), ev.ExpiresAt);

        // увеличение: тревога живёт дольше
        engine.SetLifetimes(90, settings.AllClearLifetimeMinutes);
        Assert.Equal(90, settings.AlertLifetimeMinutes);
        Assert.Equal(t.AddMinutes(90), ev.ExpiresAt);

        // уменьшение до 30 мин: тревоге 40 мин — она сразу снимается
        engine.SetLifetimes(30, settings.AllClearLifetimeMinutes);
        Assert.DoesNotContain(engine.Snapshot(), e => e.Status != AlertStatus.AllClear);
    }

    [Fact]
    public void NotifyRegionsSettingRoundTrips()
    {
        var s = new AppSettings { NotifyRegions = ["belgorod", "crimea"] };
        var json = System.Text.Json.JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(["belgorod", "crimea"], back.NotifyRegions);
        Assert.Empty(new AppSettings().NotifyRegions);
    }
}
