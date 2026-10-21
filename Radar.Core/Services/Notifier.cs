using System.Diagnostics;
using Radar.Models;

namespace Radar.Services;

/// <summary>Вид звукового сигнала.</summary>
public enum Chime
{
    /// <summary>Новая тревога: два восходящих тона.</summary>
    Alert,
    /// <summary>Высокий уровень (тревога/удар): три коротких настойчивых тона.</summary>
    Urgent,
    /// <summary>Потеря связи: нисходящий тон.</summary>
    Lost,
    /// <summary>Связь восстановлена: короткий восходящий.</summary>
    Restored,
}

/// <summary>
/// Оповещения: системные уведомления ОС и звуковые сигналы. Звуки синтезируются в WAV при первом обращении
/// (без внешних файлов) и проигрываются системным плеером; уведомления — через notify-send (Linux),
/// osascript (macOS) или всплывающее окно PowerShell (Windows). Ошибки не мешают работе приложения.
/// </summary>
public static class Notifier
{
    private static readonly Dictionary<(Chime, int), string> _wavs = [];
    private static DateTimeOffset _lastSound, _lastNotify;
    private static Chime _lastChime;
    private static string _burstTitle = "", _burstBody = "";
    private static DateTimeOffset _lastConnSound;

    /// <summary>Без звука (демо-режим для снимков и замеров).</summary>
    public static bool Muted { get; set; }

    /// <summary>Мобильная платформа: уведомления и звук через её API (Android), а не через внешние программы.</summary>
    public static IPlatformServices? Platform { get; set; }
    private static int _burst;
    private static bool _burstUrgent;
    private static int _flushPending;
    private static readonly Lock NotifyLock = new();

    /// <summary>
    /// Показать системное уведомление (не чаще ~1 в 2 с). Пачка схлопывается: через 2 с после первого
    /// показывается сводка «и ещё N» — она не ждёт следующей, возможно далёкой по времени тревоги.
    /// </summary>
    public static void Notify(string title, string body, bool urgent = false)
    {
        if (Muted) return;
        lock (NotifyLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastNotify < TimeSpan.FromSeconds(2))
            {
                _burst++;
                // единственное схлопнутое оповещение показывается целиком, а не «Ещё одно оповещение»
                (_burstTitle, _burstBody) = (title, body);
                // сводка срочная, если срочным было хоть одно схлопнутое оповещение (а не только первое)
                _burstUrgent |= urgent;
                if (Interlocked.Exchange(ref _flushPending, 1) == 0)
                    _ = Task.Delay(TimeSpan.FromSeconds(2.1)).ContinueWith(_ => FlushBurst(), TaskScheduler.Default);
                return;
            }
            _lastNotify = now;
        }
        Show(title, body, urgent);
    }

    private static void FlushBurst()
    {
        int n;
        bool urgent;
        string title, body;
        lock (NotifyLock)
        {
            Interlocked.Exchange(ref _flushPending, 0);
            n = _burst;
            _burst = 0;
            urgent = _burstUrgent;
            (title, body) = (_burstTitle, _burstBody);
            _burstUrgent = false;
            if (n == 0) return;
            _lastNotify = DateTimeOffset.UtcNow;
        }
        if (n == 1) Show(title, body, urgent);
        else Show("Радар", $"Ещё оповещений: {n}", urgent);
    }

    private static void Show(string title, string body, bool urgent)
    {
        if (Platform is { } pl) { try { pl.Notify(title, body, urgent); } catch (Exception ex) { Trace.WriteLine($"уведомление: {ex.Message}"); } return; }
        try
        {
            // текст приходит из Telegram: в командную строку скриптов он не подставляется — только отдельным
            // аргументом (notify-send, после «--», чтобы «- Белгород…» не читалось как ключ) или через переменные окружения
            if (OperatingSystem.IsLinux())
                Run("notify-send", ["-a", "Радар", "-u", urgent ? "critical" : "normal", "-i", "dialog-warning", "--", title, body]);
            else if (OperatingSystem.IsMacOS())
                Run("osascript", ["-e", "display notification (system attribute \"RADAR_BODY\") with title (system attribute \"RADAR_TITLE\")"],
                    new() { ["RADAR_TITLE"] = title, ["RADAR_BODY"] = body });
            else if (OperatingSystem.IsWindows())
                Run("powershell", ["-NoProfile", "-WindowStyle", "Hidden", "-Command",
                    "[reflection.assembly]::loadwithpartialname('System.Windows.Forms')|Out-Null;" +
                    "$n=New-Object System.Windows.Forms.NotifyIcon;$n.Icon=[System.Drawing.SystemIcons]::Warning;$n.Visible=$true;" +
                    "$n.ShowBalloonTip(8000,$env:RADAR_TITLE,$env:RADAR_BODY,[System.Windows.Forms.ToolTipIcon]::Warning);Start-Sleep 9;$n.Dispose()"],
                    new() { ["RADAR_TITLE"] = title, ["RADAR_BODY"] = body });
        }
        catch { }
    }

    /// <summary>
    /// Проиграть сигнал. Сигналы тревог — не чаще раза в 8 с (срочный — через 3 с после обычного); сигналы связи
    /// ограничиваются отдельно (короткий обрыв: «нет связи» и «восстановлена» оба слышны). <paramref name="preview"/> —
    /// проба громкости: звучит всегда и не сдвигает ограничение для настоящих тревог.
    /// </summary>
    public static void Play(Chime chime, int volume, bool preview = false)
    {
        if (Muted) return;
        var now = DateTimeOffset.UtcNow;
        if (!preview)
        {
            if (chime is Chime.Lost or Chime.Restored)
            {
                if (now - _lastConnSound < TimeSpan.FromSeconds(2)) return;
                _lastConnSound = now;
            }
            else
            {
                var gap = chime == Chime.Urgent && _lastChime != Chime.Urgent ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(8);
                if (now - _lastSound < gap) return;
                _lastSound = now;
                _lastChime = chime;
            }
        }
        try
        {
            var path = Wav(chime, Math.Clamp(volume, 0, 2));
            if (Platform is { } pl) pl.PlaySound(path);
            else if (OperatingSystem.IsLinux())
            {
                // плеер есть, но звук не идёт (PipeWire не запущен) — пробуем следующий по коду выхода
                _ = PlayLinuxAsync(path);
            }
            else if (OperatingSystem.IsMacOS()) Run("afplay", [path]);
            else if (OperatingSystem.IsWindows())
                Run("powershell", ["-NoProfile", "-WindowStyle", "Hidden", "-Command", "(New-Object Media.SoundPlayer $env:RADAR_WAV).PlaySync()"],
                    new() { ["RADAR_WAV"] = path });
        }
        catch { }
    }

    private static async Task PlayLinuxAsync(string path)
    {
        foreach (var player in new[] { "pw-play", "paplay", "aplay" })
        {
            try
            {
                var psi = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true };
                if (player == "aplay") psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add(path);
                using var p = Process.Start(psi);
                if (p == null) continue;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                // зависший плеер (звуковой сервер не отвечает) — завершаем, а не оставляем висеть
                catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } continue; }
                if (p.ExitCode == 0) return;
            }
            catch { }
        }
    }

    /// <summary>Заголовок и текст уведомления о тревоге.</summary>
    public static (string Title, string Body) Describe(AlertEvent e) =>
        ($"{e.StatusText}: {e.ThreatText}",
         e.RegionNames.Length == 0 ? e.Text : e.HasSummary ? e.RegionNames + "\n" + e.Summary : e.RegionNames);

    private static bool Run(string file, IEnumerable<string> args, Dictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
            var p = Process.Start(psi);
            if (p == null) return false;
            // процесс не ждём: звук/уведомление не блокируют интерфейс; завершённый освобождается
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => p.Dispose();
            return true;
        }
        catch { return false; }
    }


    // ---------------- синтез сигналов ----------------

    private static string Wav(Chime chime, int volume)
    {
        lock (_wavs)
        {
            if (_wavs.TryGetValue((chime, volume), out var p) && File.Exists(p)) return p;
            // в каталоге данных пользователя: общий /tmp с предсказуемыми именами мог подменить другой пользователь
            var dir = Path.Combine(AppPaths.Root, "sounds");
            Directory.CreateDirectory(dir);
            p = Path.Combine(dir, $"{chime}-{volume}.wav");
            File.WriteAllBytes(p, Synthesize(chime, volume));
            _wavs[(chime, volume)] = p;
            return p;
        }
    }

    /// <summary>Последовательность тонов (частота, длительность в мс) для каждого сигнала.</summary>
    /// <summary>Ноты сигнала: (частота, начало в мс). Средний регистр, без писка: высокие частоты режут слух.</summary>
    private static (double Hz, int At)[] Tones(Chime c) => c switch
    {
        // восходящая кварта E5 → A5
        Chime.Alert => [(659.3, 0), (880.0, 180)],
        // три ноты A4 → C#5 → E5: заметнее, но тот же мягкий тембр
        Chime.Urgent => [(440.0, 0), (554.4, 150), (659.3, 300)],
        // нисходящая G4 → D4
        Chime.Lost => [(392.0, 0), (293.7, 220)],
        _ => [(523.3, 0), (784.0, 140)],
    };

    internal static byte[] Synthesize(Chime chime, int volume)
    {
        const int rate = 22050;
        // громкость ниже прежней: сигнал заметен, но не оглушает (0,6 полной шкалы было громче системных звуков)
        double amp = volume switch { 0 => 0.07, 1 => 0.16, _ => 0.3 };
        var tones = Tones(chime);
        const double ring = 0.7; // звучание ноты после удара, с
        int total = (int)(rate * (tones.Max(t => t.At) / 1000.0 + ring));
        var mix = new double[total];
        foreach (var (hz, at) in tones)
        {
            int start = rate * at / 1000;
            for (int i = 0; start + i < total; i++)
            {
                double t = (double)i / rate;
                // колокольчик: быстрая атака (без щелчка) и экспоненциальное затухание; слабый второй обертон
                // гаснет быстрее основного тона — звук мягкий, без «пищащего» хвоста
                double env = Math.Min(1, t / 0.006) * Math.Exp(-t * 5.5);
                double v = Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Exp(-t * 9) * Math.Sin(2 * Math.PI * 2 * hz * t);
                mix[start + i] += v * env / 1.18;
            }
        }
        var samples = new List<short>(total);
        for (int i = 0; i < total; i++)
        {
            double fade = Math.Min(1, (total - i) / (rate * 0.05));
            samples.Add((short)(Math.Clamp(mix[i] * 0.8, -1, 1) * fade * amp * short.MaxValue));
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int data = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
