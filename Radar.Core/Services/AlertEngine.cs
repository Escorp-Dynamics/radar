using System.Collections.Concurrent;
using Radar.Models;

namespace Radar.Services;

/// <summary>Состояние одного канала-источника.</summary>
public sealed record ChannelStatus(
    string Name, string Title, bool Enabled, bool Ok, bool Fetching, int Failures, string? Error,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastPost, int Received);

/// <summary>Связь с источниками: ещё не было ни одного ответа / есть свежие данные / ни один канал не отвечает.</summary>
public enum ConnectionState { Connecting, Online, Offline }

public sealed class EngineStatus
{
    public ConnectionState Connection { get; init; }
    /// <summary>Сколько запросов к каналам выполняется прямо сейчас (индикация загрузки).</summary>
    public int InFlight { get; init; }
    /// <summary>Время последнего успешного ответа любого канала (null — ещё не было).</summary>
    public DateTimeOffset? LastSuccess { get; init; }
    public int ChannelsOk { get; init; }
    public int ChannelsFailed { get; init; }
    /// <summary>Сколько каналов ещё загружают историю при запуске (0 — загрузка завершена).</summary>
    public int HistoryPending { get; init; }
    /// <summary>Сколько каналов загружают историю всего (для доли прогресса).</summary>
    public int HistoryTotal { get; init; }
    /// <summary>Сколько сообщений истории уже получено (кеш + сеть).</summary>
    public int HistoryMessages { get; init; }
    public DateTimeOffset LastPoll { get; init; }
    /// <summary>По каждому каналу из настроек (в порядке списка).</summary>
    public IReadOnlyList<ChannelStatus> Channels { get; init; } = [];
    public string? LastError { get; init; }
}

/// <summary>
/// Опрашивает каналы, разбирает сообщения, объединяет дубликаты (агрегаторы репостят региональные каналы)
/// и поддерживает набор активных тревог.
/// </summary>
public sealed partial class AlertEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly TelegramSource _telegram;
    private readonly Geocoder _geocoder;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, long> _lastIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly List<AlertEvent> _events = [];
    /// <summary>Применённые сообщения (канал/id[|часть]) → время сообщения: повтор не применяется второй раз.</summary>
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Отмечает сообщение применённым; false — уже было.</summary>
    private bool MarkSeen(ParsedMessage m) => _seen.TryAdd($"{m.Raw.Channel}/{m.Raw.Id}{(m.Part is { } sp ? "|" + sp : "")}", m.Raw.Time);
    private readonly TrackBuilder _tracks = new();

    public event Action<IReadOnlyList<AlertEvent>>? Changed;
    public event Action<EngineStatus>? StatusChanged;
    public event Action<RawMessage, ParsedMessage>? MessageReceived;
    /// <summary>Аудит (--engine-audit): отбой применён — что снято и что осталось активным в тех же регионах.</summary>
    public Action<ParsedMessage, IReadOnlyList<AlertEvent>, IReadOnlyList<AlertEvent>>? AllClearAudit { get; set; }
    /// <summary>Аудит: сообщение-отбой отброшено (нет региона/пунктов).</summary>
    public Action<ParsedMessage>? AllClearDropped { get; set; }

    public AlertEngine(AppSettings settings, HttpClient http, Geocoder geocoder)
    {
        _settings = settings;
        _telegram = new TelegramSource(http);
        _http = http;
        _geocoder = geocoder;
    }

    public IReadOnlyList<AlertEvent> Snapshot()
    {
        lock (_lock) return [.. _events];
    }

    public IReadOnlyList<Track> TrackSnapshot()
    {
        lock (_lock) return _tracks.Tracks.Select(t => t.Clone()).ToList();
    }

    private readonly System.Threading.Channels.Channel<(RawMessage Msg, ChannelConfig Ch)> _inbox =
        System.Threading.Channels.Channel.CreateUnbounded<(RawMessage, ChannelConfig)>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, (bool Ok, string? Error, bool Reached)> _channelState = new(StringComparer.Ordinal);
    private sealed class ChannelStats
    {
        public long LastSuccessTicks, LastPostTicks;
        public int Received, Failures, Fetching;
    }
    private readonly ConcurrentDictionary<string, ChannelStats> _stats = new(StringComparer.Ordinal);
    private ChannelStats Stats(string name) => _stats.GetOrAdd(name, _ => new ChannelStats());
    private int _initialPending, _initialTotal, _historyMessages, _inFlight;
    private long _lastSuccessTicks; // UTC ticks последнего успешного ответа (0 — не было)
    private readonly ConcurrentDictionary<string, bool> _historyLoaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _historyGate = new(4);
    // самые активные каналы дают до ~40 страниц (по 20 постов) за сутки
    private const int MaxHistoryPages = 60;
    private const int MaxGapPages = 20;
    private readonly ConcurrentDictionary<string, bool> _initialCounted = new(StringComparer.Ordinal);
    /// <summary>Каналы, учтённые в начальной загрузке истории (включённые на момент запуска).</summary>
    private readonly ConcurrentDictionary<string, bool> _initialStarted = new(StringComparer.Ordinal);
    /// <summary>Сообщения старше этого — история (применяются по своему времени), моложе — текущая обстановка.</summary>
    private static readonly TimeSpan HistoryLag = TimeSpan.FromMinutes(3);
    private const int FeedLimit = 400;

    /// <summary>
    /// Время обстановки. При применении истории — время текущего сообщения: тревоги вчерашнего вечера живут и
    /// снимаются отбоями так же, как если бы приложение работало всё это время (иначе всё, что старше 45 мин,
    /// истекало бы сразу и треки обрывались). После истории — реальное (или модельное в демо) время.
    /// </summary>
    private DateTimeOffset? _historyNow
    {
        // тики UTC (0 — нет): читается из потоков обслуживания, геокодера и интерфейса без разрыва значения
        get { long t = Interlocked.Read(ref _historyNowTicks); return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero); }
        set => Interlocked.Exchange(ref _historyNowTicks, value?.UtcTicks ?? 0);
    }
    private long _historyNowTicks;
    private DateTimeOffset Now => _historyNow ?? ClockNow;

    /// <summary>
    /// Работа в реальном времени независимо от модельных часов: живой движок продолжает опрос и истечение по
    /// настоящему времени, пока в окне идёт воспроизведение истории (оно ведёт свои модельные часы <see cref="Clock"/>).
    /// </summary>
    public bool RealTime { get; init; }
    private DateTimeOffset ClockNow => RealTime ? DateTimeOffset.UtcNow : Clock.Now;

    /// <summary>
    /// Единый порядок одновременных сообщений, как бы они ни пришли: время, затем региональные каналы (их привязка
    /// названий надёжнее — «Таволжанка Шбк» у канала Белгородской области и у общего канала), затем имя канала и номер.
    /// Повтор одного текста создаёт тревогу из первого — результат не зависит от того, чей повтор опрос получил раньше.
    /// </summary>
    private static IEnumerable<(RawMessage Msg, ChannelConfig Ch)> Canonical(IEnumerable<(RawMessage Msg, ChannelConfig Ch)> items) =>
        items.OrderBy(x => x.Msg.Time).ThenBy(x => x.Ch.Regions.Count > 0 ? 0 : 1)
             .ThenBy(x => x.Msg.Channel, StringComparer.Ordinal).ThenBy(x => x.Msg.Id);

    /// <summary>Каналы по имени без учёта регистра; повтор имени в настройках не роняет запуск (берётся первый).</summary>
    private static Dictionary<string, ChannelConfig> ChannelsByName(IEnumerable<ChannelConfig> channels)
    {
        var res = new Dictionary<string, ChannelConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in channels) res.TryAdd(c.Name, c);
        return res;
    }

    private MessageCache? _cache;
    private DateTimeOffset _cacheCompacted = DateTimeOffset.UtcNow;
    private List<(RawMessage Msg, ChannelConfig Ch)> _cached = [];

    /// <summary>
    /// Недавние посты (канал, id): первая версия текста (по ней построена тревога) и последняя увиденная —
    /// чтобы заметить правку с дописанным отбоем.
    /// </summary>
    private readonly ConcurrentDictionary<(string, long), (string Original, string Text, DateTimeOffset Time)> _recentText = new();
    /// <summary>Сколько после публикации отслеживаются правки поста.</summary>
    internal static readonly TimeSpan EditWatch = TimeSpan.FromHours(6);
    /// <summary>Сдвиг id правки: правка обрабатывается как отдельное сообщение (свой ключ повтора), ссылка — на исходный пост.</summary>
    internal const long EditIdOffset = 1L << 40;

    private void RememberText(RawMessage m) =>
        _recentText.AddOrUpdate((m.Channel, m.Id), (m.Text, m.Text, m.Time), (_, old) => (old.Original, m.Text, old.Time));

    /// <summary>
    /// Посты, в которые канал дописал новое: отбой («Опасность по БПЛА ⏎ UPD: Отбой»), сбитие или работу ПВО
    /// («UPD: сбит»), прилёт («UPD: есть прилёт»), новое направление или пункты («UPD: в сторону Россоши»).
    /// Возвращает правки как новые сообщения: время — сейчас, текст — места и угроза исходного поста + дописанная часть
    /// (исходная тревога в нём не прочитается заново), id — со сдвигом <see cref="EditIdOffset"/> и номером правки.
    /// </summary>
    internal IEnumerable<RawMessage> EditedClears(IEnumerable<RawMessage> seen, DateTimeOffset now)
    {
        foreach (var m in seen)
        {
            var key = (m.Channel, m.Id);
            if (!_recentText.TryGetValue(key, out var old)) { if (now - m.Time <= EditWatch) RememberText(m); continue; }
            if (old.Text == m.Text) continue;
            RememberText(m);
            // дописанное по сравнению с предыдущей версией; повтор одной правки (тот же хвост) не применяется дважды.
            // Места и угроза — из первой версии: по ней построена тревога, уточнённые пункты отбой по ней не снимут
            if (EditedText(old.Text, m.Text) is null) continue;
            if (EditedText(old.Original, m.Text, newSince: old.Text) is not { } added) continue;
            int n = Interlocked.Increment(ref _editSeq);
            yield return new RawMessage(m.Channel, m.Id + EditIdOffset * n, now, added);
        }
        foreach (var k in _recentText.Where(x => now - x.Value.Time > EditWatch).Select(x => x.Key).ToList()) _recentText.TryRemove(k, out _);
    }

    private int _editSeq;

    /// <summary>
    /// Текст правки: места и угроза исходного поста + дописанные строки, если в них есть значимое (отбой, сбитие/ПВО,
    /// прилёт, курс или пункты). null — правка незначимая (опечатка, «не подтверждено», подпись) или ничего не дописано.
    /// </summary>
    /// <param name="newSince">Предыдущая увиденная версия: дописанным считается только новое относительно неё (по умолчанию — <paramref name="oldText"/>).</param>
    internal static string? EditedText(string oldText, string newText, string? newSince = null)
    {
        static List<string> Lines(string s) => s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var oldLines = Lines(oldText);
        var prev = Lines(newSince ?? oldText);
        var prevSet = prev.ToHashSet();
        var newLines = Lines(newText);
        // дописанное — новые строки, которых не было в предыдущей версии, кроме подписи канала
        var added = newLines.Where(l => !prevSet.Contains(l) && !MessageParser.IsFooterLine(l)).ToList();
        if (added.Count == 0) return null;
        var addedNorm = Gazetteer.Normalize(string.Join("\n", added));
        if (System.Text.RegularExpressions.Regex.IsMatch(addedNorm, @"\b(?:отбой|отмена)\b"))
            return EditedClearText(oldText, newText);
        // отбой в исходном тексте уже был — правки после него не про тревогу
        if (System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(oldText), @"\b(?:отбой|отмена)\b")) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(addedNorm, @"не подтвержд|не подтвердил|ложн|фейк|ошибочн")) return null;
        bool meaningful = System.Text.RegularExpressions.Regex.IsMatch(addedNorm,
            @"сбит|уничтож|минус|пво|прил[её]т|попадан|взрыв|в сторону|в направлени|курсом|далее на|\bна (?:север|юг|запад|восток)|фиксаци|пролет");
        if (!meaningful) return null;
        // правка-пересказ («Кто-то утверждает, что взрывов было несколько») — не новая засечка
        if (System.Text.RegularExpressions.Regex.IsMatch(addedNorm, @"утвержда|сообщают|говорят|по данным|предположительно|возможно|уточняется")) return null;
        // исходный пост — итоговая сводка (МО, «за прошедшую ночь», «прилётов не зафиксировано») или опровержение:
        // правка к нему уточняет итоги, а не сообщает о новой угрозе
        var oldNorm = Gazetteer.Normalize(oldText);
        if (System.Text.RegularExpressions.Regex.IsMatch(oldNorm,
                @"\bморф\b|минобороны|министерств\w* обороны|\bмо рф\b|прошедш\w* (?:ночь|сутки)|за ночь|за сутки|в течение (?:прошедшей )?ночи|" +
                @"(?:не|нет) зафиксирован|не подтвержд|не ведитесь|провокаци|фейк|ложн\w* информ"))
            return null;
        // места исходного поста и строка угрозы: «UPD: сбит» относится к ним
        var places = oldLines.TakeWhile(l => !System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(l), @"^(?:тревога|опасность|внимание|угроза|ракетная|фиксац|upd|работа)"));
        var threat = oldLines.FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(l), @"бпла|беспилот|дрон|ракет|авиац|баллист|уаб|\bкаб"));
        var body = string.Join("\n", added.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"(?i)^(?:upd|апд|обновлено|уточнение)\b[:.]?\s*(?:[\d:.]+\s*)?", "")));
        var parts = places.ToList();
        if (threat != null && !parts.Contains(threat)) parts.Add(threat);
        parts.Add(body);
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Текст правки для разбора: места исходного поста + дописанная часть, если в ней отбой, а в исходном тексте его не было.
    /// null — правка не про отбой (опечатка, уточнение пунктов).
    /// </summary>
    internal static string? EditedClearText(string oldText, string newText)
    {
        static bool HasClear(string s) => System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(s), @"\b(?:отбой|отмена)\b");
        if (HasClear(oldText) || !HasClear(newText)) return null;
        // дописанное — строки с отбоем и всё после первой из них («UPD: Отбой», «UPD: 10:15 Отбой ⏎ наши»);
        // исправленные строки выше (уточнение пунктов) к отбою не относятся
        var newLines = newText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        int first = newLines.FindIndex(HasClear);
        var added = newLines.Skip(first).Where(l => !MessageParser.IsFooterLine(l)).ToList();
        if (added.Count == 0) return null;
        // места — строки исходного поста до строки угрозы («Самарская область ⏎ Опасность по БПЛА» → «Самарская область»):
        // отбой без мест («UPD: Отбой») иначе снял бы тревоги канала по всем регионам
        // места — из исходного текста, по которому построена тревога (уточнённые в правке пункты отбой по ней не снимут)
        var places = oldText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .TakeWhile(l => !System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(l), @"^(?:тревога|опасность|внимание|угроза|ракетная|фиксац|upd|отбой)"));
        // тип угрозы — из исходной тревоги: «UPD: Отбой» относится к ней, а не ко всем угрозам
        var threat = oldText.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(l), @"^(?:тревога|опасность|внимание|угроза|ракетная)"));
        var clear = string.Join("\n", added.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"(?i)^upd:?\s*", "")));
        // «Опасность по БПЛА» → «Отбой по БПЛА», «Ракетная опасность по баллистике» → «Отбой по баллистике»
        if (threat != null && !System.Text.RegularExpressions.Regex.IsMatch(Gazetteer.Normalize(clear), @"бпла|беспилот|дрон|ракет|авиац|баллист"))
        {
            var what = System.Text.RegularExpressions.Regex.Replace(threat, @"(?i)^(?:ракетная\s+)?(?:тревога|опасность|внимание|угроза)\s*", "").Trim();
            if (what.Length == 0 && threat.StartsWith("Ракетная", StringComparison.OrdinalIgnoreCase)) what = "ракетной опасности";
            clear = System.Text.RegularExpressions.Regex.Replace(clear, @"(?i)^(отбой|отмена)\b.*$", "$1") + " " + what;
        }
        return string.Join("\n", places.Append(clear));
    }

    public void Start()
    {
        var channels = _settings.Channels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name)).ToList();
        _initialPending = _initialTotal = channels.Count;
        if (channels.Count == 0) _initialDone.TrySetResult();
        foreach (var c in channels) _initialStarted[c.Name] = true;
        // кеш сообщений за последние сутки: история показывается сразу, из сети догружаются только новые
        _cache = new MessageCache(AppPaths.MessageCache);
        if (_settings.OnlineFrontLine) _ = Task.Run(() => FrontLineSource.RefreshLoopAsync(_http, _cts.Token));
        _ = Task.Run(() => MessageParser.Parse(new RawMessage("", 0, DateTimeOffset.UtcNow, "Белгород БПЛА"), []));
        var byName = ChannelsByName(channels);
        var since = DateTimeOffset.UtcNow.AddHours(-Math.Max(_settings.HistoryHours, 1));
        foreach (var m in _cache.Load(since.AddHours(-1)))
        {
            if (!byName.TryGetValue(m.Channel, out var ch)) continue;
            _lastIds[ch.Name] = Math.Max(_lastIds.GetValueOrDefault(ch.Name, 0), m.Id);
            if (DateTimeOffset.UtcNow - m.Time <= EditWatch) RememberText(m);
            if (m.Time >= since)
            {
                _cached.Add((m, ch));
                var st = Stats(ch.Name);
                st.Received++;
                st.LastPostTicks = Math.Max(st.LastPostTicks, m.Time.UtcTicks);
            }
        }
        _historyMessages = _cached.Count;
        _ = Task.Run(() => ProcessLoopAsync(_cts.Token));
        _ = Task.Run(() => HousekeepingLoopAsync(_cts.Token));
        // каждый канал опрашивается независимо, со сдвигом фазы — сообщения поступают непрерывно
        for (int i = 0; i < channels.Count; i++) StartChannelLoop(channels[i], TimeSpan.FromMilliseconds(150 * i));
    }

    /// <summary>Цикл опроса канала с собственной отменой — канал можно включать и выключать на ходу.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _channelLoops = new(StringComparer.OrdinalIgnoreCase);

    private void StartChannelLoop(ChannelConfig ch, TimeSpan delay)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (_channelLoops.TryRemove(ch.Name, out var old)) { old.Cancel(); old.Dispose(); }
        _channelLoops[ch.Name] = cts;
        // токен берётся сразу: к старту задачи cts уже могли освободить (канал выключили, движок закрыли)
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await ChannelLoopAsync(ch, delay, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"цикл канала {ch.Name}: {ex}"); }
        });
    }

    /// <summary>
    /// Включение/выключение канала на ходу: выключенный не опрашивается и не влияет на состояние связи;
    /// включённый сразу опрашивается (с историей, если её ещё не загружали). Уже полученные тревоги остаются.
    /// </summary>
    public void SetChannelEnabled(string name, bool enabled)
    {
        var ch = _settings.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (ch == null || IsReplay) return;
        ch.Enabled = enabled;
        if (enabled)
        {
            _channelState.TryRemove(ch.Name, out _);
            StartChannelLoop(ch, TimeSpan.Zero);
        }
        else
        {
            if (_channelLoops.TryRemove(ch.Name, out var cts)) { cts.Cancel(); cts.Dispose(); }
            _channelState.TryRemove(ch.Name, out _);
            // начальная загрузка не ждёт выключенный канал
            if (_initialStarted.ContainsKey(ch.Name) && _initialCounted.TryAdd(ch.Name, true) && Interlocked.Decrement(ref _initialPending) == 0)
                _initialDone.TrySetResult();
        }
        PublishStatus();
    }

    // ---------------- воспроизведение архива ----------------

    public bool IsReplay { get; private set; }
    public DateTimeOffset ReplayStart { get; private set; }
    public DateTimeOffset ReplayEnd { get; private set; }
    /// <summary>Запрошенная перемотка (UTC ticks; −1 — нет).</summary>
    private long _seekTicks = -1;
    /// <summary>Сколько архива до точки перемотки прогоняется, чтобы восстановить обстановку на этот момент.</summary>
    public static readonly TimeSpan SeekLookback = TimeSpan.FromHours(6);
    /// <summary>Перемотка выполнена: обстановка восстановлена на новый момент.</summary>
    public event Action? Seeked;

    /// <summary>
    /// Воспроизведение архива: сообщения подаются в обработку по модельным часам <see cref="Clock"/> (ускоренным),
    /// весь остальной конвейер — разбор, трекинг, истечение — тот же, что и в реальном режиме.
    /// <paramref name="start"/>/<paramref name="end"/> — границы воспроизведения (сообщения раньше начала — предыстория для обстановки).
    /// </summary>
    public void StartReplay(IReadOnlyList<RawMessage> messages, double speed, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        if (messages.Count == 0) { Start(); return; }
        IsReplay = true;
        ReplayStart = start ?? messages[0].Time;
        ReplayEnd = end ?? messages[^1].Time;
        Clock.StartSimulation(ReplayStart, speed, owner: this);
        _initialDone.TrySetResult();
        foreach (var c in _settings.Channels) _channelState[c.Name] = (true, null, true);
        SeekReplay(ReplayStart);
        _ = Task.Run(() => HousekeepingLoopAsync(_cts.Token));
        _ = Task.Run(() => ReplayLoopAsync(messages, _cts.Token));
    }

    /// <summary>Перемотка воспроизведения на момент <paramref name="t"/> (в пределах архива).</summary>
    public void SeekReplay(DateTimeOffset t)
    {
        // счётчик — до момента: цикл, забравший момент, всегда видит и его запрос
        Interlocked.Increment(ref _seekRequested);
        Interlocked.Exchange(ref _seekTicks, Math.Clamp(t.UtcTicks, ReplayStart.UtcTicks, Math.Max(ReplayStart.UtcTicks, ReplayEnd.UtcTicks)));
    }

    private long _seekRequested, _seekApplied;
    /// <summary>Число запрошенных перемоток и выполненных к моменту обработки (сообщения до перемотки — устаревшие для ленты).</summary>
    public long SeekRequested => Interlocked.Read(ref _seekRequested);
    public long SeekApplied => Interlocked.Read(ref _seekApplied);

    /// <summary>Момент, куда идёт перемотка (ещё не выполненная), иначе текущее модельное время: шаги «вперёд» складываются.</summary>
    public DateTimeOffset SeekTarget => Interlocked.Read(ref _seekTicks) is var t and >= 0 ? new DateTimeOffset(t, TimeSpan.Zero) : Clock.Now;

    /// <summary>
    /// Синхронный прогон сообщений через весь конвейер (для замеров производительности).
    /// Возвращает число активных событий после каждого сообщения.
    /// </summary>
    public IEnumerable<int> ReplaySync(IReadOnlyList<RawMessage> messages)
    {
        var channels = ChannelsByName(_settings.Channels);
        foreach (var (m, ch) in Canonical(messages.Select(m => (m, channels.GetValueOrDefault(m.Channel) ?? new ChannelConfig { Name = m.Channel }))))
        {
            Clock.StartSimulation(m.Time, 0);
            var parsed = MessageParser.Parse(m, ch.Regions);
            ResolveFromCache(parsed);
            // как в реальной работе: к приходу сообщения истёкшие тревоги и треки уже сняты (обслуживание раз в 15 с)
            Expire();
            Apply(parsed);
            Expire();
            int n;
            lock (_lock) n = _events.Count + _tracks.Tracks.Count;
            yield return n;
        }
    }

    /// <summary>Начать воспроизведение заново (сбрасывает обстановку).</summary>
    public void RestartReplay() => SeekReplay(ReplayStart);

    /// <summary>
    /// Цикл воспроизведения — единственный, кто применяет сообщения архива: подаёт пачкой всё, чьё время настало
    /// по модельным часам; перемотка восстанавливает обстановку прогоном предыстории; в конце — пауза.
    /// </summary>
    private async Task ReplayLoopAsync(IReadOnlyList<RawMessage> messages, CancellationToken ct)
    {
        var channels = ChannelsByName(_settings.Channels);
        (RawMessage, ChannelConfig) Item(RawMessage m) => (m, channels.GetValueOrDefault(m.Channel) ?? new ChannelConfig { Name = m.Channel });
        int i = 0;
        var batch = new List<(RawMessage Msg, ChannelConfig Ch)>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long seek = Interlocked.Exchange(ref _seekTicks, -1);
                if (seek >= 0)
                {
                    Interlocked.Exchange(ref _seekApplied, Interlocked.Read(ref _seekRequested));
                    var target = new DateTimeOffset(seek, TimeSpan.Zero);
                    lock (_lock)
                    {
                        _events.Clear();
                        _tracks.Clear();
                        _seen.Clear();
                    }
                    // часы общие для всего приложения: движок, уже остановленный (выход из истории, смена месяца),
                    // не должен их трогать — иначе живой режим застывал на конце архива
                    if (ct.IsCancellationRequested) return;
                    Clock.JumpTo(target, owner: this);
                    // предыстория — по своему времени, как история при запуске: тревоги живут и снимаются отбоями
                    var from = target - SeekLookback;
                    int lo = 0, hi = messages.Count;
                    while (lo < hi) { int mid = (lo + hi) / 2; if (messages[mid].Time < from) lo = mid + 1; else hi = mid; }
                    i = lo;
                    batch.Clear();
                    while (i < messages.Count && messages[i].Time <= target) batch.Add(Item(messages[i++]));
                    ProcessBatch(batch, ct, rewind: true);
                    if (ct.IsCancellationRequested) return;
                    Changed?.Invoke(Snapshot());
                    Seeked?.Invoke();
                }
                var now = Clock.Now;
                if (now >= ReplayEnd && !Clock.IsPaused)
                {
                    if (ct.IsCancellationRequested) return;
                    // конец архива: останавливаемся на последнем моменте
                    Clock.JumpTo(ReplayEnd, pause: true, owner: this);
                    now = ReplayEnd;
                }
                batch.Clear();
                while (i < messages.Count && messages[i].Time <= now) batch.Add(Item(messages[i++]));
                if (batch.Count > 0) ProcessBatch(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // сбой одной пачки не останавливает воспроизведение; после перемотки сообщаем о готовности,
                // иначе индикатор загрузки истории висел бы навсегда
                System.Diagnostics.Trace.WriteLine($"Воспроизведение: {ex}");
                Seeked?.Invoke();
            }
            try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ChannelLoopAsync(ChannelConfig ch, TimeSpan initialDelay, CancellationToken ct)
    {
        try { await Task.Delay(initialDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            bool gotNew = false;
            TimeSpan retryAfter = TimeSpan.Zero;
            try
            {
                gotNew = await FetchChannelAsync(ch, ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RetryAfterException ra) { failures++; retryAfter = ra.Delay; }
            catch (Exception) { failures++; }

            // активный канал опрашиваем чаще, при ошибках — экспоненциальная пауза
            var baseInterval = Math.Max(5, _settings.PollIntervalSeconds);
            // нет связи ни с одним каналом (сеть пропала у нас) — пробуем часто, чтобы восстановиться сразу после её появления;
            // но не при ограничении частоты (429): частые повторы только продлевают блокировку
            var maxBackoff = Connection == ConnectionState.Offline && retryAfter == TimeSpan.Zero ? 30 : 300;
            var seconds = failures > 0 ? Math.Min(maxBackoff, baseInterval * Math.Pow(2, failures)) : gotNew ? Math.Max(5, baseInterval / 2.0) : baseInterval;
            seconds = Math.Max(seconds, retryAfter.TotalSeconds);
            seconds *= 0.85 + Random.Shared.NextDouble() * 0.3;
            // обычная пауза прерывается сменой интервала опроса (с 1 мин на 10 с — сразу, а не после минуты ожидания);
            // паузы после ошибок и ограничения частоты дорабатываются полностью
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            var wake = failures == 0 ? Volatile.Read(ref _intervalChanged).Task : delay;
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            if (!delay.IsCompleted)
                // новая пауза уже по новому интервалу — с разбросом, чтобы каналы не опрашивались разом
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.PollIntervalSeconds) * Random.Shared.NextDouble()), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
        }
    }

    private TaskCompletionSource _intervalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Интервал опроса изменён в настройках: текущие паузы каналов прерываются и пересчитываются.</summary>
    public void PollIntervalChanged() => Interlocked.Exchange(ref _intervalChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Один опрос канала за раз: цикл канала, «Обновить» и перезапуск цикла не качают историю дважды.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> FetchChannelAsync(ChannelConfig ch, CancellationToken ct)
    {
        var gate = _fetchGates.GetOrAdd(ch.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await FetchChannelCoreAsync(ch, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>Неудачные попытки догрузить «дыру» целиком: после 3 соглашаемся на частичную, чтобы канал не застрял.</summary>
    private readonly ConcurrentDictionary<string, int> _gapRetries = new(StringComparer.OrdinalIgnoreCase);
    private bool StrictGap(string channel) => _gapRetries.AddOrUpdate(channel, 1, (_, n) => n + 1) <= 3;

    private async Task<bool> FetchChannelCoreAsync(ChannelConfig ch, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        var stats = Stats(ch.Name);
        Interlocked.Increment(ref stats.Fetching);
        PublishStatus();
        try
        {
            var last = _lastIds.GetValueOrDefault(ch.Name, 0);
            var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(_settings.HistoryHours, 1));
            List<RawMessage> msgs;
            if (!_historyLoaded.ContainsKey(ch.Name))
            {
                // первый опрос: история за HistoryHours (постранично) — треки строятся от места появления объекта,
                // а не с середины пути. Если канал есть в кеше — только дельта до последнего сохранённого сообщения.
                // Глобальный лимит параллельных загрузок бережёт от блокировки t.me.
                await _historyGate.WaitAsync(ct).ConfigureAwait(false);
                try { msgs = await _telegram.FetchHistoryAsync(ch.Name, cutoff, MaxHistoryPages, ct, last, failOnPartial: last > 0 && StrictGap(ch.Name)).ConfigureAwait(false); }
                finally { _historyGate.Release(); }
                _historyLoaded[ch.Name] = true;
            }
            else
            {
                msgs = await _telegram.FetchAsync(ch.Name, ct).ConfigureAwait(false);
                // страница превью — ~20 последних постов: при всплеске, долгой паузе (ошибки сети, сон) часть сообщений
                // оказалась бы между опросами и потерялась — догружаем назад до последнего известного
                if (last > 0 && msgs.Count > 0 && msgs.Min(m => m.Id) > last + 1)
                {
                    var gap = await _telegram.FetchHistoryAsync(ch.Name, cutoff, MaxGapPages, ct, last, failOnPartial: StrictGap(ch.Name)).ConfigureAwait(false);
                    // оборванная догрузка сдвинула бы _lastIds за непрочитанную «дыру» — тогда исключение, повтор с прежним last
                    msgs = [.. msgs.Concat(gap).DistinctBy(m => m.Id)];
                }
            }
            var newOnes = msgs.Where(m => m.Id > last && m.Time >= cutoff).OrderBy(m => m.Id).ToList();
            // уже полученные посты, в которые канал дописал отбой («… ⏎ UPD: Отбой»): отбой применяется сразу
            foreach (var ed in EditedClears(msgs.Where(m => m.Id <= last), DateTimeOffset.UtcNow))
                _inbox.Writer.TryWrite((ed, ch));
            foreach (var m in newOnes) RememberText(m);
            if (!_initialDone.Task.IsCompleted) Interlocked.Add(ref _historyMessages, newOnes.Count);
            if (msgs.Count > 0) { long top = msgs.Max(m => m.Id); _lastIds.AddOrUpdate(ch.Name, top, (_, cur) => Math.Max(cur, top)); }
            foreach (var m in newOnes) _inbox.Writer.TryWrite((m, ch));
            _cache?.Append(newOnes);
            _gapRetries.TryRemove(ch.Name, out _);
            if (ch.Enabled) _channelState[ch.Name] = (true, null, true);
            Interlocked.Exchange(ref _lastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref stats.LastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref stats.Failures, 0);
            Interlocked.Add(ref stats.Received, newOnes.Count);
            if (msgs.Count > 0)
            {
                long newest = msgs.Max(m => m.Time.UtcTicks);
                long cur;
                while ((cur = Interlocked.Read(ref stats.LastPostTicks)) < newest && Interlocked.CompareExchange(ref stats.LastPostTicks, newest, cur) != cur) { }
            }
            return newOnes.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // канал выключили, пока шёл опрос, — его ошибка уже не влияет на состояние связи
            if (!ct.IsCancellationRequested && ch.Enabled) _channelState[ch.Name] = (false, DescribeError(ex), IsServerReply(ex));
            Interlocked.Increment(ref stats.Failures);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Decrement(ref stats.Fetching);
            // канал учитывается в начальной загрузке один раз (первая попытка — успешная или нет); иначе повторные
            // опросы быстрых каналов и кнопка «Обновить» досрочно «завершали» загрузку истории медленных
            if (_initialStarted.ContainsKey(ch.Name) && _initialCounted.TryAdd(ch.Name, true) && Interlocked.Decrement(ref _initialPending) == 0)
                _initialDone.TrySetResult();
            PublishStatus();
        }
    }

    private readonly TaskCompletionSource _initialDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Единственный потребитель: разбор, применение, публикация изменений пачками.</summary>
    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var batch = new List<(RawMessage Msg, ChannelConfig Ch)>();
        // кешированная история — сразу, не дожидаясь сети
        if (_cached.Count > 0)
        {
            batch.AddRange(_cached);
            _cached = [];
            SafeProcess(batch, ct);
        }

        // догружаемая история при старте обрабатывается одним пакетом в хронологическом порядке
        try { await Task.WhenAny(_initialDone.Task, Task.Delay(TimeSpan.FromSeconds(90), ct)).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var reader = _inbox.Reader;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) { return; }

            // небольшая пауза собирает репосты одного сообщения из разных каналов
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            batch.Clear();
            while (reader.TryRead(out var item)) batch.Add(item);
            SafeProcess(batch, ct);
        }
    }

    /// <summary>
    /// Пачка обрабатывается без риска остановить единственного читателя очереди: ошибка в разборе одного
    /// необычного сообщения иначе молча прекращала бы показ всех тревог при «на связи» в статусе.
    /// </summary>
    private void SafeProcess(List<(RawMessage Msg, ChannelConfig Ch)> batch, CancellationToken ct)
    {
        // сообщения, уже показанные в ленте до сбоя, при повторе по одному второй раз не отправляются
        _feedShown = [];
        try { ProcessBatch(batch, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _feedShown = null; throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"обработка пачки ({batch.Count}): {ex}");
            // пачку целиком повторно не применяем — по одному, чтобы потерять только сбойное сообщение
            if (batch.Count > 1)
                foreach (var item in batch)
                {
                    try { ProcessBatch([item], ct); }
                    catch (Exception e1) when (e1 is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine($"сообщение {item.Msg.Url}: {e1.Message}"); }
                }
        }
        finally { _feedShown = null; }
    }

    private HashSet<RawMessage>? _feedShown;

    /// <summary>Отладка: истечение по текущему времени.</summary>
    internal void ExpireNow() => Expire();

    /// <summary>Тесты: применить пачку сообщений так же, как при запуске (история) или в работе.</summary>
    internal void ApplyBatch(IEnumerable<RawMessage> msgs)
    {
        var channels = ChannelsByName(_settings.Channels);
        ProcessBatch([.. msgs.Select(m => (m, channels.GetValueOrDefault(m.Channel) ?? new ChannelConfig { Name = m.Channel }))], CancellationToken.None);
    }

    /// <summary>
    /// Статистика архива: пачка применяется строго по времени сообщений (обстановка живёт и истекает по нему),
    /// без привязки к текущему времени — между пачками ничего не снимается раньше срока.
    /// </summary>
    internal void ApplyBatchAt(IReadOnlyList<RawMessage> msgs)
    {
        var channels = ChannelsByName(_settings.Channels);
        ProcessBatch([.. msgs.Select(m => (m, channels.GetValueOrDefault(m.Channel) ?? new ChannelConfig { Name = m.Channel }))], CancellationToken.None, archive: true);
    }

    /// <summary>Статистика архива: снять всё, что истекло к моменту <paramref name="t"/>.</summary>
    internal void ExpireAt(DateTimeOffset t)
    {
        _historyNow = t;
        try { Expire(); }
        finally { _historyNow = null; }
    }

    private void ProcessBatch(List<(RawMessage Msg, ChannelConfig Ch)> batch, CancellationToken ct, bool rewind = false, bool archive = false)
    {
        bool changed = false;
        var pending = new List<ParsedMessage>();
        var ordered = Canonical(batch).ToArray();
        // разбор — чистая функция текста: большие пачки (история при старте) разбираются параллельно,
        // применение к обстановке — строго последовательно, в хронологическом порядке
        var parsedAll = new ParsedMessage[ordered.Length];
        if (ordered.Length >= 32)
            // не все ядра: поток отрисовки интерфейса должен успевать между задачами разбора истории
            Parallel.For(0, ordered.Length, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2 - 1) },
                i => parsedAll[i] = MessageParser.Parse(ordered[i].Msg, ordered[i].Ch.Regions));
        else
            for (int i = 0; i < ordered.Length; i++) parsedAll[i] = MessageParser.Parse(ordered[i].Msg, ordered[i].Ch.Regions);
        // история (сообщения старше нескольких минут) применяется по своему времени: тревоги и треки живут,
        // истекают и снимаются отбоями так, как это было бы в реальном времени
        var realNow = ClockNow;
        bool history = archive || (rewind || !IsReplay) && ordered.Length > 0 && realNow - ordered[0].Msg.Time > HistoryLag;
        try
        {
            for (int i = 0; i < ordered.Length; i++)
            {
                var msg = ordered[i].Msg;
                var parsed = parsedAll[i];
                if (history)
                {
                    var t = archive || msg.Time < realNow ? msg.Time : realNow;
                    if (_historyNow is not { } hn || t > hn) _historyNow = t;
                    if (!archive && realNow - t <= HistoryLag) { _historyNow = null; history = false; }
                    else Expire();
                }
                var unresolved = ResolveFromCache(parsed);
                // лента показывает последние 300 — при большой пачке истории старые сообщения в UI не отправляем
                if (ordered.Length - i <= FeedLimit && (_feedShown?.Add(msg) ?? true)) MessageReceived?.Invoke(msg, parsed);
                changed |= Apply(parsed);
                if (unresolved.Count > 0 && _settings.OnlineGeocoding && !IsReplay) pending.Add(parsed);
            }
        }
        catch
        {
            _historyNow = null;
            throw;
        }
        // архив: итоговое истечение — по времени последнего сообщения пачки, живой режим — по текущему
        if (!archive) _historyNow = null;
        if (pending.Count > 0) _ = Task.Run(() => GeocodeInBackgroundAsync(pending, ct), ct);
        try { changed |= Expire(); }
        finally { _historyNow = null; }
        if (changed) Changed?.Invoke(Snapshot());
    }

    private async Task HousekeepingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // при воспроизведении время ускорено — истечение тревог проверяем чаще
            try { await Task.Delay(TimeSpan.FromSeconds(IsReplay ? 1 : 15), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            try
            {
                if (Expire()) Changed?.Invoke(Snapshot());
                PublishStatus();
                // кеш сообщений обрезается при запуске; приложение работает неделями — раз в час убираем старое
                if (!IsReplay && _cache != null && DateTimeOffset.UtcNow - _cacheCompacted > TimeSpan.FromHours(1))
                {
                    _cacheCompacted = DateTimeOffset.UtcNow;
                    _cache.Load(DateTimeOffset.UtcNow.AddHours(-Math.Max(_settings.HistoryHours, 1) - 1));
                }
            }
            // сбой подписчика или истечения не должен остановить обслуживание на всё время работы
            catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("обслуживание: " + ex); }
        }
    }

    /// <summary>
    /// Состояние связи: «на связи», если хотя бы один канал недавно ответил; «отключено», если все опрошенные каналы
    /// последний раз ответили ошибкой или успешных ответов давно не было (сеть, блокировка, страница-заглушка провайдера).
    /// </summary>
    public ConnectionState Connection
    {
        get
        {
            if (IsReplay) return ConnectionState.Online;
            long ticks = Interlocked.Read(ref _lastSuccessTicks);
            // сервер ответил отказом (429, 404) — сеть у нас есть: такой канал не считается потерей связи
            bool anyOk = _channelState.Any(c => c.Value.Ok), anyFailed = _channelState.Any(c => !c.Value.Ok && !c.Value.Reached);
            if (ticks == 0)
            {
                // до первого успеха: одна ошибка (удалённый канал) — ещё не «отключено»; нужны несколько отказов подряд
                int enabled = _settings.Channels.Count(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name));
                int failedCount = _channelState.Count(c => !c.Value.Ok && !c.Value.Reached);
                if (anyFailed && failedCount >= Math.Min(3, Math.Max(1, enabled))) return ConnectionState.Offline;
                // сервер отвечает отказом (429/404) — сеть есть, данных пока нет по вине канала, а не связи
                return _channelState.Any(c => !c.Value.Ok && c.Value.Reached) ? ConnectionState.Online : ConnectionState.Connecting;
            }
            if (!anyOk) return _channelState.Any(c => !c.Value.Ok && c.Value.Reached) ? ConnectionState.Online : ConnectionState.Offline;
            return DateTimeOffset.UtcNow.UtcTicks - ticks > StaleAfter.Ticks && !_channelState.Any(c => !c.Value.Ok && c.Value.Reached) ? ConnectionState.Offline : ConnectionState.Online;
        }
    }

    /// <summary>Без единого успешного ответа дольше этого — связи нет (с запасом на паузы между опросами).</summary>
    private TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Max(90, 4 * Math.Max(5, _settings.PollIntervalSeconds)));

    /// <summary>Сервер ответил отказом (429, 404): сеть есть, ограничен или не найден сам канал. 5xx и страница-заглушка — не такой случай.</summary>
    internal static bool IsServerReply(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.NotFound };

    /// <summary>Причина ошибки канала понятными словами (для таблицы источников и строки статуса).</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Telegram ограничил частоту запросов",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "канал не найден",
        HttpRequestException { StatusCode: { } code } => $"ошибка сервера ({(int)code})",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "нет соединения с t.me",
        HttpRequestException => "нет соединения с t.me",
        TaskCanceledException or TimeoutException => "превышено время ожидания",
        InvalidDataException => "лента канала недоступна (закрыт, удалён или подменена страница)",
        _ => ex.Message,
    };

    private IReadOnlyList<ChannelStatus> ChannelStatuses() => [.. _settings.Channels
        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
        .Select(c =>
        {
            var st = Stats(c.Name);
            var state = _channelState.TryGetValue(c.Name, out var cs) ? cs : (true, null, true);
            long ls = Interlocked.Read(ref st.LastSuccessTicks), lp = Interlocked.Read(ref st.LastPostTicks);
            return new ChannelStatus(c.Name, c.Title, c.Enabled, state.Ok, Volatile.Read(ref st.Fetching) > 0, Volatile.Read(ref st.Failures), state.Error,
                ls == 0 ? null : new DateTimeOffset(ls, TimeSpan.Zero), lp == 0 ? null : new DateTimeOffset(lp, TimeSpan.Zero), Volatile.Read(ref st.Received));
        })];

    private void PublishStatus()
    {
        int ok = _channelState.Count(c => c.Value.Ok);
        var failed = _channelState.Where(c => !c.Value.Ok).ToList();
        long ticks = Interlocked.Read(ref _lastSuccessTicks);
        StatusChanged?.Invoke(new EngineStatus
        {
            Connection = Connection,
            InFlight = Math.Max(0, Volatile.Read(ref _inFlight)),
            LastSuccess = ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            ChannelsOk = ok,
            ChannelsFailed = failed.Count,
            HistoryPending = IsReplay || _initialDone.Task.IsCompleted ? 0 : Math.Max(0, Volatile.Read(ref _initialPending)),
            HistoryTotal = _initialTotal,
            HistoryMessages = Volatile.Read(ref _historyMessages),
            LastPoll = DateTimeOffset.UtcNow,
            LastError = failed.Count > 0 ? $"@{failed[0].Key}: {failed[0].Value.Error}" : null,
            Channels = ChannelStatuses(),
        });
    }

    /// <summary>Принудительный опрос всех каналов (кнопка «Обновить»).</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var channels = _settings.Channels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name)).ToList();
        using var limiter = new SemaphoreSlim(4);
        await Task.WhenAll(channels.Select(async ch =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try { await FetchChannelAsync(ch, ct).ConfigureAwait(false); }
            catch (Exception) { }
            finally { limiter.Release(); }
        })).ConfigureAwait(false);
    }

    private static List<string> Queries(ParsedPlace p)
    {
        var region = Gazetteer.Get(p.RegionId)!;
        var regionName = region.PolygonQuery ?? region.Name;
        var queries = new List<string> { $"{p.GeocodeQuery}, {regionName}" };
        if (p.GeocodeQuery!.EndsWith(" район", StringComparison.OrdinalIgnoreCase))
            queries.Add($"{p.GeocodeQuery[..^6]} муниципальный округ, {regionName}");
        queries.Add(p.GeocodeQuery);
        return queries;
    }

    /// <summary>Подставляет координаты из кэша; возвращает пункты, которые ещё нужно геокодировать.</summary>
    private List<ParsedPlace> ResolveFromCache(ParsedMessage m)
    {
        var unresolved = new List<ParsedPlace>();
        foreach (var p in m.Places)
        {
            if (p.Pos != null || p.GeocodeQuery == null || p.RegionId == null) continue;
            bool known = false;
            foreach (var q in Queries(p))
            {
                if (!_geocoder.TryGetCached(q, p.RegionId, out var pos)) { known = false; break; }
                known = true;
                if (pos != null) { p.Pos = pos; break; }
            }
            if (!known && p.Pos == null) unresolved.Add(p);
        }
        m.PendingPlaces.AddRange(unresolved);
        m.Places.RemoveAll(p => p.Pos == null);
        // блоки сводки применяются вместо целого сообщения — у них свои пункты
        foreach (var b in m.Blocks) ResolveFromCache(b);
        return unresolved;
    }

    private async Task GeocodeInBackgroundAsync(List<ParsedMessage> messages, CancellationToken ct)
    {
        // свежие сообщения важнее — геокодируем их первыми
        foreach (var m in messages.OrderByDescending(m => m.Raw.Time))
        {
            var added = new List<ParsedPlace>();
            foreach (var p in m.PendingPlaces)
            {
                foreach (var q in Queries(p))
                {
                    try { p.Pos = await _geocoder.GeocodeAsync(q, p.RegionId!, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { }
                    if (p.Pos != null) break;
                }
                if (p.Pos != null) added.Add(p);
            }
            try { if (added.Count > 0 && Refine(m, added)) Changed?.Invoke(Snapshot()); }
            // ошибка подписчика не должна обрывать геокодирование остальных сообщений пачки
            catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine($"уточнение: {ex.Message}"); }
        }
    }

    /// <summary>Уточняет уже показанную тревогу координатами, полученными геокодером.</summary>
    private bool Refine(ParsedMessage m, List<ParsedPlace> added)
    {
        lock (_lock)
        {
            var now = Now;
            var ev = _events.FirstOrDefault(e => e.ExpiresAt > now && e.MessageKeys.Contains((m.Raw.Channel, m.Raw.Id)));
            if (ev == null) return m.Status == AlertStatus.AllClear && ReapplyClear(m, added);
            if (ev.Status == AlertStatus.AllClear) return false;
            // пункт, уже снятый частичным отбоем, не возвращается; повтор того же места не дублируется
            var clearedNames = ev.PartialClears.Select(c => c.RegionName).ToHashSet(StringComparer.Ordinal);
            var points = ev.Points.Where(p => !p.IsRegionLevel || ev.WholeRegionIds.Contains(p.RegionId ?? "")).ToList();
            foreach (var p in added.Where(p => !p.IsTarget))
            {
                var mp = ToMapPoint(p);
                if (clearedNames.Contains(mp.Label) || points.Any(q => SamePlace(q, mp))) continue;
                points.Add(mp);
            }
            // регион был залит целиком только потому, что пункт ещё не был найден: теперь тревога — по пункту
            var resolvedRegions = added.Where(p => p.RegionId != null).Select(p => p.RegionId!).ToHashSet(StringComparer.Ordinal);
            if (ev.WholeRegionIds.Any(resolvedRegions.Contains) && m.Places.Count + m.PendingPlaces.Count > 0)
            {
                ev.WholeRegionIds = [.. ev.WholeRegionIds.Where(r => !resolvedRegions.Contains(r))];
                points.RemoveAll(p => p.IsRegionLevel && p.RegionId is { } r && resolvedRegions.Contains(r));
            }
            var target = added.FirstOrDefault(p => p.IsTarget);
            if (target != null && ev.Target == null)
            {
                ev.Target = target.Pos;
                ev.Origin = points.Count > 0 ? points[^1].Pos : Gazetteer.Get(ev.RegionIds.FirstOrDefault())?.Center;
            }
            if (points.Count == 0) return false;
            ev.Points = points;
            AddObservation(ev, points, ev.Origin, ev.Target, target?.Label, m);
            return true;
        }
    }

    /// <summary>
    /// Отбой по пунктам, которых не было в кеше геокодера: при первом применении он ничего не снял (не по региону —
    /// иначе отбой по одному селу гасил бы всю область), теперь пункты найдены — применяем его по ним.
    /// </summary>
    private bool ReapplyClear(ParsedMessage m, List<ParsedPlace> added)
    {
        foreach (var p in added)
            if (!m.Places.Contains(p)) m.Places.Add(p);
        m.PendingPlaces.Clear();
        m.Reapply = true;
        return ApplyOne(m);
    }

    private bool Apply(ParsedMessage m)
    {
        if (m.IsJunk || m.IsInformational) return false;
        // сводка блоками: «регионы ⏎ Опасность по БПЛА ⏎ другие регионы ⏎ Отбой …» — каждый блок со своим статусом
        if (m.Blocks.Count > 0)
        {
            bool any = false;
            foreach (var b in m.Blocks) any |= Apply(b);
            return any;
        }
        if (m.RegionLevels.Count == 0) return ApplyOne(m);
        bool changed = false;
        foreach (var part in MessageParser.SplitByLevel(m)) changed |= ApplyOne(part);
        return changed;
    }

    private bool ApplyOne(ParsedMessage m)
    {
        // пункты, которые геокодер ещё найдёт: отбой по ним — не «без пунктов» и не «по всему региону».
        // Без геокодирования (воспроизведение, офлайн) пункт так и останется неизвестным — как раньше, по региону
        bool pending = m.PendingPlaces.Count > 0 && _settings.OnlineGeocoding && !IsReplay;
        if (m.Status == AlertStatus.AllClear && m.RefersToPrevious && m.Places.Count == 0 && !pending)
            return ClearPrevious(m);
        if (m.Threat == ThreatType.None && m.Status != AlertStatus.AllClear) return false;
        // отбой только по неизвестным пока пунктам — ждём геокодер (ReapplyClear), регион целиком не снимаем
        if (m.Status == AlertStatus.AllClear && pending && m.Places.Count == 0) return false;
        if (m.Regions.Count == 0 && m.Places.Count == 0)
        {
            if (m.Status == AlertStatus.AllClear)
            {
                if (ClearLastOfChannel(m, out bool repeat)) return true;
                if (!repeat) AllClearDropped?.Invoke(m);
            }
            return false;
        }

        var signature = Gazetteer.Normalize(string.Concat(m.CleanText.Where(char.IsLetterOrDigit))) + (m.Part is { } pt ? "|" + pt : "");
        var now = Now;
        var lifetime = TimeSpan.FromMinutes(m.Status == AlertStatus.AllClear ? _settings.AllClearLifetimeMinutes : _settings.AlertLifetimeMinutes);

        lock (_lock)
        {
            // Тот же текст из другого канала (репост агрегатора) — только добавляем источник
            // репост того же текста; но повтор тревоги после (частичного) отбоя — новое объявление, не дубль
            var dup = _events.FirstOrDefault(e => e.Signature == signature && Math.Abs((e.Time - m.Raw.Time).TotalMinutes) < 15
                                                  && e.ExpiresAt > now && !e.HasPartialClear);
            if (dup != null && !m.Reapply)
            {
                dup.MessageKeys.Add((m.Raw.Channel, m.Raw.Id));
                if (dup.Sources.All(s => s.Channel != m.Raw.Channel)) { dup.Sources.Add(m.Raw); dup.Touch(); }
                // канал повтора ведёт тот же объект: трек не зависит от того, чей повтор пришёл первым
                _tracks.AddChannel(dup, m.Raw.Channel);
                return false;
            }
            if (!m.Reapply && !MarkSeen(m)) return false;

            // «Москва, район Внуково»: город федерального значения — это регион; при уточнении района точка города лишняя
            var cityRegion = m.Places.Where(p => !p.IsArea && p.RegionId != null && Gazetteer.Get(p.RegionId) is { } rg && rg.Name == p.Label).ToList();
            if (cityRegion.Count > 0 && m.Places.Count > cityRegion.Count)
                foreach (var c in cityRegion)
                    if (m.Places.Any(p => p != c && p.RegionId == c.RegionId)) m.Places.Remove(c);
            var nonTargets = m.Places.Where(p => !p.IsTarget).ToList();
            var targets = m.Places.Where(p => p.IsTarget).ToList();
            var points = new List<MapPoint>();
            foreach (var p in nonTargets) points.Add(ToMapPoint(p));
            if (points.Count == 0 && targets.Count == 0)
                foreach (var r in m.Regions)
                {
                    var reg = Gazetteer.Get(r);
                    if (reg != null) points.Add(new MapPoint(reg.Center, reg.Name, true) { RegionId = reg.Id });
                }

            GeoPoint? origin = nonTargets.FirstOrDefault(p => p.IsOrigin)?.Pos ?? TrackAnchor(nonTargets);
            // «в сторону Ладомировки, далее на Ольховатский район»: конечная цель цепочки задаёт направление,
            // ближняя (в паре км от места фиксации) — нет. Цель в другой стороне — из другого предложения сводки
            // («…на Таганрог. … Из-под Чернигова в сторону Курской области»): не продолжение цепочки
            if (targets.Count > 1 && targets[^1].Pos is { } lastT && targets[0].Pos is { } firstT &&
                nonTargets.LastOrDefault()?.Pos is { } from && Geo.DistanceKm(from, lastT) > Geo.DistanceKm(from, firstT) &&
                (Geo.DistanceKm(from, firstT) < 10 || Geo.AngleDiff(Geo.Bearing(from, firstT), Geo.Bearing(from, lastT)) <= 60))
                targets = [targets[^1], .. targets.Take(targets.Count - 1)];
            GeoPoint? target = targets.FirstOrDefault()?.Pos;
            if (origin == null && target != null && m.Regions.Count > 0)
            {
                var src = m.Regions.Select(Gazetteer.Get).FirstOrDefault(r => r != null && targets.All(t => t.RegionId != r.Id));
                if (src != null)
                {
                    points.Add(new MapPoint(src.Center, src.Name, true) { RegionId = src.Id });
                    // центр российского региона — не место объекта («Ростовская область … БПЛА на юг, Ровеньки»):
                    // курс от него к цели был бы выдумкой; регион-источник за границей — сторона пуска
                    if (Gazetteer.IsForeign(src.Id)) origin = src.Center;
                }
            }
            if (target == null) origin = null;
            // цель-регион («в направлении Ростовской области») без места объекта — не положение: центр области
            // в треке тянул бы путь назад («Тацинский район → Ростовская область», «Счастье → ЛНР»)
            bool regionTarget = targets.Count > 0 && targets[0].RegionId is { } trg && Gazetteer.Get(trg)?.Name == targets[0].Label;
            if (target != null && origin == null) points.Add(new MapPoint(target.Value, targets[0].Label, regionTarget) { RegionId = targets[0].RegionId });

            var regionIds = m.Regions.Count > 0 ? m.Regions.ToList() : m.Places.Select(p => p.RegionId!).Distinct().ToList();
            // регион «целиком» — если для него не названо ни одного пункта или района
            // зона вдоль общей границы относится к обоим регионам
            // (тревога с ещё не найденным пунктом заливает регион — безопаснее; Refine сузит её до пункта)
            var whole = regionIds.Where(r => m.Places.All(p => p.RegionId != r && !BorderTouches(p.BorderId, r))).ToList();
            bool launch = m.Status == AlertStatus.Launch;
            if (launch)
            {
                // пуск: место пуска на стороне противника — регионы РФ не заливаются, отметка ставится в месте пуска
                whole.Clear();
                var site = points.FirstOrDefault(p => !p.IsRegionLevel)?.Pos
                           ?? m.Regions.Concat(m.OriginRegions).Select(Gazetteer.Get).FirstOrDefault(r => r != null)?.Center;
                if (site is { } s0)
                {
                    points = [new MapPoint(s0, points.FirstOrDefault(p => !p.IsRegionLevel)?.Label
                                               ?? Gazetteer.Get(m.Regions.Concat(m.OriginRegions).FirstOrDefault())?.Name ?? "", false) { RegionId = m.Regions.FirstOrDefault() }];
                    origin = s0;
                }
                // направление: цель из сообщения, иначе курс словами
                if (target == null && m.Heading is { } hd && origin is { } o0) target = Geo.Destination(o0, hd, 120);
            }
            var key = $"{(launch ? "launch|" : "")}{m.Threat}|{m.Kind}|{string.Join(",", regionIds.Order())}|{string.Join(",", points.Select(p => p.Label).Order())}";
            var regionNames = string.Join(", ", regionIds.Select(r => Gazetteer.Get(r)?.Name ?? r));

            if (m.Status == AlertStatus.AllClear)
            {
                // Отбой: гасим активные тревоги по указанным пунктам либо по всему региону
                // «Отбой … Севастополь» — город федерального значения равен региону: отбой по всему региону
                var cityRegions = m.Places.Where(p => p.RegionId != null && Gazetteer.Get(p.RegionId)?.Name == p.Label && !p.IsTarget)
                    .Select(p => p.RegionId!).ToHashSet(StringComparer.Ordinal);
                var cityNames = cityRegions.Select(r => Gazetteer.Get(r)!.Name).ToHashSet(StringComparer.Ordinal);
                var clearPts = points.Where(p => !p.IsRegionLevel && !cityNames.Contains(p.Label)).ToList();
                var labels = new HashSet<string>(clearPts.Select(p => p.Label));
                // «Отбой … по южной части области»: гасим тревоги, все точки которых лежат в этой части
                var parts = points.Where(p => p.PartRegion != null).ToList();
                bool InParts(GeoPoint pos) => parts.Any(pt => RegionPart.Contains(pt.PartRegion!, pt.PartBearing, pos));
                bool anyThreat = m.Threat is ThreatType.None or ThreatType.General;
                var cleared = AllClearAudit != null ? new List<AlertEvent>() : null;
                var narrowed = new List<AlertEvent>();
                // запоздавший отбой (пришёл с историей другого канала) не снимает тревогу, подтверждённую позже него
                foreach (var e in _events.Where(e => e.Status != AlertStatus.AllClear && !NewerThanClear(e, m) && e.RegionIds.Intersect(regionIds).Any()
                                                     && (anyThreat || ThreatClearedBy(e.Threat, m.Threat))))
                {
                    narrowed.Add(e);
                    // отбой «по региону» снимает тревогу, только если все её регионы РФ упомянуты в отбое:
                    // «Отбой … Севастополь» не гасит общую тревогу «Елец, Бельбек» (Липецкая обл. + Севастополь)
                    bool covered = e.RegionIds.Where(r => !Gazetteer.IsForeign(r)).All(regionIds.Contains);
                    // «Севастополь, ЮБК — отбой» снимает Севастополь целиком, но не пункты Крыма далеко от названных мест
                    // (тревога «Джанкой, Севастополь»): такая тревога сужается — без Севастополя и без названных пунктов
                    if (e.RegionIds.Any(cityRegions.Contains) && covered && parts.Count == 0)
                    {
                        // «Крым и Севастополь» (регион без пунктов) — отбой по всему Крыму; сужаем, только если по региону названы пункты.
                        // «Далеко» — вне зон названных пунктов отбоя (центр Севастополя как ориентир не годится: город большой)
                        bool far = e.Points.Any(p => !p.IsRegionLevel && p.RegionId is { } pr && !cityRegions.Contains(pr) &&
                                                     clearPts.Any(c => c.RegionId == pr) &&
                                                     !clearPts.Any(c => SamePlace(p, c) || Geo.DistanceKm(c.Pos, p.Pos) <= c.RadiusKm + p.RadiusKm));
                        if (far)
                        {
                            bool nr = NarrowEvent(e, cityRegions, m.Raw);
                            var hit0 = e.Points.Where(p => clearPts.Any(c => SamePlace(p, c))).ToList();
                            if (NarrowPoints(e, hit0, m.Raw) || nr) cleared?.Add(e);
                            continue;
                        }
                    }
                    bool regionWide = (labels.Count == 0 || e.RegionIds.Any(cityRegions.Contains)) && covered;
                    // тревога по нескольким регионам, отбой — только в части из них: снимаем эти регионы, остальные остаются
                    if ((labels.Count == 0 || e.RegionIds.Any(cityRegions.Contains)) && !covered && parts.Count == 0)
                    {
                        if (NarrowEvent(e, regionIds, m.Raw)) cleared?.Add(e);
                        continue;
                    }
                    // тревога по всему региону и по другой части (даже пересекающейся) отбоем части не снимается
                    // отбой по пунктам/районам/части: снимаются только совпавшие точки тревоги, остальные остаются
                    var hit = regionWide ? [] : e.Points.Where(p => clearPts.Any(c => SamePlace(p, c)) ||
                        parts.Count > 0 && !p.IsRegionLevel && p.PartRegion == null && InParts(p.Pos)).ToList();
                    if (!regionWide && hit.Count == 0) continue;
                    var rest = e.Points.Except(hit).ToList();
                    if (regionWide || rest.All(p => p.IsRegionLevel) && e.WholeRegionIds.Count == 0)
                    {
                        if (e.ExpiresAt > now) cleared?.Add(e);
                        e.ExpiresAt = now;
                    }
                    else if (NarrowPoints(e, hit, m.Raw)) cleared?.Add(e);
                }
                // после частичного отбоя тревоги разных каналов могли сузиться до одной и той же местности
                foreach (var e in narrowed.Where(e => e.ExpiresAt > now)) MergeSameArea(e, now);
                if (cleared != null)
                    AllClearAudit!(m, cleared, [.. _events.Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > now && e.RegionIds.Intersect(regionIds).Any())]);
                if (parts.Count > 0 && clearPts.All(p => p.PartRegion != null))
                    _tracks.ClearWhere(InParts, anyThreat ? null : m.Threat);
                else if (clearPts.Count > 0 && cityRegions.Count == 0)
                    // отбой по пунктам: гаснут треки у этих пунктов, а не во всей области
                    _tracks.ClearWhere(h => clearPts.Any(c => Geo.DistanceKm(c.Pos, h) <= c.RadiusKm + 15), anyThreat ? null : m.Threat);
                else
                {
                    var boxes = regionIds.Select(Gazetteer.Get).Where(r => r != null).Select(r => r!.DefaultBox).ToList();
                    _tracks.ClearWhere(p => boxes.Any(b => p.Lat >= b.MinLat && p.Lat <= b.MaxLat && p.Lon >= b.MinLon && p.Lon <= b.MaxLon),
                        anyThreat ? null : m.Threat);
                }
            }
            else
            {
                var existing = _events.FirstOrDefault(e => e.Key == key && e.ExpiresAt > now && e.Status != AlertStatus.AllClear)
                               ?? (launch ? null : FindRefinable(m, points, whole, now))
                               ?? (launch ? null : FindMissileWave(m, points, whole, now))
                               ?? (launch ? null : FindRepost(m, now));
                if (existing != null)
                {
                    var before = HistoryState.Of(existing);
                    RefineThreat(existing, m, key, regionIds, whole);
                    // пункты, отданные более свежим сообщениям, возвращаются: это сообщение снова о них
                    if (!existing.HasPartialClear && points.Any(p => !p.IsRegionLevel && !existing.Points.Any(q => SamePlace(p, q))))
                        existing.Points = [.. existing.Points, .. points.Where(p => !p.IsRegionLevel && !existing.Points.Any(q => SamePlace(p, q)))];
                    // регион целиком, добавленный уточнением, получает свою отметку (плашку региона)
                    if (!existing.HasPartialClear && points.Any(p => p.IsRegionLevel && !existing.Points.Any(q => q.IsRegionLevel && q.RegionId == p.RegionId)))
                        existing.Points = [.. existing.Points, .. points.Where(p => p.IsRegionLevel && !existing.Points.Any(q => q.IsRegionLevel && q.RegionId == p.RegionId))];
                    // запоздавшее сообщение (история медленного канала) не откатывает время и срок назад
                    if (m.Raw.Time > existing.Time) existing.Time = m.Raw.Time;
                    existing.Text = m.DisplayText;
                    if (m.Level > existing.Level) existing.Level = m.Level;
                    // повтор полной тревоги после частичного отбоя — снова действует целиком
                    if (existing.HasPartialClear)
                    {
                        existing.Points = points;
                        existing.RegionIds = regionIds;
                        existing.WholeRegionIds = whole;
                        existing.RegionNames = regionNames;
                        existing.ClearPartial();
                    }
                    if (m.Raw.Time + lifetime > existing.ExpiresAt) existing.ExpiresAt = m.Raw.Time + lifetime;
                    if (existing.Sources.All(s => s.Channel != m.Raw.Channel)) existing.Sources.Add(m.Raw);
                    existing.MessageKeys.Add((m.Raw.Channel, m.Raw.Id));
                    if (!launch)
                        foreach (var r in ExpectedRegions(m, regionIds))
                            if (!existing.ExpectedRegionIds.Contains(r)) existing.ExpectedRegionIds.Add(r);
                    existing.Touch();
                    if (!launch) AddObservation(existing, points, origin, target, targets.FirstOrDefault()?.Label, m);
                    AddLaunchMarker(m, target ?? targets.FirstOrDefault()?.Pos, lifetime);
                    existing.AddHistory(m.Raw.Time, m.Raw.Channel, HistoryState.Describe(before, HistoryState.Of(existing)));
                    AbsorbOlder(existing, now);
                    return true;
                }
            }

            var ev = new AlertEvent
            {
                Key = key,
                Threat = m.Threat == ThreatType.None ? ThreatType.General : m.Threat,
                Kind = m.Kind,
                Kinds = m.Kinds,
                Level = m.Level,
                Nearby = m.Nearby,
                Status = m.Status,
                RegionIds = regionIds,
                WholeRegionIds = m.Status == AlertStatus.AllClear ? [] : whole,
                ExpectedRegionIds = m.Status is AlertStatus.AllClear or AlertStatus.Launch ? [] : new(ExpectedRegions(m, regionIds)),
                RegionNames = regionNames,
                Signature = signature,
                Time = m.Raw.Time,
                Text = m.DisplayText,
                Points = points,
                Origin = origin,
                Target = target,
                ExpiresAt = m.Raw.Time + lifetime,
            };
            ev.Sources.Add(m.Raw);
            ev.MessageKeys.Add((m.Raw.Channel, m.Raw.Id));
            ev.AddHistory(m.Raw.Time, m.Raw.Channel, HistoryState.Initial(ev));
            _events.Add(ev);
            AbsorbOlder(ev, now);
            if (m.Status is not (AlertStatus.AllClear or AlertStatus.Launch))
                AddObservation(ev, points, origin, target, targets.FirstOrDefault()?.Label, m);
            AddLaunchMarker(m, target ?? targets.FirstOrDefault()?.Pos, lifetime);
            return true;
        }
    }

    /// <summary>
    /// «Отбой ... по всем ранее объявленным»: снимаем активные тревоги того же типа (или любого — если тип не указан)
    /// в регионах сообщения; если регионы не указаны — во всех регионах, где эти тревоги объявлял тот же канал.
    /// </summary>
    /// <summary>Окно, в котором «Отбой» без региона относится к последним тревогам того же канала.</summary>
    private static readonly TimeSpan BareClearWindow = TimeSpan.FromMinutes(45);

    /// <summary>
    /// «Отбой» без региона и пунктов (radar_rschs, locatorru …) — снимает последнее объявление того же канала:
    /// тревоги из той же «серии» (до 3 минут от последней), не старше <see cref="BareClearWindow"/>, того же типа
    /// (если тип в отбое указан). Отбой отмечается в ленте с регионами снятых тревог.
    /// </summary>
    private bool ClearLastOfChannel(ParsedMessage m, out bool repeat)
    {
        var now = Now;
        bool anyThreat = m.Threat is ThreatType.None or ThreatType.General;
        lock (_lock)
        {
            repeat = !MarkSeen(m);
            if (repeat) return false;
            var own = _events.Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > now
                                         && e.Sources.Any(s => s.Channel == m.Raw.Channel)
                                         && m.Raw.Time - e.Time <= BareClearWindow && e.Time <= m.Raw.Time
                                         && (anyThreat || ThreatClearedBy(e.Threat, m.Threat))).ToList();
            if (own.Count == 0) return false;
            var last = own.Max(e => e.Time);
            var batch = own.Where(e => last - e.Time <= TimeSpan.FromMinutes(3)).ToList();
            foreach (var e in batch) e.ExpiresAt = now;
            var regionIds = batch.SelectMany(e => e.RegionIds).Where(r => !Gazetteer.IsForeign(r)).Distinct().ToList();
            var boxes = regionIds.Select(Gazetteer.Get).Where(r => r != null).Select(r => r!.DefaultBox).ToList();
            _tracks.ClearWhere(p => boxes.Any(b => p.Lat >= b.MinLat && p.Lat <= b.MaxLat && p.Lon >= b.MinLon && p.Lon <= b.MaxLon),
                anyThreat ? null : m.Threat);
            AllClearAudit?.Invoke(m, batch, []);
            // отбой в ленте — с регионами, к которым он относится
            var ev = new AlertEvent
            {
                Key = $"clear|{m.Raw.Channel}|{m.Raw.Id}",
                Threat = anyThreat ? batch[0].Threat : m.Threat,
                Kind = m.Kind,
                Kinds = m.Kinds,
                Status = AlertStatus.AllClear,
                RegionIds = regionIds,
                RegionNames = string.Join(", ", regionIds.Select(r => Gazetteer.Get(r)?.Name ?? r)),
                Signature = Gazetteer.Normalize(string.Concat(m.CleanText.Where(char.IsLetterOrDigit))) + "|" + m.Raw.Id,
                Time = m.Raw.Time,
                Text = m.DisplayText + "\n(отбой по последнему объявлению канала)",
                Points = [],
                ExpiresAt = m.Raw.Time + TimeSpan.FromMinutes(_settings.AllClearLifetimeMinutes),
            };
            ev.Sources.Add(m.Raw);
            _events.Add(ev);
            return true;
        }
    }

    /// <summary>
    /// Тревога подтверждена заметно позже отбоя: отбой запоздал (история медленного канала) и её не снимает.
    /// Небольшой допуск — репосты одной тревоги разными каналами расходятся на минуты.
    /// </summary>
    private static bool NewerThanClear(AlertEvent e, ParsedMessage m) => e.Time - m.Raw.Time > LateClearSlack;
    private static readonly TimeSpan LateClearSlack = TimeSpan.FromMinutes(5);

    private bool ClearPrevious(ParsedMessage m)
    {
        var now = Now;
        bool anyThreat = m.Threat is ThreatType.None or ThreatType.General;
        bool changed = false;
        lock (_lock)
        {
            if (!MarkSeen(m)) return false;
            var cleared = AllClearAudit != null ? new List<AlertEvent>() : null;
            var narrowed = new List<AlertEvent>();
            foreach (var e in _events.Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > now && !NewerThanClear(e, m)).ToList())
            {
                if (!anyThreat && !ThreatClearedBy(e.Threat, m.Threat)) continue;
                bool inScope = m.Regions.Count > 0 ? e.RegionIds.Intersect(m.Regions).Any() : e.Sources.Any(s => s.Channel == m.Raw.Channel);
                if (!inScope) continue;
                // «Отбой … по ранее объявленным» в Краснодарском крае не снимает ту же тревогу в Ростовской области
                if (m.Regions.Count > 0 && !e.RegionIds.Where(r => !Gazetteer.IsForeign(r)).All(m.Regions.Contains))
                {
                    if (NarrowEvent(e, m.Regions, m.Raw)) { cleared?.Add(e); narrowed.Add(e); changed = true; }
                    continue;
                }
                cleared?.Add(e);
                e.ExpiresAt = now;
                changed = true;
            }
            foreach (var e in narrowed.Where(e => e.ExpiresAt > now)) MergeSameArea(e, now);
            if (cleared != null)
                AllClearAudit!(m, cleared, [.. _events.Where(e => e.Status != AlertStatus.AllClear && e.ExpiresAt > now &&
                    (m.Regions.Count > 0 ? e.RegionIds.Intersect(m.Regions).Any() : e.Sources.Any(s => s.Channel == m.Raw.Channel)))]);
        }
        return changed;
    }

    private static MapPoint ToMapPoint(ParsedPlace p) =>
        new(p.Pos!.Value, p.Label, false, ZoneRadius(p)) { DistrictRegion = p.IsArea && p.ZoneRadiusKm == null ? p.RegionId : null, PartRegion = p.PartRegion, PartBearing = p.PartBearing, RegionId = p.RegionId,
            Corridor = p.BorderId is { } bid ? BorderCorridor(bid)
                : p.Label == CoastZones.AzovLabel && p.Pos is { } ap && CoastZones.AzovAt(ap) is { } seg ? seg.Line
                : IsSpit(p) ? CoastCorridor(p)
                : p.IsArea && p.ZoneRadiusKm is { } zr && zr >= 30 ? CorridorFor(p) : null,
            WaterArea = p.SeaId is { } sid && RouteNetwork.SeaArea(sid, p.Pos!.Value, ZoneRadius(p)) is { Count: >= 3 } wa ? wa : null,
            SeaId = p.SeaId };

    /// <summary>Коса или стрелка (именованная зона): опасность вдоль узкой полосы суши, а не круг.</summary>
    private static bool IsSpit(ParsedPlace p) =>
        p.IsArea && p.ZoneRadiusKm != null && (p.Label.EndsWith(" коса", StringComparison.Ordinal) || p.Label.EndsWith(" стрелка", StringComparison.Ordinal));

    /// <summary>Береговая линия косы в радиусе зоны (обе стороны косы и её оконечность).</summary>
    internal static IReadOnlyList<GeoPoint>? CoastCorridor(ParsedPlace p)
    {
        if (p.Pos is not { } c) return null;
        double r = p.ZoneRadiusKm!.Value * 1.3;
        List<GeoPoint>? best = null;
        double bestLen = 0;
        foreach (var l in RouteNetwork.Find(new CorridorHint(CorridorKind.Coast, null, "")))
            foreach (var part in l.Parts)
            {
                var cur = new List<GeoPoint>();
                foreach (var q in part.Append(new GeoPoint(double.NaN, double.NaN)))
                {
                    if (!double.IsNaN(q.Lat) && Geo.DistanceKm(q, c) <= r) { cur.Add(q); continue; }
                    if (cur.Count > 1 && Geo.PathLength(cur) is var len && len > bestLen) { bestLen = len; best = cur; }
                    cur = [];
                }
            }
        return best;
    }

    private static bool BorderTouches(string? borderId, string region)
    {
        if (borderId is null || !borderId.StartsWith("border:", StringComparison.Ordinal)) return false;
        var pair = borderId["border:".Length..].Split('+');
        return pair.Contains(region, StringComparer.Ordinal);
    }

    /// <summary>Полоса вдоль общей границы регионов («по границе Курской и Брянской областей»): самая длинная часть линии.</summary>
    private static IReadOnlyList<GeoPoint>? BorderCorridor(string borderId)
    {
        var pair = borderId["border:".Length..].Split('+');
        return pair.Length == 2 && RouteNetwork.BorderBetween(pair[0], pair[1]) is { } l && l.Parts.MaxBy(x => x.Length) is { Length: >= 2 } part
            ? part : null;
    }

    /// <summary>
    /// Именованная зона-трасса («Трасса Р-280», «Трасса Таврида»): участок ориентира в радиусе зоны —
    /// опасность вдоль дороги, а не круг.
    /// </summary>
    private static IReadOnlyList<GeoPoint>? CorridorFor(ParsedPlace p)
    {
        string? name = p.Label.Contains("Р-280") ? "Р-280" : p.Label.Contains("Таврида") ? "таврида" : null;
        if (name == null || p.Pos is not { } c) return null;
        var hint = new CorridorHint(CorridorKind.Road, name, name);
        foreach (var l in RouteNetwork.Find(hint))
        {
            // самая длинная непрерывная часть внутри радиуса зоны
            List<GeoPoint>? best = null;
            foreach (var part in l.Parts)
            {
                var cur = new List<GeoPoint>();
                foreach (var q in part)
                {
                    if (Geo.DistanceKm(q, c) <= p.ZoneRadiusKm!.Value * 1.6) cur.Add(q);
                    else if (cur.Count > 0) { if (best == null || Geo.PathLength(cur) > Geo.PathLength(best)) best = cur; cur = []; }
                }
                if (cur.Count > 1 && (best == null || Geo.PathLength(cur) > Geo.PathLength(best))) best = cur;
            }
            if (best is { Count: >= 2 }) return best;
        }
        return null;
    }

    /// <summary>Радиус зоны: район/округ — шире, город — по размеру, село — небольшой.</summary>
    private static double ZoneRadius(ParsedPlace p)
    {
        if (p.ZoneRadiusKm is { } zr) return zr;
        if (p.IsArea) return 25;
        var l = Gazetteer.Normalize(p.Label);
        if (l.Contains("район") || l.Contains("округ") || l.EndsWith(" мо") || l.EndsWith(" го")) return 25;
        var known = Gazetteer.Places.FirstOrDefault(x => x.Name == p.Label);
        if (known != null) return known.Name is "Москва" or "Санкт-Петербург" ? 25 : 12;
        return 7;
    }

    /// <summary>
    /// Частичный отбой: убирает из тревоги регионы <paramref name="clearedRegions"/> (и их точки).
    /// Если не осталось ни одного региона РФ — тревога снимается целиком. true — тревога изменилась.
    /// </summary>
    private bool NarrowEvent(AlertEvent e, IReadOnlyCollection<string> clearedRegions, RawMessage by)
    {
        var keep = e.RegionIds.Where(r => !clearedRegions.Contains(r)).ToList();
        if (keep.Count == e.RegionIds.Count) return false;
        if (keep.All(Gazetteer.IsForeign))
        {
            e.ExpiresAt = Now;
            return true;
        }
        var pts = e.Points.Where(p => p.RegionId == null || keep.Contains(p.RegionId)).ToList();
        var whole = e.WholeRegionIds.Where(keep.Contains).ToList();
        if (pts.Count == 0)
            foreach (var r in keep.Where(r => !Gazetteer.IsForeign(r)))
                if (Gazetteer.Get(r) is { } reg) { pts.Add(new MapPoint(reg.Center, reg.Name, true) { RegionId = r }); if (!whole.Contains(r)) whole.Add(r); }
        var removed = e.RegionIds.Where(r => !keep.Contains(r) && !Gazetteer.IsForeign(r)).Select(r => Gazetteer.Get(r)?.Name ?? r).ToList();
        e.RegionIds = keep;
        e.WholeRegionIds = whole;
        e.Points = pts;
        e.RegionNames = string.Join(", ", keep.Select(r => Gazetteer.Get(r)?.Name ?? r));
        if (e.Origin is { } o && !pts.Any(p => p.Pos == o)) e.Origin = null;
        e.AddPartialClear(removed, by.Time, by.Channel);
        e.Touch();
        return true;
    }

    /// <summary>
    /// Частичный отбой внутри тревоги: убирает точки <paramref name="hit"/> (пункты, районы, часть области).
    /// Регион остаётся в тревоге, пока в нём есть активные точки или он объявлен целиком.
    /// </summary>
    private static bool NarrowPoints(AlertEvent e, IReadOnlyList<MapPoint> hit, RawMessage by)
    {
        if (hit.Count == 0) return false;
        var keep = e.Points.Except(hit).ToList();
        var regions = e.RegionIds.Where(r => Gazetteer.IsForeign(r) || e.WholeRegionIds.Contains(r) ||
                                             keep.Any(p => p.RegionId == null || p.RegionId == r)).ToList();
        if (regions.Count == 0) regions = [.. e.RegionIds];
        e.Points = keep;
        e.RegionIds = regions;
        e.RegionNames = string.Join(", ", regions.Select(r => Gazetteer.Get(r)?.Name ?? r));
        if (e.Origin is { } o && !keep.Any(p => p.Pos == o)) e.Origin = null;
        e.AddPartialClear(hit.Select(p => p.Label).Distinct(), by.Time, by.Channel);
        e.Touch();
        return true;
    }

    /// <summary>
    /// Снимает ли отбой этого типа тревогу: тот же тип, общая тревога, либо ракетная ↔ авиационная
    /// (каналы называют одну и ту же «авиационную ракетно-бомбовую опасность» то «ракетной», то «авиационной»).
    /// </summary>
    private static bool ThreatClearedBy(ThreatType alert, ThreatType clear) =>
        alert == clear || alert == ThreatType.General ||
        alert is ThreatType.Missile or ThreatType.Aviation && clear is ThreatType.Missile or ThreatType.Aviation;

    /// <summary>Одно и то же место в тревоге и отбое: «Чертковский» = «Чертковский район», либо совпадение координат.</summary>
    private static bool SamePlace(MapPoint a, MapPoint b)
    {
        if (a.Label == b.Label) return true;
        static string Key(string l)
        {
            var n = Gazetteer.Normalize(l);
            foreach (var suf in new[] { " район", " округ", " мо", " го", " р-н" })
                if (n.EndsWith(suf, StringComparison.Ordinal)) return n[..^suf.Length];
            return n;
        }
        if (Key(a.Label) == Key(b.Label)) return true;
        if (a.IsRegionLevel || b.IsRegionLevel) return false;
        if (Geo.DistanceKm(a.Pos, b.Pos) < 2) return true;
        // город и его район («Шебекино» — «Шебекинский округ», «Суджа» — «Суджанский район»): одна местность
        return CityOfDistrict(a, b) || CityOfDistrict(b, a);
    }

    private static bool CityOfDistrict(MapPoint district, MapPoint city)
    {
        if (district.DistrictRegion == null || city.DistrictRegion != null) return false;
        var d = Gazetteer.Normalize(district.Label);
        var c = Gazetteer.Normalize(city.Label);
        if (c.Length < 4 || !d.StartsWith(c[..Math.Max(3, c.Length - 2)], StringComparison.Ordinal)) return false;
        return Geo.DistanceKm(district.Pos, city.Pos) <= Math.Max(15, district.RadiusKm);
    }

    /// <summary>Регионы-цели сообщения (направление «в сторону … области»), не входящие в регионы тревоги.</summary>
    /// <summary>
    /// Отметка пуска при тревоге в РФ («Пуски УАБ от Изюма в направлении Белгородской области»): отдельное событие
    /// категории «Пуск» в месте пуска со стрелкой к цели. Вызывается под блокировкой.
    /// </summary>
    private void AddLaunchMarker(ParsedMessage m, GeoPoint? target, TimeSpan lifetime)
    {
        if (m.LaunchSite is not { Pos: { } site } s) return;
        // цель не названа пунктом — первая область РФ сообщения («в направлении ЛНР»), иначе курс
        target ??= m.Regions.Where(r => !Gazetteer.IsForeign(r)).Select(r => Gazetteer.Get(r)?.Center).FirstOrDefault(c => c != null)
                   ?? (m.Heading is { } hd ? Geo.Destination(site, hd, 120) : null);
        var now = Now;
        var key = $"launch|{m.Threat}|{m.Kind}|{s.Label}";
        var ex = _events.FirstOrDefault(e => e.Key == key && e.ExpiresAt > now);
        if (ex != null)
        {
            if (m.Raw.Time > ex.Time) ex.Time = m.Raw.Time;
            if (m.Raw.Time + lifetime > ex.ExpiresAt) ex.ExpiresAt = m.Raw.Time + lifetime;
            if (m.LaunchLevel > ex.Level) ex.Level = m.LaunchLevel;
            if (target != null) ex.Target = target;
            if (ex.Sources.All(x => x.Channel != m.Raw.Channel)) ex.Sources.Add(m.Raw);
            ex.Touch();
            return;
        }
        var region = s.RegionId ?? "";
        var ev = new AlertEvent
        {
            Key = key,
            Threat = m.Threat == ThreatType.None ? ThreatType.General : m.Threat,
            Kind = m.Kind,
            Kinds = m.Kinds,
            Level = m.LaunchLevel,
            Status = AlertStatus.Launch,
            RegionIds = region.Length > 0 ? [region] : [],
            RegionNames = Gazetteer.Get(region)?.Name ?? "",
            Signature = "launch|" + Gazetteer.Normalize(string.Concat(m.CleanText.Where(char.IsLetterOrDigit))),
            Time = m.Raw.Time,
            Text = m.DisplayText,
            Points = [new MapPoint(site, s.Label, false) { RegionId = s.RegionId }],
            Origin = site,
            Target = target,
            ExpiresAt = m.Raw.Time + lifetime,
        };
        ev.Sources.Add(m.Raw);
        _events.Add(ev);
    }

    private static List<string> ExpectedRegions(ParsedMessage m, IReadOnlyList<string> regionIds) =>
        [.. m.Places.Where(p => p.IsTarget && p.RegionId != null && !Gazetteer.IsForeign(p.RegionId) && !regionIds.Contains(p.RegionId)
                                && Gazetteer.Get(p.RegionId)?.Name == p.Label)
            .Select(p => p.RegionId!).Distinct()];

    /// <summary>Цель — регион, район, часть области или акватория, а не конкретный пункт.</summary>
    internal static bool IsAreaTarget(ParsedPlace p) =>
        p.SeaId != null || p.PartRegion != null || p.BorderId != null ||
        // небольшие именованные зоны (мост, бухта, аэродром) — по сути точка
        p.IsArea && (p.ZoneRadiusKm ?? 30) >= 15;

    private static bool IsSubsonicMissile(ThreatKind k) => k is ThreatKind.MissileCruise or ThreatKind.MissileAntiShip;

    private static bool IsGenericKind(ThreatKind k) =>
        k is ThreatKind.Unknown or ThreatKind.UavGeneric or ThreatKind.MissileGeneric or ThreatKind.AviationGeneric;

    /// <summary>
    /// Активная тревога по той же местности, которую сообщение уточняет, а не дублирует: разные каналы пишут об одном
    /// и том же по-разному («Миллерово» у одного и «Миллерово опасность по БПЛА» у другого). Совместимы общая
    /// «опасность» и любой тип, тип группы без уточнения и конкретный подтип той же группы.
    /// Разные конкретные типы (крылатая и баллистическая ракета) — разные угрозы.
    /// </summary>
    private AlertEvent? FindRefinable(ParsedMessage m, List<MapPoint> points, List<string> whole, DateTimeOffset now)
    {
        var threat = m.Threat == ThreatType.None ? ThreatType.General : m.Threat;
        var labels = points.Where(p => !p.IsRegionLevel).Select(p => p.Label).ToHashSet(StringComparer.Ordinal);
        var wholeSet = whole.ToHashSet(StringComparer.Ordinal);
        // та же местность: те же пункты, а регионы целиком совпадают или одно сообщение шире другого
        // («Севастополь» и «Крым, Севастополь»); при слиянии регионы объединяются — охват не теряется
        return _events.Where(e => e.ExpiresAt > now && e.Status is not (AlertStatus.AllClear or AlertStatus.Launch)
                                  && Compatible(e, threat, m.Kind)
                                  && e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label).ToHashSet(StringComparer.Ordinal).SetEquals(labels)
                                  && (labels.Count > 0 ? e.WholeRegionIds.All(wholeSet.Contains) || wholeSet.All(e.WholeRegionIds.Contains)
                                                       : e.WholeRegionIds.ToHashSet(StringComparer.Ordinal).SetEquals(wholeSet)))
            .OrderByDescending(e => e.Time).FirstOrDefault();

    }

    private static readonly TimeSpan RepostWindow = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Ключи репоста уже виденных текстов: FindRepost сравнивает сообщение со всеми источниками активных тревог,
    /// и без кеша ключ каждого источника пересчитывался на каждое сообщение (заметная доля времени разбора архива).
    /// Слабые ссылки на строки — ключ живёт, пока жив текст источника.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> RepostKeys = new();

    /// <summary>Текст сообщения без подписи канала, промо, регистра, «ё», пунктуации и эмодзи — для сравнения репостов.</summary>
    internal static string RepostKey(string text) => RepostKeys.GetValue(text, ComputeRepostKey);

    private static string ComputeRepostKey(string text)
    {
        var t = Gazetteer.Normalize(MessageParser.StripPromo(MessageParser.StripFooter(text)));
        var sb = new System.Text.StringBuilder(t.Length);
        foreach (var ch in t)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        // «серебрянные»/«серебряные» — удвоенные буквы в опечатках не различаем
        var s = sb.ToString().Trim();
        return DoubledLetters().Replace(s, "$1");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\p{L})\1+")]
    private static partial System.Text.RegularExpressions.Regex DoubledLetters();

    /// <summary>
    /// Репост: тот же текст из другого канала (каналы-агрегаторы перепечатывают друг друга дословно, с разными подписями
    /// и опечатками). Это всегда одна тревога, даже если разбор пунктов разошёлся, — иначе два «объекта» в одном месте
    /// дают ложный трек и скорость.
    /// </summary>
    private AlertEvent? FindRepost(ParsedMessage m, DateTimeOffset now)
    {
        var key = RepostKey(m.Raw.Text);
        if (key.Length < 12) return null;
        var threat = m.Threat == ThreatType.None ? ThreatType.General : m.Threat;
        return _events.Where(e => e.ExpiresAt > now && e.Status is not (AlertStatus.AllClear or AlertStatus.Launch) && !e.HasPartialClear
                                  && (Compatible(e, threat, m.Kind) || threat == e.Threat)
                                  && e.Sources.Any(s => s.Channel != m.Raw.Channel && (m.Raw.Time - s.Time).Duration() <= RepostWindow && RepostKey(s.Text) == key))
            .OrderByDescending(e => e.Time).FirstOrDefault();
    }

    /// <summary>Окно ракетной волны: сводки о той же местности в пределах этого времени — одна атака.</summary>
    private static readonly TimeSpan MissileWaveWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Ракетные сводки разных каналов об одной атаке: «Геническ, Джанкой, Керчь» у одного и «Каховка, Геническ,
    /// Мелитополь» у другого, «Брянская, Орловская области» и «Орёл». Сообщение присоединяется к активной ракетной
    /// тревоге того же (или неуточнённого) типа, если у них есть общий пункт либо одна объявлена по всему региону,
    /// в котором лежат пункты другой. Перечни объединяются: охват не теряется, частичный отбой работает по пунктам.
    /// </summary>
    private AlertEvent? FindMissileWave(ParsedMessage m, List<MapPoint> points, List<string> whole, DateTimeOffset now)
    {
        if (m.Threat != ThreatType.Missile || m.Status is AlertStatus.AllClear or AlertStatus.Launch) return null;
        var pts = points.Where(p => !p.IsRegionLevel).ToList();
        var wholeSet = whole.Where(r => !Gazetteer.IsForeign(r)).ToHashSet(StringComparer.Ordinal);
        if (pts.Count == 0 && wholeSet.Count == 0) return null;
        static bool In(MapPoint p, ICollection<string> regions) => p.RegionId != null && regions.Contains(p.RegionId);
        return _events.Where(e => e.ExpiresAt > now && e.Threat == ThreatType.Missile && !e.HasPartialClear
                                  && e.Status is not (AlertStatus.AllClear or AlertStatus.Launch)
                                  && m.Raw.Time - e.Time <= MissileWaveWindow && Compatible(e, ThreatType.Missile, m.Kind))
            .Where(e =>
            {
                var ePts = e.Points.Where(p => !p.IsRegionLevel).ToList();
                var eWhole = e.WholeRegionIds.Where(r => !Gazetteer.IsForeign(r)).ToList();
                // «регион целиком» засчитывается, только если тревога объявлена по региону без пунктов:
                // «Белгород! … потенциальная опасность для Курской области» не поглощает тревогу по Рыльскому району
                return pts.Any(p => ePts.Any(q => SamePlace(p, q)))
                       || ePts.Count == 0 && pts.Any(p => In(p, eWhole)) || pts.Count == 0 && ePts.Any(q => In(q, wholeSet))
                       || ePts.Count == 0 && pts.Count == 0 && eWhole.Any(wholeSet.Contains);
            })
            .OrderByDescending(e => e.Time).FirstOrDefault();
    }

    internal static bool Compatible(AlertEvent e, ThreatType t, ThreatKind k)
    {
        if (e.Threat == ThreatType.General || t == ThreatType.General) return true;
        if (e.Threat != t) return false;
        if (e.Kind == k || IsGenericKind(e.Kind) || IsGenericKind(k)) return true;
        // крылатые и противокорабельные («Шторм», «Нептун») — дозвуковые крылатые ракеты одной волны с близким временем подлёта
        if (IsSubsonicMissile(e.Kind) && IsSubsonicMissile(k)) return true;
        // разные подтипы БПЛА над одним пунктом (ФПВ, «Дартс», реактивные) — одна опасность, тип уточняется;
        // разные ракеты (крылатая и баллистическая) — разные угрозы с разным временем подлёта
        return t != ThreatType.Missile;
    }

    /// <summary>
    /// Каждое следующее сообщение о пункте уточняет предыдущие: пункты (и регионы целиком) свежей тревоги
    /// забираются из более ранних совместимых тревог — один пункт на карте отмечен одной тревогой.
    /// Ранняя тревога, у которой ничего не осталось, поглощается: её источники и уровень переходят в свежую.
    /// </summary>
    private void AbsorbOlder(AlertEvent fresh, DateTimeOffset now)
    {
        if (fresh.Status is AlertStatus.AllClear or AlertStatus.Launch) return;
        var freshPts = fresh.Points.Where(p => !p.IsRegionLevel).ToList();
        var freshWhole = fresh.WholeRegionIds.ToHashSet(StringComparer.Ordinal);
        if (freshPts.Count == 0 && freshWhole.Count == 0) return;
        foreach (var old in _events.ToList())
        {
            if (old == fresh || old.ExpiresAt <= now || old.Status is AlertStatus.AllClear or AlertStatus.Launch) continue;
            if (!Compatible(old, fresh.Threat, fresh.Kind)) continue;
            var taken = old.Points.Where(p => !p.IsRegionLevel && freshPts.Any(f => SamePlace(p, f))).ToList();
            var takenWhole = old.WholeRegionIds.Where(freshWhole.Contains).ToList();
            if (taken.Count == 0 && takenWhole.Count == 0) continue;

            var rest = old.Points.Where(p => !taken.Contains(p) && !(p.IsRegionLevel && takenWhole.Contains(p.RegionId ?? ""))).ToList();
            var restWhole = old.WholeRegionIds.Where(r => !takenWhole.Contains(r)).ToList();
            bool narrower = old.Points.Where(p => !p.IsRegionLevel).All(p => taken.Contains(p)) && restWhole.Count == 0;
            // уровень и статус не теряются: свежая формулировка «попроще» не понижает уже объявленное
            if (old.Level > fresh.Level) fresh.Level = old.Level;
            if (narrower && StatusRank(old.Status) > StatusRank(fresh.Status)) fresh.Status = old.Status;
            // источник — по одному сообщению на канал; все сообщения тревоги нужны геокодеру для уточнения
            foreach (var s in old.Sources)
                if (fresh.Sources.All(x => x.Channel != s.Channel)) fresh.Sources.Add(s);
            fresh.MessageKeys.UnionWith(old.MessageKeys);
            foreach (var r in old.ExpectedRegionIds)
                if (!fresh.ExpectedRegionIds.Contains(r) && !fresh.WholeRegionIds.Contains(r)) fresh.ExpectedRegionIds.Add(r);
            if (narrower || rest.Count == 0)
            {
                // поглощённая тревога: её история становится частью свежей
                if (old.History.Count > 0)
                {
                    var merged = old.History.Concat(fresh.History).DistinctBy(h => (h.Time, h.Channel, h.Text)).OrderBy(h => h.Time).ToList();
                    fresh.History.Clear();
                    foreach (var h in merged.Take(merged.Count - 1))
                        if (fresh.History.Count == 0 || fresh.History[^1].Channel != h.Channel || fresh.History[^1].Text != h.Text) fresh.History.Add(h);
                    var lastH = merged[^1];
                    fresh.AddHistory(lastH.Time, lastH.Channel, lastH.Text);
                    // AddHistory пропускает повтор последней записи — уведомление о пересборке отдельно
                    fresh.NotifyHistory();
                }
                // общая «опасность» поглотила конкретную тревогу — тип не теряется
                if (fresh.Threat == ThreatType.General && old.Threat != ThreatType.General ||
                    fresh.Threat == old.Threat && IsGenericKind(fresh.Kind) && !IsGenericKind(old.Kind))
                {
                    fresh.Threat = old.Threat;
                    fresh.Kind = old.Kind;
                    fresh.Kinds = old.Kinds;
                }
                old.ExpiresAt = now;
                old.Merged = true;
                // перенесённые источники видны в карточке сразу, а не по таймеру
                fresh.Touch();
                continue;
            }
            old.Points = rest;
            old.WholeRegionIds = restWhole;
            // регионы и подпись старой тревоги — по тому, что у неё осталось
            var restRegions = old.RegionIds.Where(r => Gazetteer.IsForeign(r) || restWhole.Contains(r) ||
                                                      rest.Any(p => p.RegionId == null || p.RegionId == r)).ToList();
            if (restRegions.Count > 0 && restRegions.Count < old.RegionIds.Count)
            {
                old.RegionIds = restRegions;
                old.RegionNames = string.Join(", ", restRegions.Select(r => Gazetteer.Get(r)?.Name ?? r));
            }
            if (old.Origin is { } oo && !rest.Any(p => p.Pos == oo)) old.Origin = null;
            fresh.Touch();
        }
    }

    /// <summary>
    /// Совместимая активная тревога по той же местности (те же пункты и регионы целиком) поглощается этой:
    /// источники, история, уровень и статус переходят, на карте остаётся одна тревога. После частичного отбоя
    /// тревога могла сузиться до части местности другой («Орловская область» внутри «Тульская, Орловская области») —
    /// тогда её поглощает более широкая.
    /// </summary>
    private void MergeSameArea(AlertEvent e, DateTimeOffset now)
    {
        if (e.Status is AlertStatus.AllClear or AlertStatus.Launch) return;
        static HashSet<string> Pts(AlertEvent x) => x.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label).ToHashSet(StringComparer.Ordinal);
        static bool Within(AlertEvent a, AlertEvent b) =>
            (Pts(a).Count > 0 || a.WholeRegionIds.Count > 0) && Pts(a).IsSubsetOf(Pts(b)) &&
            a.WholeRegionIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(b.WholeRegionIds) &&
            a.RegionIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(b.RegionIds);
        foreach (var o in _events.ToList())
        {
            if (o == e || o.ExpiresAt <= now || e.ExpiresAt <= now || o.Status is AlertStatus.AllClear or AlertStatus.Launch) continue;
            if (!Compatible(o, e.Threat, e.Kind) && !Compatible(e, o.Threat, o.Kind)) continue;
            bool eIn = Within(e, o), oIn = Within(o, e);
            if (!eIn && !oIn) continue;
            // одинаковая местность — остаётся свежая; иначе — более широкая
            var (keep, drop) = eIn && oIn ? (o.Time > e.Time ? (o, e) : (e, o)) : eIn ? (o, e) : (e, o);
            if (drop.Level > keep.Level) keep.Level = drop.Level;
            if (StatusRank(drop.Status) > StatusRank(keep.Status)) keep.Status = drop.Status;
            if (keep.Threat == ThreatType.General && drop.Threat != ThreatType.General ||
                keep.Threat == drop.Threat && IsGenericKind(keep.Kind) && !IsGenericKind(drop.Kind))
            {
                keep.Threat = drop.Threat;
                keep.Kind = drop.Kind;
                keep.Kinds = drop.Kinds;
            }
            foreach (var s in drop.Sources)
                if (keep.Sources.All(x => x.Channel != s.Channel)) keep.Sources.Add(s);
            keep.MessageKeys.UnionWith(drop.MessageKeys);
            drop.Merged = true;
            var hist = drop.History.Concat(keep.History).DistinctBy(h => (h.Time, h.Channel, h.Text)).OrderBy(h => h.Time).ToList();
            keep.History.Clear();
            foreach (var h in hist) keep.AddHistory(h.Time, h.Channel, h.Text);
            if (drop.ExpiresAt > keep.ExpiresAt) keep.ExpiresAt = drop.ExpiresAt;
            drop.ExpiresAt = now;
            keep.Touch();
            if (keep != e) return;
        }
    }

    /// <summary>Снимок тревоги для истории уточнений: что именно добавило очередное сообщение.</summary>
    private sealed record HistoryState(HashSet<string> Places, HashSet<string> Whole, string Kind, AlertStatus Status, AlertLevel Level, string StatusText)
    {
        public static HistoryState Of(AlertEvent e) => new(
            e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label).ToHashSet(StringComparer.Ordinal),
            e.WholeRegionIds.ToHashSet(StringComparer.Ordinal), e.Info.Name, e.Status, e.Level, e.StatusText);

        public static string Initial(AlertEvent e)
        {
            var areas = e.WholeRegionIds.Select(r => Gazetteer.Get(r)?.Name ?? r).Concat(e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label)).Distinct().ToList();
            return $"{e.Info.Name}, {e.StatusText.ToLowerInvariant()}" + (areas.Count > 0 ? ": " + List(areas) : "");
        }

        public static string Describe(HistoryState a, HistoryState b)
        {
            var parts = new List<string>();
            var places = b.Places.Where(p => !a.Places.Contains(p)).ToList();
            var whole = b.Whole.Where(r => !a.Whole.Contains(r)).Select(r => Gazetteer.Get(r)?.Name ?? r).ToList();
            if (whole.Count + places.Count > 0) parts.Add("+ " + List([.. whole, .. places]));
            if (a.Kind != b.Kind) parts.Add("тип: " + b.Kind);
            if (a.Status != b.Status || a.Level != b.Level) parts.Add(b.StatusText.ToLowerInvariant());
            return parts.Count > 0 ? string.Join(" · ", parts) : "подтверждение";
        }

        private static string List(List<string> l) => l.Count <= 5 ? string.Join(", ", l) : string.Join(", ", l.Take(5)) + $" и ещё {l.Count - 5}";
    }

    private static int StatusRank(AlertStatus s) => s switch { AlertStatus.Impact => 2, AlertStatus.AirDefense => 1, _ => 0 };

    /// <summary>
    /// Уточнение тревоги следующим сообщением о той же местности: тип угрозы только конкретизируется, статус
    /// только усиливается (тревога → работа ПВО → прилёт), регионы объединяются.
    /// </summary>
    private static void RefineThreat(AlertEvent e, ParsedMessage m, string key, List<string> regionIds, List<string> whole)
    {
        if (StatusRank(m.Status) > StatusRank(e.Status)) e.Status = m.Status;
        if (regionIds.Any(r => !e.RegionIds.Contains(r)))
        {
            e.RegionIds = [.. e.RegionIds.Union(regionIds)];
            e.RegionNames = string.Join(", ", e.RegionIds.Select(r => Gazetteer.Get(r)?.Name ?? r));
        }
        if (whole.Any(r => !e.WholeRegionIds.Contains(r))) e.WholeRegionIds = [.. e.WholeRegionIds.Union(whole)];
        var threat = m.Threat == ThreatType.None ? ThreatType.General : m.Threat;
        // волна крылатых ракет разных типов: основной тип остаётся, перечень типов пополняется («Шторм, Нептун»)
        if (e.Threat == ThreatType.Missile && threat == ThreatType.Missile && IsSubsonicMissile(e.Kind) && IsSubsonicMissile(m.Kind) && e.Kind != m.Kind)
        {
            var kinds = e.Kinds.Count > 0 ? e.Kinds : [e.Kind];
            if (m.Kinds.Concat([m.Kind]).Any(k => !kinds.Contains(k))) e.Kinds = [.. kinds.Union(m.Kinds.Concat([m.Kind]))];
            return;
        }
        bool more = e.Threat == ThreatType.General && threat != ThreatType.General
                    || e.Threat == threat && !IsGenericKind(m.Kind) && e.Kind != m.Kind;
        if (!more) return;
        e.Threat = threat;
        e.Kind = m.Kind;
        e.Kinds = m.Kinds;
        e.Key = key;
    }

    private void AddObservation(AlertEvent ev, IReadOnlyList<MapPoint> points, GeoPoint? origin, GeoPoint? target, string? targetLabel, ParsedMessage m)
    {
        // «От Харькова фиксация БПЛА в направлении Валуйки»: точка отсчёта на стороне противника — место пуска, а не положение
        // объекта; в трек она не ставится (иначе путь начинается в Харькове), подсказка пуска остаётся в m.Route
        var enemyFrom = m.Places.Where(p => p.IsOrigin && !p.IsTarget && p.Pos != null && Borderline.IsEnemySide(p.Pos.Value, p.RegionId))
            .Select(p => p.Label).ToHashSet();
        if (enemyFrom.Count > 0)
        {
            if (origin is { } og && m.Places.Any(p => enemyFrom.Contains(p.Label) && p.Pos == og)) origin = null;
            points = points.Where(p => !enemyFrom.Contains(p.Label)).ToList();
        }
        var own = points.Where(p => !IsViaPoint(m, p)).ToList();
        // сводка по разнесённым районам без цели и курса — несколько групп: каждый район продолжает свой трек, если он есть,
        // но новых треков не начинает (раньше такая сводка не давала отметок вовсе)
        if (origin == null && target == null && m.Heading == null && TrackBuilder.ScatteredGroups(own) is { } groups)
        {
            // в морской зоне (Крым, Кубань) объекты заходят с любой стороны и облетают полуостров: районы сводки
            // там не отличить от продолжения пути (Керчь → Крымск → Славянск) — отметок не даём, как и раньше
            foreach (var g in groups.Where(g => Borderline.ZoneOf(g.Pos) != Borderline.Zone.Sea))
                if (TrackBuilder.Observe(ev, ev.Threat, [g], null, null, null, null, m.Count, m.Raw.Time, m.Raw.Channel) is { } go)
                    _tracks.Add(go with { Route = m.Route, Kind = TrackBuilder.IsShortRange(m.Kind) ? m.Kind : go.Kind, ContinueOnly = true });
            return;
        }
        var o = TrackBuilder.Observe(ev, ev.Threat, own, origin, target, targetLabel, m.Heading, m.Count, m.Raw.Time, m.Raw.Channel);
        if (o == null) return;
        var via = m.Places.Where(p => p.IsVia && !p.IsTarget && p.Pos != null).Select(p => (p.Pos!.Value, p.Label)).ToList();
        var tp = o.Target is { } tg ? m.Places.FirstOrDefault(p => p.IsTarget && p.Pos == tg) ?? m.Places.FirstOrDefault(p => p.IsTarget && p.Label == targetLabel) : null;
        // «Енакиево … опасность сохраняется», «Горловка опасность сохраняется»: подтверждение действующей опасности по площади,
        // а не засечка — без направления новой точкой пути не становится
        // только на суше: в морской зоне (Крым) «Кача … сохраняется» — новая засечка облетающего полуостров объекта
        // «продолжаются фиксации», «сохраняется, ещё фиксации», «работа ПВО» — это засечки, а не подтверждение
        // «повторно» — новая тревога в новом месте (Почепский район, повторно → Брянск, повторно): засечка, не подтверждение
        var normText = Gazetteer.Normalize(m.Raw.Text);
        bool standing = StandingWords.IsMatch(normText) && !SightingWords.IsMatch(normText) &&
                        Borderline.ZoneOf(o.Anchor) != Borderline.Zone.Sea &&
                        !m.Places.Any(p => p.IsTarget || p.IsOrigin) && m.Heading == null &&
                        m.Route.IsEmpty && !m.Route.FromSea && !m.Route.ToRear;
        // тип — из самого сообщения, если оно уточняет его (ФПВ в событии «БПЛА»): иначе ФПВ-отметка тянет трек дальнего БПЛА
        var kind = TrackBuilder.IsShortRange(m.Kind) ? m.Kind : o.Kind;
        _tracks.Add(o with { Route = m.Route, Via = via, TargetIsArea = tp != null && IsAreaTarget(tp), Standing = standing, Kind = kind });
    }

    /// <summary>Действующая опасность подтверждается, а не объявляется заново.</summary>
    private static readonly System.Text.RegularExpressions.Regex StandingWords = new(
        @"сохраня|остается в действии|продолжает действовать",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Признаки новой засечки в сообщении-«продолжении»: фиксации, пролёты, сбития, работа ПВО, количество.</summary>
    private static readonly System.Text.RegularExpressions.Regex SightingWords = new(
        @"фиксац|пролет|пролёт|летит|летят|сбит|пво|взрыв|прилет|\bот \d|\d+ бпла|групп",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Место объекта в сообщении с целью: «Дебальцево, Алчевск … и далее на Луганск, Красный Луч, Новошахтинск, Шахты» —
    /// перечень после «далее» часто теряет роль цели. В зоне сухопутного захода объект там, где пункт ближе всего
    /// к линии соприкосновения (он летит оттуда вглубь); иначе — последний названный пункт.
    /// </summary>
    private static GeoPoint? TrackAnchor(List<ParsedPlace> nonTargets)
    {
        var located = nonTargets.Where(p => p.Pos != null).ToList();
        if (located.Count == 0) return null;
        if (located.Count >= 2 && located.All(p => Borderline.ZoneOf(p.Pos!.Value) == Borderline.Zone.Land))
            return located.MinBy(p => Borderline.DistanceKm(p.Pos!.Value))!.Pos;
        return located[^1].Pos;
    }

    /// <summary>Пункт «через …» — промежуточная точка маршрута, а не текущее положение объекта.</summary>
    private static bool IsViaPoint(ParsedMessage m, MapPoint p) =>
        m.Places.Any(x => x.IsVia && x.Label == p.Label) && m.Places.Any(x => !x.IsVia && !x.IsTarget && x.Pos != null);

    /// <summary>
    /// Смена времени жизни тревог на ходу: у живых событий срок сдвигается на разницу (срок = время последнего
    /// сообщения + время жизни), снятые и истёкшие не трогаются. Затем — обычное истечение.
    /// </summary>
    public void SetLifetimes(int alertMinutes, int allClearMinutes)
    {
        alertMinutes = Math.Clamp(alertMinutes, 5, 24 * 60);
        allClearMinutes = Math.Clamp(allClearMinutes, 1, 24 * 60);
        var dAlert = TimeSpan.FromMinutes(alertMinutes - _settings.AlertLifetimeMinutes);
        var dClear = TimeSpan.FromMinutes(allClearMinutes - _settings.AllClearLifetimeMinutes);
        if (dAlert == TimeSpan.Zero && dClear == TimeSpan.Zero) return;
        var now = Now;
        lock (_lock)
        {
            _settings.AlertLifetimeMinutes = alertMinutes;
            _settings.AllClearLifetimeMinutes = allClearMinutes;
            foreach (var e in _events)
                if (e.ExpiresAt > now) e.ExpiresAt += e.Status == AlertStatus.AllClear ? dClear : dAlert;
        }
        Expire();
        Changed?.Invoke(Snapshot());
        PublishStatus();
    }

    /// <summary>Событие снято (отбой или истечение) — для статистики архива. Вызывается под блокировкой движка.</summary>
    public Action<AlertEvent>? EventRetired { get; set; }
    /// <summary>Трек снят — для статистики направлений пусков. Вызывается под блокировкой движка.</summary>
    public Action<Track>? TrackRetired { get; set; }

    private bool Expire()
    {
        var now = Now;
        lock (_lock)
        {
            if (EventRetired is { } er)
                foreach (var e in _events)
                    if (e.ExpiresAt <= now && !e.Merged) er(e);
            var removed = _events.RemoveAll(e => e.ExpiresAt <= now);
            // трек живёт часами и копит события всех отметок: снятые из обстановки держать в нём незачем
            // (каждый снимок трека копирует этот набор)
            if (removed > 0)
                foreach (var t in _tracks.Tracks)
                    if (t.Events.Count > 16) t.Events.RemoveWhere(e => e.ExpiresAt <= now);
            int before = _tracks.Tracks.Count;
            _tracks.Expire(now, TimeSpan.FromMinutes(_settings.AlertLifetimeMinutes), TrackRetired);
            removed += before - _tracks.Tracks.Count;
            // ключи повторов держим дольше, чем живёт история (повторная доставка при догрузке пропусков и перемотке):
            // целиком не сбрасываем — иначе свежее сообщение, доставленное повторно, применилось бы второй раз
            if (_seen.Count > 20000)
            {
                var keep = TimeSpan.FromHours(Math.Max(_settings.HistoryHours, 1) + 2);
                foreach (var k in _seen.Where(x => now - x.Value > keep).Select(x => x.Key).ToList()) _seen.Remove(k);
            }
            return removed > 0;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var key in _channelLoops.Keys)
            if (_channelLoops.TryRemove(key, out var cts)) { cts.Cancel(); cts.Dispose(); }
        _cts.Dispose();
    }
}
