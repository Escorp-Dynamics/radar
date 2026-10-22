using System.Globalization;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>Одна тревога архива для статистики: событие движка (репосты склеены) и его длительность.</summary>
public readonly record struct StatEvent(
    DateTimeOffset Start, DateTimeOffset End, ThreatType Threat, ThreatKind Kind, AlertLevel Level, AlertStatus Status,
    string[] Regions, string[] Places, double[] Lats, double[] Lons)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    /// <summary>Регион каждого пункта (как <see cref="Places"/>); пусто — неизвестен (старый кеш).</summary>
    public string[] PlaceRegions { get; init; } = [];
}

/// <summary>Трек архива: откуда (место пуска или первая отметка) и куда шёл объект.</summary>
public readonly record struct StatTrack(DateTimeOffset Start, ThreatType Threat, ThreatKind Kind, double FromLat, double FromLon,
    double ToLat, double ToLon, string? LaunchLabel, bool LaunchExplicit, int Points);

/// <summary>
/// Статистика полного архива: прогон всех сообщений месяца через тот же движок, что и в реальном времени
/// (тревога — событие после склейки репостов, длительность — до отбоя или истечения). Итоги месяца кешируются
/// в <c>{архив}/.stats/YYYY-MM.tsv</c>; завершённые месяцы не пересчитываются, текущий — при изменении архива.
/// </summary>
public static class ArchiveStats
{
    private const int Version = 6;
    /// <summary>
    /// Кеш привязан и к коду: правки разбора и движка меняют статистику, а архив при этом тот же. Отпечаток — git-деревья
    /// разбора и движка (см. Radar.Core.csproj): одинаков у CI и релиза из одного кода и не меняется от пересборки
    /// без правок движка. Без него (сборка вне git) — отпечаток сборки.
    /// </summary>
    private static readonly long BuildStamp = EngineStamp();

    private static long EngineStamp()
    {
        var meta = typeof(ArchiveStats).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RadarEngineStamp")?.Value;
        if (string.IsNullOrEmpty(meta)) return typeof(ArchiveStats).Assembly.ManifestModule.ModuleVersionId.GetHashCode();
        // FNV-1a: детерминированно между процессами и машинами (string.GetHashCode случаен от запуска к запуску)
        ulong h = 14695981039346656037;
        foreach (char c in meta) h = unchecked((h ^ c) * 1099511628211);
        return unchecked((long)h);
    }
    /// <summary>Запас после конца месяца: отбои и репосты тревог, начавшихся в последние часы месяца.</summary>
    private static readonly TimeSpan Tail = TimeSpan.FromHours(6);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Dir, DateTimeOffset Month), (long Stamp, List<StatEvent> Events, List<StatTrack> Tracks)> Memory = new();

    /// <summary>Итоги месяца уже посчитаны (в памяти или в кеше на диске) — открываются без прогона движка.</summary>
    public static bool IsReady(string archiveDir, DateTimeOffset month)
    {
        long stamp = ArchiveStamp(archiveDir, month);
        if (Memory.TryGetValue((archiveDir, month), out var mem) && mem.Stamp == stamp) return true;
        var cache = CachePath(archiveDir, month);
        try
        {
            if (!File.Exists(cache)) return false;
            using var r = new StreamReader(cache, Encoding.UTF8);
            return r.ReadLine() == Header(stamp);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>События и треки месяца (из памяти, кеша или прогоном архива).</summary>
    public static (List<StatEvent> Events, List<StatTrack> Tracks) Month(string archiveDir, DateTimeOffset month, CancellationToken ct = default)
    {
        var cache = CachePath(archiveDir, month);
        long stamp = ArchiveStamp(archiveDir, month);
        // в памяти: смена периода на дашборде не перечитывает файлы (весь архив — ~1,5 с разбора TSV)
        if (Memory.TryGetValue((archiveDir, month), out var mem) && mem.Stamp == stamp) return (mem.Events, mem.Tracks);
        // один месяц считается одним потоком: фоновый прогрев и запрос дашборда не прогоняют его дважды и не пишут кеш наперегонки
        lock (Locks.GetOrAdd((archiveDir, month), _ => new object()))
            return MonthLocked(archiveDir, month, cache, stamp, ct);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, DateTimeOffset), object> Locks = new();

    private static string CachePath(string archiveDir, DateTimeOffset month) =>
        Path.Combine(archiveDir, ".stats", month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".tsv");

    /// <summary>
    /// Прежние итоги месяца, даже устаревшие (архив дополнился, движок изменился): их можно показать сразу, пока месяц
    /// пересчитывается. Null — месяц ещё ни разу не считался (или кеш другого формата).
    /// </summary>
    public static (List<StatEvent> Events, List<StatTrack> Tracks)? Peek(string archiveDir, DateTimeOffset month)
    {
        if (Memory.TryGetValue((archiveDir, month), out var mem)) return (mem.Events, mem.Tracks);
        return TryRead(CachePath(archiveDir, month), null);
    }

    /// <summary>Отпечаток, с которым должен быть записан кеш месяца, чтобы считаться актуальным.</summary>
    public static long Stamp(string archiveDir, DateTimeOffset month) => ArchiveStamp(archiveDir, month);

    /// <summary>Строка-заголовок кеша месяца (по ней кеш из CI сверяется с ожидаемым).</summary>
    public static string Header(long stamp) => $"stats\t{Version}\t{stamp}";

    /// <summary>Содержимое кеша месяца (для выкладки в релиз), если он актуален.</summary>
    public static byte[]? Export(string archiveDir, DateTimeOffset month)
    {
        Month(archiveDir, month);
        var path = CachePath(archiveDir, month);
        return IsReady(archiveDir, month) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Принимает готовый кеш месяца (посчитанный в CI тем же кодом): только если его заголовок совпадает с ожидаемым
    /// отпечатком этого архива и этой сборки и он читается целиком. Возвращает, принят ли кеш.
    /// </summary>
    public static bool Import(string archiveDir, DateTimeOffset month, byte[] content)
    {
        long stamp = ArchiveStamp(archiveDir, month);
        var path = CachePath(archiveDir, month);
        var tmp = path + ".import";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, content);
            if (TryRead(tmp, stamp) is not { } data) { File.Delete(tmp); return false; }
            lock (Locks.GetOrAdd((archiveDir, month), _ => new object()))
            {
                File.Move(tmp, path, true);
                Memory[(archiveDir, month)] = (stamp, data.Item1, data.Item2);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static (List<StatEvent> Events, List<StatTrack> Tracks) MonthLocked(string archiveDir, DateTimeOffset month, string cache, long stamp, CancellationToken ct)
    {
        if (Memory.TryGetValue((archiveDir, month), out var mem) && mem.Stamp == stamp) return (mem.Events, mem.Tracks);
        if (TryRead(cache, stamp) is { } cached)
        {
            Memory[(archiveDir, month)] = (stamp, cached.Item1, cached.Item2);
            return cached;
        }
        var res = Compute(archiveDir, month, ct);
        Memory[(archiveDir, month)] = (stamp, res.Events, res.Tracks);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            Write(cache, stamp, res.Events, res.Tracks);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return res;
    }

    /// <summary>Отпечаток архива месяца (размеры файлов этого и предыдущего месяца — предыстория).</summary>
    private static long ArchiveStamp(string dir, DateTimeOffset month)
    {
        // детерминированно (без хеша строк — он случаен от запуска к запуску): имена каналов посимвольно и размеры файлов
        long h = unchecked(Version * 1_000_003L + BuildStamp);
        // пакетный архив: отпечаток — SHA-256 пакетов месяца и предыдущего
        if (ArchivePack.ReadManifest(dir) is { } manifest)
        {
            foreach (var m in new[] { month.AddMonths(-1), month, month.AddMonths(1) })
                if (manifest.Months.FirstOrDefault(e => e.Month == m.ToString("yyyy-MM", CultureInfo.InvariantCulture)) is { } e)
                    foreach (char c in e.Sha256) h = unchecked(h * 31 + c);
            return h;
        }
        foreach (var m in new[] { month.AddMonths(-1), month, month.AddMonths(1) })
        {
            var name = m.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".tsv";
            foreach (var d in ArchiveFetcher.ChannelDirs(dir))
            {
                var f = Path.Combine(d, name);
                if (!File.Exists(f)) continue;
                foreach (char c in Path.GetFileName(d)) h = unchecked(h * 31 + c);
                h = unchecked(h * 1_000_003 + new FileInfo(f).Length);
            }
        }
        return h;
    }

    /// <summary>
    /// Начало тревоги — первая запись её истории. Источники для этого не годятся: при частичном перехвате пунктов
    /// свежая тревога забирает сообщения более ранней (для геокодера), и тогда «начало» тревоги, объявленной в 23:00,
    /// уезжает на 16:30 — к чужой широкой тревоге, которая продолжает жить отдельно. В историю же попадают только
    /// полностью поглощённые тревоги той же местности — то есть непрерывная опасность.
    /// </summary>
    internal static DateTimeOffset AlertStart(AlertEvent e) =>
        e.History.Count > 0 ? e.History[0].Time : e.Sources.Count > 0 ? e.Sources.Min(s => s.Time) : e.Time;

    private static (List<StatEvent> Events, List<StatTrack> Tracks) Compute(string dir, DateTimeOffset month, CancellationToken ct)
    {
        var from = month;
        var to = month.AddMonths(1);
        // предыстория — тревоги, начавшиеся до полуночи 1-го числа, снимаются отбоями уже в этом месяце
        // и хвост после конца месяца: тревога 31-го в 23:50 снимается отбоем 1-го в 00:20 (считается по началу)
        var msgs = ArchiveFetcher.Load(dir, from - TimeSpan.FromHours(6), to + Tail);
        var events = new List<StatEvent>();
        var tracks = new List<StatTrack>();
        if (msgs.Count == 0) return (events, tracks);
        var settings = new AppSettings { OnlineGeocoding = false, OnlineFrontLine = false };
        using var http = new HttpClient();
        using var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle())) { RealTime = true };
        // отбой снимает тревогу, оставляя событие-отбой (статус AllClear) — его время и есть конец тревоги
        var seen = new HashSet<AlertEvent>(ReferenceEqualityComparer.Instance);
        engine.EventRetired = e =>
        {
            if (!seen.Add(e) || e.Status is AlertStatus.AllClear) return;
            var start = AlertStart(e);
            if (start < from || start >= to) return;
            var end = e.ExpiresAt;
            // истечение без отбоя (срок = последнее подтверждение + время жизни): тревога длилась до последнего
            // подтверждения и ещё немного, а не весь срок хранения на карте
            var lifetime = TimeSpan.FromMinutes(settings.AlertLifetimeMinutes);
            var last = e.Sources.Count > 0 ? e.Sources.Max(s => s.Time) : e.Time;
            if (end - last >= lifetime - TimeSpan.FromMinutes(1)) end = end - lifetime + TimeSpan.FromMinutes(15);
            if (end < start) end = start;
            var pts = e.Points.Where(p => !p.IsRegionLevel).ToList();
            events.Add(new StatEvent(start, end, e.Threat, e.Kind, e.EffectiveLevel, e.Status,
                [.. e.RegionIds], [.. pts.Select(p => p.Label)], [.. pts.Select(p => p.Pos.Lat)], [.. pts.Select(p => p.Pos.Lon)])
                { PlaceRegions = [.. pts.Select(p => p.RegionId ?? "")] });
        };
        engine.TrackRetired = t =>
        {
            if (t.Points.Count == 0 || t.Created < from || t.Created >= to) return;
            var a = t.Launch ?? t.Points[0].Pos;
            var b = t.Points[^1].Pos;
            tracks.Add(new StatTrack(t.Created, t.Threat, t.Kind, a.Lat, a.Lon, b.Lat, b.Lon, t.LaunchLabel, t.LaunchExplicit, t.Points.Count));
        };
        // пачками по 6 ч: обстановка живёт по своему времени (как история при запуске), между пачками ничего не теряется
        int i = 0;
        while (i < msgs.Count)
        {
            ct.ThrowIfCancellationRequested();
            var dayEnd = msgs[i].Time.AddHours(6);
            int j = i;
            while (j < msgs.Count && msgs[j].Time < dayEnd) j++;
            engine.ApplyBatchAt(msgs.GetRange(i, j - i));
            i = j;
        }
        // хвост: всё, что ещё активно, — снимаем по истечении
        engine.ExpireAt(to + Tail + TimeSpan.FromDays(1));
        events.Sort((x, y) => x.Start.CompareTo(y.Start));
        return (events, tracks);
    }

    // ---------------- кеш (TSV) ----------------

    /// <summary>Кеш месяца; <paramref name="stamp"/> null — любой отпечаток (устаревший кеш), но того же формата.</summary>
    private static (List<StatEvent>, List<StatTrack>)? TryRead(string path, long? stamp)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            var head = r.ReadLine();
            if (stamp is { } s ? head != Header(s) : head?.StartsWith($"stats\t{Version}\t", StringComparison.Ordinal) != true) return null;
            var ev = new List<StatEvent>();
            var tr = new List<StatTrack>();
            while (r.ReadLine() is { } line)
            {
                var f = line.Split('\t');
                if (f[0] == "e" && f.Length >= 12)
                {
                    static double[] D(string s) => s.Length == 0 ? [] : s.Split(';').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    static string[] S(string s) => s.Length == 0 ? [] : s.Split(';');
                    ev.Add(new StatEvent(DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[1])), DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2])),
                        (ThreatType)int.Parse(f[3]), (ThreatKind)int.Parse(f[4]), (AlertLevel)int.Parse(f[5]), (AlertStatus)int.Parse(f[6]),
                        S(f[7]), S(f[8]), D(f[9]), D(f[10])) { PlaceRegions = S(f[11]) });
                }
                else if (f[0] == "t" && f.Length >= 11)
                {
                    double P(int k) => double.Parse(f[k], CultureInfo.InvariantCulture);
                    tr.Add(new StatTrack(DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[1])), (ThreatType)int.Parse(f[2]), (ThreatKind)int.Parse(f[3]),
                        P(4), P(5), P(6), P(7), f[8].Length > 0 ? f[8] : null, f[9] == "1", int.Parse(f[10])));
                }
            }
            return (ev, tr);
        }
        catch (Exception) when (true) { return null; }
    }

    private static void Write(string path, long stamp, List<StatEvent> events, List<StatTrack> tracks)
    {
        static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string Clean(string s) => s.Replace('\t', ' ').Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine(Header(stamp));
            foreach (var e in events)
                w.WriteLine(string.Join('\t', "e", e.Start.ToUnixTimeSeconds(), e.End.ToUnixTimeSeconds(), (int)e.Threat, (int)e.Kind, (int)e.Level, (int)e.Status,
                    string.Join(';', e.Regions.Select(Clean)), string.Join(';', e.Places.Select(Clean)), string.Join(';', e.Lats.Select(N)), string.Join(';', e.Lons.Select(N)), string.Join(';', e.PlaceRegions.Select(Clean))));
            foreach (var t in tracks)
                w.WriteLine(string.Join('\t', "t", t.Start.ToUnixTimeSeconds(), (int)t.Threat, (int)t.Kind, N(t.FromLat), N(t.FromLon), N(t.ToLat), N(t.ToLon),
                    Clean(t.LaunchLabel ?? ""), t.LaunchExplicit ? "1" : "0", t.Points));
        }
        File.Move(tmp, path, true);
    }
}
