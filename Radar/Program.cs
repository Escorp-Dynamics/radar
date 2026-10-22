using System.Net.Http;
using Avalonia;

namespace Radar;

internal static class Program
{
    private static readonly Dictionary<string, int> ToolArgCounts = new()
    {
        ["--archive-pack"] = 3, ["--archive-verify"] = 2, ["--archive-fetch"] = 2, ["--archive-validate"] = 3, ["--archive-stats"] = 3, ["--archive-unpack"] = 3,
        ["--archive-stats-pack"] = 3,
    };

    [STAThread]
    public static void Main(string[] args)
    {
        // командные утилиты архива: повреждённый пакет или неверный месяц — понятное сообщение, а не стек вызовов
        if (args.Length >= 1 && args[0].StartsWith("--archive-", StringComparison.Ordinal))
        {
            // неполные аргументы или опечатка — код 2 с подсказкой, а не окно приложения
            if (!ToolArgCounts.TryGetValue(args[0], out var need) || args.Length < need)
            {
                Console.Error.WriteLine(need == 0 ? $"Неизвестная команда {args[0]}" : $"{args[0]}: ожидается аргументов: {need - 1}");
                Environment.Exit(2);
            }
            try { MainCore(args); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException
                or AggregateException or HttpRequestException or OperationCanceledException or ArgumentException)
            {
                Console.Error.WriteLine($"Ошибка: {(ex as AggregateException)?.Flatten().InnerException?.Message ?? ex.Message}");
                Environment.Exit(1);
            }
            return;
        }
        MainCore(args);
    }

    private static void MainCore(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--render-demo")
        {
            // Отладка: отрисовка демо-сценария в PNG без окна
            BuildAvaloniaApp().UseSkia().SetupWithoutStarting();
            Radar.Views.MainView.RenderDemo(args[1]);
            return;
        }
        if (args.Length >= 2 && args[0] == "--render-ui")
        {
            // Отладка: снимок всего окна (стеклянные панели поверх карты) в PNG
            BuildAvaloniaApp().UseSkia().SetupWithoutStarting();
            Radar.Views.MainView.RenderUi(args[1]);
            return;
        }
        if (args.Length >= 1 && args[0] == "--camera-test")
        {
            BuildAvaloniaApp().UseSkia().SetupWithoutStarting();
            Radar.Views.MainView.CameraTest();
            return;
        }
        if (args.Length >= 1 && args[0] == "--bench-map")
        {
            // Замер скорости отрисовки карты на самой загруженной точке недельного архива
            BuildAvaloniaApp().UseSkia().SetupWithoutStarting();
            Radar.Views.MainView.BenchMap();
            return;
        }
        if (args.Length >= 3 && args[0] == "--archive-pack")
        {
            // Упаковка рабочего архива (каталоги каналов) в продакшн-формат: --archive-pack <откуда> <куда>
            Radar.Services.ArchiveManifest man;
            try { man = Radar.Services.ArchivePack.Pack(args[1], args[2], Console.WriteLine); }
            catch (InvalidDataException ex) { Console.WriteLine(ex.Message); Environment.Exit(1); return; }
            Console.WriteLine($"месяцев {man.Months.Count}, сообщений {man.Months.Sum(m => m.Messages)}, " +
                              $"{man.Months.Sum(m => m.Bytes) / 1024 / 1024.0:F1} МБ → {Path.Combine(args[2], Radar.Services.ArchivePack.ManifestName)}");
            return;
        }
        if (args.Length >= 3 && args[0] == "--archive-unpack")
        {
            // Пакетный архив обратно в каталоги каналов (докачка на сервере сборки): --archive-unpack <пакеты> <куда>
            Radar.Services.ArchivePack.Unpack(args[1], args[2], Console.WriteLine);
            return;
        }
        if (args.Length >= 2 && args[0] == "--archive-verify")
        {
            // Проверка пакетного архива по манифесту: файлы, SHA-256, число сообщений
            var errors = Radar.Services.ArchivePack.Verify(args[1]);
            foreach (var e in errors) Console.WriteLine(e);
            Console.WriteLine(errors.Count == 0 ? "архив в порядке" : $"ошибок: {errors.Count}");
            Environment.Exit(errors.Count == 0 ? 0 : 1);
        }
        if (args.Length >= 2 && args[0] == "--archive-fetch")
        {
            // Полный архив каналов по месяцам: --archive-fetch <каталог> [канал ...]
            Radar.Services.ArchiveFetcher.RunAsync(args[1], args.Skip(2).ToList(), 4).GetAwaiter().GetResult();
            return;
        }
        Radar.Map.Basemap.Preload();
        Radar.Map.PlaceLabels.Preload();
        // прогноз к области-цели — до её края по контуру подложки
        Radar.Services.RoutePlanner.RegionAt = Radar.Map.Basemap.RegionAt;
        // отбой «части области» — по тем же районам, что подсвечены на карте
        Radar.Services.RegionPart.Exact = (region, bearing, p) =>
        {
            var part = Radar.Map.Basemap.DistrictsInPart(region, bearing);
            if (part.Count == 0 || Radar.Map.Basemap.DistrictAt(p) is not { } id || !id.StartsWith(region + "|", StringComparison.Ordinal)) return null;
            return part.Contains(id);
        };
        if (args.Length >= 3 && args[0] == "--audit")
        {
            // Разбор архива сообщений (tools/fetch_corpus.py) в TSV для проверки парсера — с теми же хуками, что и приложение
            Radar.Services.CorpusAudit.Run(args[1], args[2]);
            return;
        }
        if (args.Length >= 1 && args[0] == "--history-check")
        {
            // Загрузка истории за сутки из каналов, как при запуске, и проверка её применения
            Radar.Services.EngineAudit.HistoryCheck(args.Length >= 2 ? args[1] : null).GetAwaiter().GetResult();
            return;
        }
        if (args.Length >= 3 && args[0] == "--archive-validate")
        {
            // Разбор всего архива: отчёты по нераспознанному (новые типы угроз, формулировки, пункты)
            Radar.Services.ArchiveValidate.Run(args[1], args[2]);
            return;
        }
        if (args.Length >= 3 && args[0] == "--archive-stats-pack")
        {
            // Статистика всех месяцев для релиза: --archive-stats-pack <архив> <куда> [прежний пакет статистики]
            // (после хуков подложки — итоги те же, что посчитало бы приложение)
            if (args.Length >= 4) Console.WriteLine($"из прежнего пакета: {Radar.Services.StatsPack.ImportDir(args[1], args[3])} мес.");
            var sp = Radar.Services.StatsPack.Pack(args[1], args[2], Console.WriteLine);
            Console.WriteLine($"месяцев {sp.Months.Count} → {Path.Combine(args[2], Radar.Services.StatsPack.ManifestName)}");
            return;
        }
        if (args.Length >= 3 && args[0] == "--archive-stats")
        {
            // Статистика месяца архива: --archive-stats <каталог> YYYY-MM
            if (args[2] == "all")
            {
                // все месяцы архива параллельно: итоги кешируются, после этого «Всё время» открывается мгновенно
                var all = Radar.Services.ArchiveFetcher.Months(args[1]);
                var swa = System.Diagnostics.Stopwatch.StartNew();
                var res = new System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, (int Ev, int Tr)>();
                Parallel.ForEach(all.OrderByDescending(m => m), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, m =>
                {
                    var (e1, t1) = Radar.Services.ArchiveStats.Month(args[1], m);
                    res[m] = (e1.Count, t1.Count);
                    Console.WriteLine($"{m:yyyy-MM}: событий {e1.Count}, треков {t1.Count} · {swa.Elapsed:m\\:ss}");
                });
                Console.WriteLine($"месяцев {all.Count}, событий {res.Values.Sum(v => v.Ev)}, за {swa.Elapsed.TotalSeconds:F0} с");
                var swb = System.Diagnostics.Stopwatch.StartNew();
                var evAll = new List<Radar.Services.StatEvent>(); var trAll = new List<Radar.Services.StatTrack>();
                foreach (var m in all) { var (e1, t1) = Radar.Services.ArchiveStats.Month(args[1], m); evAll.AddRange(e1); trAll.AddRange(t1); }
                Console.WriteLine($"чтение кеша всех месяцев: {swb.ElapsedMilliseconds} мс");
                swb.Restart();
                var rep = Radar.Services.StatsSummary.Build(evAll, trAll, all[0], all[^1].AddMonths(1), null, [], 15, evAll);
                Console.WriteLine($"сводка за всё время: {swb.ElapsedMilliseconds} мс; тревог {rep.Alerts}, под тревогой {rep.AlertHours:F0} ч");
                for (int k = 0; k < 3; k++)
                {
                    swb.Restart();
                    Radar.Services.StatsSummary.Build(evAll, trAll, all[0], all[^1].AddMonths(1), null, [], 15, evAll);
                    long a1 = swb.ElapsedMilliseconds; swb.Restart();
                    Radar.Services.StatsSummary.Build(evAll, trAll, all[^1].AddDays(-7), all[^1].AddMonths(1), null, [], 15, evAll);
                    Console.WriteLine($"  повтор: всё время {a1} мс, неделя {swb.ElapsedMilliseconds} мс");
                }
                return;
            }
            var month = DateTimeOffset.ParseExact(args[2] + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ev, tr) = Radar.Services.ArchiveStats.Month(args[1], month);
            Console.WriteLine($"{args[2]}: событий {ev.Count}, треков {tr.Count}, за {sw.Elapsed.TotalSeconds:F1} с; часов под тревогой {Radar.Services.StatsSummary.UnionHours(ev, month, month.AddMonths(1)):F0}, длиннее 6 ч: {ev.Count(e => e.Duration.TotalHours > 6)}");
            foreach (var g in ev.GroupBy(e => e.Threat).OrderByDescending(g => g.Count())) Console.WriteLine($"  {g.Key}: {g.Count()}");
            foreach (var g in ev.SelectMany(e => e.Regions).GroupBy(r => r).OrderByDescending(g => g.Count()).Take(8)) Console.WriteLine($"  {g.Key}: {g.Count()}");
            Console.WriteLine($"  медиана длительности {ev.Select(e => e.Duration.TotalMinutes).Order().ElementAtOrDefault(ev.Count / 2):F0} мин");
            return;
        }
        if (args.Length >= 3 && args[0] == "--engine-audit")
        {
            // Прогон архива через движок: холостые/широкие отбои, зависшие тревоги
            Radar.Services.EngineAudit.Run(args[1], args[2]);
            return;
        }
        Radar.Services.PlaceIndex.Preload();
        Radar.Services.RouteNetwork.Preload();
        Radar.Services.RoadGraph.Preload();
        App.DesktopShell = Radar.Desktop.DesktopShell.Install;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // GPU в приоритете, программный рендер — только последний запасной вариант. RADAR_RENDER=vulkan|egl|glx|software ставит выбранный режим первым
    private static X11RenderingMode[] X11Modes()
    {
        X11RenderingMode[] order = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software];
        var want = Environment.GetEnvironmentVariable("RADAR_RENDER")?.Trim().ToLowerInvariant() switch
        {
            "vulkan" => (X11RenderingMode?)X11RenderingMode.Vulkan,
            "egl" => X11RenderingMode.Egl,
            "glx" => X11RenderingMode.Glx,
            "software" => X11RenderingMode.Software,
            _ => null
        };
        return want is { } w ? [w, .. order.Where(m => m != w)] : order;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // X11 без удерживаемого буфера кадра выделял буфер окна (≈ ширина × высота × 4 байта, с «давлением» на сборщик
            // мусора) на каждый кадр: при анимации — сотни полных сборок в минуту и до трети времени процессора
            // глобальное меню окна не используется; на KDE при выходе оно давало «ServiceUnknown» от AppMenu.Registrar
            .With(new X11PlatformOptions { UseRetainedFramebuffer = true, RenderingMode = X11Modes(), UseDBusMenu = false })
            // IBM Plex Sans — интерфейс и подписи карты, JetBrains Mono — цифры телеметрии (OFL, встроены в сборку)
            .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://Radar.Core/Assets/Fonts#IBM Plex Sans" })
            .LogToTrace();
}
