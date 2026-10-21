using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Services;

public sealed class ChannelConfig
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Регионы, которые канал покрывает (контекст для разбора коротких сообщений).</summary>
    public List<string> Regions { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

/// <summary>Зона наблюдения для уведомлений: регион, район или населённый пункт.</summary>
public sealed class WatchArea
{
    public const string RegionKind = "region", DistrictKind = "district", PlaceKind = "place";
    /// <summary>region / district / place.</summary>
    public string Kind { get; set; } = RegionKind;
    /// <summary>Регион: его id; район: «регион|ключ» (как в подложке); пункт: «название@широта,долгота».</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Регион, к которому относится район или пункт.</summary>
    public string Region { get; set; } = "";
    /// <summary>Координаты пункта (для района и региона не используются).</summary>
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>Радиус окрестности пункта, км; 0 — общий радиус по умолчанию.</summary>
    public int RadiusKm { get; set; }

    /// <summary>Префикс id своей точки (поставленной кликом в произвольном месте карты): «pt:@широта,долгота».</summary>
    public const string PointPrefix = "pt:";
    [JsonIgnore]
    public bool IsPoint => Kind == PlaceKind && Id.StartsWith(PointPrefix, StringComparison.Ordinal);

    public static string PlaceId(string name, double lat, double lon) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}@{lat:F3},{lon:F3}");
}

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 20;
    /// <summary>Доступ к Telegram: 0 — авто (напрямую, при блокировке — через прокси), 1 — всегда через прокси, 2 — только напрямую.</summary>
    public int TelegramAccess { get; set; }
    /// <summary>Через сколько минут без подтверждения тревога считается неактуальной.</summary>
    public int AlertLifetimeMinutes { get; set; } = 45;
    public int AllClearLifetimeMinutes { get; set; } = 10;
    /// <summary>Глубина истории при первом запуске, часов.</summary>
    public int HistoryHours { get; set; } = 24;
    public bool OnlineGeocoding { get; set; } = true;
    /// <summary>Загружать актуальную линию соприкосновения (DeepStateMap, раз в сутки). Без сети — встроенная линия.</summary>
    public bool OnlineFrontLine { get; set; } = true;
    /// <summary>Типы угроз, скрытые через легенду (имена ThreatKind).</summary>
    public List<string> HiddenKinds { get; set; } = [];
    /// <summary>Уровни опасности, скрытые через легенду (имена AlertLevel).</summary>
    public List<string> HiddenLevels { get; set; } = [];
    /// <summary>Подсвечивать регионы, куда направлена угроза («ожидается»).</summary>
    public bool ShowExpected { get; set; } = true;
    /// <summary>Показывать линию соприкосновения на карте.</summary>
    public bool ShowFrontLine { get; set; } = true;
    /// <summary>Легенда свёрнута в кнопку «Условные знаки».</summary>
    // на телефоне легенда закрывала бы пол-экрана — по умолчанию свёрнута в кнопку
    public bool LegendCollapsed { get; set; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    /// <summary>Легенда раскрыта на полный список типов («все типы»).</summary>
    public bool LegendExpanded { get; set; }
    /// <summary>Боковая панель убрана за правый край.</summary>
    public bool SidebarHidden { get; set; }
    /// <summary>Открытая вкладка боковой панели.</summary>
    public int SidebarTab { get; set; }
    /// <summary>Быстрые фильтры ленты и карты.</summary>
    public bool ShowUav { get; set; } = true;
    public bool ShowMissile { get; set; } = true;
    public bool ShowOther { get; set; } = true;
    public bool ShowLaunch { get; set; } = true;
    public bool ShowAllClear { get; set; } = true;
    /// <summary>Лента: только тревоги выбранных регионов (карта не фильтруется).</summary>
    public bool OnlyMine { get; set; }
    /// <summary>Геометрия окна в обычном (не развёрнутом) состоянии: позиция в пикселях экрана, размер в DIP.</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Камера карты: центр кадра и масштаб.</summary>
    public double? MapLat { get; set; }
    public double? MapLon { get; set; }
    public double? MapZoom { get; set; }
    // ---- вкладка «Настройки» ----
    /// <summary>Фон аналоговых помех на карте (ретро-глитч).</summary>
    public bool RetroNoise { get; set; } = true;
    /// <summary>Эффект «потери сигнала» на карте при обрыве связи.</summary>
    public bool SignalLossEffect { get; set; } = true;
    /// <summary>Затенение карты по краям вьюпорта.</summary>
    public bool Vignette { get; set; } = true;
    /// <summary>Частота фоновой анимации карты (пульсация, пунктиры, помехи): 0 — экономно, 1 — обычно, 2 — плавно.</summary>
    public int AnimationQuality { get; set; } = 1;
    /// <summary>Системные уведомления ОС о новых тревогах.</summary>
    public bool Notifications { get; set; } = true;
    /// <summary>Минимальный уровень для уведомления (имя AlertLevel).</summary>
    public string NotifyMinLevel { get; set; } = nameof(Models.AlertLevel.Danger);
    /// <summary>Уведомлять только когда окно приложения не в фокусе.</summary>
    public bool NotifyOnlyInactive { get; set; } = true;
    /// <summary>Уведомлять о потере и восстановлении связи.</summary>
    public bool NotifyConnection { get; set; } = true;
    /// <summary>Устаревшее (до зон наблюдения): только регионы. При загрузке переносится в <see cref="WatchAreas"/>.</summary>
    public List<string> NotifyRegions { get; set; } = [];
    /// <summary>Уведомлять только о тревогах в этих зонах (регионы, районы, пункты); пусто — по всем.</summary>
    public List<WatchArea> WatchAreas { get; set; } = [];
    /// <summary>Радиус по умолчанию вокруг населённого пункта (для новых пунктов и пунктов без своего радиуса), км.</summary>
    public int WatchRadiusKm { get; set; } = 15;

    /// <summary>Перенос старого списка регионов в зоны наблюдения. true — настройки изменились.</summary>
    public bool MigrateWatchAreas()
    {
        if (NotifyRegions.Count == 0) return false;
        foreach (var r in NotifyRegions)
            if (!WatchAreas.Any(a => a.Kind == WatchArea.RegionKind && a.Id == r))
                WatchAreas.Add(new WatchArea { Kind = WatchArea.RegionKind, Id = r, Name = Gazetteer.Get(r)?.Name ?? r, Region = r });
        NotifyRegions.Clear();
        return true;
    }
    /// <summary>Выделять выбранные регионы на карте (контур и ореол у их тревог).</summary>
    public bool HighlightMyRegions { get; set; } = true;
    /// <summary>Звуковые оповещения.</summary>
    public bool Sound { get; set; } = true;
    /// <summary>Громкость звука: 0 — тихо, 1 — средне, 2 — громко.</summary>
    public int SoundVolume { get; set; } = 1;
    /// <summary>Закрытие окна сворачивает приложение в трей, а не завершает его.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>Запускать вместе с системой.</summary>
    public bool StartWithSystem { get; set; }
    /// <summary>При запуске вместе с системой не показывать окно (уходит в трей).</summary>
    public bool StartMinimized { get; set; } = true;
    /// <summary>Android: фоновая служба опроса каналов (тревоги приходят при свёрнутом приложении).</summary>
    public bool BackgroundWatch { get; set; } = true;
    /// <summary>Проверять обновления при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;
    public List<ChannelConfig> Channels { get; set; } = DefaultChannels();
    /// <summary>
    /// Каналы из списка по умолчанию, которые уже предлагались (чтобы новые добавлялись при обновлении, а удалённые не возвращались).
    /// По умолчанию пусто: при чтении файла без этого поля инициализатор не должен «помечать виденными» ещё не добавленные каналы.
    /// </summary>
    public List<string> KnownChannels { get; set; } = [];
    /// <summary>Версия списка каналов по умолчанию, с которой синхронизированы настройки (0 — файл старой версии).</summary>
    public int ChannelListVersion { get; set; }

    /// <summary>Текущая версия списка каналов по умолчанию. Увеличивать при добавлении каналов в <see cref="DefaultChannels"/>.</summary>
    public const int CurrentChannelListVersion = 3;

    public static List<ChannelConfig> DefaultChannels() =>
    [
        new() { Name = "LPRalarm", Title = "Основной канал тревог" },
        new() { Name = "kupolrussia", Title = "Купол — тыловые регионы" },
        new() { Name = "lpr1_Crimea_Alarm", Title = "Крым и Севастополь", Regions = ["crimea", "sevastopol"] },
        new() { Name = "lpr1_Krasnodar_alarm", Title = "Краснодарский край", Regions = ["krasnodar"] },
        new() { Name = "lpr1_Rostov_alarm", Title = "Ростовская область", Regions = ["rostov"] },
        new() { Name = "lpr1_Belgorod_alarm", Title = "Белгородская область", Regions = ["belgorod"] },
        new() { Name = "lpr1_Kursk_alarm", Title = "Курская область", Regions = ["kursk"] },
        new() { Name = "lpr1_Bryansk_alarm", Title = "Брянская область", Regions = ["bryansk"] },
        new() { Name = "lpr1_Orlov_alarm", Title = "Орловская область", Regions = ["orel"] },
        new() { Name = "lpr1_Voronezh_alarm", Title = "Воронежская область", Regions = ["voronezh"] },
        new() { Name = "lpr1_Kherson_alarm", Title = "Херсонская и Запорожская области", Regions = ["kherson", "zaporozhye"] },
        new() { Name = "lpr1_LDPR_alarm", Title = "ЛНР и ДНР", Regions = ["lnr", "dnr"] },
        new() { Name = "lpr1_Kharkov_alarm", Title = "Харьковская область", Regions = ["kharkov"] },
        new() { Name = "radarrussiia", Title = "Радар по всей России" },
        new() { Name = "radar_moscoww", Title = "Радар Москва", Regions = ["moscow", "moscow_obl"] },
        new() { Name = "rada_saratov", Title = "Радар Саратов", Regions = ["saratov"] },
        new() { Name = "vrv_radar", Title = "Радар ВРВ — тыловые регионы" },
        new() { Name = "radar_samaraaaa", Title = "Радар Самара", Regions = ["samara"] },
        new() { Name = "radar_rschs", Title = "РСЧС по всей России" },
        new() { Name = "nabludatel_rf", Title = "наблюдатель.рф" },
        new() { Name = "locatorru", Title = "Локатор России" },
    ];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.Settings), SettingsJsonContext.Default.AppSettings);
                if (s != null)
                {
                    Normalize(s);
                    bool changed = MergeNewChannels(s);
                    changed |= s.MigrateWatchAreas();
                    if (changed) Save(s);
                    return s;
                }
            }
        }
        catch (JsonException)
        {
            // битый файл (оборванная запись) не затираем молча: сохраняем копию — настройки можно восстановить вручную
            try { File.Copy(AppPaths.Settings, AppPaths.Settings + ".bad", true); }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // файл временно недоступен (заблокирован антивирусом, другим экземпляром): работаем на значениях
            // по умолчанию, но рабочие настройки на диске не затираем — и позже при сохранении тоже
            ReadOnlySession = true;
            return new AppSettings
            {
                KnownChannels = [.. AppSettings.DefaultChannels().Select(c => c.Name)],
                ChannelListVersion = AppSettings.CurrentChannelListVersion,
            };
        }

        var def = new AppSettings
        {
            KnownChannels = [.. AppSettings.DefaultChannels().Select(c => c.Name)],
            ChannelListVersion = AppSettings.CurrentChannelListVersion,
        };
        Save(def);
        return def;
    }

    /// <summary>
    /// Значения из файла, отредактированного вручную или старой версией: «null» вместо списков и числа вне разумных
    /// пределов роняли запуск (NRE, AddHours вне диапазона) или ломали работу (отрицательное время жизни тревоги).
    /// </summary>
    internal static void Normalize(AppSettings s)
    {
        s.Channels ??= AppSettings.DefaultChannels();
        s.Channels.RemoveAll(c => c == null);
        foreach (var c in s.Channels) { c.Name ??= ""; c.Title ??= ""; c.Regions ??= []; }
        s.KnownChannels ??= [];
        s.HiddenKinds ??= [];
        s.HiddenLevels ??= [];
        s.NotifyRegions ??= [];
        s.WatchAreas ??= [];
        s.WatchAreas.RemoveAll(a => a == null);
        // поля зон из отредактированного вручную файла: null ронял сравнение id и сопоставление тревог с зонами
        foreach (var a in s.WatchAreas)
        {
            a.Kind ??= WatchArea.RegionKind; a.Id ??= ""; a.Name ??= ""; a.Region ??= "";
            if (a.RadiusKm > 0) a.RadiusKm = Math.Clamp(a.RadiusKm, 3, 100);
        }
        s.WatchAreas.RemoveAll(a => a.Id.Length == 0);
        // порог уведомлений — имя уровня; число вне диапазона («7») заглушило бы все уведомления
        if (!Enum.TryParse<Models.AlertLevel>(s.NotifyMinLevel, out var lvl) || !Enum.IsDefined(lvl)) s.NotifyMinLevel = nameof(Models.AlertLevel.Danger);
        s.PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, 5, 600);
        s.AnimationQuality = Math.Clamp(s.AnimationQuality, 0, 2);
        s.AlertLifetimeMinutes = Math.Clamp(s.AlertLifetimeMinutes, 5, 24 * 60);
        s.AllClearLifetimeMinutes = Math.Clamp(s.AllClearLifetimeMinutes, 1, 24 * 60);
        s.HistoryHours = Math.Clamp(s.HistoryHours, 1, 72);
        // как в настройках интерфейса (варианты 5–50, ползунок пунктов 3–100)
        s.WatchRadiusKm = Math.Clamp(s.WatchRadiusKm, 3, 100);
    }

    /// <summary>
    /// Добавляет в сохранённые настройки каналы, появившиеся в новой версии (по списку <see cref="AppSettings.KnownChannels"/>).
    /// Каналы, которые пользователь удалил или отключил, не возвращаются: учитываются только ещё не виденные.
    /// </summary>
    internal static bool MergeNewChannels(AppSettings s)
    {
        bool changed = false;
        var have = new HashSet<string>(s.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(s.KnownChannels, StringComparer.OrdinalIgnoreCase);
        if (s.ChannelListVersion < 2)
        {
            // до версии 2 список «виденных» мог заполниться всеми каналами по умолчанию ещё до их добавления
            // (инициализатор свойства при чтении старого файла) — ему нельзя доверять: считаем виденными только имеющиеся
            known = new HashSet<string>(have, StringComparer.OrdinalIgnoreCase);
            changed = true;
        }
        // настройки старых версий: всё, что уже есть, считаем «виденным»
        if (known.Count == 0) foreach (var n in have) { known.Add(n); changed = true; }
        foreach (var def in AppSettings.DefaultChannels())
        {
            if (known.Contains(def.Name)) continue;
            known.Add(def.Name);
            if (!have.Contains(def.Name)) s.Channels.Add(def);
            changed = true;
        }
        // до версии 3 история по умолчанию была 3 ч — мало, чтобы треки начинались от места появления объекта
        if (s.ChannelListVersion < 3 && s.HistoryHours == 3) { s.HistoryHours = 24; changed = true; }
        if (s.ChannelListVersion != AppSettings.CurrentChannelListVersion) { s.ChannelListVersion = AppSettings.CurrentChannelListVersion; changed = true; }
        if (changed) s.KnownChannels = [.. known.Order(StringComparer.OrdinalIgnoreCase)];
        return changed;
    }

    private static readonly object _saveLock = new();
    private static string? _pendingJson;
    private static Timer? _saveTimer;

    /// <summary>Немедленная запись (выход, закрытие окна); отменяет отложенную.</summary>
    /// <summary>Настройки не прочитались (файл занят): сеанс на значениях по умолчанию не пишет их поверх рабочих.</summary>
    public static bool ReadOnlySession { get; private set; }

    public static void Save(AppSettings s)
    {
        if (ReadOnlySession) return;
        var json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        lock (_saveLock) { _pendingJson = null; Write(json); }
    }

    /// <summary>
    /// Запись через 0,4 с в фоне: переключатели и легенда меняют настройки часто, синхронная запись на каждый клик
    /// подвешивала интерфейс на медленном диске. Снимок берётся сразу — дальнейшие изменения объекта не попадут в него.
    /// </summary>
    public static void SaveDeferred(AppSettings s)
    {
        if (ReadOnlySession) return;
        var json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        lock (_saveLock)
        {
            _pendingJson = json;
            _saveTimer ??= new Timer(_ => Flush());
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>Дописывает отложенную запись, если она есть.</summary>
    public static void Flush()
    {
        lock (_saveLock)
        {
            if (_pendingJson is not { } json) return;
            _pendingJson = null;
            Write(json);
        }
    }

    private static void Write(string json)
    {
        // атомарно: оборванная запись не оставляет битый JSON
        var tmp = AppPaths.Settings + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.Settings, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Настройки не сохранены: {ex.Message}");
        }
    }
}
