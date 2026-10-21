using System.IO.Compression;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Недельный архив реальных сообщений каналов (Assets/replay.tsv.gz, собирается tools/build_replay.py)
/// для ускоренного демо-воспроизведения.
/// </summary>
public static class ReplaySource
{
    public static List<RawMessage> Load()
    {
        using var res = typeof(ReplaySource).Assembly.GetManifestResourceStream("Radar.replay.tsv.gz");
        if (res == null) return [];
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var list = new List<RawMessage>(9000);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t', 4);
            if (parts.Length < 4 || !long.TryParse(parts[1], out var id) || !long.TryParse(parts[2], out var unix)) continue;
            list.Add(new RawMessage(parts[0], id, DateTimeOffset.FromUnixTimeSeconds(unix), Unescape(parts[3])));
        }
        // стабильно: сообщения одной секунды — в порядке канала и номера, как и при чтении архива
        return [.. list.OrderBy(m => m.Time).ThenBy(m => m.Channel, StringComparer.Ordinal).ThenBy(m => m.Id)];
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                // то же экранирование, что у кеша сообщений (MessageCache)
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
