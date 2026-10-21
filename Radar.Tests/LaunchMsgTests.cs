using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Пуск, названный областью противника, при тревогах в РФ; итоговые сводки ПВО не поднимают уровень.</summary>
[Collection("Engine")]
public class LaunchMsgTests : IDisposable
{
    // прогон движка оставляет модельные часы — остальным тестам нужно реальное время
    public void Dispose() => Clock.StopSimulation();

    private const string LaunchText = "Предварительно противник осуществляется запуск БПЛА от Днепропетровской области на восток, северо-восток в направлении ЛНР.\nРостовская область - опасность по БПЛА.\nБелгородская область, Воронежская область - внимание по БПЛА от ЛНР.\n📡Локатор России - @locatorru";
    private const string Summary1 = "С 08:00 до 20:00 силами противовоздушной обороны было уничтожено 343 БПЛА над территориями Белгородской, Брянской, Калужской, Курской, Орловской, Тульской областей, Московского региона, Краснодарского края, Ставропольского края, Республики Крым и над акваторией Черного моря.";
    private const string Summary2 = "⚡️ В течение дня в период с 8.00 до 20.00 мск дежурными силами ПВО перехвачены и уничтожены 343 украинских беспилотных летательных аппарата самолетного типа над территориями Белгородской, Брянской, Калужской, Курской, Орловской, Тульской областей, Московского региона, Краснодарского края, Ставропольского края, Республики Крым и над акваторией Черного моря.";

    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void LaunchFromEnemyRegion_GetsSite_AndRegionNotUnderAlert()
    {
        var p = MessageParser.Parse(new RawMessage("locatorru", 1, T0, LaunchText), []);
        Assert.Equal(AlertStatus.Alert, p.Status);
        Assert.Equal("ua_dnipro", p.LaunchSite?.RegionId);
        Assert.DoesNotContain("ua_dnipro", p.Regions);
        Assert.Contains("rostov", p.Regions);
    }

    [Theory]
    [InlineData(Summary1)]
    [InlineData(Summary2)]
    public void DailyAirDefenseSummary_IsInformational(string text)
    {
        var p = MessageParser.Parse(new RawMessage("vrv_radar", 1, T0, text), []);
        Assert.True(p.IsInformational);
    }

    [Fact]
    public void Summaries_DoNotTurnLaunchIntoStrike()
    {
        using var http = new HttpClient();
        var engine = new AlertEngine(new AppSettings { OnlineGeocoding = false }, http, new Geocoder(http, new NominatimThrottle()));
        var msgs = new List<RawMessage>
        {
            new("lpr1_Rostov_alarm", 1, T0, "Север Ростовской области\nОпасность по БПЛА"),
            new("locatorru", 2, T0.AddMinutes(15), Summary1),
            new("vrv_radar", 3, T0.AddMinutes(16), Summary2),
            new("locatorru", 4, T0.AddMinutes(17), LaunchText),
        };
        foreach (var _ in engine.ReplaySync(msgs)) { }
        var evs = engine.Snapshot();
        Assert.DoesNotContain(evs, e => e.EffectiveLevel == AlertLevel.Strike);
        var launch = Assert.Single(evs, e => e.Status == AlertStatus.Launch);
        Assert.Contains("ua_dnipro", launch.RegionIds);
        Assert.NotNull(launch.Target);
        Assert.DoesNotContain(evs, e => e.Status != AlertStatus.Launch && e.RegionIds.Contains("ua_dnipro"));
    }

    [Fact]
    public void PerLineLevels_SplitIntoSeparateAlerts()
    {
        using var http = new HttpClient();
        var engine = new AlertEngine(new AppSettings { OnlineGeocoding = false }, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in engine.ReplaySync([new RawMessage("locatorru", 4, T0, LaunchText)])) { }
        var evs = engine.Snapshot().Where(e => e.Status == AlertStatus.Alert).ToList();
        var main = Assert.Single(evs, e => e.RegionIds.Contains("rostov"));
        var att = Assert.Single(evs, e => e.RegionIds.Contains("belgorod"));
        Assert.Equal(AlertLevel.Danger, main.Level);
        Assert.Equal(AlertLevel.Attention, att.Level);
        Assert.Contains("voronezh", att.RegionIds);
        Assert.DoesNotContain("belgorod", main.RegionIds);
    }

    [Fact]
    public void SingleLevelMessage_NotSplit()
    {
        var p = MessageParser.Parse(new RawMessage("x", 1, T0, "Курская область, Белгородская область - опасность по БПЛА"), []);
        Assert.Empty(p.RegionLevels);
    }

    [Fact]
    public void CityDistrict_NotFarNamesakeVillage()
    {
        // «Советский» рядом с Сельцо и Жуковкой — Советский район Брянска, а не посёлок у Погара за 100 км
        var p = MessageParser.Parse(new RawMessage("lpr1_Bryansk_alarm", 1, T0, "Советский и далее на Сельцо, Отрадное, Жуковка\nОпасность по БПЛА\nБрянская область"), []);
        var s = p.Places.First(x => x.Label.StartsWith("Советск"));
        Assert.True(Geo.DistanceKm(s.Pos!.Value, new GeoPoint(53.24, 34.35)) < 15);
    }

    [Theory]
    [InlineData("Коминтерновский район\nТревога по БПЛА\nВоронежская область", "voronezh", 51.67, 39.20)]
    [InlineData("Карасунский район\nОпасность по БПЛА\nКраснодарский край", "krasnodar", 45.04, 38.98)]
    [InlineData("Сеймский район\nТревога по БПЛА\nКурская область", "kursk", 51.73, 36.19)]
    public void CityDistrict_OfRegionalCapital(string text, string region, double lat, double lon)
    {
        var p = MessageParser.Parse(new RawMessage("x", 1, T0, text), []);
        var s = p.Places.First(x => x.RegionId == region && x.Pos != null);
        Assert.True(Geo.DistanceKm(s.Pos!.Value, new GeoPoint(lat, lon)) < 15);
    }
}
