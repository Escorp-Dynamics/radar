using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Radar.Models;

public enum ThreatType
{
    None,
    General,
    Uav,
    Missile,
    Aviation,
    Artillery,
    Naval,
    /// <summary>Аэростаты, малоразмерные шары.</summary>
    Balloon,
    /// <summary>Наземные угрозы: ДРГ.</summary>
    Ground,
}

public enum AlertStatus
{
    Alert,
    AirDefense,
    Impact,
    AllClear,
    /// <summary>Пуски/запуски/выходы со стороны противника — источник угрозы, а не её место (регионы РФ не заливаются).</summary>
    Launch,
}

public readonly record struct GeoPoint(double Lat, double Lon);

public sealed record RawMessage(string Channel, long Id, DateTimeOffset Time, string Text)
{
    // правка поста (дописанный отбой) приходит с id со сдвигом 2^40 — ссылка ведёт на исходный пост
    public string Url => $"https://t.me/{Channel}/{(Id >= 1L << 40 ? Id - (1L << 40) : Id)}";
}

/// <param name="RadiusKm">Радиус зоны опасности вокруг пункта (город — меньше, район — больше).</param>
public sealed record MapPoint(GeoPoint Pos, string Label, bool IsRegionLevel, double RadiusKm = 12)
{
    /// <summary>Район/округ: подсвечивается по контуру района (если он есть в подложке).</summary>
    public string? DistrictRegion { get; init; }
    /// <summary>Часть региона («южная часть области»): подсвечиваются районы этой части.</summary>
    public string? PartRegion { get; init; }
    public double? PartBearing { get; init; }
    /// <summary>Регион точки (для частичного отбоя многорегиональной тревоги).</summary>
    public string? RegionId { get; init; }
    /// <summary>Зона — участок линейного ориентира (трасса): подсвечивается полосой вдоль него, а не кругом.</summary>
    public IReadOnlyList<GeoPoint>? Corridor { get; init; }
    /// <summary>Зона над водой: часть акватории (контур), а не круг, заходящий на сушу.</summary>
    public IReadOnlyList<GeoPoint>? WaterArea { get; init; }
    /// <summary>Акватория зоны («sea:azov»): заливка обрезается по её контуру — зона не заходит на сушу.</summary>
    public string? SeaId { get; init; }
}

public sealed class AlertEvent : INotifyPropertyChanged
{
    private DateTimeOffset _time;
    private string _text = "";
    private IReadOnlyList<MapPoint> _points = [];
    private GeoPoint? _origin;
    private GeoPoint? _target;
    private bool _isSelected;

    private ThreatType _threat;
    private ThreatKind _kind = ThreatKind.Unknown;
    private IReadOnlyList<ThreatKind> _kinds = [];
    /// <summary>Ключ слияния; меняется, когда следующее сообщение уточняет тип угрозы.</summary>
    public required string Key { get; set; }
    public required ThreatType Threat { get => _threat; set { if (_threat != value) { _threat = value; OnInfoChanged(); } } }
    /// <summary>Конкретный тип средства поражения (подтип группы).</summary>
    public ThreatKind Kind { get => _kind; set { if (_kind != value) { _kind = value; OnInfoChanged(); } } }
    /// <summary>Все упомянутые типы (например, «БПЛА и аэростаты»).</summary>
    public IReadOnlyList<ThreatKind> Kinds { get => _kinds; set { _kinds = value; OnChanged(); OnChanged(nameof(KindsText)); OnChanged(nameof(HasKinds)); OnSummaryChanged(); } }

    private void OnInfoChanged()
    {
        foreach (var n in (string[])[nameof(Threat), nameof(Kind), nameof(Info), nameof(IconPath), nameof(ThreatText), nameof(ThreatCaps), nameof(Title), nameof(ColorHex), nameof(Summary), nameof(HasSummary)])
            OnChanged(n);
    }
    private AlertLevel _level = AlertLevel.Alarm;
    /// <summary>Уровень тревоги; в пределах одного события может только повышаться.</summary>
    public AlertLevel Level
    {
        get => _level;
        set { if (_level != value) { _level = value; foreach (var n in (string[])[nameof(Level), nameof(StatusText), nameof(StatusCaps), nameof(Title), nameof(LevelRank), nameof(LevelBg), nameof(LevelFg), nameof(EffectiveLevel), "Seg1", "Seg2", "Seg3", "Seg4"]) OnChanged(n); } }
    }
    /// <summary>0..3 для разметки (индикатор из четырёх делений).</summary>
    public int LevelRank => Status == AlertStatus.AllClear ? -1 : (int)EffectiveLevel;
    /// <summary>Шкала уровня в карточке: 4 сегмента, заполненные до текущего уровня (внимание … удар).</summary>
    public double Seg1 => LevelRank >= 0 ? 1 : 0.18;
    public double Seg2 => LevelRank >= 1 ? 1 : 0.18;
    public double Seg3 => LevelRank >= 2 ? 1 : 0.18;
    public double Seg4 => LevelRank >= 3 ? 1 : 0.18;
    public string TimeShort => Time.ToLocalTime().ToString("HH:mm");
    public AlertLevel EffectiveLevel => Status is AlertStatus.Impact or AlertStatus.AirDefense ? AlertLevel.Strike : Level;
    public string LevelBg => Status == AlertStatus.AllClear ? "#1A2A23" : Status == AlertStatus.Launch ? ThreatCatalog.LaunchBadgeBg(Level) : ThreatCatalog.LevelBadgeBg(EffectiveLevel);
    public string LevelFg => Status == AlertStatus.AllClear ? "#4FB286" : Status == AlertStatus.Launch ? ThreatCatalog.LaunchColor : ThreatCatalog.LevelBadgeFg(EffectiveLevel);
    public bool IsLaunch => Status == AlertStatus.Launch;
    private AlertStatus _status;
    /// <summary>Статус; может усиливаться следующим сообщением (тревога → работа ПВО → прилёт).</summary>
    public required AlertStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            foreach (var n in (string[])["Seg1", "Seg2", "Seg3", "Seg4", nameof(Status), nameof(StatusText), nameof(StatusCaps), nameof(Title), nameof(LevelRank), nameof(LevelBg), nameof(LevelFg), nameof(EffectiveLevel), nameof(IsLaunch), nameof(ColorHex), nameof(Summary), nameof(HasSummary)])
                OnChanged(n);
        }
    }
    private IReadOnlyList<string> _regionIds = [];
    private IReadOnlyList<string> _wholeRegionIds = [];
    private string _regionNames = "";
    /// <summary>Регионы тревоги; сужаются, если отбой объявлен только в части из них.</summary>
    public required IReadOnlyList<string> RegionIds { get => _regionIds; set { _regionIds = value; OnChanged(); OnSummaryChanged(); } }
    /// <summary>Регионы, где опасность объявлена целиком (без конкретных пунктов) — только их заливаем полностью.</summary>
    public IReadOnlyList<string> WholeRegionIds { get => _wholeRegionIds; set { _wholeRegionIds = value; OnChanged(); OnChanged(nameof(PartialClearText)); } }
    /// <summary>Регионы, куда направлена угроза («в сторону Саратовской области») — подсвечиваются слабым уровнем «ожидается».</summary>
    public SnapshotList<string> ExpectedRegionIds { get; init; } = [];
    /// <summary>Опасность «по пункту и близлежащим» — подсвечиваются и смежные районы.</summary>
    public bool Nearby { get; init; }
    public required string RegionNames { get => _regionNames; set { _regionNames = value; OnChanged(); OnChanged(nameof(PartialClearText)); } }
    public required string Signature { get; init; }
    public SnapshotList<RawMessage> Sources { get; } = [];
    /// <summary>Все сообщения (канал, номер), из которых сложилась тревога, — для уточнения геокодером. Только под блокировкой движка.</summary>
    internal HashSet<(string Channel, long Id)> MessageKeys { get; } = [];
    /// <summary>Тревога поглощена другой (слита): не отдельное событие для статистики.</summary>
    internal bool Merged { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset Time { get => _time; set { _time = value; OnChanged(); OnChanged(nameof(TimeText)); OnChanged(nameof(TimeShort)); } }
    public string Text { get => _text; set { _text = value; OnChanged(); OnSummaryChanged(); } }

    private (string Text, IReadOnlyList<string> Regions, bool Clear, ThreatType Threat, IReadOnlyList<ThreatKind> Kinds, string Value)? _summary;
    /// <summary>Текст сообщения без того, что уже в заголовке и плашках (регион, уровень, тип угрозы).</summary>
    public string Summary
    {
        get
        {
            bool clear = Status == AlertStatus.AllClear;
            if (_summary is { } s && ReferenceEquals(s.Text, _text) && ReferenceEquals(s.Regions, _regionIds) && s.Clear == clear
                && s.Threat == _threat && ReferenceEquals(s.Kinds, _kinds)) return s.Value;
            var groups = _kinds.Select(k => ThreatCatalog.Info(k).Group).Append(_threat);
            var v = Radar.Services.AlertSummary.Clean(_text, _regionIds, clear, groups);
            _summary = (_text, _regionIds, clear, _threat, _kinds, v);
            return v;
        }
    }
    public bool HasSummary => Summary.Length > 0;
    private void OnSummaryChanged() { OnChanged(nameof(Summary)); OnChanged(nameof(HasSummary)); }
    // «действует: …» в плашке частичного отбоя зависит от оставшихся пунктов и регионов
    public IReadOnlyList<MapPoint> Points { get => _points; set { _points = value; OnChanged(); OnChanged(nameof(PartialClearText)); } }
    public GeoPoint? Origin { get => _origin; set { _origin = value; OnChanged(); } }
    public GeoPoint? Target { get => _target; set { _target = value; OnChanged(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnChanged(); OnChanged(nameof(ShowHistory)); } }
    /// <summary>История уточнений раскрывается в карточке выбранного события.</summary>
    public bool ShowHistory => _isSelected && HasHistory;

    private bool _flash;
    /// <summary>Короткая подсветка в ленте при появлении события (чисто UI-состояние).</summary>
    public bool Flash { get => _flash; set { if (_flash != value) { _flash = value; OnChanged(); } } }

    private bool _isMine;
    /// <summary>Тревога касается регионов, выбранных для уведомлений (UI-состояние: метка в ленте).</summary>
    public bool IsMine { get => _isMine; set { if (_isMine != value) { _isMine = value; OnChanged(); } } }

    private bool _levelUp;
    /// <summary>Короткая подсветка плашки уровня при его повышении (UI-состояние).</summary>
    public bool LevelUp { get => _levelUp; set { if (_levelUp != value) { _levelUp = value; OnChanged(); } } }

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
    public string SourceText => string.Join(", ", Sources.Select(s => "@" + s.Channel).Distinct());
    /// <summary>Каналы коротко: два первых и «и ещё N».</summary>
    public string SourcesShort => Radar.Services.AlertSummary.Sources(Sources.Select(s => s.Channel));
    public string Url => Sources.Count > 0 ? Sources[0].Url : "";

    public ThreatInfo Info => Kind != ThreatKind.Unknown ? ThreatCatalog.Info(Kind) : ThreatCatalog.GroupDefault(Threat);
    public string IconPath => Radar.Models.ThreatCatalog.IconPath(Info.Icon);

    public string ThreatText => Info.Name;

    /// <summary>Перечень всех упомянутых типов, если их несколько.</summary>
    public string KindsText => Kinds.Count > 1 ? string.Join(", ", Kinds.Select(k => ThreatCatalog.Info(k).Short)) : "";
    public bool HasKinds => Kinds.Count > 1;

    public string StatusText => Status switch
    {
        AlertStatus.AirDefense => "Работа ПВО",
        AlertStatus.Impact => "Прилёт / взрывы",
        AlertStatus.AllClear => "Отбой",
        AlertStatus.Launch => Level == AlertLevel.Attention ? "Возможны пуски" : "Пуск",
        _ => ThreatCatalog.LevelText(Level),
    };

    public string Title => $"{ThreatText} — {StatusText}";
    public string ThreatCaps => ThreatText.ToUpperInvariant();
    public string StatusCaps => StatusText.ToUpperInvariant();

    /// <summary>Частичные отбои: какие регионы сняты отбоем, когда и кем (тревога в остальных действует).</summary>
    public SnapshotList<(string RegionName, DateTimeOffset Time, string Channel)> PartialClears { get; } = [];
    public bool HasPartialClear => PartialClears.Count > 0;
    public string PartialClearText => PartialClears.Count == 0 ? "" :
        "Отбой: " + Short(PartialClears.Select(c => c.RegionName)) + " · действует: " + Short(ActiveAreas());

    /// <summary>Что ещё под тревогой: регионы целиком и оставшиеся пункты/районы.</summary>
    private IEnumerable<string> ActiveAreas()
    {
        var names = WholeRegionIds.Select(r => Radar.Services.Gazetteer.Get(r)?.Name ?? r)
            .Concat(Points.Where(p => !p.IsRegionLevel).Select(p => p.Label)).Distinct().ToList();
        return names.Count > 0 ? names : [RegionNames];
    }

    private static string Short(IEnumerable<string> items)
    {
        var l = items.ToList();
        return l.Count <= 4 ? string.Join(", ", l) : string.Join(", ", l.Take(4)) + $" и ещё {l.Count - 4}";
    }

    public void ClearPartial()
    {
        PartialClears.Clear();
        OnChanged(nameof(HasPartialClear));
        OnChanged(nameof(PartialClearText));
        OnChanged(nameof(PartialClearTip));
    }
    public string PartialClearTip => string.Join("\n", PartialClears.Select(c => $"{c.Time.ToLocalTime():HH:mm} — отбой: {c.RegionName} (@{c.Channel})"));

    /// <summary>Отметить частичный отбой (вызывается движком при сужении тревоги).</summary>
    public void AddPartialClear(IEnumerable<string> regionNames, DateTimeOffset time, string channel)
    {
        var added = new List<string>();
        foreach (var n in regionNames)
            if (!PartialClears.Any(c => c.RegionName == n)) { PartialClears.Add((n, time, channel)); added.Add(n); }
        if (added.Count > 0) AddHistory(time, channel, "отбой: " + Short(added));
        OnChanged(nameof(HasPartialClear));
        OnChanged(nameof(PartialClearText));
        OnChanged(nameof(PartialClearTip));
    }

    /// <summary>История: какой канал объявил тревогу и что уточнил каждый следующий (пункты, тип, статус, отбои).</summary>
    public SnapshotList<EventHistoryEntry> History { get; } = [];
    public IReadOnlyList<EventHistoryEntry> HistoryView => [.. History];
    public bool HasHistory => History.Count > 1;
    public string HistoryHeader => $"История · {History.Count}";

    public void AddHistory(DateTimeOffset time, string channel, string text)
    {
        if (History.Count > 0 && History[^1] is var last && last.Channel == channel && last.Text == text) return;
        History.Add(new EventHistoryEntry(time, channel, text));
        if (History.Count > 40) History.RemoveAt(1);
        OnChanged(nameof(HistoryView));
        OnChanged(nameof(HasHistory));
        OnChanged(nameof(ShowHistory));
        OnChanged(nameof(HistoryHeader));
    }

    /// <summary>История пересобрана напрямую (слияние тревог) — карточка перечитывает её.</summary>
    public void NotifyHistory()
    {
        OnChanged(nameof(HistoryView));
        OnChanged(nameof(HasHistory));
        OnChanged(nameof(ShowHistory));
        OnChanged(nameof(HistoryHeader));
    }

    private string? _motion;
    /// <summary>Скорость и время до цели по треку, если их можно оценить.</summary>
    public string? Motion { get => _motion; set { if (_motion != value) { _motion = value; OnChanged(); OnChanged(nameof(HasMotion)); } } }
    public bool HasMotion => !string.IsNullOrEmpty(_motion);

    private string? _nearText;
    private bool _nearApproaching;
    /// <summary>Где угроза относительно ближайшего выбранного пункта: «Своя точка: БПЛА в 12 км к юго-западу, идёт к вам» (UI-состояние).</summary>
    public string? NearText { get => _nearText; set { if (_nearText != value) { _nearText = value; OnChanged(); OnChanged(nameof(HasNear)); } } }
    public bool HasNear => !string.IsNullOrEmpty(_nearText);
    /// <summary>Угроза идёт к выбранному пункту — плашка выделена.</summary>
    public bool NearApproaching { get => _nearApproaching; set { if (_nearApproaching != value) { _nearApproaching = value; OnChanged(); } } }
    private string? _motionTip;
    /// <summary>Подробности скорости: замеры, разброс, достоверность.</summary>
    public string? MotionTip { get => _motionTip; set { if (_motionTip != value) { _motionTip = value; OnChanged(); } } }
    public string Ago
    {
        get
        {
            var m = (int)Math.Max(0, (Radar.Services.Clock.Now - Time).TotalMinutes);
            return m < 1 ? "сейчас" : m < 60 ? $"{m} мин назад" : $"{m / 60} ч {m % 60} мин назад";
        }
    }
    public string SourceCount => Sources.Count is var n && n % 10 == 1 && n % 100 != 11 ? $"{n} источник"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} источника" : $"{n} источников";

    public string ColorHex => Status == AlertStatus.AllClear ? Palette.ColorHex(Threat, Status) : Info.Color;

    public void Touch()
    {
        OnChanged(nameof(SourceText));
        OnChanged(nameof(SourcesShort));
        OnChanged(nameof(SourceCount));
        OnChanged(nameof(Ago));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Доставка уведомлений об изменении в поток интерфейса: движок уточняет тревоги (геокодер, отбои) из фоновых
    /// потоков, а карточки ленты привязаны к этим свойствам. Приложение задаёт её при старте; null — сразу (тесты, аудит).
    /// </summary>
    public static Action<Action>? NotifyDispatcher { get; set; }

    private void OnChanged([CallerMemberName] string? name = null)
    {
        var h = PropertyChanged;
        if (h == null) return;
        var args = new PropertyChangedEventArgs(name);
        if (NotifyDispatcher is { } d) d(() => h(this, args));
        else h(this, args);
    }
}

/// <summary>
/// Список с заменой снимка при записи: движок меняет его под своей блокировкой, интерфейс в это время читает
/// (перечисление, Count, индекс) без блокировки и без «коллекция изменена». Список только растёт или очищается целиком.
/// </summary>
[System.Runtime.CompilerServices.CollectionBuilder(typeof(SnapshotList), nameof(SnapshotList.Create))]
public sealed class SnapshotList<T> : IReadOnlyList<T>
{
    private T[] _items = [];
    private readonly Lock _sync = new();

    public SnapshotList() { }
    public SnapshotList(IEnumerable<T> items) => _items = [.. items];

    public int Count => Volatile.Read(ref _items).Length;
    public T this[int index] => Volatile.Read(ref _items)[index];
    public void Add(T item) { lock (_sync) Volatile.Write(ref _items, [.. _items, item]); }
    public void Clear() { lock (_sync) Volatile.Write(ref _items, []); }
    public void RemoveAt(int index) { lock (_sync) { var l = _items.ToList(); l.RemoveAt(index); Volatile.Write(ref _items, [.. l]); } }
    public bool Contains(T item) => Array.IndexOf(Volatile.Read(ref _items), item) >= 0;
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Volatile.Read(ref _items)).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class SnapshotList
{
    public static SnapshotList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}

public sealed record EventHistoryEntry(DateTimeOffset Time, string Channel, string Text)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm");
    public string ChannelText => "@" + Channel;
}

public static class Palette
{
    public static string ColorHex(ThreatType threat, AlertStatus status) =>
        status == AlertStatus.AllClear ? "#4FB286" : status == AlertStatus.Launch && threat == ThreatType.General ? ThreatCatalog.LaunchColor
        : ThreatCatalog.GroupDefault(threat).Color;

    public static string ColorHex(ThreatKind kind, ThreatType threat, AlertStatus status) =>
        status == AlertStatus.AllClear ? "#4FB286" : kind != ThreatKind.Unknown ? ThreatCatalog.Info(kind).Color : ColorHex(threat, status);

    public static string Glyph(ThreatType threat) => threat switch
    {
        ThreatType.Uav => "Б",
        ThreatType.Missile => "Р",
        ThreatType.Aviation => "А",
        ThreatType.Artillery => "О",
        ThreatType.Naval => "К",
        _ => "!",
    };
}
