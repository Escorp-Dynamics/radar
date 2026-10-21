using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Radar.Models;
using Radar.Services;

namespace Radar.ViewModels;

/// <summary>Вариант периода статистики.</summary>
public sealed record StatsPeriod(string Key, string Title);

/// <summary>Строка рейтинга (регион, пункт, тип угрозы, направление): подпись, значение, доля к максимуму.</summary>
public sealed record StatsBar(string Title, string Value, double Share, string Color, string? Sub = null, string? Id = null)
{
    public double BarWidth => Math.Max(2, Share * 120);
}

/// <summary>Главная цифра сводки.</summary>
public sealed record StatsKpi(string Title, string Value, string Sub, string Color);

/// <summary>Столбец графика по дням: высоты сегментов по группам угроз (0…1 к максимуму дня).</summary>
public sealed record StatsDay(DateTime Day, int Total, double Uav, double Missile, double Aviation, double Other, bool Peak, string Tip = "", string Label = "");

/// <summary>Зона наблюдения в статистике.</summary>
public sealed record StatsMine(string Name, string Count, string Hours, string Median, string Closest);

/// <summary>Дашборд режима «Статистика»: период, фильтр региона и все блоки сводки.</summary>
public sealed class StatsViewModel : INotifyPropertyChanged
{
    private static readonly string[] MonthNames = ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static readonly string[] MonthGen = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    public string MonthTitle(DateTimeOffset m) => $"{MonthNames[m.Month - 1]} {m.Year}";

    private List<DateTimeOffset> _months = [];
    public IReadOnlyList<StatsPeriod> Periods { get; private set; } = [];
    private StatsPeriod? _period;
    /// <summary>Выбранный период: «7d», «30d», «all» или месяц «YYYY-MM».</summary>
    public StatsPeriod? Period
    {
        get => _period;
        set
        {
            if (value == null || value == _period) return;
            _period = value;
            // свой диапазон без дат — по умолчанию последние 14 дней архива
            if (IsCustom && (_customFrom == null || _customTo == null))
            {
                (_customTo, _customFrom) = (ArchiveLastDay, ArchiveLastDay.AddDays(-13));
                OnChanged(nameof(CustomFrom)); OnChanged(nameof(CustomTo));
            }
            OnChanged(); OnChanged(nameof(PeriodTitle)); OnChanged(nameof(IsCustom));
            PeriodChanged?.Invoke();
        }
    }
    public event Action? PeriodChanged;
    public string PeriodTitle => _period?.Title ?? "";
    public bool IsCustom => _period?.Key == "custom";

    private DateTime? _customFrom, _customTo;
    /// <summary>Свой диапазон: первый и последний день (включительно, по местному календарю).</summary>
    public DateTime? CustomFrom { get => _customFrom; set => SetCustom(value, _customTo, true); }
    public DateTime? CustomTo { get => _customTo; set => SetCustom(_customFrom, value, false); }

    private void SetCustom(DateTime? from, DateTime? to, bool fromChanged)
    {
        from = from?.Date; to = to?.Date;
        // конец раньше начала — двигаем вторую границу, а не оставляем пустой период
        if (from is { } f && to is { } t && t < f) { if (fromChanged) to = f; else from = t; }
        if (from == _customFrom && to == _customTo) return;
        (_customFrom, _customTo) = (from, to);
        OnChanged(nameof(CustomFrom));
        OnChanged(nameof(CustomTo));
        if (IsCustom) PeriodChanged?.Invoke();
    }

    /// <summary>Первый и последний день архива — пределы календаря.</summary>
    public DateTime ArchiveFirstDay => _months.Count > 0 ? _months[0].ToLocalTime().Date : DateTime.Today.AddYears(-1);
    public DateTime ArchiveLastDay => ((ArchiveEnd ?? DateTimeOffset.Now).ToLocalTime()).Date;

    private string? _region;
    /// <summary>Фильтр по региону (клик по карте); null — все регионы.</summary>
    public string? Region
    {
        get => _region;
        set { if (_region == value) return; _region = value; OnChanged(); OnChanged(nameof(RegionTitle)); OnChanged(nameof(HasRegion)); }
    }
    public string RegionTitle => _region == null ? "Все регионы" : Gazetteer.Get(_region)?.Name ?? _region;
    public bool HasRegion => _region != null;

    public void SetMonths(List<DateTimeOffset> months)
    {
        if (months.SequenceEqual(_months) && Periods.Count > 0) return;
        _months = months;
        var list = new List<StatsPeriod> { new("7d", "Последние 7 дней"), new("30d", "Последние 30 дней"), new("all", "Всё время наблюдений"), new("custom", "Свой диапазон…") };
        list.AddRange(months.AsEnumerable().Reverse().Select(m => new StatsPeriod(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), MonthTitle(m))));
        Periods = list;
        OnChanged(nameof(Periods));
        _period = list.FirstOrDefault(p => p.Key == _period?.Key) ?? list[0];
        OnChanged(nameof(Period));
        OnChanged(nameof(PeriodTitle));
        OnChanged(nameof(IsCustom));
        OnChanged(nameof(ArchiveFirstDay));
        OnChanged(nameof(ArchiveLastDay));
    }

    /// <summary>Время последнего сообщения архива (если известно из манифеста) — конец «последних N дней».</summary>
    public DateTimeOffset? ArchiveEnd { get; set; }

    private static DateTimeOffset DaysBack(DateTimeOffset dayEnd, int n)
    {
        var d = dayEnd.ToLocalTime().Date.AddDays(-n);
        return LocalMidnight(d.Year, d.Month, d.Day);
    }

    /// <summary>
    /// Границы выбранного периода. Дни — по местной полуночи (как столбцы графика): месяц — с 00:00 1-го числа по
    /// местному времени, «последние N дней» — N полных суток до конца архива (включая день последнего сообщения).
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) Range()
    {
        var end = _months.Count > 0 ? LocalMidnight(_months[^1].AddMonths(1).Year, _months[^1].AddMonths(1).Month, 1) : DateTimeOffset.UtcNow;
        var last = ArchiveEnd is { } ae && ae < end ? ae : DateTimeOffset.UtcNow < end ? DateTimeOffset.UtcNow : end.AddTicks(-1);
        var l = last.ToLocalTime();
        var dayEnd = LocalMidnight(l.Year, l.Month, l.Day).AddDays(1);
        return _period?.Key switch
        {
            "7d" => (DaysBack(dayEnd, 7), dayEnd),
            "30d" => (DaysBack(dayEnd, 30), dayEnd),
            "all" => (_months.Count > 0 ? LocalMidnight(_months[0].Year, _months[0].Month, 1) : DaysBack(dayEnd, 30), dayEnd),
            "custom" when _customFrom is { } cf && _customTo is { } ct =>
                (LocalMidnight(cf.Year, cf.Month, cf.Day), LocalMidnight(ct.Year, ct.Month, ct.Day).AddDays(1)),
            { } k when DateTime.TryParseExact(k + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) =>
                (LocalMidnight(m.Year, m.Month, 1), LocalMidnight(m.AddMonths(1).Year, m.AddMonths(1).Month, 1)),
            _ => (DaysBack(dayEnd, 7), dayEnd),
        };
    }

    private static DateTimeOffset LocalMidnight(int y, int m, int d)
    {
        var dt = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    // ---- состояние загрузки ----
    private bool _busy;
    private string _busyText = "";
    private double _busyProgress;
    private string? _emptyText;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) OnChanged(nameof(HasData)); } }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public double BusyProgress { get => _busyProgress; private set => Set(ref _busyProgress, value); }
    public string? EmptyText { get => _emptyText; private set { Set(ref _emptyText, value); OnChanged(nameof(IsEmpty)); OnChanged(nameof(HasData)); } }
    public bool IsEmpty => _emptyText != null;
    public bool HasData => _report != null && _emptyText == null && !_busy;

    public void SetEmpty(string text) { Busy = false; EmptyText = text; }
    public void SetProgress(string text, double p) { EmptyText = null; Busy = true; BusyText = text; BusyProgress = p; }
    public void ClearBusy() => Busy = false;

    // ---- блоки ----
    private StatsReport? _report;
    public IReadOnlyList<StatsKpi> Kpis { get; private set; } = [];
    public IReadOnlyList<StatsDay> Days { get; private set; } = [];
    public string DaysCaption { get; private set; } = "";
    public string DaysTitle { get; private set; } = "ПО ДНЯМ";
    private static readonly string[] DayShort = ["вс", "пн", "вт", "ср", "чт", "пт", "сб"];
    private static readonly string[] MonthShort = ["янв", "фев", "мар", "апр", "май", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    /// <summary>Дни периода как есть (до 120) или суммы по неделям с понедельника.</summary>
    internal static List<(DateTime Day, Dictionary<ThreatType, int> ByThreat)> Bucketize(List<(DateTime Day, Dictionary<ThreatType, int> ByThreat)> days, out bool weekly)
    {
        weekly = days.Count > 120;
        if (!weekly) return days;
        var res = new List<(DateTime, Dictionary<ThreatType, int>)>();
        foreach (var g in days.GroupBy(d => d.Day.AddDays(-(((int)d.Day.DayOfWeek + 6) % 7))))
        {
            var sum = new Dictionary<ThreatType, int>();
            foreach (var (_, bt) in g) foreach (var kv in bt) sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + kv.Value;
            res.Add((g.Key, sum));
        }
        return res;
    }
    /// <summary>Тепловая сетка «день недели × час»: 7 строк по 24 ячейки, яркость 0…1.</summary>
    public IReadOnlyList<IReadOnlyList<double>> WeekHour { get; private set; } = [];
    public string WeekHourCaption { get; private set; } = "";
    public IReadOnlyList<StatsBar> TopRegions { get; private set; } = [];
    public IReadOnlyList<StatsBar> TopRegionsByHours { get; private set; } = [];
    public IReadOnlyList<StatsBar> TopPlaces { get; private set; } = [];
    public IReadOnlyList<StatsBar> Kinds { get; private set; } = [];
    public IReadOnlyList<double> Sectors { get; private set; } = [];
    public IReadOnlyList<StatsBar> SectorBars { get; private set; } = [];
    public IReadOnlyList<StatsBar> LaunchAreas { get; private set; } = [];
    public IReadOnlyList<StatsMine> Mine { get; private set; } = [];
    public bool HasMine => Mine.Count > 0;
    public bool NoMine => Mine.Count == 0;
    public bool HasPlaces => TopPlaces.Count > 0;
    public bool HasLaunchAreas => LaunchAreas.Count > 0;

    private static readonly string[] SectorNames = ["С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ"];
    private static readonly string[] SectorLong = ["с севера", "с северо-востока", "с востока", "с юго-востока", "с юга", "с юго-запада", "с запада", "с северо-запада"];

    /// <summary>Сколько сводок применено (для замеров переключения периода).</summary>
    public int AppliedCount { get; private set; }

    public void Apply(StatsReport r)
    {
        _report = r;
        AppliedCount++;
        Busy = false;
        EmptyText = r.Alerts == 0 ? "За выбранный период тревог в архиве нет" : null;
        var days = Math.Max(1, (r.To - r.From).TotalDays);
        string Trend() => r.Trend is { } t ? (t >= 0 ? "+" : "−") + $"{Math.Abs(t) * 100:0}% к прошлому периоду" : "нет данных за прошлый период";
        Kpis =
        [
            new("ТРЕВОГ", N(r.Alerts), $"{D1(r.Alerts / days)} в сутки · {Trend()}", r.Trend is > 0.05 ? "#FF8A93" : r.Trend is < -0.05 ? "#7BD3A0" : "#E8ECF1"),
            new("ПОД ТРЕВОГОЙ", $"{N(r.AlertHours)} ч", $"активна хотя бы одна тревога · {100 * r.AlertHours / (days * 24):0}% времени", "#E8ECF1"),
            new("БПЛА", N(r.Uav), r.Alerts > 0 ? $"{100.0 * r.Uav / r.Alerts:0}% тревог" : "", "#F5A04A"),
            new("РАКЕТЫ", N(r.Missile), r.Alerts > 0 ? $"{100.0 * r.Missile / r.Alerts:0}% тревог" : "", "#FF6B6B"),
            new("УДАРЫ И ПВО", N(r.Strikes), "прилёты, взрывы, работа ПВО", "#E5484D"),
            new("ПИК", r.PeakDay.Count > 0 ? $"{r.PeakDay.Day.Day} {MonthGen[r.PeakDay.Day.Month - 1]}" : "—", r.PeakDay.Count > 0 ? $"{N(r.PeakDay.Count)} тревог за сутки" : "", "#F2C14E"),
        ];

        // длинный период: столбец на неделю (за всё время дневные столбцы были бы тоньше пикселя)
        var buckets = Bucketize(r.Days, out var weekly);
        int dmax = Math.Max(1, buckets.Count > 0 ? buckets.Max(d => d.ByThreat.Values.Sum()) : 1);
        Days = [.. buckets.Select(d =>
        {
            int u = d.ByThreat.GetValueOrDefault(ThreatType.Uav), m = d.ByThreat.GetValueOrDefault(ThreatType.Missile), a = d.ByThreat.GetValueOrDefault(ThreatType.Aviation);
            int total = d.ByThreat.Values.Sum();
            string tip = weekly ? $"неделя с {d.Day:dd.MM.yyyy}: {N(total)} тревог" : $"{d.Day:dd.MM}, {DayShort[(int)d.Day.DayOfWeek]}: {N(total)} тревог";
            string label = weekly ? (d.Day.Day <= 7 ? $"{MonthShort[d.Day.Month - 1]}{(d.Day.Month == 1 ? " " + d.Day.Year : "")}" : "") : d.Day.Day == 1 ? $"{d.Day:dd.MM}" : $"{d.Day.Day}";
            bool peak = weekly ? total == dmax && total > 0 : total == r.PeakDay.Count && total > 0;
            return new StatsDay(d.Day, total, (double)u / dmax, (double)m / dmax, (double)a / dmax, (double)(total - u - m - a) / dmax, peak, tip, label);
        })];
        DaysTitle = weekly ? "ПО НЕДЕЛЯМ" : "ПО ДНЯМ";
        DaysCaption = $"максимум {N(dmax)} {(weekly ? "за неделю" : "в сутки")}";

        int whMax = 1;
        for (int d = 0; d < 7; d++) for (int h = 0; h < 24; h++) whMax = Math.Max(whMax, r.WeekHour[d, h]);
        WeekHour = [.. Enumerable.Range(0, 7).Select(d => (IReadOnlyList<double>)[.. Enumerable.Range(0, 24).Select(h => Math.Sqrt((double)r.WeekHour[d, h] / whMax))])];
        int bestD = 0, bestH = 0;
        for (int d = 0; d < 7; d++) for (int h = 0; h < 24; h++) if (r.WeekHour[d, h] > r.WeekHour[bestD, bestH]) (bestD, bestH) = (d, h);
        string[] dn = ["понедельник", "вторник", "среду", "четверг", "пятницу", "субботу", "воскресенье"];
        int[] byHour = [.. Enumerable.Range(0, 24).Select(h => Enumerable.Range(0, 7).Sum(d => r.WeekHour[d, h]))];
        int hPeak = Array.IndexOf(byHour, byHour.Max()), hLow = Array.IndexOf(byHour, byHour.Min());
        WeekHourCaption = r.Alerts == 0 ? "" : $"чаще всего — в {dn[bestD]} около {bestH:00}:00 · по часам пик {hPeak:00}–{(hPeak + 1) % 24:00}, тише всего {hLow:00}–{(hLow + 1) % 24:00}";

        string RName(string id) => Gazetteer.Get(id)?.Name ?? id;
        int rmax = Math.Max(1, r.Regions.Count > 0 ? r.Regions.Max(x => x.Count) : 1);
        TopRegions = [.. r.Regions.Take(10).Select(x => new StatsBar(RName(x.RegionId), N(x.Count), (double)x.Count / rmax, "#E8A35C", $"{N(x.Hours)} ч", x.RegionId))];
        double hmax = Math.Max(1, r.Regions.Count > 0 ? r.Regions.Max(x => x.Hours) : 1);
        TopRegionsByHours = [.. r.Regions.OrderByDescending(x => x.Hours).Take(10).Select(x => new StatsBar(RName(x.RegionId), $"{N(x.Hours)} ч", x.Hours / hmax, "#C98BFF", $"{N(x.Count)} трев.", x.RegionId))];
        int pmax = Math.Max(1, r.Places.Count > 0 ? r.Places.Max(x => x.Count) : 1);
        TopPlaces = [.. r.Places.Take(12).Select(x => new StatsBar(x.Place, N(x.Count), (double)x.Count / pmax, "#7CD4FF", RName(x.RegionId)))];
        int kmax = Math.Max(1, r.Kinds.Count > 0 ? r.Kinds.Max(x => x.Count) : 1);
        Kinds = [.. r.Kinds.Take(10).Select(x =>
        {
            var info = x.Kind != ThreatKind.Unknown ? ThreatCatalog.Info(x.Kind) : ThreatCatalog.GroupDefault(x.Threat);
            return new StatsBar(info.Name, N(x.Count), (double)x.Count / kmax, info.Color, r.Alerts > 0 ? $"{D1(100.0 * x.Count / r.Alerts)}%" : null);
        })];
        int smax = Math.Max(1, r.Sectors.Max());
        Sectors = [.. r.Sectors.Select(v => (double)v / smax)];
        int stotal = Math.Max(1, r.Sectors.Sum());
        SectorBars = [.. Enumerable.Range(0, 8).Where(i => r.Sectors[i] > 0).OrderByDescending(i => r.Sectors[i]).Take(4)
            .Select(i => new StatsBar($"{SectorNames[i]} — {SectorLong[i]}", $"{100.0 * r.Sectors[i] / stotal:0}%", (double)r.Sectors[i] / smax, "#F2C14E"))];
        int lmax = Math.Max(1, r.LaunchAreas.Count > 0 ? r.LaunchAreas.Max(x => x.Count) : 1);
        LaunchAreas = [.. r.LaunchAreas.Select(x => new StatsBar(x.Label, N(x.Count), (double)x.Count / lmax, "#FF8A93"))];
        Mine = [.. r.Mine.Select(m => new StatsMine(m.Name, N(m.Count), $"{N(m.Hours)} ч", m.Count > 0 ? $"{m.MedianMinutes:0} мин" : "—",
            m.ClosestKm is { } c ? $"{N(c)} км" : "—"))];

        foreach (var n in new[] { nameof(Kpis), nameof(Days), nameof(DaysCaption), nameof(DaysTitle), nameof(WeekHour), nameof(WeekHourCaption), nameof(TopRegions),
                     nameof(TopRegionsByHours), nameof(TopPlaces), nameof(Kinds), nameof(Sectors), nameof(SectorBars), nameof(LaunchAreas),
                     nameof(Mine), nameof(HasMine), nameof(NoMine), nameof(HasPlaces), nameof(HasLaunchAreas), nameof(HasData), nameof(RangeText) })
            OnChanged(n);
    }

    public string RangeText => _report is { } r ? $"{r.From.ToLocalTime():dd.MM.yyyy} – {r.To.ToLocalTime().AddSeconds(-1):dd.MM.yyyy}" : "";

    private static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '\u00A0');
    private static string N(double v) => N((int)Math.Round(v));
    /// <summary>Число с одним знаком после запятой по-русски: «557,4», «1 204,5».</summary>
    private static string D1(double v)
    {
        var s = Math.Round(v, 1).ToString("N1", CultureInfo.InvariantCulture);
        if (s.EndsWith(".0", StringComparison.Ordinal)) s = s[..^2];
        return s.Replace(',', '\u00A0').Replace('.', ',');
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(n);
        return true;
    }
}
