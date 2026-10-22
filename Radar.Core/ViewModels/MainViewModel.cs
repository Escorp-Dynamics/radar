using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Radar.Models;
using Radar.Services;

namespace Radar.ViewModels;

public sealed class FeedItem(RawMessage raw, ParsedMessage parsed)
{
    public string Time => raw.Time.ToLocalTime().ToString("HH:mm");
    public string Channel => "@" + raw.Channel;
    // без подписей и промо каналов («подписаться», «@канал»)
    public string Text => parsed.CleanText.Length > 0 ? parsed.DisplayText : raw.Text;
    public string Url => raw.Url;
    public string Color => parsed.IsJunk || parsed.IsInformational || (parsed.Threat == ThreatType.None && parsed.Status != AlertStatus.AllClear)
        ? "#4A5361"
        : Palette.ColorHex(parsed.Threat, parsed.Status);
    public bool IsJunk => parsed.IsJunk;
    public double Dim => parsed.IsJunk ? 0.4 : parsed.IsInformational ? 0.7 : 1.0;
    public bool Recognized => parsed.Regions.Count > 0 || parsed.Places.Count > 0;
}

/// <summary>Строка таблицы источников: канал, состояние связи, время последнего ответа и поста.</summary>
public sealed class ChannelRow : INotifyPropertyChanged
{
    public ChannelRow(string name, string title) { Name = name; Title = title; }
    public string Name { get; }
    public string Title { get; }
    public string Handle => "@" + Name;
    public string Url => $"https://t.me/{Name}";

    private ChannelStatus? _s;
    public void Update(ChannelStatus s) { _s = s; Refresh(); }
    /// <summary>Переключатель отражает новое состояние сразу, не дожидаясь статуса движка (он придёт через очередь).</summary>
    public void SetEnabledOptimistic(bool enabled) { if (_s != null) { _s = _s with { Enabled = enabled }; Refresh(); } }
    /// <summary>Канал включён (опрашивается).</summary>
    public bool Enabled => _s?.Enabled ?? true;
    public string ToggleTip => Enabled ? "Выключить канал: перестать его опрашивать" : "Включить канал";
    public void Refresh()
    {
        foreach (var n in new[] { nameof(StateColor), nameof(StateText), nameof(StateCaps), nameof(Detail), nameof(Tip), nameof(Pulsing), nameof(Dim), nameof(Enabled), nameof(ToggleTip) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private enum State { Disabled, Waiting, Ok, Silent, Error }
    private State Current => _s switch
    {
        null => State.Waiting,
        { Enabled: false } => State.Disabled,
        { Ok: false, Failures: > 0 } => State.Error,
        { LastSuccess: null } => State.Waiting,
        // ответ есть, но давно (сеть пропала после последнего опроса) — тоже нет связи
        { LastSuccess: { } ls } when DateTimeOffset.UtcNow - ls > TimeSpan.FromMinutes(3) => State.Error,
        // канал отвечает, но постов давно не было — это не ошибка, просто тишина
        { LastPost: { } lp } when DateTimeOffset.UtcNow - lp > TimeSpan.FromHours(12) => State.Silent,
        _ => State.Ok,
    };

    public string StateColor => Current switch
    {
        State.Ok => "#4FB286",
        State.Silent => "#D9A441",
        State.Error => "#E5484D",
        State.Waiting => "#7CD4FF",
        _ => "#3A4452",
    };
    public string StateText => Current switch
    {
        State.Ok => "На связи",
        State.Silent => "Тишина",
        State.Error => "Ошибка",
        State.Waiting => "Подключение",
        _ => "Отключён",
    };
    public string StateCaps => StateText.ToUpperInvariant();
    public bool Pulsing => Current == State.Waiting;
    /// <summary>Канал отвечает (в том числе «тишина» — ответ есть, постов нет).</summary>
    public bool IsAlive => Current is State.Ok or State.Silent;
    public double Dim => Current == State.Disabled ? 0.45 : 1;

    public string Detail
    {
        get
        {
            if (_s is not { } s) return "ожидание первого ответа";
            if (!s.Enabled) return "не опрашивается";
            if (Current == State.Error) return (s.Error ?? "нет ответа") + (s.Failures > 1 ? $" · попыток: {s.Failures}" : "");
            var parts = new List<string>();
            parts.Add(s.LastPost is { } lp ? "пост " + Ago(lp) : "постов за сутки нет");
            if (s.LastSuccess is { } ls) parts.Add("ответ " + Ago(ls));
            return string.Join(" · ", parts);
        }
    }

    public string Tip
    {
        get
        {
            var lines = new List<string> { $"{Title} ({Handle})" };
            if (_s is { } s)
            {
                lines.Add($"Последний ответ: {(s.LastSuccess is { } ls ? ls.ToLocalTime().ToString("dd.MM HH:mm:ss") : "не было")}");
                lines.Add($"Последний пост: {(s.LastPost is { } lp ? lp.ToLocalTime().ToString("dd.MM HH:mm") : "нет за период истории")}");
                lines.Add($"Получено сообщений: {s.Received}");
                if (!s.Ok && s.Error != null) lines.Add($"Ошибка: {s.Error}");
            }
            lines.Add("Двойной щелчок — открыть канал в Telegram");
            return string.Join("\n", lines);
        }
    }

    private static string Ago(DateTimeOffset t)
    {
        var m = (int)Math.Max(0, (DateTimeOffset.UtcNow - t).TotalMinutes);
        return m < 1 ? "только что" : m < 60 ? $"{m} мин назад" : m < 24 * 60 ? $"{m / 60} ч {m % 60} мин назад" : $"{m / 1440} сут назад";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Счётчик активных событий в легенде. При росте числа на короткое время включает <see cref="Bump"/>,
/// по которому XAML подсвечивает «таблетку» (переходы фона и масштаба).
/// </summary>
public abstract class LegendCounter : INotifyPropertyChanged
{
    /// <summary>Подсветка выключена до первого заполнения, чтобы при запуске не мигало всё сразу.</summary>
    public static bool Animate { get; set; }

    private int _count, _bumpGen;
    private bool _bump;

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            bool grew = value > _count;
            _count = value;
            Raise(nameof(Count));
            Raise(nameof(HasCount));
            if (grew && Animate) StartBump();
        }
    }
    public bool HasCount => _count > 0;
    public bool Bump => _bump;

    private void StartBump()
    {
        int gen = ++_bumpGen;
        if (!_bump) { _bump = true; Raise(nameof(Bump)); }
        DispatcherTimer.RunOnce(() =>
        {
            if (gen != _bumpGen) return;
            _bump = false;
            Raise(nameof(Bump));
        }, TimeSpan.FromMilliseconds(900));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Элемент легенды: тип угрозы с иконкой и оттенком; клик скрывает/показывает тип на карте.</summary>
public sealed class LegendItem(ThreatInfo info) : LegendCounter
{
    private bool _shown = true;
    public ThreatKind Kind => info.Kind;
    public string Name => info.Name;
    public string Color => info.Color;
    /// <summary>Цвет подписи — цвет иконки, немного приглушённый (≈75% непрозрачности).</summary>
    public string TextColor => "#C0" + info.Color.TrimStart('#');
    public Avalonia.Media.Geometry Icon => Radar.Map.MapIcons.Get(info);
    public string Tip => Shown ? $"{info.Name} — скрыть на карте" : $"{info.Name} — показать на карте";

    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            Raise(nameof(Shown));
            Raise(nameof(ItemOpacity));
            Raise(nameof(Tip));
        }
    }
    public double ItemOpacity => _shown ? 1.0 : 0.3;
}

/// <summary>Плашка уровня в легенде; клик скрывает/показывает события этого уровня.</summary>
public sealed class LevelItem(AlertLevel level, string tip) : LegendCounter
{
    private bool _shown = true;
    public AlertLevel Level => level;
    public string Name => ThreatCatalog.LevelText(level);
    public string Bg => ThreatCatalog.LevelBadgeBg(level);
    public string Fg => ThreatCatalog.LevelBadgeFg(level);
    public string Tip => tip + (Shown ? "\nНажмите, чтобы скрыть на карте" : "\nСкрыто — нажмите, чтобы показать");
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            Raise(nameof(Shown));
            Raise(nameof(ItemOpacity));
            Raise(nameof(Tip));
        }
    }
    public double ItemOpacity => _shown ? 1.0 : 0.3;
}

/// <summary>Строка списка выбора зон наблюдения: регион, район или населённый пункт.</summary>
public sealed class WatchChoice(WatchArea area, string hint, Action<WatchChoice> toggled) : INotifyPropertyChanged
{
    private bool _picked;
    public WatchArea Area => area;
    public string Key => area.Kind + ":" + area.Id;
    public string Name => area.Name;
    /// <summary>Пояснение: тип и регион («район · Белгородская область»).</summary>
    public string Hint => hint;
    public bool HasHint => hint.Length > 0;
    public string KindTag => area.Kind switch { WatchArea.DistrictKind => "РАЙОН", WatchArea.PlaceKind => "ПУНКТ", _ => "РЕГИОН" };
    public bool Picked
    {
        get => _picked;
        set { if (_picked == value) return; _picked = value; PropertyChanged?.Invoke(this, new(nameof(Picked))); toggled(this); }
    }
    internal void SetSilently(bool v) { if (_picked == v) return; _picked = v; PropertyChanged?.Invoke(this, new(nameof(Picked))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Выбранный населённый пункт со своим радиусом наблюдения.</summary>
public sealed class WatchPlaceRow(WatchArea area, Func<int> defaultRadius, Action<WatchPlaceRow> changed, Action<WatchPlaceRow>? renamed = null) : INotifyPropertyChanged
{
    public static readonly int[] Steps = [5, 10, 15, 30, 50];
    public WatchArea Area => area;

    /// <summary>
    /// Название пункта; своя точка переименовывается («Дача»). Пустое — для своей точки подпись по ближайшему пункту,
    /// для обычного пункта — прежнее название.
    /// </summary>
    public string Name
    {
        get => area.Name;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Length > 40) v = v[..40].TrimEnd();
            if (v.Length == 0) v = area.IsPoint ? MainViewModel.PointName(new GeoPoint(area.Lat, area.Lon)) : area.Name;
            if (v == area.Name) { PropertyChanged?.Invoke(this, new(nameof(Name))); return; }
            area.Name = v;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            renamed?.Invoke(this);
        }
    }
    public string RenameTip => area.IsPoint ? "Нажмите, чтобы переименовать точку («Дача»)" : "Нажмите, чтобы переименовать пункт";
    public string Region => Gazetteer.Get(area.Region)?.Name ?? "";
    /// <summary>Действующий радиус (свой или общий по умолчанию).</summary>
    public int Radius => area.RadiusKm > 0 ? area.RadiusKm : defaultRadius();
    public string RadiusText => $"{Radius} км";
    public bool CanShrink => Radius > 3;
    public bool CanGrow => Radius < 100;

    public void Step(int dir)
    {
        int cur = Radius;
        // после перетаскивания на карте радиус может быть любым — шаг к ближайшему значению шкалы
        int next = dir > 0 ? Steps.FirstOrDefault(s => s > cur, Math.Min(cur + 10, 100)) : Steps.LastOrDefault(s => s < cur, Math.Max(cur - 1, 3));
        if (next == cur) return;
        area.RadiusKm = next;
        Notify();
        changed(this);
    }

    internal void Notify()
    {
        foreach (var n in new[] { nameof(Radius), nameof(RadiusText), nameof(CanShrink), nameof(CanGrow) })
            PropertyChanged?.Invoke(this, new(n));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Счётчик активных угроз одной группы в заголовке: иконка цвета группы и число; клик скрывает/показывает группу на карте.</summary>
public sealed class TelemetryItem(ThreatType type, string name, string color, string icon, LegendGroup group) : LegendCounter
{
    public ThreatType Type => type;
    public LegendGroup Group => group;
    public string Name => name;
    public string Color => color;
    public Avalonia.Media.Geometry Icon => Radar.Map.MapIcons.Get(icon);
    /// <summary>Группа скрыта на карте целиком.</summary>
    public bool Hidden => group.Items.All(i => !i.Shown);
    public string Tip => $"{name}: активных {Count}" + (Hidden ? "\nСкрыто на карте — нажмите, чтобы показать" : "\nНажмите, чтобы скрыть на карте");
    /// <summary>Показывать в заголовке: есть активные угрозы или группа скрыта (чтобы её можно было вернуть).</summary>
    public bool Visible => HasCount || Hidden;
    internal void Notify() { Raise(nameof(Tip)); Raise(nameof(Hidden)); Raise(nameof(Visible)); }
}

public sealed class LegendGroup(string title, IEnumerable<ThreatInfo> items, string? shortTitle = null) : LegendCounter
{
    public string Title { get; } = title;
    /// <summary>Короткая подпись для строки свёрнутой легенды (колонка фиксированной ширины).</summary>
    public string ShortTitle { get; } = shortTitle ?? title;
    public string GroupTip => $"{Title}: скрыть/показать всю группу на карте";
    public IReadOnlyList<LegendItem> Items { get; } = items.Select(i => new LegendItem(i)).ToList();
    public string CountColor => Items.Count > 0 ? Items[0].Color : "#8C97A7";
}

/// <summary>Режим работы окна.</summary>
public enum AppMode { Live, History, Stats }

/// <summary>Месяц архива в выборе периода истории.</summary>
public sealed record MonthOption(DateTimeOffset Month, string Title, bool Current);

/// <summary>Пункт меню режимов в заголовке.</summary>
public sealed record ModeOption(AppMode Mode, string Title, string Hint, string Color);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _http;
    private readonly Action _routeChanged;
    /// <summary>Движок, чью обстановку показывает окно: живой или воспроизведения истории.</summary>
    private AlertEngine _engine;
    /// <summary>Живой движок: опрос каналов не прерывается, пока открыта история или статистика.</summary>
    private AlertEngine _live;
    private readonly NominatimThrottle _throttle = new();
    private string _status = "Подключение к каналам…";
    private AlertEvent? _selected;
    private string _filterText = "";
    private bool _showUav = true, _showMissile = true, _showOther = true, _showAllClear = true, _showLaunch = true;
    private IReadOnlyList<AlertEvent> _all = [];

    public AppSettings Settings { get; }
    public ObservableCollection<AlertEvent> Events { get; } = [];
    public ObservableCollection<FeedItem> Feed { get; } = [];

    public event Action<IReadOnlyList<AlertEvent>, IReadOnlyList<Track>>? MapEventsChanged;

    public MainViewModel(AppSettings settings)
    {
        _districtReady = () => { OnChanged(nameof(RegionChoicesView)); OnChanged(nameof(SearchHint)); };
        DistrictCatalogReady += _districtReady;
        Settings = settings;
        RestoreHiddenKinds();
        (_showUav, _showMissile, _showOther, _showLaunch, _showAllClear, _legendExpanded) =
            (settings.ShowUav, settings.ShowMissile, settings.ShowOther, settings.ShowLaunch, settings.ShowAllClear, settings.LegendExpanded);
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }) { Timeout = TimeSpan.FromSeconds(25) };

        TelegramRoute.Mode = (Services.TelegramAccess)Math.Clamp(settings.TelegramAccess, 0, 2);
        _routeChanged = () => Dispatcher.UIThread.Post(() => OnChanged(nameof(TelegramRouteText)));
        TelegramRoute.StatusChanged += _routeChanged;
        _engine = _live = new AlertEngine(settings, _http, new Geocoder(_http, _throttle)) { RealTime = true };
        Attach(_live);
        // линия загружена/обновлена в фоне — подпись в легенде и перерисовка карты
        _frontChanged = () => Dispatcher.UIThread.Post(() => { NotifyFrontLine(); Refresh(); });
        FrontLineSource.Changed += _frontChanged;

        // относительное время («5 мин назад») и устаревание треков
        _tickTimer = DispatcherTimer.Run(() =>
        {
            foreach (var e in Events) e.Touch();
            foreach (var c in Channels) c.Refresh();
            RecountChannels();
            // «сегодня / вчера / устарели» у линии фронта и подпись давности зависят от текущего времени
            OnChanged(nameof(StaleText));
            NotifyFrontLine();
            if (Displays(_engine) && !HistoryBusy) _all = _engine.Snapshot();
            Refresh();
            return true;
        }, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Подписка на движок. Обстановку и ленту показывает только текущий (<see cref="_engine"/>); живой движок в режиме
    /// истории продолжает работать и копит обстановку, но окно его не отображает — при возврате она сразу на месте.
    /// </summary>
    private void Attach(AlertEngine engine)
    {
        // обновления обстановки объединяются: при подгрузке каналов их приходит десятки подряд, а в UI-потоке нужно
        // лишь последнее (каждое — полный пересчёт ленты, треков и карты)
        (IReadOnlyList<AlertEvent> List, long Applied)? pendingChange = null;
        engine.Changed += list =>
        {
            // пачка, обработанная до запрошенной перемотки, устарела (обстановка старого момента)
            long applied = engine.SeekApplied;
            bool post;
            lock (_uiQueueLock) { post = pendingChange == null; pendingChange = (list, applied); }
            if (!post) return;
            Dispatcher.UIThread.Post(() =>
            {
                (IReadOnlyList<AlertEvent> List, long Applied) change;
                lock (_uiQueueLock) { change = pendingChange!.Value; pendingChange = null; }
                if (!Displays(engine) || change.Applied < engine.SeekRequested) return;
                _all = change.List;
                Refresh();
            }, DispatcherPriority.Normal);
        };
        engine.StatusChanged += s => Dispatcher.UIThread.Post(() =>
        {
            if (engine == _live) { _liveStatus = s; UpdateLiveStatus(s); }
            if (engine == _engine) UpdateStatus(s);
        });
        // сообщения ленты копятся в очереди и вставляются порциями: по одной вставке на сообщение (сотни при подгрузке
        // истории) заново раскладывали ленту на каждую
        var feedQueue = new Queue<(RawMessage Raw, ParsedMessage Parsed, long Applied)>();
        bool feedPosted = false;
        engine.MessageReceived += (raw, parsed) =>
        {
            if (parsed.IsJunk) return; // реклама и донаты не показываются даже в ленте
            long applied = engine.SeekApplied;
            bool post;
            lock (_uiQueueLock) { feedQueue.Enqueue((raw, parsed, applied)); post = !feedPosted; feedPosted = true; }
            if (post) Dispatcher.UIThread.Post(DrainFeed, DispatcherPriority.Background);

            void DrainFeed()
            {
                // порция укладывается в долю кадра; остаток — следующим проходом, интерфейс успевает отрисоваться
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                int taken = 0;
                while (true)
                {
                    (RawMessage Raw, ParsedMessage Parsed, long Applied) item;
                    lock (_uiQueueLock)
                    {
                        if (feedQueue.Count == 0) { feedPosted = false; return; }
                        if (taken >= 4 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > 6)
                        {
                            Dispatcher.UIThread.Post(DrainFeed, DispatcherPriority.Background);
                            return;
                        }
                        item = feedQueue.Dequeue();
                    }
                    taken++;
                    // живая лента копится и в истории/статистике — при возврате в реальное время она на месте
                    if (engine == _live)
                    {
                        _liveFeed.Insert(0, new FeedItem(item.Raw, item.Parsed));
                        if (_liveFeed.Count > 300) _liveFeed.RemoveAt(_liveFeed.Count - 1);
                    }
                    if (!Displays(engine)) continue;
                    // сообщения пачки, обработанной до перемотки, не возвращаются в очищенную ленту
                    if (item.Applied < engine.SeekRequested) continue;
                    Feed.Insert(0, new FeedItem(item.Raw, item.Parsed));
                    if (Feed.Count == 1) OnChanged(nameof(FeedEmpty));
                    while (Feed.Count > 300) Feed.RemoveAt(Feed.Count - 1);
                }
            }
        };
    }

    private readonly object _uiQueueLock = new();

    /// <summary>
    /// Обстановку движка показывает окно: живой — только в реальном времени (в статистике и пока грузится история
    /// окно не показывает ничего), движок воспроизведения — в истории.
    /// </summary>
    private EngineStatus? _liveStatus;

    private bool Displays(AlertEngine engine) => engine == _engine && (engine == _live ? IsLive : IsHistory);

    // ---------------- режимы работы: реальное время, история, статистика ----------------

    /// <summary>
    /// Каталог полного архива сообщений (по месяцам): RADAR_ARCHIVE, иначе рядом с программой или в каталоге данных.
    /// Упакованный архив (archive-pack с manifest.json) предпочтительнее рабочего (archive — каталоги каналов).
    /// </summary>
    public static string ArchiveDir { get; set; } = FindArchiveDir();

    private static string FindArchiveDir()
    {
        if (Environment.GetEnvironmentVariable("RADAR_ARCHIVE") is { Length: > 0 } env) return env;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = new DirectoryInfo(start);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (PickArchive(Path.Combine(d.FullName, "artifacts")) is { } a) return a;
        }
        return PickArchive(AppPaths.Root) ?? ArchiveSync.LocalDir;
    }

    /// <summary>Архив автозагружается из релиза, если он не задан вручную (RADAR_ARCHIVE или artifacts разработчика).</summary>
    private static bool ArchiveIsManaged => string.Equals(Path.GetFullPath(ArchiveDir), Path.GetFullPath(ArchiveSync.LocalDir), StringComparison.Ordinal);

    private Task? _archiveSync;
    private string? _archiveSyncText;
    /// <summary>Состояние автозагрузки архива для вкладки «Настройки».</summary>
    public string ArchiveSyncText { get => _archiveSyncText ?? (ArchiveIsManaged ? "Загружается автоматически и обновляется ежедневно" : $"Свой архив: {ArchiveDir}"); private set => Set(ref _archiveSyncText, value); }

    /// <summary>Докачка архива из релиза «archive»: новые и изменившиеся месяцы; после обновления — перечитывание открытого экрана.</summary>
    public Task SyncArchiveAsync()
    {
        if (!ArchiveIsManaged) return Task.CompletedTask;
        if (_archiveSync is { IsCompleted: false }) return _archiveSync;
        return _archiveSync = SyncArchiveCore();
    }

    private async Task SyncArchiveCore()
    {
        var dir = ArchiveDir;
        bool first = ArchivePack.ReadManifest(dir) is null;
        ArchiveSyncText = "Проверка архива…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Total == 0) return;
            ArchiveSyncText = $"Загрузка архива · {p.Done} из {p.Total} мес.";
            if (first && IsStats) Stats.SetProgress($"Загрузка архива · {p.Done} из {p.Total} мес.", (double)p.Done / p.Total);
        });
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) })
                { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            var r = await ArchiveSync.SyncAsync(http, dir, Distribution.ArchiveBaseUrl, progress, CancellationToken.None);
            ArchiveSyncText = r.Downloaded > 0 ? $"Архив обновлён · загружено {r.Downloaded} мес. · {DateTime.Now:HH:mm}" : $"Архив актуален · {r.Total} мес. · {DateTime.Now:HH:mm}";
            // готовая статистика из CI (тот же код и архив): месяцы не прогоняются через движок на устройстве
            int stats = 0;
            try { stats = await StatsPack.SyncAsync(http, dir, Distribution.ArchiveBaseUrl, CancellationToken.None); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"Статистика из релиза: {ex.Message}");
            }
            if (r.Changed || stats > 0)
            {
                _statsReports.Clear();
                if (IsStats) _ = LoadStatsAsync();
                else if (IsHistory && (HistoryEmpty || _historyMonth is null)) StartHistory();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ArchiveSyncText = "Архив не обновлён: " + ex switch
            {
                TaskCanceledException => "нет ответа сервера",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "архив на сервере ещё не опубликован",
                HttpRequestException { StatusCode: { } code } => $"ошибка сервера {(int)code}",
                HttpRequestException => "нет подключения к интернету",
                _ => ex.Message,
            } + $" · {DateTime.Now:HH:mm}";
            if (first && IsStats) _ = LoadStatsAsync();
        }
    }

    private static string? PickArchive(string parent)
    {
        var pack = Path.Combine(parent, "archive-pack");
        if (File.Exists(Path.Combine(pack, ArchivePack.ManifestName))) return pack;
        var raw = Path.Combine(parent, "archive");
        return Directory.Exists(raw) ? raw : null;
    }

    private AppMode _mode = AppMode.Live;
    public AppMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            var was = _mode;
            _mode = value;
            ModeMenuOpen = false;
            // живая обстановка на момент ухода: новые тревоги, пришедшие в истории/статистике, объявятся при возврате
            if (was == AppMode.Live) { _liveSeen = [.. _all]; _liveWasPrimed = _eventsPrimed; }
            if (was == AppMode.History) StopHistory();
            if (was == AppMode.Stats) { _statsCts?.Cancel(); StatsHeatChanged?.Invoke(null, null); }
            if (value == AppMode.History) StartHistory();
            else if (value == AppMode.Stats) ShowStats();
            else ShowLive();
            foreach (var n in new[] { nameof(Mode), nameof(IsLive), nameof(IsHistory), nameof(IsStats), nameof(IsReplay), nameof(ModeTag), nameof(LiveCaption),
                                      nameof(IsOffline), nameof(IsConnecting), nameof(HistoryLoading), nameof(ModeOptions), nameof(ModeTagColor) })
                OnChanged(n);
            // строка статуса и индикатор загрузки — сразу для нового режима, не дожидаясь следующего опроса
            if (_liveStatus is { } ls) { UpdateLiveStatus(ls); UpdateStatus(ls); }
            UpdateEmptyState();
        }
    }
    public bool IsLive => _mode == AppMode.Live;
    public bool IsHistory => _mode == AppMode.History;
    public bool IsStats => _mode == AppMode.Stats;

    private bool _modeMenuOpen;
    /// <summary>Выпадающее меню режимов под меткой в заголовке.</summary>
    public bool ModeMenuOpen { get => _modeMenuOpen; set => Set(ref _modeMenuOpen, value); }

    /// <summary>Пункты меню — два режима, отличные от текущего.</summary>
    public IReadOnlyList<ModeOption> ModeOptions => [.. AllModes.Where(m => m.Mode != _mode)];
    private static readonly ModeOption[] AllModes =
    [
        new(AppMode.Live, "В РЕАЛЬНОМ ВРЕМЕНИ", "Текущая обстановка по каналам тревог", "#FF4D5A"),
        new(AppMode.History, "ИСТОРИЯ", "Воспроизведение архива сообщений с перемоткой", "#F2C14E"),
        new(AppMode.Stats, "СТАТИСТИКА", "Сводка по архиву: тревоги, регионы, типы угроз", "#7CD4FF"),
    ];
    public string ModeTagColor => AllModes.First(m => m.Mode == _mode).Color;

    private DispatcherTimer? _replayTimer, _replayTouch;

    // ---- история: выбор месяца архива и перемотка ----

    /// <summary>Месяц воспроизведения (UTC, первое число).</summary>
    private DateTimeOffset? _historyMonth;
    private List<DateTimeOffset> _archiveMonths = [];
    /// <summary>Месяцы архива для выбора периода (последние сверху), отмечен текущий.</summary>
    public IReadOnlyList<MonthOption> HistoryMonths =>
        [.. _archiveMonths.AsEnumerable().Reverse().Select(m => new MonthOption(m, MonthTitle(m), m == _historyMonth))];
    public string HistoryMonthTitle => _historyMonth is { } hm ? MonthTitle(hm) : "Демо-неделя";
    public bool HasArchiveMonths => _archiveMonths.Count > 0;
    private bool _monthPickerOpen;
    public bool MonthPickerOpen { get => _monthPickerOpen; set => Set(ref _monthPickerOpen, value); }

    private static readonly string[] MonthNames = ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static string MonthTitle(DateTimeOffset m) => $"{MonthNames[m.Month - 1]} {m.Year}";

    private double[]? _replayHistogram;
    /// <summary>Активность каналов по часам периода (для ленты времени), 0…1.</summary>
    public double[]? ReplayHistogram { get => _replayHistogram; private set => Set(ref _replayHistogram, value); }
    public DateTimeOffset ReplayStartTime => _engine.IsReplay ? _engine.ReplayStart : default;
    public DateTimeOffset ReplayEndTime => _engine.IsReplay ? _engine.ReplayEnd : default;
    private bool _historyBusy;
    /// <summary>Загрузка месяца или восстановление обстановки после перемотки.</summary>
    public bool HistoryBusy { get => _historyBusy; private set { if (Set(ref _historyBusy, value)) UpdateEmptyState(); } }

    /// <summary>Выбор месяца архива: воспроизведение начинается заново с его начала.</summary>
    public void SelectHistoryMonth(DateTimeOffset month)
    {
        MonthPickerOpen = false;
        // сравниваем с запрошенным месяцем, а не с загруженным: выбор «вернуться» во время загрузки другого месяца не теряется
        if (!IsHistory || month == (_requestedMonth ?? _historyMonth)) return;
        _requestedMonth = month;
        StopReplayEngine();
        OnChanged(nameof(ReplayStartTime));
        OnChanged(nameof(ReplayEndTime));
        _ = StartHistoryAsync(month);
    }

    /// <summary>Перемотка на долю периода (лента времени).</summary>
    public void SeekHistory(double frac)
    {
        if (_engine == _live || !_engine.IsReplay) return;
        var t = _engine.ReplayStart + (_engine.ReplayEnd - _engine.ReplayStart) * Math.Clamp(frac, 0, 1);
        ResetForSeek();
        _engine.SeekReplay(t);
    }

    /// <summary>
    /// Перед перемоткой: обстановка нового момента — не «новые» тревоги. Сброс здесь, а не по Seeked: Changed с
    /// результатом перемотки приходит раньше Seeked и иначе подсвечивал бы всю ленту.
    /// </summary>
    private void ResetForSeek()
    {
        HistoryBusy = true;
        _eventsPrimed = false;
        _announced.Clear();
        _seenLevel.Clear();
        Feed.Clear();
        OnChanged(nameof(FeedEmpty));
    }

    /// <summary>Шаг по времени (кнопки «назад/вперёд»): ±<paramref name="hours"/> ч.</summary>
    public void StepHistory(double hours)
    {
        if (_engine == _live || !_engine.IsReplay) return;
        ResetForSeek();
        _engine.SeekReplay(_engine.SeekTarget + TimeSpan.FromHours(hours));
    }

    private DateTimeOffset? _requestedMonth;

    private void StartHistory() { _requestedMonth = null; _ = StartHistoryAsync(null); }

    /// <summary>
    /// Режим истории: свой движок воспроизводит выбранный месяц архива по модельным часам (по умолчанию — последний месяц,
    /// с начала его последней недели); живой движок продолжает опрос в фоне.
    /// </summary>
    private async Task StartHistoryAsync(DateTimeOffset? month)
    {
        // каждая загрузка — своё поколение: результат устаревшей (сменили месяц или режим) отбрасывается
        int gen = ++_historyGen;
        HistoryEmpty = false;
        _historyError = null;
        HistoryBusy = true;
        _all = [];
        Feed.Clear();
        OnChanged(nameof(FeedEmpty));
        Refresh();
        var dir = ArchiveDir;
        List<DateTimeOffset> months;
        List<RawMessage> msgs;
        DateTimeOffset start, end;
        bool demo;
        try
        {
            (months, msgs, start, end, demo) = await Task.Run(() => LoadHistory(dir, month));
        }
        catch (Exception ex)
        {
            // любой сбой чтения архива (не только IO): иначе индикатор загрузки висит, а режим истории пуст
            System.Diagnostics.Trace.WriteLine($"История: {ex}");
            if (gen != _historyGen || !IsHistory) return;
            ClearReplayState();
            // архив есть, но пакет повреждён — не «скачайте архив», а что именно сломано
            _historyError = ex is InvalidDataException ? $"Архив повреждён: {ex.Message}.\nПроверьте его командой Radar --archive-verify \"{ArchiveDir}\"" : null;
            OnChanged(nameof(HistoryEmptyText));
            HistoryEmpty = true;
            HistoryBusy = false;
            UpdateEmptyState();
            return;
        }
        if (gen != _historyGen) return;
        if (!IsHistory) { HistoryBusy = false; return; }
        _archiveMonths = months;
        _historyMonth = demo || months.Count == 0 ? null : month ?? months[^1];
        foreach (var n in new[] { nameof(HistoryMonths), nameof(HistoryMonthTitle), nameof(HasArchiveMonths) }) OnChanged(n);
        if (msgs.Count == 0)
        {
            ClearReplayState();
            HistoryEmpty = true;
            HistoryBusy = false;
            UpdateEmptyState();
            return;
        }
        HistoryEmpty = false;
        _historyError = null;
        var engine = new AlertEngine(new AppSettings
        {
            Channels = Settings.Channels.Select(c => new ChannelConfig { Name = c.Name, Title = c.Title, Regions = c.Regions, Enabled = true }).ToList(),
            AlertLifetimeMinutes = Settings.AlertLifetimeMinutes,
            AllClearLifetimeMinutes = Settings.AllClearLifetimeMinutes,
            OnlineGeocoding = false,
            OnlineFrontLine = false,
        }, _http, new Geocoder(_http, _throttle));
        Attach(engine);
        engine.Seeked += () => Dispatcher.UIThread.Post(() =>
        {
            if (engine != _engine) return;
            // обстановка на новый момент — без вспышек «новых» тревог
            _eventsPrimed = false;
            _announced.Clear();
            _seenLevel.Clear();
            HistoryBusy = false;
            UpdateReplay();
        });
        StopReplayEngine();
        _engine = engine;
        _announced.Clear();
        _seenLevel.Clear();
        _eventsPrimed = false;
        Feed.Clear();
        OnChanged(nameof(FeedEmpty));
        ReplayHistogram = Histogram(msgs, start, end);
        engine.StartReplay(msgs, DefaultReplaySpeed, start, end);
        OnChanged(nameof(ReplayStartTime));
        OnChanged(nameof(ReplayEndTime));
        _replayTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateReplay());
        _replayTimer.Start();
        // в ускоренном времени относительные подписи и прогнозы обновляем чаще
        _replayTouch ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            // во время перемотки треки движка уже очищены, а лента ещё старая — не перерисовываем до Seeked
            if (HistoryBusy) return;
            foreach (var e in Events) e.Touch();
            Refresh();
        });
        _replayTouch.Start();
        UpdateReplay();
    }

    private int _historyGen;

    /// <summary>
    /// Сообщения месяца архива (с предысторией для обстановки на его начало) и границы воспроизведения.
    /// Без архива (или месяц пуст) — встроенная демо-неделя (Demo = true).
    /// </summary>
    private static (List<DateTimeOffset> Months, List<RawMessage> Msgs, DateTimeOffset Start, DateTimeOffset End, bool Demo) LoadHistory(string dir, DateTimeOffset? month)
    {
        var months = ArchiveFetcher.Months(dir);
        if (months.Count > 0)
        {
            var m = month ?? months[^1];
            var msgs = ArchiveFetcher.Load(dir, m - AlertEngine.SeekLookback, m.AddMonths(1));
            var inMonth = msgs.Where(x => x.Time >= m).ToList();
            if (inMonth.Count > 0)
            {
                var start = inMonth[0].Time;
                var end = inMonth[^1].Time;
                // последний (текущий) месяц — с начала последней недели: свежая обстановка, а не начало месяца
                if (month == null && end - start > TimeSpan.FromDays(7)) start = end - TimeSpan.FromDays(7);
                return (months, msgs, start, end, false);
            }
        }
        var demo = ReplaySource.Load();
        return (months, demo, demo.Count > 0 ? demo[0].Time : default, demo.Count > 0 ? demo[^1].Time : default, true);
    }

    /// <summary>Сообщения по часам периода, нормированные к максимуму (квадратный корень — тихие часы тоже видны).</summary>
    private static double[] Histogram(List<RawMessage> msgs, DateTimeOffset start, DateTimeOffset end)
    {
        int n = Math.Clamp((int)Math.Ceiling((end - start).TotalHours), 1, 24 * 31);
        var h = new double[n];
        double span = Math.Max(1, (end - start).TotalSeconds);
        foreach (var m in msgs)
        {
            if (m.Time < start || m.Time > end) continue;
            h[Math.Min(n - 1, (int)((m.Time - start).TotalSeconds / span * n))]++;
        }
        double max = h.Max();
        if (max > 0) for (int i = 0; i < n; i++) h[i] = Math.Sqrt(h[i] / max);
        return h;
    }

    private void StopReplayEngine()
    {
        if (_engine != _live)
        {
            _engine.Dispose();
            _engine = _live;
        }
    }

    /// <summary>Месяц не загрузился или пуст: от прежнего месяца не остаётся часов, таймеров и гистограммы.</summary>
    private void ClearReplayState()
    {
        _replayTimer?.Stop();
        _replayTouch?.Stop();
        StopReplayEngine();
        Clock.StopSimulation();
        ReplayHistogram = null;
        foreach (var n in new[] { nameof(ReplayPaused), nameof(ReplayStartTime), nameof(ReplayEndTime) }) OnChanged(n);
    }

    private void StopHistory()
    {
        _historyGen++;
        _replayTimer?.Stop();
        _replayTouch?.Stop();
        StopReplayEngine();
        Clock.StopSimulation();
        HistoryEmpty = false;
        _historyError = null;
        HistoryBusy = false;
        MonthPickerOpen = false;
        foreach (var n in new[] { nameof(ReplayPaused), nameof(ReplayStartTime), nameof(ReplayEndTime) }) OnChanged(n);
    }

    /// <summary>Обстановка живого движка — сразу, без вспышек и оповещений о «новых» тревогах.</summary>
    // сообщения живого движка (новые первыми), в том числе пришедшие, пока окно показывало историю или статистику
    private readonly List<FeedItem> _liveFeed = [];
    private HashSet<AlertEvent> _liveSeen = [];
    private bool _liveWasPrimed;

    private void ShowLive()
    {
        // тревоги, пришедшие, пока окно показывало историю или статистику, объявляются при возврате (Refresh ниже):
        // уже известные помечаются, новые — нет
        _all = _live.Snapshot();
        _eventsPrimed = _liveWasPrimed;
        foreach (var e in _all) if (_liveSeen.Contains(e)) _announced.Add(e);
        Feed.Clear();
        foreach (var f in _liveFeed) Feed.Add(f);
        OnChanged(nameof(FeedEmpty));
        Refresh();
    }

    /// <summary>Статистика: карта без текущей обстановки (раскраска по интенсивности), дашборд по архиву.</summary>
    private void ShowStats()
    {
        _all = [];
        Feed.Clear();
        OnChanged(nameof(FeedEmpty));
        Refresh();
        _ = LoadStatsAsync();
    }

    private StatsViewModel? _stats;
    public StatsViewModel Stats => _stats ??= CreateStats();
    private StatsViewModel CreateStats()
    {
        var st = new StatsViewModel();
        st.PeriodChanged += StatsChanged;
        return st;
    }

    private CancellationTokenSource? _statsCts;

    /// <summary>Загружает (или считает и кеширует) месяцы периода и собирает сводку.</summary>
    private async Task LoadStatsAsync()
    {
        var old = _statsCts;
        var cts = _statsCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        var dir = ArchiveDir;
        try
        {
            await LoadStatsCore(dir, cts);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // не только IO: сбой расчёта иначе оставлял бы «Расчёт…» навсегда
            if (ex is not (IOException or UnauthorizedAccessException or InvalidDataException)) System.Diagnostics.Trace.WriteLine($"Статистика: {ex}");
            if (_statsCts == cts) { Stats.SetEmpty($"Не удалось прочитать архив: {ex.Message}"); StatsHeatChanged?.Invoke(null, null); }
        }
    }

    private async Task LoadStatsCore(string dir, CancellationTokenSource cts)
    {
        var months = await Task.Run(() => ArchiveFetcher.Months(dir));
        if (cts.IsCancellationRequested) return;
        Stats.SetMonths(months);
        Stats.ArchiveEnd = await Task.Run(() => ArchivePack.ReadManifest(dir) is { Months.Count: > 0 } man
            ? DateTimeOffset.FromUnixTimeSeconds(man.Months.Max(e => e.To)) : (DateTimeOffset?)null);
        if (cts.IsCancellationRequested) return;
        if (months.Count == 0)
        {
            Stats.SetEmpty(NoArchiveText(dir, false));
            StatsHeatChanged?.Invoke(null, null);
            return;
        }
        var (from, to) = Stats.Range();
        // месяцы периода и предыдущий период той же длины (для динамики)
        var need = new List<DateTimeOffset>();
        // прошлый период: для календарного месяца — предыдущий месяц (StatsSummary), иначе окно той же длины
        var prevFrom = (from.Day == 1 && from.TimeOfDay == TimeSpan.Zero ? from.AddMonths(-1) : from - (to - from)).ToUniversalTime();
        for (var m = new DateTimeOffset(prevFrom.Year, prevFrom.Month, 1, 0, 0, 0, TimeSpan.Zero); m < to; m = m.AddMonths(1))
            if (months.Contains(m)) need.Add(m);
        // посчитанные месяцы берутся из памяти/кеша — сводка пересобирается без индикатора, прежняя остаётся на экране;
        // прогресс — только если какой-то месяц предстоит прогнать через движок
        var missing = await Task.Run(() => need.Where(m => !ArchiveStats.IsReady(dir, m)).ToList());
        if (cts.IsCancellationRequested) return;
        var watch = Settings.WatchAreas.ToList();
        var region = Stats.Region;
        if (missing.Count > 0)
        {
            // у всех недостающих месяцев есть прежние итоги (архив дополнился, обновился движок) — они показываются сразу,
            // а месяцы тихо пересчитываются; индикатор — только если какой-то месяц не считался вовсе
            var stale = await Task.Run(() =>
            {
                var parts = need.Select(m => ArchiveStats.Peek(dir, m)).ToList();
                if (parts.Any(p => p is null)) return null;
                var ev = parts.SelectMany(p => p!.Value.Events).ToList();
                var tr = parts.SelectMany(p => p!.Value.Tracks).ToList();
                return StatsSummary.Build(ev, tr, from, to, region, watch, Settings.WatchRadiusKm, ev);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (stale != null)
            {
                Stats.Apply(stale);
                StatsHeatChanged?.Invoke(stale.RegionHeat, Stats.Region);
            }
            // идёт загрузка архива и готовой статистики из релиза — по её окончании экран перечитается сам;
            // считать сейчас то, что вот-вот придёт готовым, — минуты работы процессора впустую
            if (_archiveSync is { IsCompleted: false } sync)
            {
                if (stale == null) Stats.SetProgress("Загрузка статистики архива…", 0);
                await sync.ContinueWith(_ => { }, TaskScheduler.Default);
                if (cts.IsCancellationRequested) return;
                missing = await Task.Run(() => need.Where(m => !ArchiveStats.IsReady(dir, m)).ToList());
                if (cts.IsCancellationRequested) return;
            }
            // недостающие месяцы — параллельно (каждый — свой прогон движка), новые первыми
            int done = 0;
            if (stale == null && missing.Count > 0)
                Stats.SetProgress(missing.Count == 1 ? $"Расчёт {Stats.MonthTitle(missing[0])}" : $"Расчёт архива · 0 из {missing.Count} мес.", 0);
            await Task.Run(() => Parallel.ForEach(missing.OrderByDescending(m => m),
                new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, m =>
                {
                    ArchiveStats.Month(dir, m, cts.Token);
                    int d = Interlocked.Increment(ref done);
                    if (missing.Count > 1 && stale == null)
                        Dispatcher.UIThread.Post(() => { if (_statsCts == cts) Stats.SetProgress($"Расчёт архива · {d} из {missing.Count} мес.", (double)d / missing.Count); });
                }), cts.Token);
        }
        if (cts.IsCancellationRequested) return;
        // сводка того же периода и фильтра уже собиралась — без пересчёта
        var reportKey = (from, to, region, string.Join("|", watch.Select(w => $"{w.Kind}:{w.Id}:{w.Lat}:{w.Lon}:{w.RadiusKm}")), Settings.WatchRadiusKm,
            string.Join(",", need.Select(m => ArchiveStats.IsReady(dir, m) ? m.ToUnixTimeSeconds() : 0)));
        if (_statsReports.TryGetValue(reportKey, out var known))
        {
            Stats.Apply(known);
            StatsHeatChanged?.Invoke(known.RegionHeat, Stats.Region);
            WarmStats(dir, months);
            return;
        }
        var report = await Task.Run(() =>
        {
            var events = new List<StatEvent>();
            var tracks = new List<StatTrack>();
            foreach (var m in need)
            {
                var (ev, tr) = ArchiveStats.Month(dir, m, cts.Token);
                events.AddRange(ev);
                tracks.AddRange(tr);
            }
            return StatsSummary.Build(events, tracks, from, to, region, watch, Settings.WatchRadiusKm, events);
        }, cts.Token);
        if (cts.IsCancellationRequested) return;
        if (_statsReports.Count > 24) _statsReports.Clear();
        _statsReports[reportKey] = report;
        Stats.Apply(report);
        StatsHeatChanged?.Invoke(report.RegionHeat, Stats.Region);
        // остальной архив — после показанной сводки: прогрев не отнимает ядра у запрошенного периода
        WarmStats(dir, months);
    }

    private readonly Dictionary<(DateTimeOffset, DateTimeOffset, string?, string, int, string), StatsReport> _statsReports = [];

    private Task? _statsWarm;
    /// <summary>
    /// Фоновый расчёт остальных месяцев архива (новые — первыми), чтобы любой период, включая «всё время наблюдений»,
    /// открывался сразу. Двумя потоками и с пониженным приоритетом — интерфейс и живая лента не страдают.
    /// </summary>
    private void WarmStats(string dir, List<DateTimeOffset> months)
    {
        if (_statsWarm is { IsCompleted: false }) return;
        var sync = _archiveSync;
        _statsWarm = Task.Run(async () =>
        {
            // готовые месяцы из релиза приходят с загрузкой архива — прогреваются только оставшиеся
            if (sync != null) await sync.ContinueWith(_ => { }, TaskScheduler.Default);
            var todo = months.Where(m => !ArchiveStats.IsReady(dir, m)).OrderByDescending(m => m).ToList();
            try
            {
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) }, m =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    try { ArchiveStats.Month(dir, m); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                    finally { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
                });
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Фоновый расчёт статистики: {ex.Message}"); }
        });
    }

    /// <summary>Раскраска карты для статистики (интенсивность по регионам, выбранный регион).</summary>
    public event Action<IReadOnlyDictionary<string, double>?, string?>? StatsHeatChanged;

    /// <summary>Смена периода или региона на дашборде.</summary>
    public void StatsChanged() { if (IsStats) _ = LoadStatsAsync(); }

    /// <summary>Клик по региону на карте: фильтр статистики (повторный клик — снять).</summary>
    public void StatsPickRegion(string? id)
    {
        if (!IsStats) return;
        Stats.Region = id == null || id == Stats.Region ? null : id;
        _ = LoadStatsAsync();
    }

    private bool _historyEmpty;
    /// <summary>Архив для истории не найден — на карте подсказка, как его получить.</summary>
    public bool HistoryEmpty { get => _historyEmpty; private set => Set(ref _historyEmpty, value); }
    private string? _historyError;
    public string HistoryEmptyText => _historyError ?? NoArchiveText(ArchiveDir, true);

    // на телефоне командной строки нет — подсказка про CLI там бессмысленна
    private string NoArchiveText(string dir, bool wrap) => ArchiveIsManaged
        ? (_archiveSync is { IsCompleted: false } ? "Архив сообщений загружается…" : $"Архив сообщений не загружен. Проверьте подключение к интернету.{(wrap ? "\n" : " ")}{ArchiveSyncText}")
        : $"Архив сообщений не найден. Скачайте его командой{(wrap ? "\n" : " ")}Radar --archive-fetch \"{dir}\"";

    /// <summary>Ускоренное воспроизведение: 1 минута реального времени = 1 час архива.</summary>
    public const double DefaultReplaySpeed = 60;

    /// <summary>Окно показывает не живую обстановку (история или статистика): без оповещений, связи и управления каналами.</summary>
    public bool IsReplay => _mode != AppMode.Live;

    public void Start()
    {
        _live.Start();
        _ = SyncArchiveAsync();
        if (Settings.CheckUpdates) CheckForUpdates(silent: true);
        // архив в релизе обновляется ежедневно — долго открытое приложение подтягивает его само
        DispatcherTimer.Run(() => { _ = SyncArchiveAsync(); return true; }, TimeSpan.FromHours(6));
        if (Environment.GetEnvironmentVariable("RADAR_REPLAY") == "1" || Environment.GetCommandLineArgs().Contains("--replay"))
            Mode = AppMode.History;
    }

    // ---------------- воспроизведение ----------------

    public string LiveCaption => _mode switch { AppMode.History => "·  воспроизведение архива", AppMode.Stats => "·  статистика", _ => "·  в реальном времени" };
    /// <summary>Режим для полосы заголовка: в реальном времени (с состоянием связи) / история / статистика.</summary>
    public string ModeTag => _mode switch
    {
        AppMode.History => "ИСТОРИЯ",
        AppMode.Stats => "СТАТИСТИКА",
        _ => Connection switch
        {
            ConnectionState.Offline => "ОТКЛЮЧЕНО",
            ConnectionState.Connecting => "ПОДКЛЮЧЕНИЕ",
            _ => "В РЕАЛЬНОМ ВРЕМЕНИ",
        },
    };

    private ConnectionState _connection = ConnectionState.Connecting;
    /// <summary>Связь с каналами (для индикатора в заголовке).</summary>
    public ConnectionState Connection
    {
        get => _connection;
        private set
        {
            var was = _connection;
            if (!Set(ref _connection, value)) return;
            _offlineDetected = value == ConnectionState.Offline ? DateTimeOffset.UtcNow : null;
            // связь — свойство живого опроса, он идёт и в истории/статистике: объявляем в любом режиме
            if (value == ConnectionState.Offline) AnnounceConnection(lost: true);
            else if (was == ConnectionState.Offline && value == ConnectionState.Online) AnnounceConnection(lost: false);
            OnChanged(nameof(StaleSince));
            OnChanged(nameof(ModeTag));
            OnChanged(nameof(IsOffline));
            OnChanged(nameof(IsConnecting));
        }
    }
    public bool IsConnecting => !IsReplay && Connection == ConnectionState.Connecting;
    public bool IsOffline => !IsReplay && Connection == ConnectionState.Offline;

    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _offlineDetected;
    /// <summary>С какого момента нет свежих данных: последний успешный ответ, а если его не было — момент обнаружения обрыва.</summary>
    public DateTimeOffset? StaleSince => IsOffline ? _lastSuccess ?? _offlineDetected : null;
    /// <summary>Пояснение баннера «нет связи»: когда были последние данные.</summary>
    public string StaleText
    {
        get
        {
            if (_lastSuccess is not { } t)
                return _all.Count > 0
                    ? "С момента запуска данные не получены — на карте сохранённая обстановка прошлого сеанса. Новые тревоги не поступают, попытки подключения продолжаются."
                    : "Данные ещё не получены. Тревоги могут быть, но не поступают — попытки подключения продолжаются.";
            var m = (int)Math.Max(0, (DateTimeOffset.UtcNow - t).TotalMinutes);
            var ago = m < 1 ? "меньше минуты назад" : m < 60 ? $"{m} мин назад" : $"{m / 60} ч {m % 60} мин назад";
            return $"Последние данные в {t.ToLocalTime():HH:mm} ({ago}). Новые тревоги не поступают — попытки подключения продолжаются.";
        }
    }

    private bool _manualRefresh;
    private bool _isBusy;
    /// <summary>Длительная загрузка: история при запуске или ручное обновление всех каналов.</summary>
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    /// <summary>Запрос к каналу начат — импульс на кромке заголовка.</summary>
    public event Action? FetchStarted;
    private int _lastInFlight;

    private string _replayClock = "", _replayDate = "", _replaySpeedText = "";
    private double _replayProgress;
    public string ReplayClock { get => _replayClock; private set => Set(ref _replayClock, value); }
    public string ReplayDate { get => _replayDate; private set => Set(ref _replayDate, value); }
    public double ReplayProgress { get => _replayProgress; private set => Set(ref _replayProgress, value); }
    public string ReplaySpeedText { get => _replaySpeedText; private set => Set(ref _replaySpeedText, value); }
    private string _replaySpeedHint = "";
    public string ReplaySpeedHint { get => _replaySpeedHint; private set => Set(ref _replaySpeedHint, value); }
    private string _replayRange = "";
    public string ReplayRange { get => _replayRange; private set => Set(ref _replayRange, value); }
    public bool ReplayPaused
    {
        get => Clock.IsPaused;
        set { Clock.SetPaused(value); OnChanged(); UpdateReplay(); }
    }

    private static readonly double[] Speeds = [1, 15, 60, 180, 600];

    public void ReplayFaster() => SetReplaySpeed(Speeds.FirstOrDefault(s => s > Clock.Speed + 0.01, Speeds[^1]));
    public void ReplaySlower() => SetReplaySpeed(Speeds.LastOrDefault(s => s < Clock.Speed - 0.01, Speeds[0]));
    public void ReplayRestart() => SeekHistory(0);

    private void SetReplaySpeed(double s) { Clock.SetSpeed(s); UpdateReplay(); }

    // InvariantGlobalization: русские названия задаём вручную
    private static readonly string[] Days = ["вс", "пн", "вт", "ср", "чт", "пт", "сб"];
    private static readonly string[] Months = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];
    private static string RuDate(DateTimeOffset d) => $"{d.Day} {Months[d.Month - 1]}";

    private void UpdateReplay()
    {
        if (!IsHistory || _engine == _live) return;
        var now = Clock.Now;
        var local = now.ToLocalTime();
        ReplayClock = local.ToString("HH:mm");
        ReplayDate = $"{Days[(int)local.DayOfWeek]}, {RuDate(local)}";
        var span = (_engine.ReplayEnd - _engine.ReplayStart).TotalSeconds;
        ReplayProgress = span <= 0 ? 0 : Math.Clamp((now - _engine.ReplayStart).TotalSeconds / span, 0, 1);
        double sp = Clock.Speed;
        ReplayRange = $"{RuDate(_engine.ReplayStart.ToLocalTime())} – {RuDate(_engine.ReplayEnd.ToLocalTime())}";
        OnChanged(nameof(ReplayPaused));
        ReplaySpeedHint = sp switch
        {
            <= 1 => "реальное время",
            < 60 => $"1 мин = {sp:0} мин",
            _ => $"1 мин = {sp / 60:0.#} ч",
        };
        ReplaySpeedText = sp switch
        {
            <= 1 => "×1",
            < 60 => $"×{sp:0}",
            _ => $"×{sp:0}",
        };
    }

    private Action? _frontChanged, _districtReady;
    private IDisposable? _tickTimer;

    public void Shutdown()
    {
        if (_frontChanged != null) { FrontLineSource.Changed -= _frontChanged; _frontChanged = null; }
        if (_districtReady != null) { DistrictCatalogReady -= _districtReady; _districtReady = null; }
        _tickTimer?.Dispose();
        _tickTimer = null;
        if (_engine != _live) _engine.Dispose();
        _live.Dispose();
        TelegramRoute.StatusChanged -= _routeChanged;
        _http.Dispose();
        SettingsStore.Save(Settings);
    }

    public string Status { get => _status; private set => Set(ref _status, value); }

    // ---- строка статуса внизу боковой панели: метка состояния · время · каналы · ошибки ----
    private string _statusTag = "ПОДКЛЮЧЕНИЕ", _statusTime = "", _statusChannels = "", _statusErrors = "", _statusColor = "#7CD4FF";
    public string StatusTag { get => _statusTag; private set => Set(ref _statusTag, value); }
    public string StatusTime { get => _statusTime; private set { if (Set(ref _statusTime, value)) OnChanged(nameof(HasStatusTime)); } }
    public bool HasStatusTime => _statusTime.Length > 0;
    public string StatusChannels { get => _statusChannels; private set => Set(ref _statusChannels, value); }
    public string StatusErrors { get => _statusErrors; private set { if (Set(ref _statusErrors, value)) OnChanged(nameof(HasStatusErrors)); } }
    public bool HasStatusErrors => _statusErrors.Length > 0;
    public string StatusColor { get => _statusColor; private set => Set(ref _statusColor, value); }

    private bool _historyLoading = true;
    private double _historyProgress;
    private string _historyTitle = "Подключение к каналам", _historyDetail = "";
    /// <summary>Идёт первичная загрузка истории — на карте показывается плашка с прогрессом.</summary>
    public bool HistoryLoading { get => _historyLoading && !IsReplay; private set => Set(ref _historyLoading, value); }
    /// <summary>Только для офлайн-снимка: задать счётчики групп в заголовке.</summary>
    internal void DebugSetTelemetry(int[] counts)
    {
        for (int i = 0; i < TelemetryKinds.Count; i++) { TelemetryKinds[i].Count = i < counts.Length ? counts[i] : 0; TelemetryKinds[i].Notify(); }
        OnChanged(nameof(HasTelemetryKinds));
    }

    /// <summary>Только для офлайн-снимка: показать события в ленте.</summary>
    internal void DebugSetEvents(IReadOnlyList<AlertEvent> events) { _all = events; Refresh(); }

    /// <summary>Замер: время одного обновления обстановки (фильтры, лента, треки, счётчики, карта).</summary>
    internal double DebugRefreshMs(IReadOnlyList<AlertEvent> events)
    {
        _all = events;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Refresh();
        return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Только для офлайн-снимка: сообщения и состояния каналов.</summary>
    internal void DebugFillSources(IEnumerable<RawMessage> msgs)
    {
        foreach (var m in msgs.TakeLast(20))
        {
            var p = MessageParser.Parse(m, []);
            if (!p.IsJunk) Feed.Insert(0, new FeedItem(m, p));
        }
        OnChanged(nameof(FeedEmpty));
        var now = DateTimeOffset.UtcNow;
        UpdateChannels(
        [
            new("LPRalarm", "Основной канал тревог", true, true, false, 0, null, now.AddSeconds(-20), now.AddMinutes(-3), 412),
            new("lpr1_Crimea_Alarm", "Крым и Севастополь", true, true, true, 0, null, now.AddSeconds(-40), now.AddMinutes(-18), 96),
            new("lpr1_Kursk_alarm", "Курская область", true, true, false, 0, null, now.AddMinutes(-1), now.AddHours(-14), 12),
            new("lpr1_Orlov_alarm", "Орловская область", true, false, false, 3, "таймаут", now.AddMinutes(-9), now.AddHours(-2), 30),
            new("kupolrussia", "Купол России", false, false, false, 0, null, null, null, 0),
        ]);
        (StatusTag, StatusColor, StatusTime, StatusChannels, StatusErrors) = ("ОБНОВЛЕНО", "#4FB286", $"{now.ToLocalTime():HH:mm:ss}", "каналов 3", "ошибок 1");
    }

    internal void DebugSetOffline() => Connection = ConnectionState.Offline;

    /// <summary>Только для офлайн-снимка: каналы на связи, история загружена (без плашки «Подключение … 0%»).</summary>
    internal void DebugSetOnline()
    {
        Connection = ConnectionState.Online;
        HistoryLoading = false;
        OnChanged(nameof(FeedEmpty));
    }

    internal void DebugShowReplay()
    {
        _mode = AppMode.History;
        foreach (var n in new[] { nameof(IsReplay), nameof(IsHistory), nameof(IsLive), nameof(ModeTag), nameof(ModeOptions), nameof(ModeTagColor) }) OnChanged(n);
        ReplayClock = "14:37"; ReplayDate = "ср, 12 мар"; ReplayRange = "9 мар – 16 мар";
        ReplaySpeedText = "×60"; ReplaySpeedHint = "1 мин = 1 ч"; ReplayProgress = 0.42;
    }
    /// <summary>Только для офлайн-снимка интерфейса: показать панель загрузки истории.</summary>
    internal void DebugShowHistory()
    {
        HistoryTitle = $"ИСТОРИЯ ЗА {Settings.HistoryHours} Ч"; HistoryDetail = "каналов 5 из 13 · сообщений 1240";
        HistoryProgress = 0.4; HistoryLoading = true; OnChanged(nameof(HistoryLoading));
    }
    public double HistoryProgress { get => _historyProgress; private set { Set(ref _historyProgress, value); OnChanged(nameof(HistoryPercent)); } }
    public string HistoryPercent => $"{_historyProgress * 100:0}%";
    public string HistoryTitle { get => _historyTitle; private set => Set(ref _historyTitle, value); }
    public string HistoryDetail { get => _historyDetail; private set => Set(ref _historyDetail, value); }

    private IEnumerable<AlertEvent> Active => _all.Where(e => e.Status is not (AlertStatus.AllClear or AlertStatus.Launch));
    public int ActiveCount => Active.Count();
    private IReadOnlyList<TelemetryItem>? _telemetryKinds;
    /// <summary>Счётчики по группам угроз для заголовка (порядок и группы — как в легенде).</summary>
    public IReadOnlyList<TelemetryItem> TelemetryKinds => _telemetryKinds ??= Legend.Select(g =>
    {
        var t = g.Items.Count > 0 ? ThreatCatalog.Info(g.Items[0].Kind).Group : ThreatType.General;
        var info = ThreatCatalog.GroupDefault(t);
        string name = t switch { ThreatType.Balloon => "Аэростаты и МРШ", ThreatType.Ground => "ДРГ", _ => g.Title };
        return new TelemetryItem(t, name, info.Color, info.Icon, g);
    }).ToList();
    public bool HasTelemetryKinds => TelemetryKinds.Any(k => k.Visible);
    public int RegionCount => Active.SelectMany(e => e.RegionIds).Distinct().Count();

    private bool _reordering;
    /// <summary>Выбранная тревога переставлена в ленте: окно восстанавливает выделение в списке без перефокусировки карты.</summary>
    public event Action<AlertEvent>? SelectionRestored;

    public AlertEvent? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            if (_reordering && value == null) return;
            if (_selected != null) _selected.IsSelected = false;
            _selected = value;
            if (_selected != null) _selected.IsSelected = true;
            OnChanged();
            // в режиме «только мои» выбранная на карте чужая тревога добавляется в ленту (Refresh держит Selected в ней)
            if (OnlyMine && _selected != null && !Events.Contains(_selected)) Refresh();
        }
    }

    public string FilterText { get => _filterText; set { if (Set(ref _filterText, value)) Refresh(); } }
    public bool ShowUav { get => _showUav; set { if (Set(ref _showUav, value)) { Settings.ShowUav = value; SettingsStore.SaveDeferred(Settings); Refresh(); } } }
    public bool ShowMissile { get => _showMissile; set { if (Set(ref _showMissile, value)) { Settings.ShowMissile = value; SettingsStore.SaveDeferred(Settings); Refresh(); } } }
    public bool ShowOther { get => _showOther; set { if (Set(ref _showOther, value)) { Settings.ShowOther = value; SettingsStore.SaveDeferred(Settings); Refresh(); } } }
    public bool ShowAllClear { get => _showAllClear; set { if (Set(ref _showAllClear, value)) { Settings.ShowAllClear = value; SettingsStore.SaveDeferred(Settings); Refresh(); } } }
    /// <summary>
    /// Лента показывает только тревоги выбранных регионов. Карта не фильтруется (обстановка вокруг важна),
    /// там «свои» и так выделены контуром и ореолом. Без выбранных регионов фильтр недоступен.
    /// </summary>
    public bool OnlyMine
    {
        get => Settings.OnlyMine && HasPickedRegions;
        set
        {
            if (Settings.OnlyMine == value) return;
            Settings.OnlyMine = value;
            SettingsStore.SaveDeferred(Settings);
            OnChanged();
            Refresh();
        }
    }
    /// <summary>Сообщения о пусках (место пуска на стороне противника).</summary>
    public bool ShowLaunch { get => _showLaunch; set { if (Set(ref _showLaunch, value)) { Settings.ShowLaunch = value; SettingsStore.SaveDeferred(Settings); Refresh(); } } }
    public int LaunchCount => _all.Count(e => e.Status == AlertStatus.Launch);
    public bool HasLaunches => LaunchCount > 0;

    public IReadOnlyList<LegendGroup> Legend { get; } =
    [
        new("Беспилотники", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Uav)),
        new("Ракеты", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Missile)),
        new("Авиация", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Aviation)),
        new("Морские дроны", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Naval), "Море"),
        new("Обстрелы и минирование", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Artillery), "Обстрелы"),
        new("Аэростаты", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Balloon)),
        new("Диверсионные группы", ThreatCatalog.All.Values.Where(i => i.Group == ThreatType.Ground), "ДРГ"),
    ];

    private bool _legendExpanded;
    private readonly HashSet<ThreatKind> _hiddenKinds = [];

    /// <summary>Есть ли скрытые через легенду типы (показывает кнопку «показать все»).</summary>
    public bool HasHiddenKinds => _hiddenKinds.Count > 0 || _hiddenLevels.Count > 0 || !ShowExpected || !ShowFrontLine;

    /// <summary>Линия соприкосновения на карте.</summary>
    public bool ShowFrontLine => Settings.ShowFrontLine;
    public double FrontLineOpacity => ShowFrontLine ? 1.0 : 0.3;
    /// <summary>Источник линии для легенды: «данные на 01.10 20:23» или «встроенная, приблизительно».</summary>
    public string FrontLineSourceText
    {
        get
        {
            if (!Borderline.IsLive) return "встроенная, приблизительно";
            var t = FrontLineSource.DataTime ?? FrontLineSource.LoadedAt;
            if (t is not { } dt) return "актуальная";
            var local = dt.ToLocalTime();
            var age = DateTimeOffset.UtcNow - dt;
            string when = local.Date == DateTime.Today ? $"сегодня {local:HH:mm}"
                : local.Date == DateTime.Today.AddDays(-1) ? $"вчера {local:HH:mm}"
                : local.ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            return age > TimeSpan.FromDays(3) ? $"данные на {when} — устарели" : $"данные на {when}";
        }
    }

    /// <summary>Подпись источника тусклее, если линия встроенная или данные устарели.</summary>
    public string FrontLineSourceColor =>
        Borderline.IsLive && (FrontLineSource.DataTime ?? FrontLineSource.LoadedAt) is { } t && DateTimeOffset.UtcNow - t <= TimeSpan.FromDays(3)
            ? "#8C97A7" : "#9A5546";

    public string FrontLineTip => (Borderline.IsLive
            ? "Линия соприкосновения по открытым данным DeepStateMap (обновляется раз в сутки): через неё объекты заходят в ЛНР, ДНР, " +
              "Запорожскую и Херсонскую области; по ней строится начало траекторий и определяется место пуска.\n"
            : "Встроенная приблизительная линия соприкосновения (точность 10–20 км): актуальные данные ещё не загружены или нет сети.\n") +
        (ShowFrontLine ? "Нажмите, чтобы скрыть на карте" : "Скрыто — нажмите, чтобы показать");

    public void ToggleFrontLine()
    {
        Settings.ShowFrontLine = !Settings.ShowFrontLine;
        NotifyFrontLine();
        FiltersChanged();
    }

    private void NotifyFrontLine()
    {
        OnChanged(nameof(ShowFrontLine));
        OnChanged(nameof(FrontLineOpacity));
        OnChanged(nameof(FrontLineTip));
        OnChanged(nameof(FrontLineSourceText));
        OnChanged(nameof(FrontLineSourceColor));
    }

    /// <summary>Подсветка регионов «ожидается» (куда направлена угроза).</summary>
    public bool ShowExpected => Settings.ShowExpected;
    public double ExpectedOpacity => ShowExpected ? 1.0 : 0.3;
    public string ExpectedTip => "Регион, куда направлена угроза («в сторону … области»): слабая заливка от стороны подлёта, пунктирный контур.\n" +
                                 (ShowExpected ? "Нажмите, чтобы скрыть на карте" : "Скрыто — нажмите, чтобы показать");
    public int ExpectedCount => _all.Where(e => e.Status != AlertStatus.AllClear && KindShown(e.Kind, e.Threat, e.Kinds) && !_hiddenLevels.Contains(e.EffectiveLevel))
        .SelectMany(e => e.ExpectedRegionIds).Distinct().Count();
    public bool HasExpectedCount => ExpectedCount > 0;

    public void ToggleExpected()
    {
        Settings.ShowExpected = !Settings.ShowExpected;
        OnChanged(nameof(ShowExpected));
        OnChanged(nameof(ExpectedOpacity));
        OnChanged(nameof(ExpectedTip));
        FiltersChanged();
    }

    public string HiddenKindsText
    {
        get
        {
            static string Pl(int n, string one, string few, string many) =>
                $"{n} " + ((n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many);
            var parts = new List<string>();
            if (_hiddenKinds.Count > 0) parts.Add(Pl(_hiddenKinds.Count, "тип угроз", "типа угроз", "типов угроз"));
            if (_hiddenLevels.Count > 0) parts.Add(Pl(_hiddenLevels.Count, "уровень", "уровня", "уровней"));
            if (!ShowExpected) parts.Add("«ожидается»");
            if (!ShowFrontLine) parts.Add("линия соприкосновения");
            return "Скрыто " + string.Join(" и ", parts);
        }
    }

    public string HiddenKindsTip => "Не показываются на карте: " +
        string.Join(", ", Legend.SelectMany(g => g.Items).Where(i => !i.Shown).Select(i => i.Name)
            .Concat(Levels.Where(l => !l.Shown).Select(l => $"уровень «{l.Name}»"))
            .Concat(ShowExpected ? [] : ["регионы «ожидается»"])
            .Concat(ShowFrontLine ? [] : ["линия соприкосновения"])) + ". Нажмите, чтобы показать все.";

    public IReadOnlyList<LevelItem> Levels { get; } =
    [
        new(AlertLevel.Attention, "Возможна угроза: пунктирный контур, без заливки"),
        new(AlertLevel.Danger, "Угроза пусков/подлёта: бледная заливка, медленный пульс"),
        new(AlertLevel.Alarm, "Цель в воздухе: полная заливка, двойная кайма"),
        new(AlertLevel.Strike, "Прилёты / работа ПВО: вспышка и частый пульс"),
    ];
    private readonly HashSet<AlertLevel> _hiddenLevels = [];

    public void ToggleLevel(LevelItem item)
    {
        item.Shown = !item.Shown;
        if (item.Shown) _hiddenLevels.Remove(item.Level); else _hiddenLevels.Add(item.Level);
        FiltersChanged();
    }

    public void ToggleKind(LegendItem item) => SetKindsShown([item], !item.Shown);

    /// <summary>Клик по заголовку группы: если в группе что-то скрыто — показать всё, иначе скрыть всю группу.</summary>
    public void ToggleGroup(LegendGroup g) => SetKindsShown(g.Items, g.Items.Any(i => !i.Shown));

    public void ShowAllKinds()
    {
        foreach (var l in Levels) l.Shown = true;
        _hiddenLevels.Clear();
        if (!Settings.ShowExpected)
        {
            Settings.ShowExpected = true;
            OnChanged(nameof(ShowExpected));
            OnChanged(nameof(ExpectedOpacity));
            OnChanged(nameof(ExpectedTip));
        }
        if (!Settings.ShowFrontLine)
        {
            Settings.ShowFrontLine = true;
            NotifyFrontLine();
        }
        SetKindsShown(Legend.SelectMany(g => g.Items), true);
    }

    /// <summary>Счётчики легенды по типам и группам (текущие тревоги видимых уровней).</summary>
    private void UpdateLegendCounts()
    {
        var kindCounts = new Dictionary<ThreatKind, int>();
        var groupCounts = new int[Legend.Count];
        foreach (var e in _all)
        {
            if (e.Status == AlertStatus.AllClear || _hiddenLevels.Contains(e.EffectiveLevel)) continue;
            var ks = e.Kinds.Count > 1 ? e.Kinds.Distinct().ToList()
                : [e.Kind != ThreatKind.Unknown ? e.Kind : ThreatCatalog.GroupDefault(e.Threat).Kind];
            foreach (var k in ks) kindCounts[k] = kindCounts.GetValueOrDefault(k) + 1;
            // в группе событие учитывается, если хотя бы один его тип из группы не скрыт
            for (int g = 0; g < Legend.Count; g++)
                if (Legend[g].Items.Any(i => i.Shown && ks.Contains(i.Kind))) groupCounts[g]++;
        }
        foreach (var i in Legend.SelectMany(g => g.Items)) i.Count = kindCounts.GetValueOrDefault(i.Kind);
        for (int g = 0; g < Legend.Count; g++) Legend[g].Count = groupCounts[g];
    }

    private void SetKindsShown(IEnumerable<LegendItem> items, bool shown)
    {
        foreach (var i in items)
        {
            i.Shown = shown;
            if (shown) _hiddenKinds.Remove(i.Kind); else _hiddenKinds.Add(i.Kind);
        }
        FiltersChanged();
    }

    private void FiltersChanged()
    {
        Settings.HiddenKinds = _hiddenKinds.Select(k => k.ToString()).Order().ToList();
        Settings.HiddenLevels = _hiddenLevels.Select(k => k.ToString()).Order().ToList();
        SettingsStore.SaveDeferred(Settings);
        OnChanged(nameof(HasHiddenKinds));
        OnChanged(nameof(HiddenKindsText));
        OnChanged(nameof(HiddenKindsTip));
        foreach (var k in TelemetryKinds) k.Notify();
        OnChanged(nameof(HasTelemetryKinds));
        Refresh();
    }

    private void RestoreHiddenKinds()
    {
        foreach (var name in Settings.HiddenKinds)
            if (Enum.TryParse<ThreatKind>(name, out var k)) _hiddenKinds.Add(k);
        foreach (var i in Legend.SelectMany(g => g.Items)) i.Shown = !_hiddenKinds.Contains(i.Kind);
        foreach (var name in Settings.HiddenLevels)
            if (Enum.TryParse<AlertLevel>(name, out var l)) _hiddenLevels.Add(l);
        foreach (var l in Levels) l.Shown = !_hiddenLevels.Contains(l.Level);
    }

    /// <summary>Событие видно, если виден хотя бы один из его типов (для неуточнённых — тип группы по умолчанию).</summary>
    private bool KindShown(ThreatKind kind, ThreatType threat, IReadOnlyList<ThreatKind>? kinds = null)
    {
        if (_hiddenKinds.Count == 0) return true;
        if (kinds is { Count: > 1 }) return kinds.Any(k => !_hiddenKinds.Contains(k));
        var k = kind != ThreatKind.Unknown ? kind : ThreatCatalog.GroupDefault(threat).Kind;
        return !_hiddenKinds.Contains(k);
    }
    public bool LegendExpanded { get => _legendExpanded; set { if (Set(ref _legendExpanded, value)) { Settings.LegendExpanded = value; SettingsStore.SaveDeferred(Settings); } } }

    /// <summary>Легенда свёрнута в кнопку (состояние сохраняется между запусками).</summary>
    public bool LegendCollapsed
    {
        get => Settings.LegendCollapsed;
        set
        {
            if (Settings.LegendCollapsed == value) return;
            Settings.LegendCollapsed = value;
            SettingsStore.SaveDeferred(Settings);
            OnChanged();
            OnChanged(nameof(LegendOpen));
        }
    }
    public bool LegendOpen => !LegendCollapsed;

    /// <summary>Таблица источников (вкладка «Источники»).</summary>
    public ObservableCollection<ChannelRow> Channels { get; } = [];
    private int _channelsOk, _channelsTotal;
    // ================= вкладка «Настройки» =================

    /// <summary>Окно приложения в фокусе (уведомления «только в фоне» в это время не показываются).</summary>
    public bool WindowActive { get; set; } = true;

    private void SaveSetting([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        SettingsStore.SaveDeferred(Settings);
        OnChanged(name);
    }

    public bool RetroNoise { get => Settings.RetroNoise; set { if (Settings.RetroNoise == value) return; Settings.RetroNoise = value; SaveSetting(); } }
    public bool SignalLossEffect { get => Settings.SignalLossEffect; set { if (Settings.SignalLossEffect == value) return; Settings.SignalLossEffect = value; SaveSetting(); } }
    public bool Vignette { get => Settings.Vignette; set { if (Settings.Vignette == value) return; Settings.Vignette = value; SaveSetting(); } }
    public int AnimationQuality { get => Settings.AnimationQuality; set { value = Math.Clamp(value, 0, 2); if (Settings.AnimationQuality == value) return; Settings.AnimationQuality = value; SaveSetting(); } }
    public bool Notifications { get => Settings.Notifications; set { if (Settings.Notifications == value) return; Settings.Notifications = value; SaveSetting(); } }
    public bool NotifyOnlyInactive { get => Settings.NotifyOnlyInactive; set { if (Settings.NotifyOnlyInactive == value) return; Settings.NotifyOnlyInactive = value; SaveSetting(); } }
    public bool NotifyConnection { get => Settings.NotifyConnection; set { if (Settings.NotifyConnection == value) return; Settings.NotifyConnection = value; SaveSetting(); } }
    public bool Sound { get => Settings.Sound; set { if (Settings.Sound == value) return; Settings.Sound = value; SaveSetting(); } }
    public bool CloseToTray { get => Settings.CloseToTray; set { if (Settings.CloseToTray == value) return; Settings.CloseToTray = value; SaveSetting(); } }
    public bool StartMinimized { get => Settings.StartMinimized; set { if (Settings.StartMinimized == value) return; Settings.StartMinimized = value; SaveSetting(); } }
    public bool StartWithSystem
    {
        get => Settings.StartWithSystem;
        set
        {
            if (Settings.StartWithSystem == value) return;
            // не удалось записать запуск — переключатель остаётся выключенным, а не врёт
            Settings.StartWithSystem = value && Autostart.Set(true);
            if (!value) Autostart.Set(false);
            SaveSetting();
        }
    }
    public bool CheckUpdates { get => Settings.CheckUpdates; set { if (Settings.CheckUpdates == value) return; Settings.CheckUpdates = value; SaveSetting(); } }
    public bool HighlightMyRegions { get => Settings.HighlightMyRegions; set { if (Settings.HighlightMyRegions == value) return; Settings.HighlightMyRegions = value; SaveSetting(); MarkMine(); } }

    // ---- данные: опрос каналов и время жизни тревог (применяются на ходу) ----

    /// <summary>Пауза между опросами канала, секунд (новое значение действует со следующего опроса).</summary>
    public int PollInterval
    {
        get => Settings.PollIntervalSeconds;
        set
        {
            value = Math.Clamp(value, 5, 600);
            if (Settings.PollIntervalSeconds == value) return;
            Settings.PollIntervalSeconds = value;
            SaveSetting();
            _live.PollIntervalChanged();
        }
    }

    /// <summary>Доступ к Telegram: 0 — авто, 1 — через прокси, 2 — напрямую.</summary>
    public int TelegramAccess
    {
        get => Settings.TelegramAccess;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (Settings.TelegramAccess == value) return;
            Settings.TelegramAccess = value;
            TelegramRoute.Mode = (Services.TelegramAccess)value;
            TelegramRoute.Reset();
            SaveSetting();
            OnChanged();
            OnChanged(nameof(TelegramRouteText));
        }
    }

    public string TelegramRouteText => TelegramRoute.Status is { Length: > 0 } s ? "Сейчас: " + s
        : TelegramAccess == 1 ? "Сейчас: поиск рабочих прокси…" : "Сейчас: напрямую";

    /// <summary>Сколько минут тревога остаётся на карте без новых сообщений.</summary>
    public int AlertLifetime
    {
        get => Settings.AlertLifetimeMinutes;
        set
        {
            if (Settings.AlertLifetimeMinutes == value) return;
            SetLifetimes(value, Settings.AllClearLifetimeMinutes);
            SaveSetting();
        }
    }

    /// <summary>Сколько минут на карте виден отбой.</summary>
    public int AllClearLifetime
    {
        get => Settings.AllClearLifetimeMinutes;
        set
        {
            if (Settings.AllClearLifetimeMinutes == value) return;
            SetLifetimes(Settings.AlertLifetimeMinutes, value);
            SaveSetting();
        }
    }

    /// <summary>Сроки жизни — живому движку (он хранит их в настройках, с ограничением диапазона) и воспроизведению истории.</summary>
    private void SetLifetimes(int alert, int allClear)
    {
        _live.SetLifetimes(alert, allClear);
        if (_engine != _live) _engine.SetLifetimes(Settings.AlertLifetimeMinutes, Settings.AllClearLifetimeMinutes);
        OnChanged(nameof(AlertLifetime));
        OnChanged(nameof(AllClearLifetime));
    }

    /// <summary>Порог уведомлений: 0 — «внимание» и выше, 1 — «опасность», 2 — «тревога», 3 — только «удар».</summary>
    public int NotifyLevelIndex
    {
        get => Enum.TryParse<AlertLevel>(Settings.NotifyMinLevel, out var l) ? (int)l : (int)AlertLevel.Danger;
        set
        {
            var l = (AlertLevel)Math.Clamp(value, 0, 3);
            if (Settings.NotifyMinLevel == l.ToString()) return;
            Settings.NotifyMinLevel = l.ToString();
            SaveSetting();
        }
    }

    /// <summary>Громкость: 0 — тихо, 1 — средне, 2 — громко.</summary>
    public int SoundVolume
    {
        get => Math.Clamp(Settings.SoundVolume, 0, 2);
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (Settings.SoundVolume == value) return;
            Settings.SoundVolume = value;
            SaveSetting();
            if (Settings.Sound) Notifier.Play(Chime.Alert, value, preview: true);
        }
    }

    public void TestNotification()
    {
        Notifier.Notify("Тревога: Беспилотники", "Проверка уведомлений — так будут выглядеть оповещения о новых тревогах", urgent: false);
        if (Settings.Sound) Notifier.Play(Chime.Urgent, SoundVolume, preview: true);
    }

    private readonly HashSet<AlertEvent> _announced = [];
    // тревоги, о которых действительно сообщили (прошли порог уровня и зоны) — для «Приближается»
    private readonly HashSet<AlertEvent> _notified = [];

    private bool ShouldAnnounce => !IsReplay && (!Settings.NotifyOnlyInactive || !WindowActive);

    /// <summary>Новая тревога или повышение уровня: системное уведомление и звук по настройкам.</summary>
    private void Announce(AlertEvent e, bool levelUp)
    {
        if (!ShouldAnnounce || e.Status is AlertStatus.AllClear) return;
        if (!KindShown(e.Kind, e.Threat, e.Kinds)) return;
        if (!InNotifyRegions(e)) return;
        var lvl = e.Status == AlertStatus.Launch ? e.Level : e.EffectiveLevel;
        if ((int)lvl < NotifyLevelIndex) return;
        _notified.Add(e);
        bool urgent = lvl >= AlertLevel.Alarm;
        var near = ProximityOf(e);
        if (near is { Motion: Proximity.Motion.Approaching }) _approachAnnounced.Add((e, near.Place.Id));
        if (Settings.Notifications)
        {
            var (title, body) = Notifier.Describe(e);
            if (near != null) body = Proximity.Text(near, e.Info.Short) + "\n" + body;
            Notifier.Notify(levelUp ? "Повышение: " + title : title, body, urgent);
        }
        if (Settings.Sound) Notifier.Play(urgent ? Chime.Urgent : Chime.Alert, SoundVolume);
    }

    // снимок треков на один Refresh: копия под блокировкой движка дорогая, а нужна для каждой объявляемой тревоги
    private IReadOnlyList<Track>? _refreshTracks;
    private Dictionary<AlertEvent, Track>? _refreshTrackOf;
    private IReadOnlyList<Track> RefreshTracks() => _refreshTracks ??= _engine.TrackSnapshot();

    private Dictionary<AlertEvent, Track> TrackOf()
    {
        if (_refreshTrackOf != null) return _refreshTrackOf;
        var map = new Dictionary<AlertEvent, Track>();
        foreach (var t in RefreshTracks())
            foreach (var e in t.Events)
                if (!map.TryGetValue(e, out var o) || t.Updated > o.Updated) map[e] = t;
        return _refreshTrackOf = map;
    }

    // объявленные сближения: (тревога, пункт) — «идёт к вам» звучит один раз, а не при каждом обновлении
    private readonly HashSet<(AlertEvent, string)> _approachAnnounced = [];

    /// <summary>Положение угрозы относительно ближайшего выбранного пункта или своей точки (по треку тревоги).</summary>
    private Proximity.Report? ProximityOf(AlertEvent e)
    {
        if (!Settings.WatchAreas.Any(a => a.Kind == WatchArea.PlaceKind)) return null;
        var track = TrackOf().GetValueOrDefault(e);
        return Proximity.Compute(e, track, Settings.WatchAreas, Settings.WatchRadiusKm, Clock.Now);
    }

    /// <summary>
    /// Уже объявленная угроза повернула к выбранному пункту (или своей точке) и находится не дальше трёх его радиусов
    /// (не меньше 30 км): отдельное срочное уведомление «идёт к вам» — один раз на пару «тревога — пункт».
    /// </summary>
    private void AnnounceApproaches(IEnumerable<AlertEvent> shown)
    {
        if (!ShouldAnnounce || !Settings.WatchAreas.Any(a => a.Kind == WatchArea.PlaceKind)) return;
        foreach (var e in shown)
        {
            // только то, о чём уже сообщили: тревога ниже порога уровня или вне зон не становится срочной «Приближается»
            if (e.Status is AlertStatus.AllClear || !_notified.Contains(e) || !KindShown(e.Kind, e.Threat, e.Kinds)) continue;
            if (ProximityOf(e) is not { Motion: Proximity.Motion.Approaching } r) continue;
            double radius = r.Place.RadiusKm > 0 ? r.Place.RadiusKm : Settings.WatchRadiusKm;
            if (r.DistanceKm > Math.Max(30, radius * 3) || !_approachAnnounced.Add((e, r.Place.Id))) continue;
            if (Settings.Notifications) Notifier.Notify($"Приближается: {e.ThreatText}", Proximity.Text(r, e.Info.Short), urgent: true);
            if (Settings.Sound) Notifier.Play(Chime.Urgent, SoundVolume);
        }
        if (_approachAnnounced.Count > 400) _approachAnnounced.RemoveWhere(x => !_all.Contains(x.Item1));
    }

    // ---- зоны наблюдения: регионы, районы и населённые пункты ----

    /// <summary>Выбранные зоны (чипами над поиском).</summary>
    public IReadOnlyList<WatchChoice> PickedAreas => [.. Settings.WatchAreas.Select(a => Choice(a, picked: true))];
    public bool HasPickedRegions => Settings.WatchAreas.Count > 0;
    public string NotifyRegionsText
    {
        get
        {
            var w = Settings.WatchAreas;
            if (w.Count == 0) return "по всем регионам";
            if (w.Count == 1) return w[0].Name;
            static string Pl(int n, string one, string few, string many) =>
                $"{n} " + ((n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many);
            return Pl(w.Count, "зона", "зоны", "зон");
        }
    }
    /// <summary>Выбранные регионы целиком (для подсветки на карте).</summary>
    public IReadOnlySet<string> NotifyRegionSet => Settings.WatchAreas.Where(a => a.Kind == WatchArea.RegionKind).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
    /// <summary>Все зоны (для подсветки на карте: регионы и районы — контуром, пункты — кругом).</summary>
    public IReadOnlyList<WatchArea> WatchAreas => Settings.WatchAreas;
    public int WatchRadiusKm
    {
        get => Settings.WatchRadiusKm;
        set
        {
            value = Math.Clamp(value, 3, 100);
            if (Settings.WatchRadiusKm == value) return;
            Settings.WatchRadiusKm = value;
            SaveSetting();
            OnChanged(nameof(WatchAreas));
            AreasChanged();
        }
    }
    public bool HasPickedPlaces => Settings.WatchAreas.Any(a => a.Kind == WatchArea.PlaceKind);

    /// <summary>Выбранные населённые пункты со своими радиусами.</summary>
    public IReadOnlyList<WatchPlaceRow> PickedPlaces => [.. Settings.WatchAreas.Where(a => a.Kind == WatchArea.PlaceKind)
        .Select(a => new WatchPlaceRow(a, () => Settings.WatchRadiusKm, OnPlaceRadius, OnPlaceRenamed))];

    private void OnPlaceRenamed(WatchPlaceRow r)
    {
        SettingsStore.SaveDeferred(Settings);
        OnChanged(nameof(PickedAreas));
        OnChanged(nameof(NotifyRegionsText));
        OnChanged(nameof(WatchAreas));
        Refresh();
    }

    private void OnPlaceRadius(WatchPlaceRow r)
    {
        SettingsStore.SaveDeferred(Settings);
        OnChanged(nameof(WatchAreas));
        // подписи близости к своим пунктам (NearText) считаются в Refresh
        Refresh();
    }

    private string _regionSearch = "";
    /// <summary>Поиск по регионам, районам и населённым пунктам.</summary>
    public string RegionSearch
    {
        get => _regionSearch;
        set { if (Set(ref _regionSearch, value ?? "")) { OnChanged(nameof(RegionChoicesView)); OnChanged(nameof(SearchHint)); } }
    }

    /// <summary>Подсказка под списком: что искать, если ничего не найдено.</summary>
    public string SearchHint => _regionSearch.Trim().Length switch
    {
        0 => "",
        1 => "Введите ещё хотя бы одну букву",
        _ => RegionChoicesView.Any() ? "" : "Ничего не найдено",
    };

    private List<WatchChoice>? _regionList;

    /// <summary>
    /// Без запроса — все регионы РФ по алфавиту; с запросом — подходящие регионы, затем районы, затем населённые пункты
    /// (крупные первыми).
    /// </summary>
    public IReadOnlyList<WatchChoice> RegionChoicesView
    {
        get
        {
            // результат запроса кешируется: подсказка и список читают его по нескольку раз на каждое нажатие,
            // а новые элементы заставляли бы список пересоздавать все строки
            var q = Gazetteer.Normalize(_regionSearch.Trim());
            if (_choices is { } ch && ch.Query == q && ch.Districts == DistrictCatalog)
            {
                foreach (var c in ch.List) c.SetSilently(IsPicked(c.Area));
                return ch.List;
            }
            var list = BuildChoices(q);
            _choices = (q, DistrictCatalog, list);
            return list;
        }
    }

    private (string Query, object? Districts, IReadOnlyList<WatchChoice> List)? _choices;

    private IReadOnlyList<WatchChoice> BuildChoices(string q)
    {
        _regionList ??= [.. Gazetteer.Regions.Values.Where(r => !Gazetteer.IsForeign(r.Id)).OrderBy(r => r.Name, StringComparer.CurrentCulture)
            .Select(r => new WatchArea { Kind = WatchArea.RegionKind, Id = r.Id, Name = r.Name, Region = r.Id })
            .Select(a => Choice(a))];
        foreach (var c in _regionList) c.SetSilently(IsPicked(c.Area));
        if (q.Length == 0) return _regionList;
        if (q.Length < 2) return [.. _regionList.Where(c => Gazetteer.Normalize(c.Name).StartsWith(q, StringComparison.Ordinal))];
        var res = new List<WatchChoice>();
        res.AddRange(_regionList.Where(c => Gazetteer.Normalize(c.Name).Split(' ', '-').Any(w => w.StartsWith(q, StringComparison.Ordinal))));
        if (DistrictCatalog is { } dc)
            res.AddRange(dc.Where(d => d.Key.StartsWith(q, StringComparison.Ordinal)).Take(20)
                .Select(d => Choice(new WatchArea { Kind = WatchArea.DistrictKind, Id = d.Id, Name = d.Name, Region = d.Region })));
        res.AddRange(PlaceIndex.SearchPlaces(q, 25).Select(p => Choice(new WatchArea
        {
            Kind = WatchArea.PlaceKind, Id = WatchArea.PlaceId(p.Name, p.Pos.Lat, p.Pos.Lon), Name = p.Name, Region = p.RegionId, Lat = p.Pos.Lat, Lon = p.Pos.Lon,
        }, population: p.Population)));
        return res;
    }

    /// <summary>Районы для поиска (ключ — нормализованное название, id — «регион|ключ»); задаётся слоем карты.</summary>
    public static IReadOnlyList<(string Key, string Id, string Name, string Region)>? DistrictCatalog { get; set; }
    /// <summary>Каталог районов загружен (в UI-потоке): открытый поиск зон показывает и районы.</summary>
    public static event Action? DistrictCatalogReady;

    private WatchChoice Choice(WatchArea a, bool picked = false, int population = 0)
    {
        string region = Gazetteer.Get(a.Region)?.Name ?? "";
        string hint = a.Kind switch
        {
            WatchArea.DistrictKind => region,
            WatchArea.PlaceKind => region + (population >= 1000 ? $" · {population / 1000.0:0.#} тыс. жит." : ""),
            _ => "",
        };
        var c = new WatchChoice(a, hint, OnWatchChoice);
        c.SetSilently(picked || IsPicked(a));
        return c;
    }

    private bool IsPicked(WatchArea a) => Settings.WatchAreas.Any(x => x.Kind == a.Kind && x.Id == a.Id);

    private void OnWatchChoice(WatchChoice c)
    {
        var a = c.Area;
        if (c.Picked)
        {
            if (!IsPicked(a))
            {
                if (a.Kind == WatchArea.PlaceKind && a.RadiusKm <= 0) a.RadiusKm = Settings.WatchRadiusKm;
                Settings.WatchAreas.Add(a);
            }
        }
        else Settings.WatchAreas.RemoveAll(x => x.Kind == a.Kind && x.Id == a.Id);
        AreasChanged(save: true);
    }

    private void AreasChanged(bool save = false)
    {
        // зон не осталось — фильтр «только мои» выключается, а не ждёт скрыто: иначе после добавления первой зоны
        // он молча включился бы снова и спрятал ленту
        if (Settings.WatchAreas.Count == 0 && Settings.OnlyMine) { Settings.OnlyMine = false; save = true; }
        if (save) SettingsStore.SaveDeferred(Settings);
        OnChanged(nameof(PickedAreas));
        OnChanged(nameof(HasPickedRegions));
        OnChanged(nameof(HasPickedPlaces));
        OnChanged(nameof(PickedPlaces));
        OnChanged(nameof(NotifyRegionsText));
        OnChanged(nameof(NotifyRegionSet));
        OnChanged(nameof(WatchAreas));
        OnChanged(nameof(OnlyMine));
        OnChanged(nameof(SearchHint));
        foreach (var c in _regionList ?? []) c.SetSilently(IsPicked(c.Area));
        // подписи близости к своим пунктам (NearText) считаются в Refresh
        Refresh();
    }

    /// <summary>Пометка «моя зона» у событий ленты (по тому же правилу, что уведомления и ореол на карте).</summary>
    private void MarkMine()
    {
        bool on = Settings.HighlightMyRegions && Settings.WatchAreas.Count > 0;
        foreach (var e in Events) e.IsMine = on && e.Status != AlertStatus.AllClear && InNotifyRegions(e);
    }

    /// <summary>Клик по карте в режиме выбора: регион или район целиком.</summary>
    public void TogglePickedRegion(string id) => ToggleArea(id.Contains('|')
        ? new WatchArea { Kind = WatchArea.DistrictKind, Id = id, Name = DistrictName(id), Region = id[..id.IndexOf('|')] }
        : new WatchArea { Kind = WatchArea.RegionKind, Id = id, Name = Gazetteer.Get(id)?.Name ?? id, Region = id });

    private static string DistrictName(string id) =>
        DistrictCatalog?.FirstOrDefault(d => d.Id == id).Name ?? id[(id.IndexOf('|') + 1)..];

    public void ToggleArea(WatchArea a)
    {
        if (IsPicked(a)) Settings.WatchAreas.RemoveAll(x => x.Kind == a.Kind && x.Id == a.Id);
        else
        {
            if (a.Kind == WatchArea.PlaceKind && a.RadiusKm <= 0) a.RadiusKm = Settings.WatchRadiusKm;
            Settings.WatchAreas.Add(a);
        }
        AreasChanged(save: true);
    }

    /// <summary>Клик по подписи населённого пункта на карте: пункт добавляется (или убирается, если уже выбран рядом).</summary>
    public void TogglePickedPlace(string name, GeoPoint pos)
    {
        var existing = Settings.WatchAreas.FirstOrDefault(a => a.Kind == WatchArea.PlaceKind && Geo.DistanceKm(new GeoPoint(a.Lat, a.Lon), pos) < 0.5);
        if (existing != null) { RemoveArea(existing); return; }
        // регион — по контуру, иначе по ближайшему одноимённому пункту справочника
        var region = RegionAtPoint?.Invoke(pos)
                     ?? PlaceIndex.SearchPlaces(name, 50).Where(p => Geo.DistanceKm(p.Pos, pos) < 5).Select(p => p.RegionId).FirstOrDefault() ?? "";
        ToggleArea(new WatchArea { Kind = WatchArea.PlaceKind, Id = WatchArea.PlaceId(name, pos.Lat, pos.Lon), Name = name, Region = region, Lat = pos.Lat, Lon = pos.Lon });
    }

    public static Func<GeoPoint, string?>? RegionAtPoint { get; set; }

    /// <summary>
    /// Клик по любому месту карты в режиме «ТОЧКА» (дача, участок вне подписей): точка с радиусом по умолчанию,
    /// подпись — по ближайшему пункту («Своя точка · Почеп, 6 км СВ»). Клик рядом с уже поставленной точкой её убирает.
    /// </summary>
    public void TogglePickedPoint(GeoPoint pos)
    {
        var existing = Settings.WatchAreas.FirstOrDefault(a => a.Kind == WatchArea.PlaceKind && a.Id.StartsWith(WatchArea.PointPrefix, StringComparison.Ordinal) &&
                                                               Geo.DistanceKm(new GeoPoint(a.Lat, a.Lon), pos) < 1);
        if (existing != null) { RemoveArea(existing); return; }
        var region = RegionAtPoint?.Invoke(pos) ?? "";
        if (Gazetteer.IsForeign(region)) return;
        ToggleArea(new WatchArea
        {
            Kind = WatchArea.PlaceKind, Id = WatchArea.PointPrefix + WatchArea.PlaceId("", pos.Lat, pos.Lon),
            Name = PointName(pos), Region = region, Lat = Math.Round(pos.Lat, 5), Lon = Math.Round(pos.Lon, 5),
        });
    }

    internal static string PointName(GeoPoint pos)
    {
        if (PlaceIndex.NearestPlace(pos) is not { } near) return $"Своя точка · {pos.Lat:F3}, {pos.Lon:F3}".Replace(',', '.').Replace(". ", ", ");
        double km = Geo.DistanceKm(near.Pos, pos);
        if (km < 1.5) return $"Своя точка · {near.Name}";
        string[] dirs = ["С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ"];
        var dir = dirs[(int)Math.Round(Geo.Bearing(near.Pos, pos) / 45) % 8];
        return $"Своя точка · {near.Name}, {km:0} км {dir}";
    }

    /// <summary>Режим выбора на карте: false — регионы/районы/пункты по подписи, true — своя точка в любом месте.</summary>
    public bool PickPointMode { get => _pickPointMode; set => Set(ref _pickPointMode, value); }
    private bool _pickPointMode;

    /// <summary>Радиус пункта задан перетаскиванием края круга на карте.</summary>
    public void SetPlaceRadius(WatchArea a, int km)
    {
        var target = Settings.WatchAreas.FirstOrDefault(x => x.Kind == WatchArea.PlaceKind && x.Id == a.Id);
        if (target == null) return;
        km = Math.Clamp(km, 3, 100);
        if (target.RadiusKm == km) return;
        target.RadiusKm = km;
        SettingsStore.SaveDeferred(Settings);
        OnChanged(nameof(PickedPlaces));
        OnChanged(nameof(WatchAreas));
        // подписи близости к своим пунктам (NearText) считаются в Refresh
        Refresh();
    }

    public void RemoveArea(WatchArea a)
    {
        Settings.WatchAreas.RemoveAll(x => x.Kind == a.Kind && x.Id == a.Id);
        AreasChanged(save: true);
    }

    public void ClearPickedRegions()
    {
        Settings.WatchAreas.Clear();
        AreasChanged(save: true);
    }

    private bool _pickingOnMap;
    /// <summary>Режим выбора зон кликом по карте.</summary>
    public bool PickingOnMap { get => _pickingOnMap; set { if (Set(ref _pickingOnMap, value) && !value) PickPointMode = false; } }

    /// <summary>Подключает геометрию районов из слоя карты (контуры — в подложке, загружаются в фоне).</summary>
    public static void ConfigureGeometry()
    {
        DistrictsOfPoint = Radar.Map.Basemap.DistrictsOf;
        RegionAtPoint = Radar.Map.Basemap.RegionAt;
        DistrictAtPoint = Radar.Map.Basemap.DistrictAt;
        Task.Run(() =>
        {
            try { DistrictCatalog = Radar.Map.Basemap.DistrictCatalog(); Dispatcher.UIThread.Post(() => DistrictCatalogReady?.Invoke()); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Каталог районов: {ex}"); }
        });
    }

    /// <summary>Геометрия районов для сопоставления тревог с выбранными районами (задаётся слоем карты).</summary>
    public static Func<MapPoint, IEnumerable<string>>? DistrictsOfPoint { get; set; }
    public static Func<GeoPoint, string?>? DistrictAtPoint { get; set; }

    /// <summary>Событие касается выбранных зон (регион, район, окрестность пункта, направление угрозы).</summary>
    private bool InNotifyRegions(AlertEvent e) =>
        WatchMatcher.Matches(e, Settings.WatchAreas, Settings.WatchRadiusKm, DistrictsOfPoint, DistrictAtPoint);

    private void AnnounceConnection(bool lost)
    {
        if (!Settings.NotifyConnection) return;
        if (Settings.Notifications)
            Notifier.Notify(lost ? "Нет связи с каналами" : "Связь восстановлена",
                lost ? "Обстановка на карте может быть устаревшей. Попытки подключения продолжаются." : "Данные снова поступают в реальном времени.", urgent: lost);
        if (Settings.Sound) Notifier.Play(lost ? Chime.Lost : Chime.Restored, SoundVolume);
    }

    // ---- о программе и обновления ----
    public string AppVersion { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0");
    private string _updateStatus = "Обновления — с GitHub, проверка при запуске";
    public string UpdateCheckText { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    private bool _checkingUpdates;
    public bool CheckingUpdates { get => _checkingUpdates; private set => Set(ref _checkingUpdates, value); }

    private string? _updateUrl;
    /// <summary>Найдена новая версия: кнопка «Скачать» ведёт на её файл (или страницу релиза).</summary>
    public bool HasUpdate => _updateUrl != null;

    /// <summary>Проверка новой версии по последнему релизу на GitHub; <paramref name="silent"/> — при запуске, без сообщений об ошибках.</summary>
    public async void CheckForUpdates(bool silent = false)
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        if (!silent) UpdateCheckText = "Проверка…";
        try
        {
            var current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
            var rel = await UpdateChecker.LatestAsync(_http, CancellationToken.None);
            if (rel != null && UpdateChecker.IsNewer(rel.Tag, current))
            {
                _updateUrl = UpdateChecker.AssetFor(rel)?.Url ?? rel.Page;
                UpdateCheckText = $"Доступна версия {rel.Tag}";
                if (silent && Settings.Notifications) Notifier.Notify("Доступно обновление Радара", $"Версия {rel.Tag} — скачать во вкладке «Настройки».", urgent: false);
            }
            else
            {
                _updateUrl = null;
                UpdateCheckText = $"Установлена последняя версия · {DateTime.Now:HH:mm}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!silent) UpdateCheckText = $"Не удалось проверить обновления · {DateTime.Now:HH:mm}";
        }
        finally
        {
            OnChanged(nameof(HasUpdate));
            CheckingUpdates = false;
        }
    }

    /// <summary>Ссылка, которую представление открывает во внешнем приложении (браузер, Telegram).</summary>
    public event Action<Uri>? OpenLinkRequested;
    public void OpenUpdate() { if (_updateUrl != null) OpenLinkRequested?.Invoke(new Uri(_updateUrl)); }
    public void OpenDonate() => OpenLinkRequested?.Invoke(new Uri(Distribution.DonateUrl));
    public void OpenFeedback() => OpenLinkRequested?.Invoke(new Uri(Distribution.FeedbackUrl));
    public void OpenProject() => OpenLinkRequested?.Invoke(new Uri(Distribution.RepoUrl));

    /// <summary>Мобильная платформа: другие разделы настроек (фон вместо трея и автозапуска).</summary>
    public bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public bool BackgroundWatch
    {
        get => Settings.BackgroundWatch;
        set
        {
            if (Settings.BackgroundWatch == value) return;
            Settings.BackgroundWatch = value;
            SaveSetting();
            OnChanged();
            App.Platform?.SetBackgroundService(value);
        }
    }

    // ---- внешний экран (трансляция) ----
    private bool _castHooked;
    /// <summary>Трансляция на телевизор или монитор доступна (Android).</summary>
    public bool CanCast => App.Platform != null;
    public string CastText
    {
        get
        {
            if (App.Platform is { } p && !_castHooked) { _castHooked = true; p.ExternalScreenChanged += () => Dispatcher.UIThread.Post(() => { OnChanged(nameof(CastText)); OnChanged(nameof(Casting)); }); }
            return App.Platform?.ExternalScreen is { } s ? $"Карта выведена на {s}" : "Телевизор или монитор в сети (Miracast) либо по кабелю: карта — в его разрешении, телефон — пульт";
        }
    }
    public bool Casting => App.Platform?.ExternalScreen != null;
    public void OpenCastSettings() => App.Platform?.OpenCastSettings();

    /// <summary>На телефоне файловый менеджер к каталогу приложения доступа не имеет — пункт скрыт.</summary>
    public bool CanOpenDataFolder => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public void OpenDataFolder()
    {
        if (!CanOpenDataFolder) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(AppPaths.Root);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch { }
    }

    /// <summary>Включает или выключает канал: опрос на ходу, выбор сохраняется в настройках.</summary>
    public void ToggleChannel(ChannelRow row)
    {
        if (IsReplay) return;
        bool enable = !row.Enabled;
        _live.SetChannelEnabled(row.Name, enable);
        row.SetEnabledOptimistic(enable);
        SettingsStore.SaveDeferred(Settings);
    }

    public string ChannelsSummary => _channelsTotal == 0 ? "" : $"{_channelsOk} из {_channelsTotal} на связи";

    private void UpdateChannels(IReadOnlyList<ChannelStatus> list)
    {
        foreach (var c in list)
        {
            var row = Channels.FirstOrDefault(r => r.Name == c.Name);
            if (row == null) Channels.Add(row = new ChannelRow(c.Name, c.Title));
            row.Update(c);
        }
        for (int i = Channels.Count - 1; i >= 0; i--)
            if (!list.Any(c => c.Name == Channels[i].Name)) Channels.RemoveAt(i);
        int total = list.Count(c => c.Enabled);
        _channelsEnabled = total;
        RecountChannels();
    }

    private int _channelsEnabled;
    private void RecountChannels()
    {
        int ok = Channels.Count(r => r.IsAlive);
        if (ok == _channelsOk && _channelsEnabled == _channelsTotal) return;
        (_channelsOk, _channelsTotal) = (ok, _channelsEnabled);
        OnChanged(nameof(ChannelsSummary));
    }

    public void PollNow()
    {
        if (IsReplay || _manualRefresh) return;
        _manualRefresh = true;
        IsBusy = true;
        _ = Task.Run(async () =>
        {
            try { await _live.PollOnceAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Обновление: {ex}"); }
            finally
            {
                Dispatcher.UIThread.Post(() => { _manualRefresh = false; IsBusy = HistoryLoading; });
            }
        });
    }

    private void Refresh()
    {
        _refreshTracks = null; _refreshTrackOf = null;
        try { RefreshCore(); }
        finally { _refreshTracks = null; _refreshTrackOf = null; }
    }

    /// <summary>Фильтры типов, уровней и статусов (без строки поиска ленты).</summary>
    private bool PassesFilters(AlertEvent e) =>
        (e.Status != AlertStatus.AllClear || _showAllClear) &&
        (e.Status != AlertStatus.Launch || _showLaunch) &&
        (e.Threat switch
        {
            ThreatType.Uav => _showUav,
            ThreatType.Missile => _showMissile,
            _ => _showOther,
        }) &&
        (e.Status == AlertStatus.AllClear || (KindShown(e.Kind, e.Threat, e.Kinds) && !_hiddenLevels.Contains(e.EffectiveLevel)));

    private HashSet<AlertEvent> NotifiableSet() => [.. _all.Where(PassesFilters)];

    private void RefreshCore()
    {
        var f = Gazetteer.Normalize(_filterText.Trim());
        var visible = _all.Where(e => PassesFilters(e) &&
                (f.Length == 0 || Gazetteer.Normalize(e.RegionNames + " " + e.Text).Contains(f, StringComparison.Ordinal)))
            .OrderByDescending(e => e.Time)
            .ToList();

        // фильтр «мои регионы» — только для ленты: карта показывает всю обстановку
        var feed = OnlyMine ? visible.Where(e => e.Status != AlertStatus.AllClear && InNotifyRegions(e) || e == Selected).ToList() : visible;
        // инкрементальное обновление: элементы не пересоздаются, новые плавно вставляются сверху
        var keep = feed.ToHashSet();
        // ListBox обрабатывает Move как удаление и вставку и снимает выделение (пишет Selected = null обратно):
        // на время перестановки выбранная тревога запоминается и восстанавливается
        var keepSelected = _selected;
        bool selectedMoved = false;
        _reordering = true;
        try
        {
        for (int i = Events.Count - 1; i >= 0; i--)
            if (!keep.Contains(Events[i])) Events.RemoveAt(i);
        for (int i = 0; i < feed.Count; i++)
        {
            var e = feed[i];
            // обычно порядок не меняется — проверка на месте вместо поиска по всей ленте
            if (i < Events.Count && ReferenceEquals(Events[i], e)) continue;
            int cur = Events.IndexOf(e);
            if (cur == i) continue;
            if (cur >= 0) { Events.Move(cur, i); if (ReferenceEquals(e, keepSelected)) selectedMoved = true; }
            else
            {
                Events.Insert(i, e);
                if (_eventsPrimed && e.Status != AlertStatus.AllClear) FlashEvent(e);
            }
        }
        }
        finally { _reordering = false; }
        // выделение в ListBox снято перемещением — возвращаем его (VM своё значение сохранила)
        if (selectedMoved && _selected == keepSelected) SelectionRestored?.Invoke(keepSelected!);
        // оповещение — один раз на событие, когда оно впервые пришло (не при возврате в ленту фильтром);
        // скрытые фильтрами в момент прихода не объявляются и позже, при включении фильтра
        var shown = visible.ToHashSet();
        // для уведомлений — те же фильтры типов и уровней, но без строки поиска ленты: введённый «Брянск» не должен
        // навсегда заглушить тревоги остальных регионов
        var notifiable = string.IsNullOrEmpty(f) ? shown : NotifiableSet();
        foreach (var e in _all)
            if (_announced.Add(e) && _eventsPrimed && e.Status != AlertStatus.AllClear && notifiable.Contains(e)) Announce(e, levelUp: false);
        if (_announced.Count > _all.Count * 2 + 50) { _announced.IntersectWith(_all); _notified.IntersectWith(_all); }
        if (_eventsPrimed) AnnounceApproaches(notifiable);
        if (Selected != null && !keep.Contains(Selected)) Selected = null;
        // повышение уровня уже показанного события (например, Тревога → Удар) — тоже вспышка
        foreach (var e in visible)
        {
            int rank = e.LevelRank;
            if (_seenLevel.TryGetValue(e, out var was) && rank > was && _eventsPrimed) { FlashEvent(e, levelUp: true); Announce(e, levelUp: true); }
            _seenLevel[e] = rank;
        }
        if (_seenLevel.Count > visible.Count * 2 + 50)
            foreach (var k in _seenLevel.Keys.Where(k => !shown.Contains(k)).ToList()) _seenLevel.Remove(k);
        if (_all.Count > 0) _eventsPrimed = true;
        MarkMine();

        var visibleSet = visible.ToHashSet();
        var tracks = RefreshTracks()
            .Where(t => t.IsMoving && t.Events.Any(visibleSet.Contains) && (t.Threat switch
            {
                ThreatType.Uav => _showUav,
                ThreatType.Missile => _showMissile,
                _ => _showOther,
            }) && KindShown(t.Kind, t.Threat))
            .ToList();
        var now = Clock.Now;
        var motion = new Dictionary<AlertEvent, string>();
        var motionTip = new Dictionary<AlertEvent, string>();
        foreach (var t in tracks.Where(t => t.HasMeasuredSpeed && t.StateAt(now) != TrackState.Cleared))
        {
            var text = $"~{Math.Round(t.SpeedKmh / 10) * 10:0} км/ч";
            if (t.SpeedSpread is { } sp && sp >= 10) text += $" ±{Math.Round(sp / 10) * 10:0}";
            text += t.SpeedConfidence switch { 2 => " · надёжно", 1 => " · оценка", _ => " · грубо" };
            int n = Math.Max(t.SpeedLog.Count, t.SpeedSamples), cross = t.SpeedLog.Count(x => x.Cross);
            var tip = $"Скорость по {n} {(n % 10 == 1 && n % 100 != 11 ? "замеру" : "замерам")} перемещения" +
                      (t.SpeedLog.Count > 0 ? $": {string.Join(", ", t.SpeedLog.Select(x => $"{x.Kmh:0}"))} км/ч" : "") +
                      (t.SpeedSpread is { } s2 ? $"\nРазброс ±{s2:0} км/ч" : "") +
                      (cross > 0 ? $"\nПо отметкам разных каналов: {cross} (их задержки публикации различаются — точность ниже)" : "") +
                      "\n" + (t.SpeedConfidence switch
                      {
                          2 => "Достоверность высокая: несколько согласованных замеров одного источника",
                          1 => "Достоверность средняя",
                          _ => "Достоверность низкая: один замер, большой разброс или только разные каналы",
                      });
            if (t.EtaAt(now) is { } eta && t.TargetLabel != null)
                text += eta < TimeSpan.FromMinutes(1.5) ? $" · {t.TargetLabel}: прибытие" : $" · до {t.TargetLabel} {t.EtaText(now)}";
            else if (t.TargetIsArea && t.TargetLabel != null)
                text += $" · в направлении: {t.TargetLabel}";
            foreach (var e in t.Events) { motion[e] = text; motionTip[e] = tip; }
        }
        bool places = Settings.WatchAreas.Any(a => a.Kind == WatchArea.PlaceKind);
        var trackOf = places ? TrackOf() : [];
        foreach (var e in visible)
        {
            e.MotionTip = motionTip.GetValueOrDefault(e);
            e.Motion = motion.GetValueOrDefault(e);
            // относительно своих пунктов — только для тревог, касающихся зон наблюдения
            var near = places && e.Status != AlertStatus.AllClear && InNotifyRegions(e)
                ? Proximity.Compute(e, trackOf.GetValueOrDefault(e), Settings.WatchAreas, Settings.WatchRadiusKm, now) : null;
            e.NearText = near != null ? Proximity.Text(near, e.Info.Short) : null;
            e.NearApproaching = near is { Motion: Proximity.Motion.Approaching };
        }
        MapEventsChanged?.Invoke(visible, tracks);
        foreach (var l in Levels)
            l.Count = _all.Count(e => e.Status != AlertStatus.AllClear && e.EffectiveLevel == l.Level && KindShown(e.Kind, e.Threat, e.Kinds));
        UpdateLegendCounts();
        // счётчики заголовка: уведомление только при изменении — каждое уведомление пересчитывает раскладку полосы
        var counts = (ExpectedCount, ActiveCount, LaunchCount, RegionCount);
        bool countsChanged = counts != _lastCounts;
        _lastCounts = counts;
        if (countsChanged)
        {
            OnChanged(nameof(ExpectedCount));
            OnChanged(nameof(HasExpectedCount));
        }
        foreach (var k in TelemetryKinds)
        {
            k.Count = Active.Count(e => e.Threat == k.Type);
            k.Notify();
        }
        if (_all.Count > 0) LegendCounter.Animate = true;
        OnChanged(nameof(HasTelemetryKinds));
        if (countsChanged)
        {
            OnChanged(nameof(ActiveCount));
            OnChanged(nameof(LaunchCount));
            OnChanged(nameof(HasLaunches));
            OnChanged(nameof(RegionCount));
        }
        UpdateEmptyState();
    }

    private (int, int, int, int) _lastCounts = (-1, -1, -1, -1);

    // ---------------- пустые состояния ленты ----------------

    public enum EmptyKind { None, Loading, Offline, Search, Filtered, Calm }

    private EmptyKind _empty = EmptyKind.Loading;
    public EmptyKind Empty
    {
        get => _empty;
        private set
        {
            if (!Set(ref _empty, value)) return;
            foreach (var n in new[] { nameof(IsEmpty), nameof(EmptyLoading), nameof(EmptyOffline), nameof(EmptySearch), nameof(EmptyFiltered), nameof(EmptyCalm) })
                OnChanged(n);
        }
    }
    public bool IsEmpty => _empty != EmptyKind.None;
    public bool EmptyLoading => _empty == EmptyKind.Loading;
    public bool EmptyOffline => _empty == EmptyKind.Offline;
    public bool EmptySearch => _empty == EmptyKind.Search;
    public bool EmptyFiltered => _empty == EmptyKind.Filtered;
    public bool EmptyCalm => _empty == EmptyKind.Calm;

    private string _emptyTitle = "Загрузка обстановки…", _emptyDetail = "Получаем сообщения каналов тревог.", _emptyAction = "";
    public string EmptyTitle { get => _emptyTitle; private set => Set(ref _emptyTitle, value); }
    public string EmptyDetail { get => _emptyDetail; private set => Set(ref _emptyDetail, value); }
    public string EmptyAction { get => _emptyAction; private set { if (Set(ref _emptyAction, value)) OnChanged(nameof(HasEmptyAction)); } }
    public bool HasEmptyAction => _emptyAction.Length > 0;

    /// <summary>Лента «Сообщения» пуста — своя подпись (загрузка / нет связи / пока ничего не пришло).</summary>
    public bool FeedEmpty => Feed.Count == 0;
    public string FeedEmptyTitle => IsOffline ? "Нет связи с каналами"
        : HistoryLoading || Connection == ConnectionState.Connecting ? "Загрузка сообщений…" : "Сообщений пока нет";
    public string FeedEmptyDetail => IsOffline ? "Сообщения появятся, когда соединение восстановится. Попытки подключения продолжаются."
        : HistoryLoading || Connection == ConnectionState.Connecting ? "Получаем последние сообщения из каналов тревог."
        : "Здесь появляются все сообщения каналов по мере поступления — с разбором и без рекламы.";

    /// <summary>
    /// Почему лента тревог пуста: идёт загрузка, нет связи, ничего не найдено поиском, всё скрыто фильтрами
    /// или активных тревог действительно нет. Каждому случаю — своя подпись и действие.
    /// </summary>
    private void UpdateEmptyState()
    {
        OnChanged(nameof(FeedEmpty));
        OnChanged(nameof(FeedEmptyTitle));
        OnChanged(nameof(FeedEmptyDetail));
        if (Events.Count > 0) { Empty = EmptyKind.None; return; }

        var query = _filterText.Trim();
        int active = _all.Count(e => e.Status != AlertStatus.AllClear);
        bool filtersOn = !_showUav || !_showMissile || !_showOther || !_showLaunch || HasHiddenKinds;
        bool loading = !IsReplay && (HistoryLoading || Connection == ConnectionState.Connecting) && _all.Count == 0;

        if (query.Length > 0)
        {
            Empty = EmptyKind.Search;
            EmptyTitle = $"Ничего не найдено по запросу «{query}»";
            EmptyDetail = filtersOn
                ? "Поиск идёт только среди тревог, видимых с текущими фильтрами. Проверьте написание или сбросьте фильтры."
                : "Поиск идёт по названиям регионов, районов, городов и тексту сообщений. Проверьте написание.";
            EmptyAction = filtersOn ? "Сбросить поиск и фильтры" : "Сбросить поиск";
        }
        else if (IsStats)
        {
            Empty = EmptyKind.Calm;
            EmptyTitle = "Режим статистики";
            EmptyDetail = "Текущая обстановка продолжает собираться в фоне — вернуться к ней можно через меню режима в заголовке.";
            EmptyAction = "";
        }
        else if (IsHistory && active == 0 && HistoryBusy && !HistoryEmpty)
        {
            Empty = EmptyKind.Loading;
            EmptyTitle = "Загрузка архива…";
            EmptyDetail = "Восстанавливаем обстановку на выбранный момент.";
            EmptyAction = "";
        }
        else if (IsHistory && active == 0)
        {
            Empty = EmptyKind.Calm;
            EmptyTitle = HistoryEmpty ? (_historyError != null ? "Архив повреждён" : "Архив не найден") : "На этот момент архива тревог нет";
            EmptyDetail = HistoryEmpty ? HistoryEmptyText : "Тревоги появятся здесь по мере воспроизведения архива.";
            EmptyAction = "";
        }
        else if (OnlyMine && active > 0)
        {
            // спокойно в своих регионах, но тревоги есть в других — это хорошая новость, а не «скрыто фильтром»
            Empty = EmptyKind.Calm;
            EmptyTitle = "В ваших зонах тревог нет";
            EmptyDetail = $"Выбрано: {NotifyRegionsText}. В других местах активных тревог — {active}, они на карте.";
            EmptyAction = "Показать все";
        }
        else if (active > 0)
        {
            Empty = EmptyKind.Filtered;
            EmptyTitle = $"Все тревоги скрыты фильтрами ({active})";
            EmptyDetail = "Отключены типы угроз или уровни опасности — в ленте и на карте они не показываются.";
            EmptyAction = "Показать все";
        }
        else if (loading)
        {
            Empty = EmptyKind.Loading;
            EmptyTitle = "Загрузка обстановки…";
            EmptyDetail = $"Получаем сообщения каналов тревог за последние {Settings.HistoryHours} ч.";
            EmptyAction = "";
        }
        else if (IsOffline)
        {
            Empty = EmptyKind.Offline;
            EmptyTitle = "Нет связи с каналами";
            EmptyDetail = "Обстановка неизвестна: тревоги могут быть, но не поступают. Проверьте подключение к интернету — попытки продолжаются автоматически.";
            EmptyAction = "Повторить сейчас";
        }
        else
        {
            Empty = EmptyKind.Calm;
            EmptyTitle = "Активных тревог нет";
            EmptyDetail = "Каналы на связи. Новые тревоги появятся здесь и на карте автоматически.";
            EmptyAction = "";
        }
    }

    /// <summary>Действие кнопки пустого состояния.</summary>
    public void RunEmptyAction()
    {
        switch (_empty)
        {
            case EmptyKind.Search:
                bool resetFilters = _emptyAction != "Сбросить поиск";
                FilterText = "";
                if (resetFilters) ResetFilters();
                break;
            case EmptyKind.Filtered:
                ResetFilters();
                break;
            case EmptyKind.Offline:
                PollNow();
                break;
            case EmptyKind.Calm when OnlyMine:
                OnlyMine = false;
                break;
        }
    }

    private void ResetFilters()
    {
        ShowUav = ShowMissile = ShowOther = ShowLaunch = ShowAllClear = true;
        ShowAllKinds();
    }

    // первое заполнение ленты (история при запуске) не подсвечивается
    private bool _eventsPrimed;

    private readonly Dictionary<AlertEvent, int> _seenLevel = [];

    private static void FlashEvent(AlertEvent e, bool levelUp = false)
    {
        e.Flash = false;
        e.Flash = true;
        if (levelUp) { e.LevelUp = false; e.LevelUp = true; }
        DispatcherTimer.RunOnce(() => { e.Flash = false; e.LevelUp = false; }, TimeSpan.FromSeconds(2));
    }

    /// <summary>Состояние живого опроса (каналы, связь) — обновляется и в режимах истории и статистики.</summary>
    private void UpdateLiveStatus(EngineStatus s)
    {
        UpdateChannels(s.Channels);
        if (s.InFlight > _lastInFlight && !IsReplay) FetchStarted?.Invoke();
        _lastInFlight = s.InFlight;
        Connection = s.Connection;
        IsBusy = !IsReplay && (s.HistoryPending > 0 || _manualRefresh);
        if (s.LastSuccess != _lastSuccess) { _lastSuccess = s.LastSuccess; OnChanged(nameof(StaleText)); OnChanged(nameof(StaleSince)); }
    }

    private void UpdateStatus(EngineStatus s)
    {
        // UpdateStatus уже в UI-потоке (см. подписку на StatusChanged)
        UpdateEmptyState();
        if (IsStats)
        {
            Status = "Статистика по архиву сообщений";
            (StatusTag, StatusColor, StatusTime) = ("СТАТИСТИКА", "#7CD4FF", "");
            (StatusChannels, StatusErrors) = ("", "");
            return;
        }
        if (IsHistory)
        {
            if (_engine == _live)
            {
                // месяц ещё загружается — живой движок в этом режиме не показывается
                Status = "История: загрузка архива";
                (StatusTag, StatusColor, StatusTime) = ("ИСТОРИЯ", "#D9A441", "");
                (StatusChannels, StatusErrors) = ("", "");
                return;
            }
            var range = $"{RuDate(_engine.ReplayStart.ToLocalTime())} – {RuDate(_engine.ReplayEnd.ToLocalTime())}";
            Status = $"История: архив сообщений {range}, {s.ChannelsOk} каналов";
            (StatusTag, StatusColor, StatusTime) = ("ИСТОРИЯ", "#D9A441", range);
            (StatusChannels, StatusErrors) = ($"каналов {s.ChannelsOk}", "");
            return;
        }
        HistoryLoading = s.HistoryPending > 0;
        if (s.HistoryTotal > 0)
        {
            int done = s.HistoryTotal - s.HistoryPending;
            HistoryProgress = Math.Clamp((double)done / s.HistoryTotal, 0, 1);
            HistoryTitle = $"ИСТОРИЯ ЗА {Settings.HistoryHours} Ч";
            HistoryDetail = $"каналов {done} из {s.HistoryTotal} · сообщений {s.HistoryMessages}";
        }
        var text = s.Connection == ConnectionState.Offline
            ? "Нет связи с каналами" + (s.LastSuccess is { } ls ? $" · последние данные в {ls.ToLocalTime():HH:mm:ss}" : "") + " · повторные попытки продолжаются"
            : s.HistoryPending > 0
            ? $"Загрузка истории за {Settings.HistoryHours} ч · осталось каналов: {s.HistoryPending}"
            : $"Обновлено {(s.LastSuccess ?? s.LastPoll).ToLocalTime():HH:mm:ss} · каналов: {s.ChannelsOk}";
        if (s.ChannelsFailed > 0) text += $" · ошибок: {s.ChannelsFailed} ({s.LastError})";
        Status = text;
        if (s.Connection == ConnectionState.Offline)
            (StatusTag, StatusColor, StatusTime) = ("НЕТ СВЯЗИ", "#E5484D", s.LastSuccess is { } lsx ? $"данные {lsx.ToLocalTime():HH:mm:ss}" : "");
        else if (s.HistoryPending > 0)
            (StatusTag, StatusColor, StatusTime) = ("ЗАГРУЗКА", "#7CD4FF", $"осталось {s.HistoryPending}");
        else if (s.Connection == ConnectionState.Connecting)
            (StatusTag, StatusColor, StatusTime) = ("ПОДКЛЮЧЕНИЕ", "#7CD4FF", "");
        else
            (StatusTag, StatusColor, StatusTime) = ("ОБНОВЛЕНО", "#4FB286", $"{(s.LastSuccess ?? s.LastPoll).ToLocalTime():HH:mm:ss}");
        StatusChannels = $"каналов {s.ChannelsOk}";
        StatusErrors = s.ChannelsFailed > 0 ? $"ошибок {s.ChannelsFailed}" : "";
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
