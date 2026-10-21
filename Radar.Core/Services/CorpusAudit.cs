using System.Text;
using System.Text.Json;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Отладка: прогоняет архив сообщений каналов (JSON из tools/fetch_corpus.py) через парсер
/// и пишет TSV — канал, id, вердикт (junk/info/signal/skip), угроза, статус, уровень, регионы, пункты, текст.
/// </summary>
public static class CorpusAudit
{
    public static void Run(string corpusPath, string outPath)
    {
        FrontLineSource.LoadForAudit();
        var settings = new AppSettings();
        var regionsByChannel = settings.Channels.ToDictionary(c => c.Name, c => (IReadOnlyList<string>)c.Regions, StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(corpusPath));
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
        w.WriteLine("ch\tid\tverdict\treason\tthreat\tkind\tstatus\tlevel\tregions\tplaces\ttext");
        int n = 0;
        // RADAR_AUDIT_HOMONYMS=1: подозрительные одноимённые пункты — в <out>.homonyms.tsv
        bool geo = Environment.GetEnvironmentVariable("RADAR_AUDIT_HOMONYMS") == "1";
        // RADAR_AUDIT_PLACEREGION=1: регион каждого пункта в колонке places
        bool withRegion = Environment.GetEnvironmentVariable("RADAR_AUDIT_PLACEREGION") == "1";
        using var w2 = geo ? new StreamWriter(Path.ChangeExtension(outPath, ".homonyms.tsv"), false, new UTF8Encoding(false)) : null;
        w2?.WriteLine("kind\tch\tid\tlabel\tpicked\talternatives\tregions\tother_km\ttext");
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            // неполная запись корпуса (нет текста, канала, номера или времени) пропускается, а не обрывает прогон
            if (!m.TryGetProperty("text", out _) || !m.TryGetProperty("ch", out _) || !m.TryGetProperty("id", out var idEl) ||
                idEl.ValueKind != JsonValueKind.Number || !m.TryGetProperty("time", out _)) continue;
            var text = m.GetProperty("text").GetString() ?? "";
            if (text.Length == 0) continue;
            var ch = m.GetProperty("ch").GetString() ?? "";
            var id = m.GetProperty("id").GetInt64();
            if (!DateTimeOffset.TryParse(m.GetProperty("time").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) continue;
            var p = MessageParser.Parse(new RawMessage(ch, id, time, text), regionsByChannel.GetValueOrDefault(ch) ?? []);
            string verdict = p.IsJunk ? "junk" : p.IsInformational ? "info"
                : p.Threat == ThreatType.None && p.Status != AlertStatus.AllClear ? "skip"
                : p.Regions.Count == 0 && p.Places.Count == 0 ? "noloc" : "signal";
            string places = string.Join("; ", p.Places.Select(x => $"{x.Label}{(x.Pos == null ? "?" : "")}{(x.IsTarget ? "→" : "")}{(x.IsOrigin ? "←" : "")}{(withRegion ? $"[{x.RegionId}]" : "")}"));
            if (geo) foreach (var x in p.Places) Homonym(w2!, ch, id, p, x, regionsByChannel.GetValueOrDefault(ch) ?? [], text);
            w.WriteLine(string.Join('\t', ch, id, verdict, p.JunkReason ?? "", p.Threat, p.Kind, p.Status, p.Level,
                string.Join(",", p.Regions), places, text.Replace('\t', ' ').Replace("\n", " ⏎ ")));
            n++;
        }
        Console.WriteLine($"audited {n} messages → {outPath}");
    }

    /// <summary>
    /// Пункт выбран не там: в регионе, названном в сообщении (или в регионе канала), есть пункт с тем же именем,
    /// а выбран другой; или выбранный пункт далеко (> 150 км) от всех остальных пунктов сообщения.
    /// </summary>
    private static void Homonym(StreamWriter w, string ch, long id, ParsedMessage p, ParsedPlace x, IReadOnlyList<string> chRegions, string text)
    {
        if (x.Pos is not { } pos || x.RegionId == null || x.SeaId != null || x.BorderId != null) return;
        var name = x.Label.Replace(" район", "").Replace(" округ", "");
        var alts = PlaceIndex.ByPrefix(Gazetteer.Normalize(name)).Where(a => Gazetteer.Normalize(a.Name) == Gazetteer.Normalize(name) && a.RegionId != x.RegionId)
            .Select(a => a.RegionId).Distinct().ToList();
        alts.AddRange(Gazetteer.Places.Where(g => g.Name == x.Label && g.RegionId != x.RegionId).Select(g => g.RegionId).Where(r => !alts.Contains(r)));
        var others = p.Places.Where(o => o != x && o.Pos != null && o.SeaId == null).Select(o => Geo.DistanceKm(o.Pos!.Value, pos)).ToList();
        double minOther = others.Count > 0 ? others.Min() : -1;
        string flat = text.Replace('\t', ' ').Replace("\n", " ⏎ ");
        if (alts.Count == 0) return;
        var ctx = p.Regions.Concat(chRegions).ToList();
        // тёзка вплотную у остальных пунктов, а выбранный — заметно дальше («Глубокий» в ЛНР вместо ростовского у М4)
        if (minOther > 40)
        {
            var located = p.Places.Where(o => o != x && o.Pos != null && o.SeaId == null).ToList();
            var tight = PlaceIndex.ByPrefix(Gazetteer.Normalize(name))
                .Where(a => Gazetteer.Normalize(a.Name) == Gazetteer.Normalize(name) && a.RegionId != x.RegionId && !Gazetteer.IsForeign(a.RegionId))
                .Select(a => (a, d: located.Min(o => Geo.DistanceKm(o.Pos!.Value, a.Pos))))
                .Where(t => t.d < 25 && t.d * 3 < minOther).OrderBy(t => t.d).FirstOrDefault();
            if (tight.a != null)
                w.WriteLine(string.Join('\t', "tight-namesake", ch, id, x.Label, x.RegionId, $"{tight.a.RegionId} ({tight.d:F0} км)", string.Join(",", ctx), minOther.ToString("F0"), flat));
        }
        if (!ctx.Contains(x.RegionId) && alts.Any(ctx.Contains))
            w.WriteLine(string.Join('\t', "context-has-other", ch, id, x.Label, x.RegionId, string.Join(",", alts), string.Join(",", ctx), minOther.ToString("F0"), flat));
        else if (minOther > 150)
        {
            // одноимённый пункт рядом с остальными пунктами сообщения — вероятно, выбран не тот
            var near = PlaceIndex.ByPrefix(Gazetteer.Normalize(name))
                .Where(a => Gazetteer.Normalize(a.Name) == Gazetteer.Normalize(name) && a.RegionId != x.RegionId)
                .Select(a => (a, d: p.Places.Where(o => o != x && o.Pos != null && o.SeaId == null).Min(o => Geo.DistanceKm(o.Pos!.Value, a.Pos))))
                .Where(t => t.d < 60).OrderBy(t => t.d).FirstOrDefault();
            if (near.a != null)
                w.WriteLine(string.Join('\t', "closer-namesake", ch, id, x.Label, x.RegionId, $"{near.a.RegionId} ({near.d:F0} км)", string.Join(",", ctx), minOther.ToString("F0"), flat));
        }
        else if (!ctx.Contains(x.RegionId))
            w.WriteLine(string.Join('\t', "outside-context", ch, id, x.Label, x.RegionId, string.Join(",", alts), string.Join(",", ctx), minOther.ToString("F0"), flat));
    }
}
