using Radar.Models;

namespace Radar.Map;

/// <summary>Состояние анимации одного трека на экране.</summary>
public sealed class TrackVisual
{
    public required Track Track;
    public DateTimeOffset Born;
    public DateTimeOffset? Dying;
    /// <summary>Отображаемая позиция головы (плавно догоняет реальную).</summary>
    public GeoPoint Head;
    /// <summary>Отображаемый курс в градусах.</summary>
    public double Bearing;
    /// <summary>Сколько точек пройденного пути уже «прорисовано» (дробное — для анимации).</summary>
    public double Reveal;
    public DateTimeOffset LastPulse;
    /// <summary>Максимальный уровень среди событий трека на прошлом снимке и момент его повышения.</summary>
    public int Rank;
    public DateTimeOffset LevelUpAt;
}

/// <summary>Состояние анимации тревоги (маркер или регион).</summary>
public sealed class EventVisual
{
    public required AlertEvent Event;
    public DateTimeOffset Born;
    public DateTimeOffset? Dying;
    public DateTimeOffset LastUpdate;
    /// <summary>Время события на прошлом снимке: AlertEvent изменяется на месте, сравнивать с ним самим бессмысленно.</summary>
    public DateTimeOffset LastTime;
    /// <summary>Уровень на прошлом снимке (AlertEvent изменяется на месте, поэтому храним копию) и момент повышения.</summary>
    public int Rank;
    public DateTimeOffset LevelUpAt;
}

/// <summary>
/// Сопоставляет новые снимки данных с текущей картинкой и ведёт анимации появления, обновления и исчезновения.
/// Вся логика — в UI-потоке, вызывается на каждом кадре.
/// </summary>
public sealed class MapAnimator
{
    public static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan Flash = TimeSpan.FromMilliseconds(1600);
    /// <summary>Длительность «ударной волны» при повышении уровня (две волны).</summary>
    public static readonly TimeSpan LevelUp = TimeSpan.FromMilliseconds(2400);

    private readonly Dictionary<int, TrackVisual> _tracks = [];
    private readonly Dictionary<AlertEvent, EventVisual> _events = [];
    private readonly Dictionary<string, (double Value, double Target)> _regions = new(StringComparer.Ordinal);
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;

    public IEnumerable<TrackVisual> Tracks => _tracks.Values;
    public IEnumerable<EventVisual> Events => _events.Values;
    public EventVisual? Get(AlertEvent e) => _events.GetValueOrDefault(e);

    public bool IsAnimating { get; private set; }
    /// <summary>Идут анимации, меняющие заливки областей (проявление/угасание) — кэш слоя зон нельзя использовать.</summary>
    public bool AreasAnimating { get; private set; }

    public void SetData(IReadOnlyList<AlertEvent> events, IReadOnlyList<Track> tracks, DateTimeOffset now)
    {
        // трек без точек нарисовать не из чего (голова, путь берутся от точек)
        tracks = [.. tracks.Where(t => t.Points.Count > 0)];
        // два трека с одним Id (повторная публикация снимка) не должны ронять обновление всей карты
        var liveTracks = new Dictionary<int, Track>();
        foreach (var t in tracks) liveTracks[t.Id] = t;
        foreach (var (id, v) in _tracks)
            if (!liveTracks.ContainsKey(id) && v.Dying == null) v.Dying = now;

        foreach (var t in tracks)
        {
            if (_tracks.TryGetValue(t.Id, out var v))
            {
                if (t.Updated > v.Track.Updated || t.Points.Count != v.Track.Points.Count) v.LastPulse = now;
                int rank = TrackRank(t);
                if (rank > v.Rank) v.LevelUpAt = now;
                v.Rank = rank;
                v.Track = t;
                v.Dying = null;
            }
            else
            {
                var pos = t.Points[0].Pos;
                _tracks[t.Id] = new TrackVisual
                {
                    Track = t, Born = now, Head = pos, Bearing = t.Bearing ?? 0, Reveal = 0, LastPulse = now, Rank = TrackRank(t),
                };
            }
        }

        var live = events.ToHashSet();
        foreach (var (e, v) in _events)
            if (!live.Contains(e) && v.Dying == null) v.Dying = now;
        foreach (var e in events)
        {
            if (_events.TryGetValue(e, out var v))
            {
                if (e.Time > v.LastTime || v.LastUpdate == default) v.LastUpdate = now;
                v.LastTime = e.Time;
                if (e.LevelRank > v.Rank && v.Dying == null) v.LevelUpAt = now;
                v.Rank = e.LevelRank;
                v.Dying = null;
            }
            else _events[e] = new EventVisual { Event = e, Born = now, LastUpdate = now, LastTime = e.Time, Rank = e.LevelRank };
        }

        var activeRegions = new HashSet<string>(events.Where(e => e.Status != AlertStatus.AllClear).SelectMany(e => e.WholeRegionIds), StringComparer.Ordinal);
        foreach (var key in _regions.Keys.ToList())
            _regions[key] = (_regions[key].Value, activeRegions.Contains(key) ? 1 : 0);
        foreach (var r in activeRegions)
            if (!_regions.ContainsKey(r)) _regions[r] = (0, 1);
    }

    /// <summary>Сдвигает моменты появления так, будто анимации уже завершились к <paramref name="now"/>.</summary>
    public void Rebase(DateTimeOffset now)
    {
        foreach (var v in _tracks.Values) { v.Born = now - TimeSpan.FromSeconds(10); v.LastPulse = now - TimeSpan.FromSeconds(10); v.LevelUpAt = default; }
        foreach (var v in _events.Values) { v.Born = now - TimeSpan.FromSeconds(10); v.LastUpdate = now - TimeSpan.FromSeconds(10); v.LevelUpAt = default; }
        _lastTick = DateTimeOffset.UtcNow;
    }

    /// <summary>Продвигает анимации; итог — в <see cref="IsAnimating"/> и <see cref="AreasAnimating"/>.</summary>
    public void Tick(DateTimeOffset now, bool simulated = false)
    {
        if (simulated && _lastTick > now) _lastTick = now - TimeSpan.FromMilliseconds(50);
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.25);
        _lastTick = now;
        bool animating = false;

        bool areas = false;
        foreach (var key in _regions.Keys.ToList())
        {
            var (v, target) = _regions[key];
            double nv = v + (target - v) * (1 - Math.Exp(-dt * 4));
            if (Math.Abs(nv - target) < 0.005) nv = target;
            if (nv != target) { animating = true; areas = true; }
            if (nv == 0 && target == 0) _regions.Remove(key);
            else _regions[key] = (nv, target);
        }

        foreach (var id in _tracks.Keys.ToList())
        {
            var v = _tracks[id];
            if (v.Dying is { } d && now - d > FadeOut) { _tracks.Remove(id); continue; }

            var t = v.Track;
            // прорисовка пути: ~1.5 точки в секунду
            double targetReveal = t.Points.Count - 1;
            if (v.Reveal < targetReveal) { v.Reveal = Math.Min(targetReveal, v.Reveal + dt * 1.6); animating = true; }
            else v.Reveal = targetReveal;

            // голова: пока путь прорисовывается — едет по нему; затем — счисление пути
            GeoPoint goal;
            if (v.Reveal < targetReveal - 1e-6)
            {
                int i = (int)Math.Floor(v.Reveal);
                goal = Geo.Lerp(t.Points[i].Pos, t.Points[Math.Min(i + 1, t.Points.Count - 1)].Pos, v.Reveal - i);
                v.Head = goal;
            }
            else
            {
                goal = t.EstimatedPosition(Radar.Services.Clock.Now);
                // экспоненциальное сглаживание — плавный «доезд» к новой позиции
                double k = 1 - Math.Exp(-dt * 3);
                var nh = Geo.Lerp(v.Head, goal, k);
                if (Geo.DistanceKm(nh, goal) > 0.2) animating = true;
                v.Head = nh;
            }

            // курс иконки — по маршруту в расчётной позиции (повороты вдоль трассы/реки)
            double desired = t.RouteBearingAt(Radar.Services.Clock.Now) ?? t.Bearing ?? v.Bearing;
            double diff = ((desired - v.Bearing + 540) % 360) - 180;
            if (Math.Abs(diff) > 0.5) { v.Bearing = (v.Bearing + diff * (1 - Math.Exp(-dt * 5)) + 360) % 360; animating = true; }
        }

        foreach (var e in _events.Keys.ToList())
        {
            var v = _events[e];
            if (v.Dying is { } d && now - d > FadeOut) _events.Remove(e);
            else
            {
                if (now - v.Born < FadeIn || v.Dying != null) { animating = true; areas = true; }
                else if (now - v.LastUpdate < Flash || now - v.LevelUpAt < LevelUp) animating = true;
            }
        }
        AreasAnimating = areas;

        if (_tracks.Values.Any(v => now - v.LevelUpAt < LevelUp)) animating = true;
        IsAnimating = animating;
    }

    public double RegionLevel(string id) => _regions.TryGetValue(id, out var r) ? r.Value : 0;

    /// <summary>Заливка региона не анимируется (проявление/угасание завершено).</summary>
    public bool RegionSettled(string id) => !_regions.TryGetValue(id, out var r) || r.Value == r.Target;

    /// <summary>Событие полностью проявлено и не исчезает — его область можно брать из кэша.</summary>
    public static bool Settled(EventVisual? v, DateTimeOffset now) => v != null && v.Dying == null && now - v.Born >= FadeIn;

    public IEnumerable<string> Regions => _regions.Keys;

    public static double Presence(DateTimeOffset born, DateTimeOffset? dying, DateTimeOffset now)
    {
        double a = Math.Clamp((now - born) / FadeIn, 0, 1);
        a = 1 - Math.Pow(1 - a, 3);
        if (dying is { } d) a *= 1 - Math.Clamp((now - d) / FadeOut, 0, 1);
        return a;
    }

    private static int TrackRank(Track t) => t.Events.Count == 0 ? -1 : t.Events.Max(e => e.LevelRank);

    /// <summary>
    /// Фазы «ударной волны» после повышения уровня: две волны со сдвигом 0.35 от общей длительности.
    /// Возвращает фазу 0..1 каждой волны или -1, если волна не активна.
    /// </summary>
    public static (double A, double B) LevelUpWaves(DateTimeOffset at, DateTimeOffset now)
    {
        if (at == default) return (-1, -1);
        double x = (now - at) / LevelUp;
        if (x is < 0 or >= 1) return (-1, -1);
        const double len = 0.65;
        double a = x < len ? x / len : -1;
        double b = x >= 0.35 ? (x - 0.35) / len : -1;
        return (a, b is >= 1 ? -1 : b);
    }

    /// <summary>Интенсивность вспышки после обновления: 1 → 0.</summary>
    public static double FlashLevel(DateTimeOffset at, DateTimeOffset now)
    {
        double x = (now - at) / Flash;
        return x is < 0 or > 1 ? 0 : 1 - x;
    }
}
