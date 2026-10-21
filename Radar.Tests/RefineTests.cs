using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Разные каналы об одном и том же — следующее сообщение уточняет тревогу, а не дублирует её.</summary>
[Collection("Engine")]
public class RefineTests
{
    private static List<AlertEvent> Run(params (string Ch, string Text)[] items)
    {
        var settings = new AppSettings { Channels = [new ChannelConfig { Name = "a" }, new ChannelConfig { Name = "b" }] };
        using var http = new HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var t0 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var msgs = items.Select((t, i) => new RawMessage(t.Ch, i + 1, t0.AddMinutes(i), t.Text.Replace(" | ", "\n"))).ToList();
        foreach (var _ in engine.ReplaySync(msgs)) { }
        return engine.Snapshot().Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > t0.AddMinutes(items.Length)).ToList();
    }

    [Fact]
    public void GeneralThenUavIsOneUavAlert()
    {
        var active = Run(("b", "Миллерово | Ростовская область"), ("a", "Миллерово опасность по БПЛА | Ростовская область"));
        var e = Assert.Single(active);
        Assert.Equal(ThreatType.Uav, e.Threat);
        Assert.Equal(2, e.Sources.Count);
    }

    [Fact]
    public void UavThenGeneralKeepsUav()
    {
        var active = Run(("a", "Миллерово опасность по БПЛА | Ростовская область"), ("b", "Миллерово | Ростовская область"));
        var e = Assert.Single(active);
        Assert.Equal(ThreatType.Uav, e.Threat);
    }

    [Fact]
    public void AirDefenseRefinesAlert()
    {
        var active = Run(("a", "Алексин | Тульская область | Опасность по БПЛА"), ("b", "Алексин | Тульская область | Работа ПВО по БПЛА"));
        var e = Assert.Single(active);
        Assert.Equal(AlertStatus.AirDefense, e.Status);
        // статус не ослабевает обратно
        active = Run(("a", "Алексин | Тульская область | Работа ПВО по БПЛА"), ("b", "Алексин | Тульская область | Опасность по БПЛА"));
        Assert.Equal(AlertStatus.AirDefense, Assert.Single(active).Status);
    }

    [Fact]
    public void WiderRegionMergesWithCity()
    {
        var active = Run(("a", "Севастополь | Тревога по БПЛА"), ("b", "Республика Крым | Севастополь | Тревога по БПЛА"));
        var e = Assert.Single(active);
        Assert.Contains("crimea", e.RegionIds);
        Assert.Contains("crimea", e.WholeRegionIds);
    }

    [Fact]
    public void PlaceIsMarkedByOneAlertOnly()
    {
        // «Туапсе» в разных сводках: отдельно и вместе с Сочи/Адлером — на карте одна отметка
        var active = Run(
            ("a", "Сочи | Адлер | Туапсе | И близлежащие | Тревога по БПЛА сохраняется | Краснодарский край"),
            ("b", "Туапсе меры безопасности при БПЛА"),
            ("a", "Туапсе и близлежащие | Тревога по БПЛА сохраняется | Краснодарский край"));
        Assert.Single(active, e => e.Points.Any(p => p.Label == "Туапсе"));
        Assert.Contains(active, e => e.Points.Any(p => p.Label == "Сочи"));
    }

    [Fact]
    public void DifferentPlacesStaySeparate()
    {
        var active = Run(("a", "Миллерово опасность по БПЛА | Ростовская область"), ("b", "Таганрог опасность по БПЛА | Ростовская область"));
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public void DifferentThreatGroupsStaySeparate()
    {
        var active = Run(("a", "Брянская область | Опасность по БПЛА"), ("b", "Брянская область | Ракетная опасность"));
        Assert.Equal(2, active.Count);
    }

    [Theory]
    [InlineData("Движение автотранспорта по Крымскому мосту возобновлено. | Чтобы получать информацию без задержек, подписывайтесь на официальный канал в MAX «КРЫМСКИЙ МОСТ: оперативная информация».")]
    [InlineData("Опасность по БПЛА | ❗️Радар Москва - @radar_moscoww | 🌐 Обход белых списков - @Internet_Boost_bot")]
    [InlineData("Миллерово | наблюдатель.рф | подписаться")]
    public void PromoLinesAreHidden(string text)
    {
        var p = MessageParser.Parse(new RawMessage("a", 1, DateTimeOffset.UtcNow, text.Replace(" | ", "\n")), []);
        Assert.DoesNotContain("подпис", p.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", p.DisplayText);
        Assert.NotEmpty(p.DisplayText);
    }

    [Fact]
    public void MissileReportsWithSharedPlaceMerge()
    {
        var active = Run(("a", "Ракетная опасность | Геническ, Джанкой, Керчь"), ("b", "Ракетная опасность | Каховка, Геническ, Мелитополь"));
        var e = Assert.Single(active);
        Assert.Equal(ThreatType.Missile, e.Threat);
        foreach (var p in new[] { "Джанкой", "Керчь", "Каховка", "Мелитополь" })
            Assert.Contains(e.Points, x => x.Label == p);
        Assert.Equal(2, e.Sources.Count);
    }

    [Fact]
    public void HistoryRecordsWhoAddedWhat()
    {
        var active = Run(("b", "Миллерово | Ростовская область"), ("a", "Миллерово опасность по БПЛА | Ростовская область"),
                         ("a", "Ракетная опасность | Геническ, Джанкой"), ("b", "Ракетная опасность | Геническ, Мелитополь"));
        var uav = Assert.Single(active, e => e.Threat == ThreatType.Uav);
        Assert.Equal(2, uav.History.Count);
        Assert.Equal("b", uav.History[0].Channel);
        Assert.Contains("тип", uav.History[1].Text);
        var mis = Assert.Single(active, e => e.Threat == ThreatType.Missile);
        Assert.Equal("b", mis.History[^1].Channel);
        Assert.Contains("Мелитополь", mis.History[^1].Text);
        Assert.DoesNotContain("Геническ", mis.History[^1].Text);
    }

    [Fact]
    public void MissileWholeRegionAbsorbsPlaceInside()
    {
        var active = Run(("a", "Ракетная опасность | Орловская область"), ("b", "Ракетная опасность | Орёл"));
        var e = Assert.Single(active);
        Assert.Contains("orel", e.WholeRegionIds);
    }

    [Fact]
    public void DifferentMissileTypesStaySeparate()
    {
        var active = Run(("a", "Опасность баллистических ракет | Белгород"), ("b", "Опасность крылатых ракет | Белгород"));
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public void RepostWithTypoIsOneAlert()
    {
        var active = Run(("a", "МО Серебрянные пруды | Московская область | Фиксация от 2 БПЛА"),
                         ("b", "МО Серебряные Пруды | Московская область | Фиксация от 2 БПЛА | ❗️Радар по всей России - @radarrussiia | 📲 Наш канал в МАХ - | https://max.ru/join/radarrussiia"));
        var e = Assert.Single(active);
        Assert.Equal(2, e.Sources.Count);
        Assert.Contains(e.Points, p => p.Label == "Серебряные Пруды");
    }

    [Fact]
    public void AlertsNarrowedToSameAreaMerge()
    {
        // два канала объявили разные наборы регионов; после частичного отбоя у обоих осталась ЛДНР — одна тревога
        var active = Run(("a", "Херсонская область РФ | Запорожская область РФ | ЛДНР | Авиационная ракетно бомбовая опасность"),
                         ("b", "Запорожская область РФ | ЛДНР | Авиационная ракетно бомбовая опасность"),
                         ("a", "Херсонская область РФ | Запорожская область РФ | Отбой авиационной ракетно бомбовой опасности"));
        var e = Assert.Single(active);
        Assert.Equal(2, e.Sources.Count);
        Assert.Contains("lnr", e.RegionIds);
        Assert.Contains("dnr", e.RegionIds);
    }

    [Fact]
    public void CruiseAndAntiShipWaveMerges()
    {
        var active = Run(("a", "Опасность крылатых ракет | Геническ, Джанкой, Керчь"), ("b", "Ракетная опасность по ПКР Нептун | Каховка, Геническ, Мелитополь"));
        var e = Assert.Single(active);
        Assert.Contains(ThreatKind.MissileAntiShip, e.Kinds);
        Assert.Contains(e.Points, x => x.Label == "Мелитополь");
    }

    [Fact]
    public void MissileDifferentPlacesStaySeparate()
    {
        // «Белгород … потенциальная опасность для Курской области» не поглощает тревогу по Рыльскому району
        var active = Run(("a", "Ракетная опасность на Крупецкую зону Рыльского района | Курская область"),
                         ("b", "Белгород, Белгородский район! | Ракетная опасность! | Потенциальная опасность для Курской области"));
        Assert.Equal(2, active.Count);
    }

    [Theory]
    [InlineData("Пока по обстановке тихо, в случае появления угрозы незамедлительно сообщим\n❗️Радар Саратов - @rada_saratov\n📲 Наш канал в МАХ -\nhttps://max.ru/join/radarrussiia")]
    [InlineData("Пока по обстановке тихо, в случае появления угрозы незамедлительно сообщим")]
    [InlineData("На данный момент угрозы нет, в случае возникновения опасности сообщим")]
    public void CalmStatusIsNotSignal(string text)
    {
        var p = MessageParser.Parse(new RawMessage("rada_saratov", 1, DateTimeOffset.UtcNow, text), ["saratov"]);
        Assert.True(p.IsJunk || p.IsInformational || p.Threat == ThreatType.None, $"{p.Threat} {p.Status} junk={p.IsJunk}");
        Assert.DoesNotContain("max.ru", p.DisplayText);
    }
}
