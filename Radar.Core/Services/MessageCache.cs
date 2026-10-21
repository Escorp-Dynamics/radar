using System.Globalization;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Локальный кеш сообщений каналов за последние часы (TSV, одна строка — одно сообщение, только дописывание).
/// При запуске история берётся отсюда мгновенно, из сети догружаются только новые сообщения.
/// </summary>
public sealed class MessageCache(string path)
{
    private readonly object _lock = new();
    private readonly HashSet<(string, long)> _known = [];
    private TimeSpan _window;
    private DateTimeOffset _compactedAt = DateTimeOffset.UtcNow;

    /// <summary>Читает кеш, отбрасывает сообщения старше <paramref name="since"/> и перезаписывает файл без них.</summary>
    public List<RawMessage> Load(DateTimeOffset since)
    {
        var res = new Dictionary<(string, long), RawMessage>();
        lock (_lock)
        {
            _window = DateTimeOffset.UtcNow - since;
            _compactedAt = DateTimeOffset.UtcNow;
            bool readOk = true;
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path))
                        if (Parse(line) is { } m && m.Time >= since) res[(m.Channel, m.Id)] = m;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { readOk = false; }
            var list = res.Values.OrderBy(m => m.Time).ThenBy(m => m.Id).ToList();
            _known.Clear();
            foreach (var m in list) _known.Add((m.Channel, m.Id));
            // чтение не удалось — файл не трогаем, иначе потеряем непрочитанное
            if (!readOk) return list;
            var tmp = path + ".tmp";
            try
            {
                using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                    foreach (var m in list) w.Write(Format(m) + "\n");
                File.Move(tmp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            }
            return list;
        }
    }

    /// <summary>Дописывает в кеш ещё не сохранённые сообщения.</summary>
    public void Append(IEnumerable<RawMessage> msgs)
    {
        lock (_lock)
        {
            // долгая работа: окно кеша сдвигается, старые сообщения уходят из файла и _known без перезапуска
            if (_window > TimeSpan.Zero && DateTimeOffset.UtcNow - _compactedAt > TimeSpan.FromHours(6)) Load(DateTimeOffset.UtcNow - _window);
            var seen = new HashSet<(string, long)>();
            var fresh = msgs.Where(m => !_known.Contains((m.Channel, m.Id)) && seen.Add((m.Channel, m.Id))).ToList();
            if (fresh.Count == 0) return;
            try
            {
                AppendLines(path, fresh.Select(Format));
                // известными считаем только успешно записанные
                foreach (var m in fresh) _known.Add((m.Channel, m.Id));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Дописывает строки; если файл оборван без перевода строки — сначала завершает последнюю строку.</summary>
    internal static void AppendLines(string file, IEnumerable<string> lines)
    {
        using var fs = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var sb = new StringBuilder();
        if (fs.Length > 0)
        {
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n') sb.Append('\n');
        }
        fs.Seek(0, SeekOrigin.End);
        foreach (var l in lines) sb.Append(l).Append('\n');
        var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        fs.Write(bytes);
    }

    /// <summary>Имя канала для TSV: табы и переводы строк недопустимы (ломают разбор).</summary>
    internal static string CleanChannel(string ch) =>
        ch.Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    internal static string Format(RawMessage m) =>
        string.Join('\t', CleanChannel(m.Channel), m.Id.ToString(CultureInfo.InvariantCulture),
            m.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), Escape(m.Text));

    internal static RawMessage? Parse(string line)
    {
        var f = line.Split('\t', 4);
        if (f.Length != 4 || f[0].Length == 0 ||
            !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
            !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return null;
        if (unix is < -62135596800 or > 253402300799) return null;
        return new RawMessage(f[0], id, DateTimeOffset.FromUnixTimeSeconds(unix), Unescape(f[3]));
    }

    // \r → «\r» обратимо; старые файлы (без \r) читаются так же: неизвестная пара «\x» по-прежнему даёт «x»
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
        }
        return sb.ToString();
    }
}
