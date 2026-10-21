using System.Diagnostics;

namespace Radar.Services;

/// <summary>
/// Время предметной области (возраст тревог, прогноз положения, ETA). В обычном режиме — реальное UTC,
/// в режиме воспроизведения — ускоренное модельное. Анимации интерфейса идут по реальному времени.
/// </summary>
public static class Clock
{
    private static readonly Lock Sync = new();
    private static DateTimeOffset _base;
    private static long _wallBase;
    private static double _speed = 1;
    private static bool _paused;
    // кто запустил модельное время (движок воспроизведения); после остановки его прыжки по времени игнорируются
    private static object? _owner;

    public static bool IsSimulated { get; private set; }
    public static double Speed { get { lock (Sync) return _speed; } }
    public static bool IsPaused { get { lock (Sync) return _paused; } }

    public static DateTimeOffset Now
    {
        get
        {
            if (!IsSimulated) return DateTimeOffset.UtcNow;
            lock (Sync) return Current();
        }
    }

    private static DateTimeOffset Current() =>
        _paused ? _base : _base + Stopwatch.GetElapsedTime(_wallBase) * _speed;

    public static void StartSimulation(DateTimeOffset start, double speed, object? owner = null)
    {
        lock (Sync)
        {
            _owner = owner;
            _base = start;
            _wallBase = Stopwatch.GetTimestamp();
            _speed = speed;
            _paused = false;
            IsSimulated = true;
        }
    }

    /// <summary>Перевод модельных часов на момент <paramref name="t"/> с сохранением скорости и паузы (одной операцией).</summary>
    public static void JumpTo(DateTimeOffset t, bool? pause = null, object? owner = null)
    {
        lock (Sync)
        {
            // остановленный (или сменённый) владелец часов не переводит их: проверка и перевод — под одной блокировкой
            if (owner != null && !ReferenceEquals(owner, _owner)) return;
            _base = t;
            _wallBase = Stopwatch.GetTimestamp();
            if (pause is { } p) _paused = p;
            IsSimulated = true;
        }
    }

    /// <summary>Возврат к реальному времени.</summary>
    public static void StopSimulation()
    {
        lock (Sync) { IsSimulated = false; _paused = false; _speed = 1; _owner = null; }
    }

    public static void SetSpeed(double speed)
    {
        lock (Sync)
        {
            _base = Current();
            _wallBase = Stopwatch.GetTimestamp();
            _speed = speed;
        }
    }

    public static void SetPaused(bool paused)
    {
        lock (Sync)
        {
            if (_paused == paused) return;
            _base = Current();
            _wallBase = Stopwatch.GetTimestamp();
            _paused = paused;
        }
    }
}
