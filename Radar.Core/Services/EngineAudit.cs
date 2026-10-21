using System.Text;
using System.Text.Json;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Отладка (--engine-audit): прогон архива каналов через весь движок в хронологическом порядке.
/// Ищет:
///   «холостые» отбои — отбой с регионом, который ничего не снял (нечего было снимать или не нашлось совпадения);
///   отброшенные отбои — без региона/пунктов (не применились вовсе);
///   «зависшие» тревоги — после отбоя по их региону от того же канала остались активными;
///   «широкие» отбои — сняли тревоги других регионов или конкретных пунктов при отбое по другому пункту.
/// </summary>
public static class EngineAudit
{
    /// <summary>
    /// Отладка (--history-check): загрузка истории за HistoryHours из всех каналов так же, как при запуске приложения,
    /// применение одной пачкой (как при старте) и сравнение с последовательным прогоном по времени.
    /// </summary>
    public static async Task HistoryCheck(string? savePath)
    {
        FrontLineSource.LoadForAudit();
        var settings = new AppSettings { OnlineGeocoding = false };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var tg = new TelegramSource(http);
        var since = DateTimeOffset.UtcNow.AddHours(-settings.HistoryHours);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(4);
        var channels = settings.Channels.Where(c => c.Enabled).ToList();
        var results = await Task.WhenAll(channels.Select(async ch =>
        {
            await gate.WaitAsync();
            try
            {
                var t0 = sw.Elapsed;
                var msgs = await tg.FetchHistoryAsync(ch.Name, since, 60, CancellationToken.None);
                return (ch.Name, Msgs: msgs, Took: sw.Elapsed - t0, Error: (string?)null);
            }
            catch (Exception ex) { return (ch.Name, Msgs: new List<RawMessage>(), Took: TimeSpan.Zero, Error: ex.Message); }
            finally { gate.Release(); }
        }));
        foreach (var r in results)
        {
            var oldest = r.Msgs.Count > 0 ? r.Msgs.Min(m => m.Time) : (DateTimeOffset?)null;
            Console.WriteLine($"{r.Name,-24} {r.Msgs.Count,5} msgs  oldest {oldest?.ToLocalTime():dd.MM HH:mm}  {(oldest <= since ? "full" : "PARTIAL")}  {r.Took.TotalSeconds,5:F1}s {r.Error}");
        }
        Console.WriteLine($"fetched in {sw.Elapsed.TotalSeconds:F1}s");
        var all = results.SelectMany(r => r.Msgs).Where(m => m.Time >= since).OrderBy(m => m.Time).ToList();
        // загруженное сохраняется (--engine-audit) — для повторной проверки на тех же данных
        if (savePath != null)
        {
            using var fs = File.Create(savePath);
            using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            jw.WriteStartArray();
            foreach (var m in all)
            {
                jw.WriteStartObject();
                jw.WriteString("ch", m.Channel);
                jw.WriteNumber("id", m.Id);
                jw.WriteString("time", m.Time.ToString("o"));
                jw.WriteString("text", m.Text);
                jw.WriteEndObject();
            }
            jw.WriteEndArray();
        }

        // как при запуске: одна пачка
        sw.Restart();
        var batch = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        var realNow = DateTimeOffset.UtcNow;
        Clock.StartSimulation(realNow, 0);
        batch.ApplyBatch(all);
        var bt = sw.Elapsed;
        // эталон: по одному сообщению в своё время
        var seq = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in seq.ReplaySync(all)) { }
        Clock.StartSimulation(realNow, 0);
        seq.ExpireNow();
        static string Sig(IEnumerable<AlertEvent> ev) => string.Join("\n", ev.Where(e => e.Status != AlertStatus.AllClear)
            .Select(e => $"{e.Threat} {string.Join(",", e.RegionIds)}: {string.Join(", ", e.Points.Select(p => p.Label))}").Order());
        static string TSig(IEnumerable<Track> ts) => string.Join("\n", ts.Where(t => t.IsMoving)
            .Select(t => string.Join(" → ", t.Points.Select(p => p.Label))).Order());
        var (be, se) = (Sig(batch.Snapshot()), Sig(seq.Snapshot()));
        var (btk, stk) = (TSig(batch.TrackSnapshot()), TSig(seq.TrackSnapshot()));
        Console.WriteLine($"messages {all.Count}; batch applied in {bt.TotalMilliseconds:F0} ms");
        Console.WriteLine($"active events: batch {batch.Snapshot().Count(e => e.Status != AlertStatus.AllClear)}, sequential {seq.Snapshot().Count(e => e.Status != AlertStatus.AllClear)}, same: {be == se}");
        Console.WriteLine($"moving tracks: batch {batch.TrackSnapshot().Count(t => t.IsMoving)}, sequential {seq.TrackSnapshot().Count(t => t.IsMoving)}, same: {btk == stk}");
        foreach (var t in batch.TrackSnapshot().Where(t => t.IsMoving).OrderByDescending(t => t.Points.Count).Take(12))
            Console.WriteLine($"  {t.Threat} {string.Join(" → ", t.Points.Select(p => $"{p.Label}@{p.Time.ToLocalTime():HH:mm}"))}");
        if (be != se) Console.WriteLine($"--- batch\n{be}\n--- sequential\n{se}");
        if (btk != stk)
        {
            var bs = btk.Split('\n').ToHashSet();
            var ss = stk.Split('\n').ToHashSet();
            foreach (var x in bs.Except(ss)) Console.WriteLine($"  only batch: {x}");
            foreach (var x in ss.Except(bs)) Console.WriteLine($"  only sequential: {x}");
        }
    }

    public static void Run(string corpusPath, string outPath)
    {
        FrontLineSource.LoadForAudit();
        var settings = new AppSettings();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(corpusPath));
        var msgs = new List<RawMessage>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            // неполная запись корпуса (нет текста, канала, номера или времени) пропускается, а не обрывает прогон
            if (!m.TryGetProperty("text", out _) || !m.TryGetProperty("ch", out _) || !m.TryGetProperty("id", out var idEl) ||
                idEl.ValueKind != JsonValueKind.Number || !m.TryGetProperty("time", out _)) continue;
            var text = m.GetProperty("text").GetString() ?? "";
            if (text.Length == 0) continue;
            // время без пояса — UTC; без времени запись пропускается (иначе встала бы в «сейчас»)
            if (!DateTimeOffset.TryParse(m.GetProperty("time").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) continue;
            msgs.Add(new RawMessage(m.GetProperty("ch").GetString() ?? "", m.GetProperty("id").GetInt64(), time, text));
        }
        // стабильно: сообщения одной секунды — по каналу и номеру (результат аудита не плавает от запуска к запуску)
        msgs = [.. msgs.OrderBy(m => m.Time).ThenBy(m => m.Channel, StringComparer.Ordinal).ThenBy(m => m.Id)];

        using var http = new HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
        w.WriteLine("kind\tch\tid\ttime\tregions\tplaces\tdetail\ttext");
        int idle = 0, dropped = 0, stuck = 0, wide = 0, clears = 0, partial = 0;
        static string Flat(string s) => s.Replace('\t', ' ').Replace("\n", " ⏎ ");
        static string Places(ParsedMessage p) => string.Join("; ", p.Places.Select(x => x.Label + (x.IsTarget ? "→" : "")));
        static string Ev(AlertEvent e) => $"[{e.Sources[0].Channel}/{e.Sources[0].Id} {e.Threat} {string.Join(",", e.RegionIds)}: {string.Join(", ", e.Points.Select(p => p.Label))}]";

        int droppedLive = 0;
        engine.AllClearDropped = p =>
        {
            dropped++;
            // остались ли активные тревоги этого канала того же типа за последние 3 часа — отбой мог их снять
            var live = engine.Snapshot().Where(e => e.Status is not (AlertStatus.AllClear or AlertStatus.Launch) && e.ExpiresAt > p.Raw.Time
                                                     && e.Sources.Any(s => s.Channel == p.Raw.Channel) && p.Raw.Time - e.Time < TimeSpan.FromHours(3)
                                                     && (p.Threat is ThreatType.None or ThreatType.General || e.Threat == p.Threat)).ToList();
            if (live.Count > 0) droppedLive++;
            w.WriteLine(string.Join('\t', "dropped", p.Raw.Channel, p.Raw.Id, p.Raw.Time.ToString("u"), "", Places(p),
                live.Count > 0 ? $"активных {live.Count}, последняя {(p.Raw.Time - live.Max(e => e.Time)).TotalMinutes:F0} мин назад: " + string.Join(" ", live.OrderByDescending(e => e.Time).Take(3).Select(Ev)) : "",
                Flat(p.CleanText)));
        };
        engine.AllClearAudit = (p, cleared, remaining) =>
        {
            clears++;
            partial += cleared.Count(e => e.HasPartialClear && e.ExpiresAt > p.Raw.Time);
            // город федерального значения в отбое — это отбой по региону («Севастополь» = весь Севастополь)
            static bool IsCity(ParsedPlace x) => x.RegionId != null && Gazetteer.Get(x.RegionId)?.Name == x.Label;
            // «Отбой … на Крупецкую зону Рыльского района» — место отбоя названо как цель
            var own = p.Places.Where(x => !x.IsTarget).ToList();
            if (own.Count == 0) own = p.Places;
            var labels = own.Where(x => !IsCity(x)).Select(x => x.Label).ToHashSet();
            var cities = own.Where(IsCity).Select(x => x.RegionId!).ToHashSet();
            // регион без названных пунктов («Отбой … Приазовье ⏎ Крым и Севастополь») — отбой по всему региону
            var withPlaces = own.Where(x => !IsCity(x)).Select(x => x.RegionId).ToHashSet();
            foreach (var r in p.Regions)
                if (!withPlaces.Contains(r)) cities.Add(r);
            static string K(string l) => System.Text.RegularExpressions.Regex.Replace(Gazetteer.Normalize(l), @" (?:район|округ|мо|го)$", "");
            var keys = labels.Select(K).ToHashSet();
            // «Трубчевск» = «Трубчевский район», «Чаплинский район» = «Чаплынка», «Сочи» ~ «Адлерский район»: та же местность
            var clearPos = own.Where(x => x.Pos != null).Select(x => x.Pos!.Value).ToList();
            bool Matches(AlertEvent e) =>
                e.RegionIds.Count > 0 && e.RegionIds.Where(r => !Gazetteer.IsForeign(r)).All(cities.Contains) ||
                // «Ейск» = «Ейский район»: общая основа названия (город и его район)
                e.Points.Any(pt => keys.Contains(K(pt.Label)) || keys.Any(k => k.Length >= 6 && K(pt.Label).Length >= 6 && k[..6] == K(pt.Label)[..6]) ||
                                   keys.Any(k => Math.Min(k.Length, K(pt.Label).Length) >= 4 && k[..^2].Length >= 2 &&
                                                 (K(pt.Label).StartsWith(k[..Math.Max(3, k.Length - 2)], StringComparison.Ordinal) ||
                                                  k.StartsWith(K(pt.Label)[..Math.Max(3, K(pt.Label).Length - 2)], StringComparison.Ordinal))) ||
                                   !pt.IsRegionLevel && clearPos.Any(c => Geo.DistanceKm(c, pt.Pos) < 20));
            if (cleared.Count == 0)
            {
                idle++;
                w.WriteLine(string.Join('\t', "idle", p.Raw.Channel, p.Raw.Id, p.Raw.Time.ToString("u"), string.Join(",", p.Regions), Places(p),
                    remaining.Count > 0 ? "осталось: " + string.Join(" ", remaining.Take(4).Select(Ev)) : "", Flat(p.CleanText)));
            }
            // отбой по всему региону (без пунктов) от канала, который сам объявлял тревогу, — его тревоги не должны остаться
            if (labels.Count == 0)
            {
                var left = remaining.Where(e => e.Sources.Any(s => s.Channel == p.Raw.Channel) && e.Time < p.Raw.Time
                                                && (p.Threat is ThreatType.None or ThreatType.General || e.Threat == p.Threat)).ToList();
                if (left.Count > 0)
                {
                    stuck++;
                    w.WriteLine(string.Join('\t', "stuck", p.Raw.Channel, p.Raw.Id, p.Raw.Time.ToString("u"), string.Join(",", p.Regions), Places(p),
                        string.Join(" ", left.Take(4).Select(Ev)), Flat(p.CleanText)));
                }
            }
            // отбой по конкретным пунктам снял тревогу, у которой нет ни одного из этих пунктов
            else
            {
                // суженные тревоги (частичный отбой) уже без снятых точек — их проверяем по отметке отбоя
                var foreign = cleared.Where(e => !(e.HasPartialClear && e.PartialClears[^1].Time == p.Raw.Time)
                                                 && !Matches(e) && !e.Points.Any(pt => pt.PartRegion != null)
                                                 && p.Places.All(x => x.PartRegion == null)).ToList();
                if (foreign.Count > 0)
                {
                    wide++;
                    w.WriteLine(string.Join('\t', "wide", p.Raw.Channel, p.Raw.Id, p.Raw.Time.ToString("u"), string.Join(",", p.Regions), Places(p),
                        string.Join(" ", foreign.Take(4).Select(Ev)), Flat(p.CleanText)));
                }
            }
        };
        int n = 0;
        int corridorTracks = 0, launchExplicit = 0, launchInferred = 0, viaTracks = 0, routeTracks = 0;
        var seenTracks = new HashSet<int>();
        // качество траекторий — по последнему состоянию каждого трека
        var lastState = new Dictionary<int, Track>();
        int dupSamples = 0, dupGroups = 0, pointDup = 0;
        var pointDupEx = new Dictionary<string, int>();
        var dupExamples = new Dictionary<string, int>();
        int missileOverlap = 0, repostDup = 0;
        var repostEx = new Dictionary<string, int>();
        var missileEx = new Dictionary<string, int>();
        // измерения скорости: разные каналы vs один канал, интервал, отклонение от типовой скорости
        var speeds = new List<(bool Cross, double Min, double Km, double Raw, bool Ok, ThreatKind Kind, string Desc)>();
        TrackBuilder.SpeedAudit = (t, head, o, d, h, raw, ok) =>
            speeds.Add((head.Channel != o.Channel, h * 60, d, raw, ok, t.Kind,
                $"#{t.Id} {t.Kind} {head.Label}@{head.Time:MM-dd HH:mm}/{head.Channel} → {o.Label}@{o.Time:HH:mm}/{o.Channel}: {d:F0} км за {h * 60:F0} мин = {raw:F0} км/ч{(ok ? "" : " (отброшено)")}"));
        foreach (var _ in engine.ReplaySync(msgs))
        {
            n++;
            // дубли: одновременно активные события одной группы угроз по той же местности — дорогая проверка, раз в 10 сообщений;
            // состояние треков, наоборот, снимается на каждом сообщении: иначе короткоживущие треки выпадают из метрик
            if (n % 10 == 0)
            {
                var at = msgs[Math.Min(n, msgs.Count) - 1].Time;
                var act = engine.Snapshot().Where(e => e.Status is not (AlertStatus.AllClear or AlertStatus.Launch) && e.ExpiresAt > at).ToList();
                static string Area(AlertEvent e) => e.WholeRegionIds.Count > 0 && e.Points.All(p => p.IsRegionLevel)
                    ? "R:" + string.Join(",", e.WholeRegionIds.Order())
                    : "P:" + string.Join(",", e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label).Order());
                dupSamples++;
                // один пункт сразу в нескольких тревогах совместимого типа
                foreach (var pg in act.SelectMany(e => e.Points.Where(p => !p.IsRegionLevel).Select(p => (p.Label, E: e))).GroupBy(x => x.Label))
                {
                    var es = pg.Select(x => x.E).Distinct().ToList();
                    // баллистическая и крылатая ракета над одним пунктом — разные угрозы, движок держит их раздельно намеренно
                    bool clash = es.Any(a => es.Any(b => a != b && (AlertEngine.Compatible(a, b.Threat, b.Kind) || AlertEngine.Compatible(b, a.Threat, a.Kind))));
                    if (!clash) continue;
                    pointDup++;
                    var pk = $"PT {pg.Key} :: " + string.Join(" | ", es.Select(e => $"{e.Threat}/{e.Kind}/{e.Status}{(e.HasPartialClear ? "/P" : "")}@{e.Time:HH:mm}[{string.Join(",", e.Points.Select(p => p.Label))}]@{e.Sources[0].Channel}"));
                    pointDupEx[pk] = pointDupEx.GetValueOrDefault(pk) + 1;
                }
                // репосты: один и тот же текст (разных каналов) в разных активных тревогах
                foreach (var rg in act.Select(e => (K: AlertEngine.RepostKey(e.Sources[0].Text), E: e, S: e.Sources[0])).Where(x => x.K.Length >= 12).GroupBy(x => x.K))
                {
                    var xs = rg.ToList();
                    // одно сообщение (в пределах 20 мин) в двух разных тревогах; разные события одного времени и текста
                    // части одного сообщения с разными уровнями по регионам («Белгородская — опасность ⏎ Воронежская — внимание»)
                    // — отдельные тревоги намеренно (MessageParser.SplitByLevel), не повтор
                    // (уровни частей потом могут сравняться от уточнений — местность всё равно разная)
                    bool clash = xs.Any(a => xs.Any(b => a.E != b.E && (a.S.Time - b.S.Time).Duration() <= TimeSpan.FromMinutes(20)
                                                        && !(a.S.Channel == b.S.Channel && a.S.Id == b.S.Id &&
                                                             (!a.E.RegionIds.Intersect(b.E.RegionIds).Any() ||
                                                              // блоки сводки (MessageParser.Blocks): у каждого свои пункты
                                                              !a.E.Points.Select(p => p.Label).Intersect(b.E.Points.Select(p => p.Label)).Any()))));
                    if (!clash) continue;
                    repostDup++;
                    var rk = rg.Key.Length > 90 ? rg.Key[..90] : rg.Key;
                    repostEx[rk] = repostEx.GetValueOrDefault(rk) + 1;
                }
                // ракетные сводки: несколько активных ракетных тревог с пересекающимися регионами
                var mis = act.Where(e => e.Threat == ThreatType.Missile).ToList();
                for (int i = 0; i < mis.Count; i++)
                    for (int j = i + 1; j < mis.Count; j++)
                        if (mis[i].RegionIds.Intersect(mis[j].RegionIds).Any())
                        {
                            missileOverlap++;
                            string D(AlertEvent e) => $"{e.Kind}/{e.Status}[{string.Join(",", e.RegionIds)}|{string.Join(",", e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Label).Take(6))}]@{e.Sources[0].Channel}";
                            var mk = string.Join(" ‖ ", new[] { D(mis[i]), D(mis[j]) }.Order());
                            missileEx[mk] = missileEx.GetValueOrDefault(mk) + 1;
                        }
                foreach (var g in act.GroupBy(e => (Group: e.Threat == ThreatType.General ? "any" : e.Threat.ToString(), Area: Area(e))))
                {
                    var l = g.ToList();
                    // «общая» тревога по той же местности тоже дубль любой конкретной
                    var gen = act.Where(e => e.Threat == ThreatType.General && Area(e) == g.Key.Area && !l.Contains(e)).ToList();
                    int cnt = l.Count + (g.Key.Group != "any" ? gen.Count : 0);
                    if (cnt < 2) continue;
                    // баллистическая и крылатая ракета над одной местностью — разные угрозы (разное время подлёта), не дубль
                    var all2 = l.Concat(g.Key.Group != "any" ? gen : []).ToList();
                    if (!all2.Any(a => all2.Any(b => a != b && (AlertEngine.Compatible(a, b.Threat, b.Kind) || AlertEngine.Compatible(b, a.Threat, a.Kind)))))
                        continue;
                    dupGroups++;
                    var k = $"{g.Key.Area} :: " + string.Join(" | ", l.Concat(g.Key.Group != "any" ? gen : []).Select(e => $"{e.Threat}/{e.Kind}/{e.Level}/{e.Status}{(e.HasPartialClear ? "/P" : "")}@{at:MM-dd HH:mm} H={string.Join(";", e.History.Select(h => h.TimeText + " " + h.Channel + " " + h.Text))}[{string.Join(",", e.RegionIds)}~{string.Join(",", e.WholeRegionIds)}]@{e.Sources[0].Channel}"));
                    dupExamples[k] = dupExamples.GetValueOrDefault(k) + 1;
                }
            }
            foreach (var t in engine.TrackSnapshot())
            {
                lastState[t.Id] = t;
                if (!seenTracks.Add(t.Id)) continue;
                if (t.Legs.Any(l => l.Count > 2)) corridorTracks++;
                if (t.LaunchExplicit) launchExplicit++; else if (t.Launch != null) launchInferred++;
                if (t.Via.Count > 0) viaTracks++;
                if (t.FutureRoute.Count > 2) routeTracks++;
            }
        }
        Console.WriteLine($"duplicates: {dupGroups} overlapping groups in {dupSamples} samples");
        foreach (var (k, c) in dupExamples.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"  {c,4}  {k}");
        if (Environment.GetEnvironmentVariable("RADAR_AUDIT_HISTORY") == "1")
            foreach (var e in engine.Snapshot().OrderByDescending(e => e.History.Count).Take(3))
            {
                Console.WriteLine($"history of {e.Title} [{e.RegionNames}]:");
                foreach (var h in e.History) Console.WriteLine($"    {h.TimeText} {h.ChannelText}: {h.Text}");
            }
        TrackBuilder.SpeedAudit = null;
        {
            var measured = lastState.Values.Where(t => t.HasMeasuredSpeed).ToList();
            Console.WriteLine($"speed confidence over {measured.Count} tracks: high {measured.Count(t => t.SpeedConfidence == 2)}, medium {measured.Count(t => t.SpeedConfidence == 1)}, low {measured.Count(t => t.SpeedConfidence == 0)}; " +
                              $"median spread {measured.Where(t => t.SpeedSpread != null).Select(t => t.SpeedSpread!.Value).Order().DefaultIfEmpty(0).ElementAt(measured.Count(t => t.SpeedSpread != null) / 2):F0} km/h");
        }
        foreach (var cross in new[] { false, true })
        {
            var s = speeds.Where(x => x.Cross == cross).ToList();
            var ok = s.Where(x => x.Ok).ToList();
            static double Med(List<double> l) => l.Count == 0 ? 0 : l.Order().ElementAt(l.Count / 2);
            Console.WriteLine($"speed samples {(cross ? "cross-channel" : "same-channel")}: {s.Count}, accepted {ok.Count}, rejected {s.Count - ok.Count}; " +
                              $"median interval {Med(s.Select(x => x.Min).ToList()):F0} min, median accepted UAV speed {Med(ok.Where(x => ThreatCatalog.Info(x.Kind).Group == ThreatType.Uav).Select(x => x.Raw).ToList()):F0} km/h");
            int fast = ok.Count(x => ThreatCatalog.Info(x.Kind).Group == ThreatType.Uav && x.Raw > Track.DefaultSpeed(x.Kind, ThreatType.Uav) * 1.6);
            Console.WriteLine($"  UAV accepted > 1.6× typical: {fast}; rejected as too fast: {s.Count(x => !x.Ok && x.Raw > 100)}");
            if (Environment.GetEnvironmentVariable("RADAR_AUDIT_SPEEDS") == "1")
                foreach (var x in s.Where(x => (Environment.GetEnvironmentVariable("RADAR_AUDIT_SPEEDS_REJ") == "1" && !x.Ok) || x.Ok && ThreatCatalog.Info(x.Kind).Group == ThreatType.Uav && x.Raw > Track.DefaultSpeed(x.Kind, ThreatType.Uav) * 1.6).Take(25))
                    Console.WriteLine("    " + x.Desc);
        }
        Console.WriteLine($"repost duplicates: {repostDup}");
        foreach (var (k, c) in repostEx.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {c,4}  {k}");
        Console.WriteLine($"missile overlaps: {missileOverlap}");
        foreach (var (k, c) in missileEx.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"  {c,4}  {k}");
        Console.WriteLine($"point duplicates: {pointDup}");
        foreach (var (k, c) in pointDupEx.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {c,4}  {k}");
        Console.WriteLine($"tracks: {seenTracks.Count}; along corridor {corridorTracks}, forecast along corridor/via {routeTracks}, via {viaTracks}; launch explicit {launchExplicit}, inferred {launchInferred}");
        TrackQuality(lastState.Values.ToList(), Path.ChangeExtension(outPath, ".tracks.tsv"),
            msgs.GroupBy(m => (m.Channel, m.Id)).ToDictionary(g => g.Key, g => g.First()));
        Console.WriteLine($"partial clears (alert narrowed, rest still active): {partial}");
        Console.WriteLine($"messages {n}, all-clears applied {clears}: idle {idle}, stuck {stuck}, wide {wide}; dropped {dropped} → {outPath}");
        Console.WriteLine($"dropped all-clears with live alerts of the channel: {droppedLive}");
    }

    /// <summary>
    /// Качество траекторий (многоточечные треки):
    ///   разворот — соседние отрезки пути расходятся более чем на 120°;
    ///   петля — путь длиннее прямой от первой до последней точки более чем в 2 раза;
    ///   «к Украине» — итоговое смещение направлено к месту захода: на суше — к линии (25+ км, не вдоль неё), в Крыму/на Кубани — к точке входа;
    ///   «к городу» — то же, но трек кончается у крупного города (подлёт к цели у линии), считается отдельно;
    ///   пуск за спиной — место пуска лежит не позади первой точки (угол к обратному курсу > 90°).
    /// </summary>
    internal const double NearCityKm = 15;
    internal const int NearCityPopulation = 100_000;

    private static void TrackQuality(List<Track> tracks, string path, Dictionary<(string, long), RawMessage> byKey)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("flags\tid\tthreat\tpoints\tlen_km\tdirect_km\tpath");
        int multi = 0, reversals = 0, loops = 0, toUkraine = 0, toCity = 0, launchAhead = 0, two = 0, twoToUkraine = 0, uncertain = 0;
        bool all = Environment.GetEnvironmentVariable("RADAR_AUDIT_ALLTRACKS") == "1";
        foreach (var t in tracks.Where(t => t.Points.Count >= (all ? 1 : 2)))
        {
            // основные метрики — по треку из 3+ точек; у двух отметок — отдельный счётчик «к Украине» (на карте это
            // короткая линия со значком, развёрнутым к линии фронта), в вывод они попадают с флагом «to-ukraine-2»
            bool counted = t.Points.Count >= 3;
            if (t.Points.Count == 2) two++;
            // на карте значок без направления (Track.UncertainHeading): курс по двум отметкам к линии не подтверждён
            if (t.UncertainHeading) uncertain++;
            if (counted) multi++;
            var pts = t.Points.Select(p => p.Pos).ToList();
            double len = Geo.PathLength(pts), direct = Geo.DistanceKm(pts[0], pts[^1]);
            var flags = new List<string>();
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                if (Geo.DistanceKm(pts[i - 1], pts[i]) < 8 || Geo.DistanceKm(pts[i], pts[i + 1]) < 8) continue;
                if (Geo.AngleDiff(Geo.Bearing(pts[i - 1], pts[i]), Geo.Bearing(pts[i], pts[i + 1])) > 120) { flags.Add("reversal"); break; }
            }
            if (len > 40 && len > direct * 2) flags.Add("loop");
            // к месту захода: на суше — к границе; в Крыму/на Кубани — обратно к точке входа (максимальное удаление − итоговое).
            // Авиация (КАБ) — удары по соседним пунктам, а не маршрут: направление у неё не проверяем
            if (t.Threat != ThreatType.Aviation && (Borderline.HeadsToLine(pts[0], pts[^1], 25) ||
                Borderline.ZoneOf(pts[0]) == Borderline.Zone.Sea && pts.Max(p => Geo.DistanceKm(pts[0], p)) - direct > 25 && len > direct * 1.5))
                // подлёт к крупному городу у линии (Белгород, Горловка) — реальная цель, а не возврат: считаем отдельно
                flags.Add(PlaceIndex.NearestCity(pts[^1], NearCityKm, NearCityPopulation) != null ? "to-city" : "to-ukraine");
            if (t.Launch is { } l && direct > 10 && Geo.AngleDiff(Geo.Bearing(pts[0], l), (Geo.Bearing(pts[0], pts[^1]) + 180) % 360) > 90) flags.Add("launch-ahead");
            if (!counted)
            {
                // курс или цель из сообщения ведут туда же («пролетают с юго-востока», «в сторону Ракитного») —
                // направление сказано каналом, это не ошибка склейки
                double? stated = t.HeadingHint ?? (t.Target is { } tg ? Geo.Bearing(pts[^1], tg) : null);
                bool told = stated is { } sb && Geo.AngleDiff(sb, Geo.Bearing(pts[0], pts[^1])) < 60;
                bool back2 = t.Points.Count == 2 && flags.Contains("to-ukraine") && !told;
                flags.Clear();
                if (back2) { twoToUkraine++; flags.Add("to-ukraine-2"); }
                if (t.UncertainHeading) flags.Add("no-heading");
            }
            if (flags.Contains("reversal")) reversals++;
            if (flags.Contains("loop")) loops++;
            if (flags.Contains("to-ukraine")) toUkraine++;
            if (flags.Contains("to-city")) toCity++;
            if (flags.Contains("launch-ahead")) launchAhead++;
            if (flags.Count > 0 || all)
                w.WriteLine(string.Join('\t', string.Join(",", flags), t.Id, t.Threat, pts.Count, Math.Round(len), Math.Round(direct),
                    (t.Launch is { } lp ? $"[{t.LaunchLabel}{(t.LaunchExplicit ? "*" : "")} {lp.Lat:F2},{lp.Lon:F2}] " : "") + string.Join(" → ", t.Points.Select(p => all ? $"{p.Label}({p.Pos.Lat:F2},{p.Pos.Lon:F2})@{p.Time:HH:mm}" : $"{p.Label}@{p.Time:HH:mm}"))
                    + (all ? $" | legs {string.Join("/", t.Legs.Select(l => $"{l.Count}:{Geo.PathLength(l):F0}"))} pre {t.PreRoute.Count} target {t.TargetLabel ?? "-"} heading {t.HeadingHint?.ToString("F0") ?? "-"} bearing {t.Bearing?.ToString("F0") ?? "-"} speed {t.SpeedKmh:F0}{(t.HasMeasuredSpeed ? "m" : "")} future {(t.FutureRoute.Count > 0 ? $"{t.FutureRoute[^1].Lat:F2},{t.FutureRoute[^1].Lon:F2}" : "-")} src {string.Join(",", t.Events.SelectMany(e => e.Sources).Select(s => s.Channel + "/" + s.Id).Distinct())}" : "")));
        }
        Console.WriteLine($"track quality ({multi} tracks with 3+ points): reversals {reversals}, loops {loops}, towards Ukraine {toUkraine}, launch ahead {launchAhead}, towards city near line {toCity} → {path}");
        Console.WriteLine($"two-point tracks: {two}, towards Ukraine {twoToUkraine}, shown without heading {uncertain}");
        // прогноз (что видит пользователь: стрелка и пунктир) ведёт обратно к линии пуска
        int fcBack = 0, fcAll = 0, fcStated = 0;
        // объект ещё на стороне противника (пуски, «от Чугуева на Волчанск») — к линии он и летит, это не ошибка
        // как и в планировщике, правило «не назад к линии» — для БПЛА и ракет: аэростаты дрейфуют по ветру, авиация бьёт по пунктам
        foreach (var t in tracks.Where(t => t.FutureRoute.Count >= 2 && !t.Cleared && t.Threat is ThreatType.Uav or ThreatType.Missile &&
                                            !t.Events.Any(e => e.Points.Any(p => Geo.DistanceKm(p.Pos, t.Head.Pos) < 3 && Borderline.IsEnemySide(p.Pos, p.RegionId)))))
        {
            fcAll++;
            // в морской зоне (Крым, Кубань) объекты заходят с моря с любой стороны и облетают полуостров: «назад к первой отметке»
            // там не признак ошибки (Тарханкут → Евпатория, Славянск → Абинск). Ошибка — только разворот прогноза против пути
            bool land = Borderline.ZoneOf(t.Head.Pos) != Borderline.Zone.Sea;
            // последний отрезок — реальное перемещение, а не шум порядка сообщений (60 км за 3 мин у БПЛА невозможны)
            bool realLeg = t.Points.Count >= 2 && Geo.DistanceKm(t.Points[^2].Pos, t.Head.Pos) >= 15 &&
                           Geo.DistanceKm(t.Points[^2].Pos, t.Head.Pos) / Math.Max(1.0 / 60, (t.Head.Time - t.Points[^2].Time).TotalHours) <=
                           Track.SpeedRange(t.Kind, t.Threat).Max * 1.5;
            if (land && Borderline.BackKm(t.Head.Pos, t.FutureRoute[^1], t.Points[0].Pos) > 25 ||
                realLeg && Geo.DistanceKm(t.Head.Pos, t.FutureRoute[^1]) >= 15 &&
                Geo.AngleDiff(Geo.Bearing(t.Points[^2].Pos, t.Head.Pos), Geo.Bearing(t.Head.Pos, t.FutureRoute[^1])) > 120)
            {
                var end = t.FutureRoute[^1];
                // цель названа в последнем сообщении трека («Острогожск и далее на Воронеж») — прогноз показывает именно то,
                // что сказано в сводке; это не ошибка движка, считаем отдельно
                var lastEv = t.Events.Count > 0 ? t.Events.MaxBy(e => e.Time) : null;
                // то же для курса словами из последнего сообщения («Дебальцево на восток», «и далее на юг»)
                // цель названа словами в свежих сообщениях трека («Мирный, Новоозерное и далее на Евпаторию, Саки»)
                // основа первого слова: «Ростовская область» ↔ «в направлении Ростовской области», «Орёл» ↔ «на Орёл»
                static string Stem(string s)
                {
                    var n = Gazetteer.Normalize(s).Split(' ', '-')[0];
                    return n.Length > 6 ? n[..^3] : n.Length > 4 ? n[..^1] : n;
                }
                // источники — по одному сообщению на канал, а все сообщения трека — в журнале воспроизведения (byKey)
                var recent = t.Events.SelectMany(e => e.MessageKeys).Select(k => byKey.GetValueOrDefault(k)).OfType<RawMessage>()
                    .Where(s => (t.Head.Time - s.Time).Duration() <= TimeSpan.FromMinutes(30)).ToList();
                bool named = t.TargetLabel is { Length: > 2 } tl &&
                    recent.Any(s => Gazetteer.Normalize(s.Text).Contains(Stem(tl), StringComparison.Ordinal));
                bool fresh = named || t.Target is { } tg0 && lastEv?.Target is { } et && Geo.DistanceKm(et, tg0) < 3 ||
                             t.Target == null && t.HeadingHint is { } hh0 &&
                             recent.Any(s => MessageParser.DetectHeadingText(s.Text) is { } lh && Geo.AngleDiff(lh, hh0) < 1) ||
                             // прогноз к цели идёт по курсу, названному в самом свежем сообщении («Старотитаровская и далее
                             // на юг, юго-восток» → к Туапсе): сводка так и говорит
                             t.Target != null && recent.Where(s => (t.Head.Time - s.Time).Duration() <= TimeSpan.FromMinutes(5))
                                 .Any(s => MessageParser.DetectHeadingText(s.Text) is { } lh2 && Geo.AngleDiff(lh2, Geo.Bearing(t.Head.Pos, end)) < 60);
                if (fresh) { fcStated++; continue; }
                fcBack++;
                string cause = t.Target != null ? (t.TargetIsArea ? "area-target" : "target") : t.HeadingHint != null ? "heading" : t.Points.Count >= 2 ? "course" : "none";
                w.WriteLine(string.Join('\t', "forecast-back", t.Id, t.Threat, t.Points.Count, cause, Borderline.ZoneOf(t.Head.Pos),
                    string.Join(" → ", t.Points.Select(p => $"{p.Label}@{p.Time:HH:mm}")) + $" ⇒ {t.TargetLabel ?? "по курсу"}" +
                    $" | head {t.Head.Pos.Lat:F2},{t.Head.Pos.Lon:F2} end {end.Lat:F2},{end.Lon:F2} len {Geo.PathLength(t.FutureRoute):F0} brg {Geo.Bearing(t.Head.Pos, end):F0}" +
                    $" | depth {Borderline.DistanceKm(t.Head.Pos):F0}→{Borderline.DistanceKm(end):F0} hint {t.HeadingHint?.ToString("F0") ?? "-"} src {string.Join(",", t.Events.SelectMany(e => e.Sources).Select(s => s.Channel + "/" + s.Id).Distinct().Take(4))}"));
            }
        }
        Console.WriteLine($"forecast back towards launch side: {fcBack} of {fcAll} (+{fcStated} to the target stated in the latest message)");
    }
}
