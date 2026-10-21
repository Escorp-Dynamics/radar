using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

[Collection("Engine")]
/// <summary>История за сутки при запуске применяется по времени сообщений — как если бы приложение работало всё это время.</summary>
public class HistoryLoadTests
{
    private static AlertEngine NewEngine() =>
        new(new AppSettings { OnlineGeocoding = false }, new HttpClient(), new Geocoder(new HttpClient(), new NominatimThrottle()));

    private static RawMessage Msg(string ch, long id, DateTimeOffset t, string text) => new(ch, id, t, text);

    [Fact]
    public void TrackStartsAtFirstHistoricalFix()
    {
        // объект появился у границы 30 мин назад и идёт вглубь — трек должен начинаться у Суджи, а не с последней отметки
        var now = Clock.Now;
        var e = NewEngine();
        e.ApplyBatch([
            Msg("lpr1_Kursk_alarm", 1, now.AddMinutes(-30), "Суджа\nТревога по БПЛА\nКурская область"),
            Msg("lpr1_Kursk_alarm", 2, now.AddMinutes(-15), "Курчатов\nТревога по БПЛА\nКурская область"),
            Msg("lpr1_Kursk_alarm", 3, now.AddMinutes(-1), "Курск\nТревога по БПЛА\nКурская область"),
        ]);
        var t = Assert.Single(e.TrackSnapshot(), t => t.Points.Count >= 2);
        Assert.Equal("Суджа", t.Points[0].Label);
        Assert.Equal("Курск", t.Head.Label);
    }

    [Fact]
    public void EditedPostWithClearRemovesAlert()
    {
        // vrv_radar дописывает отбой в тот же пост: «Самарская область ⏎ Опасность по БПЛА» → «… ⏎ UPD: Отбой»
        var now = Clock.Now;
        var e = NewEngine();
        var post = Msg("vrv_radar", 77, now.AddMinutes(-20), "Самарская область\nОпасность по БПЛА");
        e.ApplyBatch([post]);
        Assert.Contains(e.Snapshot(), x => x.Status != AlertStatus.AllClear && x.RegionIds.Contains("samara"));

        // первый опрос — текст запомнен, правки нет
        Assert.Empty(e.EditedClears([post], now));
        // тот же текст повторно — правки нет; исправление пунктов без отбоя — тоже
        Assert.Empty(e.EditedClears([post], now));
        Assert.Empty(e.EditedClears([post with { Text = "Самарская область, Тольятти\nОпасность по БПЛА" }], now));
        var edits = e.EditedClears([post with { Text = "Самарская область, Тольятти\nОпасность по БПЛА\nUPD: Отбой" }], now).ToList();
        var ed = Assert.Single(edits);
        Assert.True(ed.Id > AlertEngine.EditIdOffset && ed.Id % AlertEngine.EditIdOffset == 77);
        Assert.EndsWith("/77", ed.Url);
        Assert.DoesNotContain("Опасность", ed.Text);

        e.ApplyBatch(edits);
        Assert.DoesNotContain(e.Snapshot(), x => x.Status != AlertStatus.AllClear && x.ExpiresAt > Clock.Now && x.RegionIds.Contains("samara"));
    }

    [Fact]
    public void EditedClearTextKeepsPlacesAndThreat()
    {
        // места и тип угрозы — из исходного поста; отбой без них снял бы тревоги канала по всем регионам и типам
        Assert.Equal("Ростовская область\nОтбой по баллистике",
            AlertEngine.EditedClearText("Ростовская область\nРакетная опасность по баллистике", "Ростовская область\nРакетная опасность по баллистике\nUPD: Отбой"));
        // в исходном тексте уже был отбой или правка без отбоя — не правка-отбой
        Assert.Null(AlertEngine.EditedClearText("Курская область\nОтбой опасности БПЛА", "Курская область\nОтбой опасности БПЛА\nUPD: всё сбито"));
        Assert.Null(AlertEngine.EditedClearText("Курская область\nОпасность по БПЛА", "Курская область, Льгов\nОпасность по БПЛА"));
    }

    [Theory]
    // сбитие, прилёт, направление — дописанная строка с местами и угрозой исходного поста
    [InlineData("От Старого Оскола в сторону Нижнедевицк, Воронежской области\nОпасность по БПЛА", "UPD: сбит", "сбит")]
    [InlineData("Чеченская Республика, Грозный\nРабота ПВО по БПЛА", "UPD: есть прилет", "прилет")]
    [InlineData("Россошанский район\nОпасность по БПЛА", "UPD: в сторону Подгорного", "в сторону Подгорного")]
    public void EditedPostMeaningfulAdditions(string original, string upd, string expected)
    {
        var t = AlertEngine.EditedText(original, original + "\n" + upd + "\n‼️Подписывайся на Радар ВРВ");
        Assert.NotNull(t);
        Assert.Contains(expected, t);
        Assert.Contains("БПЛА", t);
        Assert.DoesNotContain("UPD", t);
        Assert.DoesNotContain("Подписывайся", t);
    }

    [Theory]
    // опровержение, подпись, правка после отбоя — не новая информация о тревоге
    [InlineData("Таганрог\nПредположительно сбитие БПЛА", "Таганрог\nПредположительно сбитие БПЛА\nUPD: не подтверждено")]
    [InlineData("Курская область\nОпасность по БПЛА", "Курская область\nОпасность по БПЛА\n‼️Подписывайся на Радар ВРВ")]
    [InlineData("Курская область\nОтбой опасности БПЛА", "Курская область\nОтбой опасности БПЛА\nUPD: всё сбито")]
    // итоговая сводка МО и пост-опровержение: правка уточняет итоги, а не сообщает о новой угрозе
    [InlineData("МОРФ:\nВ течение прошедшей ночи пресечены попытки атак БПЛА по Белгородской, Курской областям",
                "МОРФ:\nВ течение прошедшей ночи пресечены попытки атак БПЛА по Белгородской, Курской областям\nUPD: уничтожено 12 БПЛА")]
    [InlineData("❗️Прилетов не зафиксировано\nНе ведитесь на провокации",
                "❗️Прилетов не зафиксировано\nНе ведитесь на провокации\nUPD: был прилет по Н.П. Счастье без потерь")]
    public void EditedPostIgnoredAdditions(string original, string edited) => Assert.Null(AlertEngine.EditedText(original, edited));

    [Fact]
    public void EditedPostSameEditAppliedOnce()
    {
        // повторный опрос с той же правкой — одна правка, а не две
        var now = Clock.Now;
        var e = NewEngine();
        var post = Msg("vrv_radar", 5, now.AddMinutes(-5), "Россошанский район\nОпасность по БПЛА");
        e.EditedClears([post], now).ToList();
        var edited = post with { Text = post.Text + "\nUPD: сбит" };
        Assert.Single(e.EditedClears([edited], now));
        Assert.Empty(e.EditedClears([edited], now));
        // следующая правка того же поста — отдельное сообщение со своим id
        var again = e.EditedClears([edited with { Text = edited.Text + "\nUPD2: отбой" }], now).ToList();
        Assert.Single(again);
    }

    [Fact]
    public void OldHistoryExpiresAndAllClearApplies()
    {
        var now = Clock.Now;
        var e = NewEngine();
        e.ApplyBatch([
            // вчерашняя тревога, снятая отбоем, и давняя тревога без отбоя — к текущему моменту обе неактивны
            Msg("lpr1_Voronezh_alarm", 1, now.AddHours(-20), "Россошь\nТревога по БПЛА\nВоронежская область"),
            Msg("lpr1_Voronezh_alarm", 2, now.AddHours(-19), "Воронежская область\nОтбой опасности БПЛА"),
            Msg("lpr1_Bryansk_alarm", 3, now.AddHours(-5), "Навля\nТревога по БПЛА\nБрянская область"),
            // свежая — активна
            Msg("lpr1_Belgorod_alarm", 4, now.AddMinutes(-10), "Шебекино\nТревога по БПЛА\nБелгородская область"),
        ]);
        var active = e.Snapshot().Where(x => x.Status != AlertStatus.AllClear).ToList();
        var ev = Assert.Single(active);
        Assert.Contains("belgorod", ev.RegionIds);
    }

    [Fact]
    public void RegionTargetIsNotTrackPosition()
    {
        // «ЛНР … в направлении Ростовской области»: центр области — не место объекта, трек не должен уходить туда назад
        var now = Clock.Now;
        var e = NewEngine();
        e.ApplyBatch([
            Msg("vrv_radar", 1, now.AddMinutes(-20), "Тацинский район\nРостовская область\nФиксация БПЛА"),
            Msg("lpr1_Rostov_alarm", 2, now.AddMinutes(-2), "ЛНР, тревога по БПЛА сохраняется, высокая активность в направлении Ростовской области"),
        ]);
        Assert.DoesNotContain(e.TrackSnapshot(), t => t.Points.Any(p => p.Label == "Ростовская область"));
    }

    [Fact]
    public void AllClearInHistoryClearsOnlyWhatWasActiveThen()
    {
        // отбой 2 ч назад не должен снимать тревогу, объявленную после него
        var now = Clock.Now;
        var e = NewEngine();
        e.ApplyBatch([
            Msg("lpr1_Voronezh_alarm", 1, now.AddHours(-3), "Россошь\nТревога по БПЛА\nВоронежская область"),
            Msg("lpr1_Voronezh_alarm", 2, now.AddHours(-2), "Воронежская область\nОтбой опасности БПЛА"),
            Msg("lpr1_Voronezh_alarm", 3, now.AddMinutes(-20), "Богучар\nТревога по БПЛА\nВоронежская область"),
        ]);
        var active = e.Snapshot().Where(x => x.Status != AlertStatus.AllClear).ToList();
        var ev = Assert.Single(active);
        Assert.Contains(ev.Points, p => p.Label == "Богучар");
    }
}
