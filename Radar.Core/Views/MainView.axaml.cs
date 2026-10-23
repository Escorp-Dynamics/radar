using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Radar.Models;
using Radar.Services;
using Radar.ViewModels;

namespace Radar.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;

    private bool _sidebarHidden;
    // панель убрана автоматически на экране статистики: при уходе с него вернётся
    private bool _sidebarAutoHidden, _pickAutoHidden;

    public MainView() : this(false) { }

    public MainView(bool external)
    {
        IsExternal = external;
        InitializeComponent();
        InitLayout();
        InitMirror();
        // Ctrl+Shift+O — эмуляция обрыва сети (проверка поведения интерфейса); RADAR_NET_FLAP=<сек> — автоматическое переключение
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.O && e.KeyModifiers == (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift))
            { ToggleSimulatedOffline(); e.Handled = true; }
            if (e.Key == Avalonia.Input.Key.Escape && _vm is { ModeMenuOpen: true }) { _vm.ModeMenuOpen = false; e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape && _vm is { MonthPickerOpen: true }) { _vm.MonthPickerOpen = false; e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (int.TryParse(Environment.GetEnvironmentVariable("RADAR_NET_FLAP"), out var flap) && flap > 0)
        {
            _flapTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(flap), Avalonia.Threading.DispatcherPriority.Background,
                (_, _) => ToggleSimulatedOffline());
            _flapTimer.Start();
        }
        // карта под стеклянной лентой: точки фокуса центрируются в видимой части
        Map.Obscured = new Avalonia.Thickness(0, BarHeight + 1, SidebarWidth, 0);
        TitleBar.SizeChanged += (_, e) => UpdateTelemetryLayout(e.NewSize.Width);
        Overlays.SizeChanged += (_, _) => FitOverlays();
        TopStack.SizeChanged += (_, _) => FitOverlays();
        HistoryPanel.SizeChanged += (_, _) => FitOverlays();
        ReplayPanel.SizeChanged += (_, _) => FitOverlays();
        HiddenChip.SizeChanged += (_, _) => FitOverlays();
        foreach (var c in new Control[] { HistoryPanel, ReplayPanel, LegendPanel, LegendButton, HiddenChip })
            c.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) FitOverlays(); };
        SidebarToggle.Classes.Set("on", true);
        ReplayTimeline.SeekRequested += f => _vm?.SeekHistory(f);
        Map.HeatRegionClicked += id => _vm?.StatsPickRegion(id);
        Map.RegionPicked += id => _vm?.TogglePickedRegion(id);
        Map.PlacePicked += (name, pos) => _vm?.TogglePickedPlace(name, pos);
        Map.PointPicked += pos => _vm?.TogglePickedPoint(pos);
        Map.PlaceRadiusChanged += (a, km) => _vm?.SetPlaceRadius(a, km);
        // выбор на карте — только пока открыты настройки и есть связь
        SideTabs.SelectionChanged += (_, _) => { if (_vm != null && SideTabs.SelectedIndex != 3) _vm.PickingOnMap = false; };
        Map.EventClicked += ev =>
        {
            if (_vm != null) _vm.Selected = ev;
            if (ev != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => EventsList.ScrollIntoView(ev), Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    private void OnSelectionRestored(AlertEvent e) { if (!ReferenceEquals(EventsList.SelectedItem, e)) EventsList.SelectedItem = e; }

    private void OnStatsHeat(IReadOnlyDictionary<string, double>? heat, string? sel) { Map.Heat = heat; Map.HeatSelected = sel; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.MapEventsChanged -= OnMapEvents;
            _vm.SelectionRestored -= OnSelectionRestored;
            _vm.PropertyChanged -= OnVmChanged;
            _vm.FetchStarted -= LoadLine.Pulse;
            _vm.StatsHeatChanged -= OnStatsHeat;
            _vm.OpenLinkRequested -= OpenUri;
        }
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;

        _vm.MapEventsChanged += OnMapEvents;
        _vm.SelectionRestored += OnSelectionRestored;
        _vm.PropertyChanged += OnVmChanged;
        _vm.FetchStarted += LoadLine.Pulse;
        _vm.StatsHeatChanged += OnStatsHeat;
        _vm.OpenLinkRequested += OpenUri;
        Map.ShowExpected = _vm.ShowExpected;
        Map.ShowFrontLine = _vm.ShowFrontLine;
        Map.SignalLost = _vm.IsOffline;
        Map.SignalLostSince = _vm.StaleSince;
        Map.RetroNoise = _vm.RetroNoise;
        Map.AnimationQuality = _vm.AnimationQuality;
        Map.SignalLossEffect = _vm.SignalLossEffect;
        Map.PickRegions = _vm.PickingOnMap;
        Map.PickPoint = _vm.PickPointMode;
        Map.Vignette = _vm.Vignette;
        Map.PickedRegions = _vm.NotifyRegionSet;
        Map.WatchAreas = _vm.WatchAreas;
        Map.WatchRadiusKm = _vm.WatchRadiusKm;
        Map.HighlightPicked = _vm.HighlightMyRegions;
        if (Environment.GetEnvironmentVariable("RADAR_DEMO") == "1") OnMapEvents([], []);
    }

    private void OnMapEvents(IReadOnlyList<AlertEvent> events, IReadOnlyList<Track> tracks)
    {
        if (Environment.GetEnvironmentVariable("RADAR_DEMO") == "1")
        {
            var (de, dt) = DemoScenario();
            Map.SetEvents(de, dt);
            return;
        }
        Map.SetEvents(events, tracks);
    }

    public static void CameraTest()
    {
        var map = new Radar.Map.MapControl { Width = 1600, Height = 1000 };
        map.Measure(new Avalonia.Size(1600, 1000));
        map.Arrange(new Avalonia.Rect(0, 0, 1600, 1000));
        foreach (var l in map.CameraSelfTest()) Console.WriteLine(l);

        // фоновая перерисовка подложки: кадр не блокируется, готовая подложка подменяется после завершения
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1600, 1000));
        map.CenterOn(new GeoPoint(48.3, 39.9), 6);
        bmp.Render(map);
        Radar.Map.MapControl.ForceBackgroundBase = true;
        map.CenterOn(new GeoPoint(48.3, 39.9), 8);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bmp.Render(map);
        Console.WriteLine($"background: frame {sw.ElapsedMilliseconds} ms, base zoom {map.DebugBaseZoom:0.0} (stretched), job running {map.DebugBaseJob is { IsCompleted: false }}");
        map.DebugBaseJob?.Wait();
        Console.WriteLine($"background: job done in {sw.ElapsedMilliseconds} ms");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"background: base zoom after swap {map.DebugBaseZoom:0.0}");
        sw.Restart();
        bmp.Render(map);
        Console.WriteLine($"background: next frame {sw.ElapsedMilliseconds} ms");

        // фоновая перерисовка слоя зон: в UI-потоке — только запись команд
        var (de, dt) = DemoScenario();
        map.SetEvents(de, dt);
        map.AdvanceAnimations(TimeSpan.FromSeconds(6));
        bmp.Render(map);
        map.DebugAreasJob?.Wait();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 3; i++) { sw.Restart(); bmp.Render(map); Console.WriteLine($"areas: plain frame {sw.ElapsedMilliseconds} ms"); }
        Radar.Map.MapControl.AreasCount = 0;
        System.Threading.Thread.Sleep(300); bmp.Render(map); // кроссфейд после первой отрисовки завершён
        map.DebugAreasJob?.Wait();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        bmp.Render(map);
        Radar.Map.MapControl.AreasCount = 0;
        map.InvalidateAreas();
        sw.Restart();
        bmp.Render(map);
        Console.WriteLine($"areas: frame with re-record {sw.ElapsedMilliseconds} ms (areas renders {Radar.Map.MapControl.AreasCount}), job running {map.DebugAreasJob is { IsCompleted: false }}");
        map.DebugAreasJob?.Wait();
        Console.WriteLine($"areas: rasterized in {sw.ElapsedMilliseconds} ms");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        sw.Restart();
        bmp.Render(map);
        Console.WriteLine($"areas: next frame {sw.ElapsedMilliseconds} ms");
        Radar.Map.MapControl.ForceBackgroundBase = false;
    }

    /// <summary>Офлайн-снимок всего окна (панели поверх карты) — проверка стекла.</summary>
    public static void RenderUi(string path)
    {
        Radar.Services.FrontLineSource.LoadForAudit();
        ThreatCatalog.IconPathResolver = k => Radar.Map.MapIcons.Paths.TryGetValue(k, out var d) ? d : Radar.Map.MapIcons.Paths["alert"];
        MainViewModel.ConfigureGeometry();
        var vm = new MainViewModel(new AppSettings { Channels = [], LegendCollapsed = Environment.GetEnvironmentVariable("RADAR_UI_MOBILE") == "1" && Environment.GetEnvironmentVariable("RADAR_UI_LEGEND") != "1" });
        if (Environment.GetEnvironmentVariable("RADAR_UI_LEGEND_ALL") == "1") vm.LegendExpanded = true;
        // RADAR_UI_MOBILE=1 — представление без хрома окна, как на телефоне (шапка без кнопок окна)
        bool mobile = Environment.GetEnvironmentVariable("RADAR_UI_MOBILE") == "1";
        var shell = new MainWindow { DataContext = vm };
        var w = shell.View;
        if (mobile) w.Chrome = null;
        // окно не показываем: размечаем его как обычный элемент (стили приложения действуют на содержимое)
        int W = int.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_W"), out var uw) ? uw : 1440;
        int H = int.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_H"), out var uh) ? uh : 880;
        shell.MinWidth = shell.MinHeight = 0;
        shell.Width = W; shell.Height = H;
        // непоказанное окно ограничено рабочей областью экрана с его масштабом — снимок размечает представление в нужном размере
        shell.Measure(new Avalonia.Size(W, H));
        shell.Arrange(new Avalonia.Rect(0, 0, W, H));
        w.Measure(new Avalonia.Size(W, H));
        w.Arrange(new Avalonia.Rect(0, 0, W, H));
        if (Environment.GetEnvironmentVariable("RADAR_UI_SAFE") is { Length: > 0 } safeEnv)
        {
            w.DebugSafeArea(Avalonia.Thickness.Parse(safeEnv));
            w.Measure(new Avalonia.Size(W, H));
            w.Arrange(new Avalonia.Rect(0, 0, W, H));
        }
        if (Environment.GetEnvironmentVariable("RADAR_UI_DRAWER") == "1")
        {
            w.Sidebar.Transitions = null;
            w._userOpenedDrawer = true;
            w.SetSidebarHidden(false);
            w.SidebarScrim.Transitions = null;
            w.SidebarScrim.Opacity = 1;
        }
        if (Environment.GetEnvironmentVariable("RADAR_UI_HIDE_SIDEBAR") == "1")
        {
            w.Sidebar.Transitions = null; // без анимации: снимок конечного состояния
            w.SetSidebarHidden(true);
        }
        if (Environment.GetEnvironmentVariable("RADAR_UI_SEARCH") is { Length: > 0 } q) vm.FilterText = q;
        if (Environment.GetEnvironmentVariable("RADAR_UI_HISTORY") == "1") vm.DebugShowHistory();
        if (Environment.GetEnvironmentVariable("RADAR_UI_REPLAY") == "1") vm.DebugShowReplay();
        if (Environment.GetEnvironmentVariable("RADAR_UI_HIDDEN") == "1") vm.ToggleGroup(vm.Legend[3]);
        foreach (var k in w.GetVisualDescendants().OfType<Control>().Where(p => p.Classes.Contains("telk") || p.Classes.Contains("telks") || p.Classes.Contains("telb") || p.Classes.Contains("tel") || p.Parent is Panel { Classes: var pc } && pc.Contains("telkicon"))) k.Transitions = null;
        if (Environment.GetEnvironmentVariable("RADAR_UI_BUMP") == "1") LegendCounter.Animate = true;
        if (Environment.GetEnvironmentVariable("RADAR_UI_TEL") is { Length: > 0 } tel)
            vm.DebugSetTelemetry(tel.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray());
        w.HiddenChip.Transitions = null; // снимок конечного положения, без анимации
        if (Environment.GetEnvironmentVariable("RADAR_UI_MODE") is { Length: > 0 } um)
        {
            // переключение режима как из меню; воспроизведение идёт несколько секунд реального времени
            vm.Mode = um.StartsWith("history") ? AppMode.History : um == "stats" ? AppMode.Stats : AppMode.Live;
            // период статистики для снимка: RADAR_UI_STATS=all | custom:YYYY-MM-DD:YYYY-MM-DD | ключ периода
            if (um == "stats" && Environment.GetEnvironmentVariable("RADAR_UI_STATS") is { Length: > 0 } statsPeriod)
            {
                var st0 = DateTime.UtcNow.AddSeconds(20);
                while (vm.Stats.Periods.Count == 0 && DateTime.UtcNow < st0) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
                var parts = statsPeriod.Split(':');
                if (parts[0] == "custom" && parts.Length == 3)
                {
                    vm.Stats.CustomFrom = DateTime.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    vm.Stats.CustomTo = DateTime.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                }
                void Switch(string key)
                {
                    int before = vm.Stats.AppliedCount;
                    var sw0 = System.Diagnostics.Stopwatch.StartNew();
                    vm.Stats.Period = vm.Stats.Periods.FirstOrDefault(p => p.Key == key) ?? vm.Stats.Period;
                    while (vm.Stats.AppliedCount == before && sw0.Elapsed.TotalSeconds < 300) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
                    Console.WriteLine($"stats period {vm.Stats.PeriodTitle}: {vm.Stats.RangeText} · {sw0.ElapsedMilliseconds} мс");
                }
                // замер переключения: туда-обратно через разные периоды, затем итоговый
                if (Environment.GetEnvironmentVariable("RADAR_UI_STATS_BENCH") == "1") foreach (var k in new[] { "30d", "7d", "all", "7d" }) Switch(k);
                Switch(parts[0]);
            }
            var until = DateTime.UtcNow.AddSeconds(double.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_MODE_WAIT"), out var ws) ? ws : 6);
            while (DateTime.UtcNow < until) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
            Console.WriteLine($"mode {vm.Mode}: tag {vm.ModeTag}, events {vm.Events.Count}, feed {vm.Feed.Count}, clock {vm.ReplayClock} {vm.ReplayDate}, range {vm.ReplayRange}, stats {vm.Stats.BusyText} {vm.Stats.Kpis.Count} kpi, regions {vm.Stats.TopRegions.Count}, days {vm.Stats.Days.Count}, empty {vm.Stats.EmptyText}");
            void Pump(double sec) { var u = DateTime.UtcNow.AddSeconds(sec); while (DateTime.UtcNow < u) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); } }
            if (um == "history-seek")
            {
                Console.WriteLine($"months: {string.Join(", ", vm.HistoryMonths.Take(4).Select(m => m.Title + (m.Current ? "*" : "")))} … всего {vm.HistoryMonths.Count}");
                vm.SeekHistory(0.5);
                Pump(4);
                Console.WriteLine($"seek 0.5: clock {vm.ReplayClock} {vm.ReplayDate}, progress {vm.ReplayProgress:F3}, events {vm.Events.Count}, busy {vm.HistoryBusy}");
                var m = vm.HistoryMonths.Skip(3).First();
                vm.SelectHistoryMonth(m.Month);
                Pump(8);
                Console.WriteLine($"month {vm.HistoryMonthTitle}: range {vm.ReplayRange}, clock {vm.ReplayClock} {vm.ReplayDate}, events {vm.Events.Count}, hist {vm.ReplayHistogram?.Length}");
                vm.SeekHistory(0.8);
                Pump(5);
                Console.WriteLine($"seek 0.8: clock {vm.ReplayClock} {vm.ReplayDate}, progress {vm.ReplayProgress:F3}, events {vm.Events.Count}");
                vm.ReplayPaused = true;
                vm.StepHistory(-1);
                Pump(3);
                Console.WriteLine($"step -1h paused: clock {vm.ReplayClock}, paused {vm.ReplayPaused}");
                var root = (Control)w;
                root.Measure(new Avalonia.Size(W, H)); root.Arrange(new Avalonia.Rect(0, 0, W, H));
                w.MonthButtonClick(null, new RoutedEventArgs());
                Pump(0.8);
                root.Measure(new Avalonia.Size(W, H)); root.Arrange(new Avalonia.Rect(0, 0, W, H));
                foreach (var c in new Control[] { w.ReplayPanel, w.ReplayTimeline, w.MonthPicker })
                    Console.WriteLine($"  {c.Name}: {c.Bounds} visible {c.IsVisible} opacity {c.Opacity} in overlays {c.TranslatePoint(default, w.Overlays)}");
            }
            if (um == "stats" && Environment.GetEnvironmentVariable("RADAR_UI_STATS_PERIOD") is { Length: > 0 } sp)
            {
                vm.Stats.Period = vm.Stats.Periods.FirstOrDefault(p => p.Key == sp) ?? vm.Stats.Period;
                var u2 = DateTime.UtcNow.AddSeconds(double.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_MODE_WAIT"), out var ws2) ? ws2 : 6);
                while (DateTime.UtcNow < u2) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
                Console.WriteLine($"stats {vm.Stats.PeriodTitle}: busy {vm.Stats.Busy} {vm.Stats.BusyText}, kpi {string.Join(" | ", vm.Stats.Kpis.Select(k => k.Title + " " + k.Value))}");
                Console.WriteLine($"  regions: {string.Join(", ", vm.Stats.TopRegions.Take(5).Select(r => r.Title + " " + r.Value))}");
                Console.WriteLine($"  places: {string.Join(", ", vm.Stats.TopPlaces.Take(5).Select(r => r.Title + " " + r.Value))}");
                Console.WriteLine($"  kinds: {string.Join(", ", vm.Stats.Kinds.Take(5).Select(r => r.Title + " " + r.Value + " " + r.Sub))}");
                Console.WriteLine($"  sectors: {string.Join(", ", vm.Stats.SectorBars.Select(r => r.Title + " " + r.Value))}; launch: {string.Join(", ", vm.Stats.LaunchAreas.Take(4).Select(r => r.Title + " " + r.Value))}");
                Console.WriteLine($"  week: {vm.Stats.WeekHourCaption}; days {vm.Stats.Days.Count}; heat {w.Map.Heat?.Count}");
                if (Environment.GetEnvironmentVariable("RADAR_UI_STATS_REGION") is { Length: > 0 } sr)
                {
                    vm.StatsPickRegion(sr);
                    Pump(4);
                    Console.WriteLine($"region {vm.Stats.RegionTitle}: kpi {string.Join(" | ", vm.Stats.Kpis.Take(2).Select(k => k.Title + " " + k.Value))}; places {string.Join(", ", vm.Stats.TopPlaces.Take(4).Select(r => r.Title + " " + r.Value))}");
                }
                var root2 = (Control)w;
                for (int pass = 0; pass < 3; pass++)
                {
                    root2.InvalidateMeasure();
                    root2.Measure(new Avalonia.Size(W, H)); root2.Arrange(new Avalonia.Rect(0, 0, W, H));
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
                Console.WriteLine($"  board visible {w.StatsBoard.IsVisible}/{w.StatsBoard.IsEffectivelyVisible} parentVis {(w.StatsBoard.Parent as Control)?.IsEffectivelyVisible} vm.IsStats {vm.IsStats} dc {w.StatsBoard.DataContext?.GetType().Name}");
                foreach (var c in w.StatsBoard.GetVisualDescendants().OfType<Control>()
                             .Where(c => c is Radar.Views.GlassPanel or Radar.Views.DaysChart or Radar.Views.WeekHourChart or Radar.Views.SectorRose or ScrollViewer or ComboBox)
                             .Where(c => c.IsVisible && c.GetVisualAncestors().OfType<Control>().TakeWhile(a => a != w.StatsBoard).All(a => a.IsVisible)).Take(30))
                {
                    var p0 = c.TranslatePoint(default, w.StatsBoard) ?? default;
                    var title = c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
                    Console.WriteLine($"  [{c.GetType().Name}] x{p0.X:0} y{p0.Y:0} {c.Bounds.Width:0}×{c.Bounds.Height:0} {title}");
                }
            }
            if (um == "history-back")
            {
                vm.Mode = AppMode.History;
                until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
                Console.WriteLine($"history: events {vm.Events.Count}, clock {vm.ReplayClock}");
                vm.Mode = AppMode.Live;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"back to live: tag {vm.ModeTag}, events {vm.Events.Count}, simulated {Clock.IsSimulated}");
            }
        }
        if (Environment.GetEnvironmentVariable("RADAR_UI_MODEMENU") == "1")
        {
            foreach (var c in w.ModeMenu.GetVisualDescendants().OfType<Control>().Append(w.ModeMenu)) c.Transitions = null;
            if (w.ModeMenu.Children[0] is Control mc) mc.Transitions = null;
            w.ModeButtonClick(null, new RoutedEventArgs());
        }

        var host = (Avalonia.Controls.Control)w;
        host.Measure(new Avalonia.Size(W, H));
        host.Arrange(new Avalonia.Rect(0, 0, W, H));
        if (Environment.GetEnvironmentVariable("RADAR_DEMO_CORPUS") is { Length: > 0 } corpus)
        {
            var (ce, ct) = ReplayCorpus(corpus);
            w.Map.SetEvents(ce, ct);
            if (Environment.GetEnvironmentVariable("RADAR_UI_FEED") == "1")
            {
                // без окна виртуализирующая панель не получает видимую область (её сообщает менеджер раскладки)
                // и создаёт одну карточку — для снимка лента без виртуализации
                w.EventsList.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
                vm.DebugSetEvents(ce);
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(corpus));
                vm.DebugFillSources(doc.RootElement.EnumerateArray()
                    .Where(m => (m.GetProperty("text").GetString() ?? "").Length > 0)
                    .Select(m => new RawMessage(m.GetProperty("ch").GetString() ?? "", m.GetProperty("id").GetInt64(),
                        DateTimeOffset.Parse(m.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture), m.GetProperty("text").GetString()!))
                    .OrderBy(m => m.Time).ToList());
                // лента и каналы заполнены — снимок «на связи», без плашки загрузки
                vm.DebugSetOnline();
                // лента в модели пересылает на карту события без треков архива — на снимке карта та же, что без ленты
                w._vm!.MapEventsChanged -= w.OnMapEvents;
                w.Map.SetEvents(ce, ct);
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_TAB"), out var tab)) w.SideTabs.SelectedIndex = tab;
            if (Environment.GetEnvironmentVariable("RADAR_UI_REGIONS") is { Length: > 0 } rg)
            {
                // «belgorod», «belgorod|валуйский» (район) или «Шебекино@50.413,36.925» (пункт)
                foreach (var id in rg.Split(','))
                {
                    if (id.Split('@') is [var pn, var ll] && ll.Split(';') is [var la, var lo, ..] parts)
                        vm.ToggleArea(new WatchArea { Kind = WatchArea.PlaceKind, Id = id, Name = pn, Region = "belgorod",
                            Lat = double.Parse(la, System.Globalization.CultureInfo.InvariantCulture), Lon = double.Parse(lo, System.Globalization.CultureInfo.InvariantCulture),
                            RadiusKm = parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0 });
                    else vm.TogglePickedRegion(id);
                }
                if (Environment.GetEnvironmentVariable("RADAR_UI_RSEARCH") is { Length: > 0 } rs) vm.RegionSearch = rs;

                if (Environment.GetEnvironmentVariable("RADAR_UI_PICK") == "1") vm.PickingOnMap = true;
                // RADAR_UI_POINT=lat,lon — своя точка (режим «ТОЧКА»)
                if (Environment.GetEnvironmentVariable("RADAR_UI_POINT") is { Length: > 0 } upt && upt.Split(',') is [var pla, var plo])
                {
                    vm.PickPointMode = true;
                    vm.TogglePickedPoint(new GeoPoint(double.Parse(pla, System.Globalization.CultureInfo.InvariantCulture), double.Parse(plo, System.Globalization.CultureInfo.InvariantCulture)));
                }
                if (Environment.GetEnvironmentVariable("RADAR_UI_MINE") == "1") vm.OnlyMine = true;
            }
        }
        if (Environment.GetEnvironmentVariable("RADAR_UI_OFFLINE") is { Length: > 0 } offMin && double.TryParse(offMin, System.Globalization.CultureInfo.InvariantCulture, out var om))
        {
            // значение — сколько минут нет данных
            vm.DebugSetOffline();
            w.Map.SignalLost = true;
            w.Map.SignalLostSince = DateTimeOffset.UtcNow.AddMinutes(-om);
        }
        w.Map.AdvanceAnimations(TimeSpan.FromSeconds(6));
        // повторная раскладка: SizeChanged заголовка переключает вариант счётчиков
        w.UpdateTelemetryLayout(W);
        host.Measure(new Avalonia.Size(W, H));
        host.Arrange(new Avalonia.Rect(0, 0, W, H));
        w.FitOverlays();
        // виртуализированная лента создаёт элементы по одному проходу раскладки — несколько проходов, пока не заполнится
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (var c in host.GetSelfAndVisualDescendants().OfType<Avalonia.Layout.Layoutable>()) c.InvalidateMeasure();
            host.Measure(new Avalonia.Size(W, H));
            host.Arrange(new Avalonia.Rect(0, 0, W, H));
        }
        // выбор пункта по подписи: RADAR_UI_PICKLABEL=<название> — навести (и кликнуть, если RADAR_UI_PICKCLICK=1)
        if (Environment.GetEnvironmentVariable("RADAR_UI_PICKLABEL") is { Length: > 0 } pln)
        {
            // подложка (а с ней — подписи) строится при первой отрисовке
            using (var pre = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(W, H))) pre.Render(host);
            var hit = w.Map.DebugLabels.FirstOrDefault(l => l.Name == pln);
            Console.WriteLine($"подписей на подложке: {w.Map.DebugLabels.Count}; «{pln}»: {(hit.Name != null ? "есть" : "нет")}; примеры: {string.Join(", ", w.Map.DebugLabels.Take(12).Select(l => l.Name))}");
            if (hit.Name != null)
            {
                var at = w.Map.DebugWorldToScreen(hit.World.Center);
                if (Environment.GetEnvironmentVariable("RADAR_UI_PICKCLICK") == "1")
                {
                    w.Map.DebugClick(at);
                    Console.WriteLine($"зоны после клика: {string.Join("; ", vm.WatchAreas.Select(a => $"{a.Kind}:{a.Name} ({a.Region})"))}");
                }
                w.Map.DebugHover(at);
            }
        }
        // RADAR_UI_CENTER=lat,lon,zoom — вид карты
        if (Environment.GetEnvironmentVariable("RADAR_UI_CENTER") is { Length: > 0 } cz && cz.Split(',') is [var cla, var clo, var czz])
            w.Map.CenterOn(new GeoPoint(double.Parse(cla, System.Globalization.CultureInfo.InvariantCulture), double.Parse(clo, System.Globalization.CultureInfo.InvariantCulture)), double.Parse(czz, System.Globalization.CultureInfo.InvariantCulture));
        // RADAR_UI_HOVER=<пункт> — наведение на отметку события: всплывающая карточка (снимок для README)
        if (Environment.GetEnvironmentVariable("RADAR_UI_HOVER") is { Length: > 0 } hov)
        {
            using (var pre = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(W, H))) pre.Render(host);
            Console.WriteLine(w.Map.DebugHoverEvent(hov));
        }
        // RADAR_UI_DRAG=<пункт>:<км>[:release] — перетаскивание края круга
        if (Environment.GetEnvironmentVariable("RADAR_UI_DRAG") is { Length: > 0 } dr && dr.Split(':') is [var dn, var dk, ..] dp)
        {
            Console.WriteLine(w.Map.DebugDragRadius(dn, double.Parse(dk, System.Globalization.CultureInfo.InvariantCulture), dp.Length > 2));
            Console.WriteLine($"радиусы: {string.Join("; ", vm.WatchAreas.Where(a => a.Kind == WatchArea.PlaceKind).Select(a => $"{a.Name} {a.RadiusKm} км"))}");
        }
        // прокрутка открытой вкладки боковой панели (проверка нижних секций настроек)
        if (double.TryParse(Environment.GetEnvironmentVariable("RADAR_UI_SCROLL"), System.Globalization.CultureInfo.InvariantCulture, out var sy))
        {
            // окно не показано (IsEffectivelyVisible ложно) — берём самый длинный прокручиваемый список открытой вкладки
            if (w.SideTabs.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.Viewport.Height > 300).MaxBy(v => v.Extent.Height) is { } sv)
            {
                sv.Offset = new Avalonia.Vector(0, sy);
                Console.WriteLine($"scroll: {sv.Offset.Y:0} из {sv.Extent.Height - sv.Viewport.Height:0}");
            }
            foreach (var c in host.GetSelfAndVisualDescendants().OfType<Avalonia.Layout.Layoutable>()) { c.InvalidateMeasure(); c.InvalidateArrange(); }
            host.Measure(new Avalonia.Size(W, H));
            host.Arrange(new Avalonia.Rect(0, 0, W, H));
        }
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(W, H));
        bmp.Render(host);
        bmp.Render(host);
#pragma warning disable CS0618
        using (var fs = File.Create(path)) bmp.Save(fs);
#pragma warning restore CS0618
        if (Environment.GetEnvironmentVariable("RADAR_UI_GEOM") == "1")
            foreach (var (n, c) in new (string, Control)[] { ("overlays", w.Overlays), ("верх", w.TopStack), ("скрыто", w.HiddenChip), ("история", w.HistoryPanel), ("архив", w.ReplayPanel), ("легенда", w.LegendPanel), ("кнопка легенды", w.LegendButton), ("панель", w.Sidebar) })
                if (c.IsVisible) Console.WriteLine($"{n}: {c.TranslatePoint(default, host)} {c.Bounds.Size} max={c.MaxHeight:0} m={c.Margin}{(c.Classes.Contains("compact") ? " compact" : "")}");
        Console.WriteLine($"пустая лента: {vm.Empty} «{vm.EmptyTitle}» / {vm.EmptyAction}");
        // для проверки снимка (ToolsScriptTests): сколько карточек ленты отрисовано и видна ли плашка загрузки
        Console.WriteLine($"лента: событий {vm.Events.Count}, карточек {w.EventsList.GetVisualDescendants().OfType<ListBoxItem>().Count()}; " +
                          $"подключение: {(vm.IsConnecting ? "да" : "нет")}; загрузка: {(w.HistoryPanel.IsVisible ? "да" : "нет")}");
        Console.WriteLine($"ui rendered; счётчики: {(w.Telemetry.IsVisible ? "полные" : w.TelemetryCompact.IsVisible ? "компактные" : "скрыты")}; " +
                          $"ширины: марка {w.Brand.DesiredSize.Width:0}, режим {w.ModePanel.DesiredSize.Width:0}, полные {w.Telemetry.DesiredSize.Width:0}, " +
                          $"компактные {w.TelemetryCompact.DesiredSize.Width:0}, кнопки {w.MapButtons.DesiredSize.Width + w.WinButtons.DesiredSize.Width:0} из {W}");
    }

    public static void RenderDemo(string path)
    {
        Radar.Services.FrontLineSource.LoadForAudit();
        var map = new Radar.Map.MapControl { Width = 1100, Height = 800, ShowFrontLine = Environment.GetEnvironmentVariable("RADAR_DEMO_FRONT") != "0" };
        map.Measure(new Avalonia.Size(1100, 800));
        map.Arrange(new Avalonia.Rect(0, 0, 1100, 800));
        var c = Environment.GetEnvironmentVariable("RADAR_DEMO_VIEW")?.Split(',');
        map.CenterOn(c is { Length: 3 } ? new(double.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture)) : new(52.3, 34.8),
            c is { Length: 3 } ? double.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture) : 7.6);
        if (Environment.GetEnvironmentVariable("RADAR_DEMO_CORPUS") is { Length: > 0 } corpus)
        {
            // архив каналов (JSON, как для --engine-audit) — обстановка на момент последнего сообщения
            var (ce, ct) = ReplayCorpus(corpus);
            map.SetEvents(ce, ct);
        }
        else if (Environment.GetEnvironmentVariable("RADAR_DEMO_PEAK") == "1")
        {
            var (pe, pt) = ReplayPeak();
            map.SetEvents(pe, pt);
        }
        else if (Environment.GetEnvironmentVariable("RADAR_DEMO_LAUNCH") is { Length: > 0 } launchText)
        {
            // пуск по тексту сообщения: веер возможных направлений строится по типу средства (отладка дальности)
            var engine = new AlertEngine(new AppSettings { OnlineGeocoding = false }, new System.Net.Http.HttpClient(), new Geocoder(new System.Net.Http.HttpClient(), new NominatimThrottle()));
            engine.ApplyBatch([new RawMessage("demo", 1, DateTimeOffset.UtcNow, launchText.Replace("\\n", "\n"))]);
            var (ce, ct) = (engine.Snapshot(), engine.TrackSnapshot().ToList());
            Console.WriteLine($"events {ce.Count}: " + string.Join("; ", ce.Select(e => $"{e.Status}/{e.Kind}/origin={e.Origin}")));
            map.SetEvents(ce, ct);
            map.AdvanceAnimations(TimeSpan.FromSeconds(6));
            if (c is { Length: 3 }) map.CenterOn(new(double.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture)), double.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture));
            var origin = ce.FirstOrDefault()?.Origin;
            if (origin is { } og && Environment.GetEnvironmentVariable("RADAR_DEMO_ORIGIN_VIEW") == "1") map.CenterOn(og, double.Parse(c?[2] ?? "7", System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (Environment.GetEnvironmentVariable("RADAR_DEMO_TIP") == "1")
        {
            // проверка всплывающих карточек: короткая, с длинными строками, с отбоем части и историей
            map.CenterOn(new(56.5, 39.0), 6.2);
            map.DebugTips = TipSamples();
            map.SetEvents(map.DebugTips, []);
        }
        else
        {
            var (e, t) = DemoScenario();
            map.SetEvents(e, t);
        }
        map.AdvanceAnimations(TimeSpan.FromSeconds(6));
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1100, 800));
        Avalonia.Visual root = map;
        if (Environment.GetEnvironmentVariable("RADAR_DEMO_GLASS") == "1")
        {
            // проверка стекла: карта и стеклянная панель поверх неё
            var grid = new Avalonia.Controls.Grid { Width = 1100, Height = 800 };
            grid.Children.Add(map);
            grid.Children.Add(new GlassPanel { Width = 300, Height = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Avalonia.Thickness(100, 100, 0, 0) });
            grid.Measure(new Avalonia.Size(1100, 800));
            grid.Arrange(new Avalonia.Rect(0, 0, 1100, 800));
            root = grid;
        }
        bmp.Render(root);
        // второй кадр: подложка уже знает места подписей верхнего слоя и освобождает их
        bmp.Render(root);
        if (Environment.GetEnvironmentVariable("RADAR_DEMO_SPIDER") == "1")
        {
            // щелчок по самой крупной группе на экране — проверка веера
            var groups = map.DebugGroups().Where(g => g.At.X is > 0 and < 1100 && g.At.Y is > 0 and < 800).OrderByDescending(g => g.Count).ToList();
            foreach (var g in groups) Console.WriteLine($"group {g.Count} at {g.At.X:F0},{g.At.Y:F0}");
            if (groups.Count > 0)
            {
                map.DebugClick(groups[0].At);
                Console.WriteLine($"spider open: {map.DebugSpiderOpen}");
                System.Threading.Thread.Sleep(400);
                bmp.Render(map);
                if (Environment.GetEnvironmentVariable("RADAR_DEMO_SPIDER_PICK") == "1")
                {
                    // щелчок по верхнему элементу кольца: выбор события, веер остаётся открытым
                    map.DebugClick(new Avalonia.Point(groups[0].At.X, groups[0].At.Y - 38));
                    Console.WriteLine($"picked: {map.Selected?.Key ?? "-"}; spider open: {map.DebugSpiderOpen}");
                    map.DebugClick(new Avalonia.Point(5, 5));
                    Console.WriteLine($"after outside click spider open: {map.DebugSpiderOpen}");
                }
            }
        }
        #pragma warning disable CS0618
        using (var fs = File.Create(path)) bmp.Save(fs);
#pragma warning restore CS0618
        Console.WriteLine($"labels: {map.LastLabelStats}; city labels: {Radar.Map.BasemapRenderer.LastCityLabels}; top-layer keep-out: {Radar.Map.BasemapRenderer.LastHardOut}");
        if (Environment.GetEnvironmentVariable("RADAR_DEMO_GEOM") == "1")
            foreach (var line in map.DebugGeometry()) Console.WriteLine(line);
    }

    /// <summary>
    /// Нагрузочный замер карты: прогоняет недельный архив, берёт момент с максимумом активных событий
    /// и меряет время кадра на разных масштабах — отдельно статичный кадр и кадр во время панорамирования.
    /// </summary>
    /// <summary>Состояние движка в момент максимума активных событий недельного архива.</summary>
    private static (IReadOnlyList<AlertEvent> Events, IReadOnlyList<Radar.Models.Track> Tracks) ReplayCorpus(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var msgs = new List<RawMessage>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var text = m.GetProperty("text").GetString() ?? "";
            if (text.Length == 0) continue;
            msgs.Add(new RawMessage(m.GetProperty("ch").GetString() ?? "", m.GetProperty("id").GetInt64(),
                DateTimeOffset.Parse(m.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture), text));
        }
        msgs.Sort((a, b) => a.Time.CompareTo(b.Time));
        // RADAR_UI_FREEZE=1 — «сейчас» = время последнего сообщения архива (треки активны, как в момент события)
        if (Environment.GetEnvironmentVariable("RADAR_UI_FREEZE") == "1" && msgs.Count > 0)
        {
            Clock.StartSimulation(msgs[^1].Time.AddMinutes(2), 1);
            Clock.SetPaused(true);
        }
        var settings = new AppSettings { OnlineGeocoding = false };
        using var http = new System.Net.Http.HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in engine.ReplaySync(msgs)) { }
        return (engine.Snapshot(), engine.TrackSnapshot().Where(t => t.IsMoving || t.Points.Count > 1).ToList());
    }

    /// <summary>Замер простоя: обстановка из архива без опроса и без изменений, анимации появления пропущены.</summary>
    internal void ShowIdleScenario(string corpus)
    {
        Environment.SetEnvironmentVariable("RADAR_UI_FREEZE", "1");
        var (events, tracks) = ReplayCorpus(corpus);
        _vm!.MapEventsChanged -= OnMapEvents;
        _vm.DebugSetEvents(events);
        _vm.DebugSetOnline();
        Map.SignalLost = false;
        Map.SetEvents(events, tracks);
        Map.AdvanceAnimations(TimeSpan.FromSeconds(6));
    }

    private static (IReadOnlyList<AlertEvent> Events, IReadOnlyList<Radar.Models.Track> Tracks) ReplayPeak()
    {
        var msgs = ReplaySource.Load();
        var settings = new AppSettings { OnlineGeocoding = false };
        using var http = new System.Net.Http.HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        int best = 0, bestAt = 0, i = 0;
        foreach (var n in engine.ReplaySync(msgs))
        {
            if (n > best) { best = n; bestAt = i; }
            i++;
        }
        engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in engine.ReplaySync(msgs.Take(bestAt + 1).ToList())) { }
        return (engine.Snapshot(), engine.TrackSnapshot().Where(t => t.IsMoving).ToList());
    }

    public static void BenchMap()
    {
        var msgs = ReplaySource.Load();
        var settings = new AppSettings { OnlineGeocoding = false };
        using var http = new System.Net.Http.HttpClient();
        var engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        int best = 0, bestAt = 0, i = 0;
        foreach (var n in engine.ReplaySync(msgs))
        {
            if (n > best) { best = n; bestAt = i; }
            i++;
        }
        // повторный прогон до пика
        engine = new AlertEngine(settings, http, new Geocoder(http, new NominatimThrottle()));
        foreach (var _ in engine.ReplaySync(msgs.Take(bestAt + 1).ToList())) { }
        var events = engine.Snapshot();
        var tracks = engine.TrackSnapshot().Where(t => t.IsMoving).ToList();
        Console.WriteLine($"peak at {msgs[bestAt].Time:u}: events={events.Count} tracks={tracks.Count} moving");

        foreach (var g in Radar.Map.Basemap.Features.GroupBy(f => (f.Kind, f.Lod)).OrderBy(g => g.Key))
            Console.WriteLine($"   {g.Key.Kind,-14} lod{g.Key.Lod}: {g.Count(),5} features, {g.Sum(f => f.Parts.Sum(p => p.Length)),8} vertices");
        const int W = 1600, H = 1000;
        var map = new Radar.Map.MapControl { Width = W, Height = H };
        map.Measure(new Avalonia.Size(W, H));
        map.Arrange(new Avalonia.Rect(0, 0, W, H));
        map.SetEvents(events, tracks);
        map.AdvanceAnimations(TimeSpan.FromSeconds(6));
        Radar.Map.MapControl.Profiling = Environment.GetEnvironmentVariable("BENCH_PROF") == "1";
        if (Radar.Map.MapControl.Profiling) Radar.Map.BasemapRenderer.Phase = new double[2];
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(W, H));
        var sw = new System.Diagnostics.Stopwatch();
        if (Environment.GetEnvironmentVariable("BENCH_LOAD") == "1")
        {
            // подгрузка каналов: на каждом кадре приходит обновление обстановки (SetEvents с новыми снимками)
            Radar.Map.MapControl.ForceBackgroundBase = true;
            Radar.Map.MapControl.Profiling = Environment.GetEnvironmentVariable("BENCH_PROF") == "1";
            map.CenterOn(new GeoPoint(50.6, 36.6), double.Parse(Environment.GetEnvironmentVariable("BENCH_Z") ?? "7.5", System.Globalization.CultureInfo.InvariantCulture));
            bmp.Render(map); System.Threading.Thread.Sleep(400); bmp.Render(map);
            var plain = new List<double>(); var upd = new List<double>(); var setEv = new List<double>();
            for (int f = 0; f < 40; f++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bmp.Render(map);
                plain.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            if (Radar.Map.MapControl.Profiling)
            {
                var pm0 = Radar.Map.MapControl.ProfMs; var px0 = Radar.Map.MapControl.ProfX_;
                Console.WriteLine($"plain(40): base {pm0[0]/40:0.0} regions {pm0[1]/40:0.0} zones {pm0[2]/40:0.0} tracks {pm0[3]/40:0.0} markers {pm0[4]/40:0.0} heads {pm0[5]/40:0.0} hud {pm0[6]/40:0.0}");
                Array.Clear(pm0); Array.Clear(px0);
            }
            for (int f = 0; f < 40; f++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var ev = engine.Snapshot(); var tr = engine.TrackSnapshot().Where(t => t.IsMoving).ToList();
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                map.SetEvents(ev, tr);
                setEv.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
                bmp.Render(map);
                upd.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            if (Radar.Map.MapControl.Profiling)
            {
                var pm = Radar.Map.MapControl.ProfMs; var px = Radar.Map.MapControl.ProfX_;
                Console.WriteLine($"update(40): base {pm[0]/40:0.0} regions {pm[1]/40:0.0} zones {pm[2]/40:0.0} tracks {pm[3]/40:0.0} markers {pm[4]/40:0.0} heads {pm[5]/40:0.0} hud {pm[6]/40:0.0}");
            }
            {
                var vmx = new MainViewModel(new AppSettings { OnlineGeocoding = false });
                var evx = engine.Snapshot();
                vmx.DebugRefreshMs(evx);
                var rs = new List<double>();
                for (int f = 0; f < 30; f++) rs.Add(vmx.DebugRefreshMs(evx));
                rs.Sort();
                Console.WriteLine($"vm.Refresh with {evx.Count} events: median {rs[15]:0.0} ms, p90 {rs[27]:0.0} ms, max {rs[^1]:0.0} ms");
            }
            plain.Sort(); upd.Sort(); setEv.Sort();
            Console.WriteLine($"load: plain frame best {plain[0]:0.0} p25 {plain[10]:0.0} median {plain[20]:0.0} p90 {plain[36]:0.0} max {plain[^1]:0.0} | frame+update best {upd[0]:0.0} p25 {upd[10]:0.0} median {upd[20]:0.0} p90 {upd[36]:0.0} max {upd[^1]:0.0} | SetEvents median {setEv[20]:0.0} max {setEv[^1]:0.0}");
            return;
        }
        if (Environment.GetEnvironmentVariable("BENCH_DRAG") == "1")
        {
            // кадры во время перетаскивания: время и снимки первого/последнего кадра
            Radar.Map.MapControl.ForceBackgroundBase = true;
            Radar.Map.MapControl.Profiling = Environment.GetEnvironmentVariable("BENCH_PROF") == "1";
            foreach (var z in new[] { 6.1, 7.5, 9.5 })
            {
                map.CenterOn(new GeoPoint(50.6, 36.6), z);
                bmp.Render(map);
                System.Threading.Thread.Sleep(400); bmp.Render(map);
                var ts = new List<double>();
                double stepX = double.Parse(Environment.GetEnvironmentVariable("BENCH_STEP") ?? "14", System.Globalization.CultureInfo.InvariantCulture);
                map.RetroNoise = Environment.GetEnvironmentVariable("BENCH_NORETRO") != "1";
                for (int f = 0; f < 30; f++)
                {
                    map.DebugDragBy(new Avalonia.Point(800, 500), new Avalonia.Vector(-stepX, -stepX * 0.4));
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bmp.Render(map);
                    ts.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    if (f == 0 || f == 29) { using var fs = File.Create($"/tmp/drag_{z}_{f}.png"); bmp.Save(fs); }
                }
                if (Radar.Map.MapControl.Profiling)
                {
                    var pm = Radar.Map.MapControl.ProfMs; var px = Radar.Map.MapControl.ProfX_;
                    Console.WriteLine($"  layers/frame: base {pm[0]/30:0.0} regions {pm[1]/30:0.0} zones {pm[2]/30:0.0} tracks {pm[3]/30:0.0} markers {pm[4]/30:0.0} heads {pm[5]/30:0.0} hud {pm[6]/30:0.0} | vignette {px[0]/30:0.0} signal {px[1]/30:0.0} spider {px[2]/30:0.0}");
                    Array.Clear(pm); Array.Clear(px);
                }
                map.DebugDragEnd();
                ts.Sort();
                Console.WriteLine($"drag zoom {z}: median {ts[15]:0.0} ms, p90 {ts[27]:0.0} ms, max {ts[^1]:0.0} ms");
            }
            Radar.Map.MapControl.ForceBackgroundBase = false;
            return;
        }
        foreach (var (lat, lon, z) in new[] { (50.5, 38.0, 4.5), (50.5, 38.0, 6.1), (51.0, 37.0, 7.5), (50.6, 36.6, 9.5), (55.7, 37.6, 11.0) })
        {
            map.CenterOn(new GeoPoint(lat, lon), z);
            bmp.Render(map); // прогрев: подложка + кэши
            if (Radar.Map.BasemapRenderer.Phase is { } ph0) Array.Clear(ph0);
            sw.Restart();
            for (int f = 0; f < 5; f++) { map.InvalidateBase(); bmp.Render(map); }
            double baseMs = sw.Elapsed.TotalMilliseconds / 5;
            System.Threading.Thread.Sleep(250); bmp.Render(map); // кроссфейд после перерисовки подложки завершён
            Console.WriteLine($"   basemap re-render ≈ {baseMs:0.0} ms (labels {Radar.Map.MapControl.ProfMs[7] / 6:0.0} ms)");
            if (Radar.Map.BasemapRenderer.Phase is { } ph)
            {
                string[] nm = ["geometry", "labels"];
                Console.WriteLine("      " + string.Join(" ", nm.Select((n, k) => $"{n} {ph[k] / 5:0.0}")));
                Array.Clear(ph);
            }
            if (Radar.Map.MapControl.Profiling) Array.Clear(Radar.Map.MapControl.ProfMs);
            Radar.Map.MapControl.AreasMs = 0; Radar.Map.MapControl.AreasCount = 0;
            for (int f = 0; f < 5; f++) { map.InvalidateAreas(); bmp.Render(map); }
            Console.WriteLine($"   areas re-render ≈ {Radar.Map.MapControl.AreasMs / Math.Max(1, Radar.Map.MapControl.AreasCount):0.0} ms");
            System.Threading.Thread.Sleep(250); bmp.Render(map); // кроссфейд слоя зон завершён
            const int frames = 40;
            double Median(Action step)
            {
                var ts = new double[frames];
                for (int f = 0; f < frames; f++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    step();
                    bmp.Render(map);
                    ts[f] = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
                Array.Sort(ts);
                return ts[frames / 2];
            }
            void Dump(string n)
            {
                if (!Radar.Map.MapControl.Profiling) return;
                var pm = Radar.Map.MapControl.ProfMs;
                Console.WriteLine($"   {n}: base {pm[0] / frames:0.0} regions {pm[1] / frames:0.0} zones {pm[2] / frames:0.0} tracks {pm[3] / frames:0.0} markers {pm[4] / frames:0.0} heads {pm[5] / frames:0.0} hud {pm[6] / frames:0.0}");
                var px = Radar.Map.MapControl.ProfX_;
                Console.WriteLine($"      hud: vignette {px[0] / frames:0.0} signal {px[1] / frames:0.0} spider+overlay {px[2] / frames:0.0}");
                Array.Clear(pm); Array.Clear(px);
            }
            double still = Median(() => { });
            Dump("still");
            // панорамирование: каждый кадр сдвиг на 6 px
            double pan = Median(() => map.PanBy(new Avalonia.Vector(6, 3)));
            Dump("pan");
            // плавный зум колесом
            int zf = 0;
            double zoom = Median(() => map.ZoomBy(zf++ % 2 == 0 ? 0.05 : -0.05, animate: false));
            Dump("wheel");
            Console.WriteLine($"zoom {z,4:0.0}: still {still,6:0.0} ms | pan {pan,6:0.0} ms | wheel {zoom,6:0.0} ms");
        }
        if (Environment.GetEnvironmentVariable("BENCH_EDGE") is "1" or "2")
        {
            Radar.Map.MapControl.ForceBackgroundBase = Environment.GetEnvironmentVariable("BENCH_EDGE") == "1";
            map.CenterOn(new GeoPoint(50.5, 38.0), 7.5);
            bmp.Render(map);
            for (int step = 0; step < 12; step++)
            {
                if (step % 3 == 2) map.ZoomBy(-0.6, animate: false); else map.PanBy(new Avalonia.Vector(260, 140));
                bmp.Render(map);
                using var ms = new MemoryStream();
#pragma warning disable CS0618
                bmp.Save(ms);
#pragma warning restore CS0618
                ms.Position = 0;
                using var sk = SkiaSharp.SKBitmap.Decode(ms);
                int dark = 0, tot = 0;
                for (int y = 40; y < sk.Height - 10; y += 4)
                    foreach (int x in new[] { 3, 8, sk.Width - 4, sk.Width - 9 })
                    { var c = sk.GetPixel(x, y); tot++; if (c.Red + c.Green + c.Blue < 40) dark++; }
                Console.WriteLine($"edge step {step}: dark {dark}/{tot}");
                File.WriteAllBytes($"/tmp/edge{Environment.GetEnvironmentVariable("BENCH_EDGE")}_{step}.png", ms.ToArray());
            }
        }
        if (Environment.GetEnvironmentVariable("BENCH_PNG") is { Length: > 0 } png)
        {
            map.CenterOn(new GeoPoint(50.5, 38.0), 6.1);
            bmp.Render(map);
#pragma warning disable CS0618
            using var fs = File.Create(png); bmp.Save(fs);
#pragma warning restore CS0618
        }
    }

    /// <summary>Тестовый сценарий для проверки отрисовки (RADAR_DEMO=1).</summary>
    private static List<AlertEvent> TipSamples()
    {
        var now = DateTimeOffset.UtcNow;
        AlertEvent E(ThreatType th, ThreatKind kind, AlertLevel lvl, string[] regions, string text, string[] channels, params MapPoint[] pts)
        {
            var e = new AlertEvent
            {
                Key = text, Threat = th, Kind = kind, Kinds = [kind], Level = lvl, Status = AlertStatus.Alert, RegionIds = regions,
                RegionNames = string.Join(", ", regions.Select(r => Radar.Services.Gazetteer.Get(r)?.Name ?? r)),
                Signature = text, Time = now.AddMinutes(-3), Text = text, Points = pts,
            };
            foreach (var c in channels) e.Sources.Add(new RawMessage(c, 1, now, text));
            return e;
        }
        var a = E(ThreatType.Balloon, ThreatKind.Balloon, AlertLevel.Danger, ["yaroslavl"], "Ярославская область Опасность по МВШ",
            ["vrv_radar", "radarrussiia", "nabludatel_rf"], new MapPoint(new(57.8, 39.3), "Ярославская область", true));
        var b = E(ThreatType.Uav, ThreatKind.UavGeneric, AlertLevel.Alarm, ["moscow_obl", "vladimir", "ivanovo", "ryazan"],
            "Раменское, Бронницы, Жуковский, Островцы, Котельники, Люберцы, Дзержинский, Лыткарино и близлежащие\nПродолжаются фиксации БПЛА в вашем направлении с юго-востока, возможны сбросы\nМосковская область",
            ["radar_moscoww", "vrv_radar", "radarrussiia", "nabludatel_rf", "kupolrussia"], new MapPoint(new(55.6, 38.1), "Раменское", false, 15));
        b.Motion = "Курс на северо-запад, ~180 км/ч, к Москве через Раменский городской округ";
        b.AddHistory(now.AddMinutes(-9), "radar_moscoww", "Раменское и близлежащие\nОпасность по БПЛА");
        b.AddHistory(now.AddMinutes(-5), "vrv_radar", "Бронницы, Жуковский, Островцы, Котельники, Люберцы — уточнение пунктов по всему направлению движения группы");
        b.AddPartialClear(["Владимирская область"], now.AddMinutes(-1), "vrv_radar");
        return [a, b];
    }

    private static (List<AlertEvent>, List<Track>) DemoScenario()
    {
        var now = DateTimeOffset.UtcNow;
        AlertEvent Ev(string k, ThreatType th, string region, IReadOnlyList<string> whole, params MapPoint[] pts) => EvK(k, th, ThreatKind.Unknown, region, whole, pts);
        AlertEvent EvK(string k, ThreatType th, ThreatKind kind, string region, IReadOnlyList<string> whole, params MapPoint[] pts) => EvN(k, th, kind, region, whole, false, pts);
        AlertEvent EvN(string k, ThreatType th, ThreatKind kind, string region, IReadOnlyList<string> whole, bool nearby, params MapPoint[] pts)
        {
            var e = new AlertEvent { Key = k, Threat = th, Kind = kind, Nearby = nearby, Kinds = kind == ThreatKind.Unknown ? [] : [kind], Status = AlertStatus.Alert, RegionIds = [region], WholeRegionIds = whole, RegionNames = region, Signature = k, Time = now.AddMinutes(-3), Text = k, Points = pts };
            e.Sources.Add(new RawMessage("demo", 1, now, k));
            return e;
        }
        var e1 = Ev("Трубчевск", ThreatType.Uav, "bryansk", [], new MapPoint(new(52.579, 33.766), "Трубчевск", false, 12));
        var e2 = Ev("Воронеж", ThreatType.Uav, "voronezh", ["voronezh"], new MapPoint(new(50.95, 40.2), "Воронежская область", true));
        var e3 = Ev("Льгов", ThreatType.Uav, "kursk", [], new MapPoint(new(51.659, 35.271), "Льгов", false, 12));
        var e4 = Ev("Суземский район", ThreatType.Uav, "bryansk", [], new MapPoint(new(52.32, 34.08), "Суземский район", false, 25) { DistrictRegion = "bryansk" });
        var e5 = Ev("Глушковский район", ThreatType.Aviation, "kursk", [], new MapPoint(new(51.34, 34.64), "Глушковский район", false, 25) { DistrictRegion = "kursk" });
        var e6 = EvK("Белгород", ThreatType.Missile, ThreatKind.MissileBallistic, "belgorod", [], new MapPoint(new(50.595, 36.587), "Белгород", false, 15));
        var e7 = EvN("Шебекино и близлежащие", ThreatType.Aviation, ThreatKind.GuidedBomb, "belgorod", [], true, new MapPoint(new(50.41, 36.89), "Шебекино", false, 10));
        var e8 = EvK("Новороссийск", ThreatType.Naval, ThreatKind.Usv, "krasnodar", [], new MapPoint(new(44.72, 37.77), "Новороссийск", false, 15));
        var e9 = EvK("Миллерово", ThreatType.Balloon, ThreatKind.Balloon, "rostov", [], new MapPoint(new(48.92, 40.40), "Миллерово", false, 15));
        var e10 = EvK("Белая Берёзка", ThreatType.Artillery, ThreatKind.Mlrs, "bryansk", [], new MapPoint(new(52.47, 33.20), "Белая Берёзка", false, 8));
        var e11 = EvK("Грайворон", ThreatType.Uav, ThreatKind.UavFpv, "belgorod", [], new MapPoint(new(50.48, 35.67), "Грайворон", false, 8));
        var e12 = EvN("Валуйки, Уразово и близлежащие", ThreatType.Uav, ThreatKind.UavHeavy, "belgorod", [], true, new MapPoint(new(50.08, 38.03), "Уразово", false, 8), new MapPoint(new(50.21, 38.10), "Валуйки", false, 8));
        var e13 = EvN("Новомосковск и близлежащие", ThreatType.Uav, ThreatKind.UavGeneric, "tula", [], true, new MapPoint(new(54.01, 38.29), "Новомосковск", false, 12));
        var e14 = EvK("Энгельс", ThreatType.Uav, ThreatKind.UavJet, "saratov", [], new MapPoint(new(51.49, 46.12), "Энгельс", false, 12));
        var e15 = EvN("Москва, Внуково и близлежащие", ThreatType.Uav, ThreatKind.UavGeneric, "moscow", [], true, new MapPoint(new(55.60, 37.27), "Внуково", false, 6));
        var e16 = EvK("Санкт-Петербург, Пушкинский район", ThreatType.Uav, ThreatKind.UavGeneric, "spb", [], new MapPoint(new(59.72, 30.40), "Пушкинский район", false, 25) { DistrictRegion = "spb" });
        var e17 = EvK("Южная часть Ростовской области", ThreatType.Uav, ThreatKind.UavGeneric, "rostov", [], new MapPoint(new(47.0, 40.9), "Юг — Ростовская область", false, 90) { PartRegion = "rostov", PartBearing = 180 });
        var e18 = EvK("Восточная часть Белгородской области", ThreatType.Uav, ThreatKind.UavGeneric, "belgorod", [], new MapPoint(new(50.75, 38.1), "Восток — Белгородская область", false, 50) { PartRegion = "belgorod", PartBearing = 90 });
        var e19 = EvK("Новониколаевский район — в сторону Саратовской области", ThreatType.Uav, ThreatKind.UavGeneric, "volgograd", [], new MapPoint(new(50.95, 42.4), "Новониколаевский район", false, 25) { DistrictRegion = "volgograd" });
        e19.ExpectedRegionIds.Add("saratov");
        var r280 = Radar.Services.RouteNetwork.Find(new Radar.Services.CorridorHint(Radar.Services.CorridorKind.Road, "Р-280", "Р-280"))
            .SelectMany(l => l.Parts).Where(pp => pp.Any(q => Geo.DistanceKm(q, new GeoPoint(46.85, 35.9)) < 90))
            .OrderByDescending(pp => pp.Length).FirstOrDefault()?.Where(q => Geo.DistanceKm(q, new GeoPoint(46.85, 35.9)) < 96).ToList();
        var e20 = EvK("Трасса Р-280", ThreatType.Uav, ThreatKind.UavHornet, "zaporozhye", [], new MapPoint(new(46.85, 35.9), "Трасса Р-280 «Новороссия»", false, 60) { Corridor = r280 });
        // зона над водой («Таганрогский залив») и полоса вдоль общей границы регионов
        var gulf = Radar.Services.RouteNetwork.SeaArea("sea:taganrog", new GeoPoint(47.05, 38.45), 40);
        var e21 = EvK("Таганрогский залив", ThreatType.Uav, ThreatKind.UavGeneric, "rostov", [], new MapPoint(new(47.05, 38.45), "Таганрогский залив", false, 40) { WaterArea = gulf.Count >= 3 ? gulf : null, SeaId = "sea:taganrog" });
        var vrBorder = Radar.Services.RouteNetwork.BorderBetween("voronezh", "rostov")?.Parts.MaxBy(pp => pp.Length);
        var e22 = EvK("По границе Воронежской и Ростовской области", ThreatType.Uav, ThreatKind.UavGeneric, "voronezh", [], new MapPoint(new(49.6, 40.6), "Граница: Воронежская — Ростовская", false, 30) { Corridor = vrBorder });
        // Крым: Сиваш, Керченский пролив (над водой), Арабатская стрелка и Бакальская коса (полосой вдоль берега)
        MapPoint Water(string label, string sea, double lat, double lon, double r)
        {
            var area = Radar.Services.RouteNetwork.SeaArea(sea, new(lat, lon), r);
            return new MapPoint(new(lat, lon), label, false, r) { WaterArea = area.Count >= 3 ? area : null, SeaId = sea };
        }
        MapPoint Spit(string label, double lat, double lon, double r) =>
            new(new(lat, lon), label, false, r)
            {
                Corridor = Radar.Services.AlertEngine.CoastCorridor(new Radar.Services.ParsedPlace { Label = label, Pos = new(lat, lon), IsArea = true, ZoneRadiusKm = r }),
            };
        var e23 = EvK("Через залив Сиваш", ThreatType.Uav, ThreatKind.UavGeneric, "crimea", [], Water("Залив Сиваш", "sea:sivash", 45.74, 34.92, 30));
        var e24 = EvK("Керченский пролив", ThreatType.Uav, ThreatKind.UavHornet, "crimea", [], Water("Керченский пролив", "sea:azov", 45.3, 36.55, 15));
        var e25 = EvK("Арабатская стрелка", ThreatType.Uav, ThreatKind.UavGeneric, "kherson", [], Spit("Арабатская стрелка", 45.85, 35.05, 30));
        var e26 = EvK("Бакальская коса", ThreatType.Uav, ThreatKind.UavGeneric, "crimea", [], Spit("Бакальская коса", 45.75, 33.19, 6));
        var t4 = new Track { Id = 4, Threat = ThreatType.Uav, Kind = ThreatKind.UavJet, Created = now.AddMinutes(-12), Updated = now.AddMinutes(-2), SpeedKmh = 520, SpeedSamples = 1, Target = new(52.97, 36.07), TargetLabel = "Орёл" };
        t4.Points.Add(new TrackPoint(new(52.00, 35.0), now.AddMinutes(-12), "Хомутовка", "demo"));
        t4.Points.Add(new TrackPoint(new(52.33, 35.35), now.AddMinutes(-2), "Железногорск", "demo"));
        var t5 = new Track { Id = 5, Threat = ThreatType.Missile, Kind = ThreatKind.MissileAntiShip, Created = now.AddMinutes(-3), Updated = now.AddMinutes(-1), SpeedKmh = 850, Target = new(45.30, 36.50), TargetLabel = "Керчь" };
        t5.Points.Add(new TrackPoint(new(45.60, 33.6), now.AddMinutes(-1), "Черноморское", "demo"));
        var t6 = new Track { Id = 6, Threat = ThreatType.Uav, Kind = ThreatKind.UavHornet, Created = now.AddMinutes(-30), Updated = now.AddMinutes(-2), SpeedKmh = 140, SpeedSamples = 1, Target = new(46.76, 36.79), TargetLabel = "Бердянск" };
        t6.Points.Add(new TrackPoint(new(46.85, 35.37), now.AddMinutes(-30), "Мелитополь", "demo"));
        t6.Points.Add(new TrackPoint(new(46.80, 35.95), now.AddMinutes(-2), "Розовка", "demo"));
        var t1 = new Track { Id = 1, Threat = ThreatType.Uav, Created = now.AddMinutes(-30), Updated = now.AddMinutes(-3), SpeedKmh = 180, SpeedSamples = 1, Target = new(53.243, 34.364), TargetLabel = "Брянск" };
        t1.Points.Add(new TrackPoint(new(52.15, 34.49), now.AddMinutes(-30), "Севск", "demo"));
        t1.Points.Add(new TrackPoint(new(52.415, 34.0), now.AddMinutes(-15), "Суземка", "demo"));
        t1.Points.Add(new TrackPoint(new(52.579, 33.766), now.AddMinutes(-3), "Трубчевск", "demo"));
        t1.Events.Add(e1); t1.Channels.Add("a"); t1.Channels.Add("b");
        var t2 = new Track { Id = 2, Threat = ThreatType.Missile, Created = now.AddMinutes(-2), Updated = now.AddMinutes(-1), SpeedKmh = 850, Target = new(51.73, 36.19), TargetLabel = "Курск" };
        t2.Points.Add(new TrackPoint(new(51.30, 34.70), now.AddMinutes(-1), "Глушково", "demo"));
        var t3 = new Track { Id = 3, Threat = ThreatType.Uav, Created = now.AddMinutes(-20), Updated = now.AddMinutes(-2), SpeedKmh = 160, SpeedSamples = 2, Count = 3 };
        t3.Points.Add(new TrackPoint(new(51.40, 34.91), now.AddMinutes(-20), "Кореново", "demo"));
        t3.Points.Add(new TrackPoint(new(51.659, 35.271), now.AddMinutes(-2), "Льгов", "demo"));
        t3.Events.Add(e3);
        t2.Kind = ThreatKind.MissileCruise;
        // маршруты: пуск → фиксации → (через) → цель
        t1.Launch = new GeoPoint(51.87, 33.48); t1.LaunchLabel = "Сумская обл."; t1.LaunchExplicit = true;
        t4.Via.Add((new GeoPoint(52.75, 35.80), "Кромы"));
        t6.Corridors.Add(new Radar.Services.CorridorHint(Radar.Services.CorridorKind.Road, "Р-280", "Р-280"));
        // «по трассе Рыльск — Хомутовка»: путь по дорогам
        var t7 = new Track { Id = 7, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, Created = now.AddMinutes(-25), Updated = now.AddMinutes(-3), SpeedKmh = 150, SpeedSamples = 1 };
        t7.Points.Add(new TrackPoint(new(51.57, 34.68), now.AddMinutes(-25), "Рыльск", "demo"));
        t7.Points.Add(new TrackPoint(new(51.74, 34.60), now.AddMinutes(-3), "Дурово", "demo"));
        t7.RoadSegments.Add(new Radar.Services.RoadSegmentHint(new(51.57, 34.68), "Рыльск", new(51.92, 34.56), "Хомутовка"));
        // «в направлении Орловской области»: цель-регион — только направление
        var t8 = new Track { Id = 8, Threat = ThreatType.Uav, Kind = ThreatKind.UavGeneric, Created = now.AddMinutes(-20), Updated = now.AddMinutes(-2), SpeedKmh = 170, SpeedSamples = 1,
            Target = new(52.95, 36.07), TargetLabel = "Орловская область", TargetIsArea = true };
        t8.Points.Add(new TrackPoint(new(52.53, 32.60), now.AddMinutes(-20), "Чубковичи", "demo"));
        t8.Points.Add(new TrackPoint(new(52.93, 33.45), now.AddMinutes(-2), "Почеп", "demo"));
        foreach (var tr in new[] { t1, t2, t3, t4, t5, t6, t7, t8 }) Radar.Services.RoutePlanner.Plan(tr);
        e2.Level = AlertLevel.Danger;
        e4.Level = AlertLevel.Attention;
        e5.Level = AlertLevel.Attention;
        e6.Level = AlertLevel.Strike;
        e8.Level = AlertLevel.Danger;
        e9.Level = AlertLevel.Attention;
        // пуски: факт пуска с направлением и «возможны пуски»
        AlertEvent Launch(string k, ThreatType th, ThreatKind kind, string site, GeoPoint at, GeoPoint? to, AlertLevel lv)
        {
            var e = new AlertEvent { Key = k, Threat = th, Kind = kind, Kinds = [kind], Status = AlertStatus.Launch, Level = lv, RegionIds = ["kharkov"], RegionNames = "Харьковская область", Signature = k, Time = now.AddMinutes(-2), Text = k, Points = [new MapPoint(at, site, false)], Origin = at, Target = to };
            e.Sources.Add(new RawMessage("demo", 2, now, k));
            return e;
        }
        var e27 = Launch("Пуски Уаб от Изюма", ThreatType.Aviation, ThreatKind.GuidedBomb, "Изюм", new(49.21, 37.26), new(50.0, 38.0), AlertLevel.Alarm);
        var e28 = Launch("Чугуев выходы реактивных БПЛА", ThreatType.Uav, ThreatKind.UavJet, "Чугуев", new(49.84, 36.69), new(50.6, 37.6), AlertLevel.Alarm);
        var e29 = Launch("Готовность к пускам от Балаклеи", ThreatType.Missile, ThreatKind.MissileCruise, "Балаклея", new(49.46, 36.86), null, AlertLevel.Attention);
        return ([e1, e2, e3, e4, e5, e6, e7, e8, e9, e10, e11, e12, e13, e14, e15, e16, e17, e18, e19, e20, e21, e22, e23, e24, e25, e26, e27, e28, e29], [t1, t2, t3, t4, t5, t6, t7, t8]);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Selected) && _vm!.Selected != Map.Selected)
            Map.Select(_vm.Selected, focus: true);
        if (e.PropertyName == nameof(MainViewModel.IsStats))
        {
            if (_vm!.IsStats && !_sidebarHidden) { SetSidebarHidden(true); _sidebarAutoHidden = true; }
            else if (!_vm.IsStats && _sidebarAutoHidden) { _sidebarAutoHidden = false; SetSidebarHidden(false); }
        }
        if (e.PropertyName == nameof(MainViewModel.ShowExpected)) Map.ShowExpected = _vm!.ShowExpected;
        if (e.PropertyName == nameof(MainViewModel.ShowFrontLine)) Map.ShowFrontLine = _vm!.ShowFrontLine;
        if (e.PropertyName == nameof(MainViewModel.IsOffline)) { Map.SignalLost = _vm!.IsOffline; FitOverlays(); if (_vm.IsOffline) _vm.PickingOnMap = false; }
        if (e.PropertyName == nameof(MainViewModel.RetroNoise)) Map.RetroNoise = _vm!.RetroNoise;
        if (e.PropertyName == nameof(MainViewModel.AnimationQuality)) Map.AnimationQuality = _vm!.AnimationQuality;
        if (e.PropertyName == nameof(MainViewModel.SignalLossEffect)) Map.SignalLossEffect = _vm!.SignalLossEffect;
        if (e.PropertyName == nameof(MainViewModel.Vignette)) Map.Vignette = _vm!.Vignette;
        if (e.PropertyName == nameof(MainViewModel.NotifyRegionSet)) Map.PickedRegions = _vm!.NotifyRegionSet;
        if (e.PropertyName == nameof(MainViewModel.WatchAreas)) { Map.WatchAreas = _vm!.WatchAreas; Map.WatchRadiusKm = _vm.WatchRadiusKm; }
        if (e.PropertyName == nameof(MainViewModel.HighlightMyRegions)) Map.HighlightPicked = _vm!.HighlightMyRegions;
        if (e.PropertyName == nameof(MainViewModel.PickingOnMap))
        {
            Map.PickRegions = _vm!.PickingOnMap;
            // шторка на телефоне закрывает карту целиком: на время выбора зон убирается и возвращается после «Готово»
            if (_vm.PickingOnMap && DrawerMode && !_sidebarHidden) { _pickAutoHidden = true; SetSidebarHidden(true); }
            else if (!_vm.PickingOnMap && _pickAutoHidden) { _pickAutoHidden = false; if (_sidebarHidden && _vm.IsLive) SetSidebarHidden(false); }
            FitOverlays();
        }
        if (e.PropertyName == nameof(MainViewModel.PickPointMode)) Map.PickPoint = _vm!.PickPointMode;
        if (e.PropertyName is nameof(MainViewModel.StaleSince) or nameof(MainViewModel.IsOffline)) Map.SignalLostSince = _vm!.StaleSince;
        // ширина чисел и метки режима меняется — пересчитываем, какой вариант счётчиков помещается
        if (e.PropertyName is nameof(MainViewModel.ModeTag) or nameof(MainViewModel.IsOffline) or nameof(MainViewModel.ActiveCount) or nameof(MainViewModel.RegionCount))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTelemetryLayout(TitleBar.Bounds.Width));
    }

    // ---------------- режимы работы ----------------

    private void ModeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // меню — под меткой режима
        var p = ModePanel.TranslatePoint(new Point(0, 0), this) ?? default;
        // под полосой заголовка (с учётом строки состояния); на телефоне — по центру экрана с равными полями
        double menuTop = BarHeight + Safe.Top + 1;
        if (Compact)
        {
            ModeMenu.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            ModeMenu.Margin = new Thickness(12 + Safe.Left, menuTop, 12 + Safe.Right, 0);
            ModeMenuPanel.Width = double.NaN;
        }
        else
        {
            ModeMenu.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            ModeMenu.Margin = new Thickness(Math.Max(0, Math.Min(p.X, Bounds.Width - 300)), menuTop, 0, 0);
            ModeMenuPanel.Width = 300;
        }
        ModeDismiss.Margin = new Thickness(0, menuTop, 0, 0);
        _vm.ModeMenuOpen = !_vm.ModeMenuOpen;
    }

    private void StatsRegionClear(object? sender, RoutedEventArgs e) => _vm?.StatsPickRegion(null);

    private void ModeItemClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is ModeOption o) _vm.Mode = o.Mode;
    }

    private void ModeDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm != null) _vm.ModeMenuOpen = false;
    }

    private void ReplayRestart(object? sender, RoutedEventArgs e) => _vm?.ReplayRestart();
    private void ReplayBack(object? sender, RoutedEventArgs e) => _vm?.StepHistory(-1);
    private void ReplayForward(object? sender, RoutedEventArgs e) => _vm?.StepHistory(1);

    private void MonthButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is Control mb) PlaceMonthPicker(mb);
        _vm.MonthPickerOpen = !_vm.MonthPickerOpen;
    }

    private void MonthItemClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && (sender as Control)?.DataContext is MonthOption m) _vm.SelectHistoryMonth(m.Month);
    }

    /// <summary>Список месяцев — над кнопкой выбора, правым краем по ней.</summary>
    private void PlaceMonthPicker(Control mb)
    {
        if (mb.TranslatePoint(new Point(mb.Bounds.Width, 0), Overlays) is { } p)
        {
            MonthPicker.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            MonthPicker.Margin = new Thickness(Math.Max(8, p.X - MonthPicker.Width), 0, 0, Math.Max(8, Overlays.Bounds.Height - p.Y + 6));
        }
    }
    private void ReplaySlower(object? sender, RoutedEventArgs e) => _vm?.ReplaySlower();
    private void ReplayFaster(object? sender, RoutedEventArgs e) => _vm?.ReplayFaster();

    private void LegendItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LegendItem item) _vm?.ToggleKind(item);
    }

    private void LegendGroupClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LegendGroup g) _vm?.ToggleGroup(g);
    }

    private void LevelClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LevelItem l) _vm?.ToggleLevel(l);
    }

    private void EmptyActionClick(object? sender, RoutedEventArgs e) => _vm?.RunEmptyAction();

    private void ExpectedClick(object? sender, RoutedEventArgs e) => _vm?.ToggleExpected();

    private void FrontLineClick(object? sender, RoutedEventArgs e) => _vm?.ToggleFrontLine();

    private void LegendShowAll(object? sender, RoutedEventArgs e) => _vm?.ShowAllKinds();

    private void TelemetryKindClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TelemetryItem t) _vm?.ToggleGroup(t.Group);
    }

    private void LegendToggleCollapsed(object? sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.LegendCollapsed = !_vm.LegendCollapsed;
    }

    // ---------------- собственная рамка окна ----------------

    // ---------------- окно (на десктопе; на телефоне шапка без кнопок окна) ----------------

    /// <summary>Хозяин представления: окно десктопа. Null — мобильная платформа (кнопки окна скрыты).</summary>
    public IWindowChrome? Chrome
    {
        get => _chrome;
        set
        {
            _chrome = value;
            WinButtons.IsVisible = value != null;
            ResizeGrips.IsVisible = WindowFrame.IsVisible = value?.Framed == true;
        }
    }
    private IWindowChrome? _chrome;

    private void TitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // перетаскивание — только с «пустого» места полосы (не с кнопок)
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) _chrome?.BeginMove(e);
    }

    private void TitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        _chrome?.ToggleMaximize();
    }

    private void WinMinimize(object? sender, RoutedEventArgs e) => _chrome?.Minimize();
    private void WinMaximize(object? sender, RoutedEventArgs e) => _chrome?.ToggleMaximize();
    private void WinClose(object? sender, RoutedEventArgs e) => _chrome?.Close();

    /// <summary>Окно сменило состояние: рамка, зоны изменения размера и значок «развернуть / восстановить».</summary>
    public void OnWindowStateChanged(bool framed, bool maximized, bool minimized)
    {
        WindowFrame.IsVisible = framed;
        ResizeGrips.IsVisible = framed;
        MaximizeIcon.Data = Avalonia.Media.Geometry.Parse(maximized ? "M0.5 2.5 H6.5 V8.5 H0.5 Z M2.5 2.5 V0.5 H8.5 V6.5 H6.5" : "M0.5 0.5 H8.5 V8.5 H0.5 Z");
        ToolTip.SetTip(MaximizeButton, maximized ? "Восстановить" : "Развернуть");
        _minimized = minimized;
        if (minimized) _pulse?.Stop(); else _pulse?.Start();
    }
    private bool _minimized;

    /// <summary>
    /// Счётчики в заголовке: полные подписи, если хватает места; в узком окне — маркеры с числами
    /// (подписи во всплывающих подсказках); совсем тесно — скрыты.
    /// </summary>
    private void UpdateTelemetryLayout(double width)
    {
        if (width <= 1) return;
        var inf = new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity);
        double used = 0;
        foreach (var c in new Control[] { Brand, ModePanel, MapButtons, WinButtons })
        {
            c.Measure(inf);
            used += c.DesiredSize.Width;
        }
        double free = width - used - 24;
        // скрытый элемент меряется нулём — на время замера включаем оба варианта
        Telemetry.IsVisible = TelemetryCompact.IsVisible = true;
        Telemetry.Measure(inf);
        TelemetryCompact.Measure(inf);
        bool full = Telemetry.DesiredSize.Width <= free;
        Telemetry.IsVisible = full;
        TelemetryCompact.IsVisible = !full && TelemetryCompact.DesiredSize.Width <= free;
        // разделитель перед кнопками — только когда слева от него есть счётчики или справа кнопки окна
        MapSepLeft.IsVisible = _chrome != null || Telemetry.IsVisible || TelemetryCompact.IsVisible;
    }

    // ---------------- состояние между запусками ----------------

    private AppSettings? _state;

    /// <summary>Применяет сохранённые камеру карты и состояние панелей (геометрию окна восстанавливает само окно).</summary>
    public void RestoreState(AppSettings s)
    {
        _state = s;
        if (s.MapLat is double lat && s.MapLon is double lon && s.MapZoom is double z && double.IsFinite(lat + lon + z))
            Map.RestoreCamera(new GeoPoint(lat, lon), z);
        if (s.SidebarTab >= 0 && s.SidebarTab < SideTabs.ItemCount) SideTabs.SelectedIndex = s.SidebarTab;
        // на телефоне панель — шторка: при запуске закрыта, на виду карта
        if (s.SidebarHidden || Compact)
        {
            // стартовое состояние — без анимации выезда
            var tr = Sidebar.Transitions;
            Sidebar.Transitions = null;
            SetSidebarHidden(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Sidebar.Transitions = tr, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // пульс индикатора связи: ~8 кадров в секунду; в свёрнутом окне и без связи — стоит
        _pulse ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(125), Avalonia.Threading.DispatcherPriority.Background, (_, _) => PulseLiveDots());
        if (!_minimized) _pulse.Start();
        AttachPlatform(TopLevel.GetTopLevel(this));
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pulse?.Stop(); _sideTimer?.Stop(); _flapTimer?.Stop();
        DetachPlatform();
    }

    private static readonly long PulseStart = System.Diagnostics.Stopwatch.GetTimestamp();

    private void PulseLiveDots()
    {
        bool on = !_minimized && _vm is { IsOffline: false };
        // 1 → 0.15 и обратно за 2,8 с, как прежняя анимация стиля
        double t = System.Diagnostics.Stopwatch.GetElapsedTime(PulseStart).TotalSeconds % 2.8 / 1.4;
        double tri = t <= 1 ? t : 2 - t;
        double op = on ? Math.Round(1 - 0.85 * (0.5 - 0.5 * Math.Cos(Math.PI * tri)), 2) : 1;
        // облегчённый режим: точка не мигает, а таймер (8 изменений в секунду → перерисовка шапки и ленты) стоит
        if (Radar.Map.Glass.LowPower) { op = 1; _pulse?.Stop(); }
        if (LiveDot.Opacity != op) LiveDot.Opacity = op;
        if (StatusDot.Opacity != op) StatusDot.Opacity = op;
    }

    private string? _savedStateKey;
    private Avalonia.Threading.DispatcherTimer? _pulse, _flapTimer;

    /// <summary>Ключ состояния представления (камера, панель, вкладка) — окно добавляет к нему свою геометрию.</summary>
    public string StateKey
    {
        get
        {
            var (c, z) = Map.Camera;
            return FormattableString.Invariant($"{c.Lat:0.#####},{c.Lon:0.#####},{z:0.###}|{_sidebarHidden && !_sidebarAutoHidden}|{Math.Max(0, SideTabs.SelectedIndex)}");
        }
    }

    /// <summary>Записывает камеру и панели в настройки (без сохранения на диск).</summary>
    public void WriteState(AppSettings s)
    {
        var (c, z) = Map.Camera;
        (s.MapLat, s.MapLon, s.MapZoom) = (c.Lat, c.Lon, z);
        // автоскрытие статистики и закрытая шторка телефона — не выбор пользователя
        if (!Compact) s.SidebarHidden = _sidebarHidden && !_sidebarAutoHidden;
        s.SidebarTab = Math.Max(0, SideTabs.SelectedIndex);
    }

    /// <summary>
    /// Сохраняет состояние представления. <paramref name="onlyIfChanged"/> — при уходе в фон: запись на диск синхронная,
    /// поэтому файл пишется, только если с прошлого сохранения что-то изменилось.
    /// </summary>
    public void SaveState(bool onlyIfChanged = false)
    {
        if (IsExternal) return;
        if (_state is not { } s) return;
        if (_chrome != null) { _chrome.SaveState(onlyIfChanged); return; }
        var key = StateKey;
        if (onlyIfChanged && key == _savedStateKey) return;
        WriteState(s);
        if (onlyIfChanged) SettingsStore.SaveDeferred(s); else SettingsStore.Save(s);
        _savedStateKey = key;
    }

    private void ToggleSidebar(object? sender, RoutedEventArgs e)
    {
        // ручное переключение отменяет автоматическое возвращение панели
        _sidebarAutoHidden = _pickAutoHidden = false;
        _userOpenedDrawer = _sidebarHidden;
        _userHidSidebar = !_sidebarHidden;
        SetSidebarHidden(!_sidebarHidden);
    }

    private void SetSidebarHidden(bool hidden)
    {
        _sidebarHidden = hidden;
        // уезжает за правый край целиком: ширина колонки + запас на тень
        Sidebar.RenderTransform = SlideX(hidden ? SidebarWidth + 24 + Safe.Right : 0);
        Sidebar.Opacity = hidden ? 0 : 1;
        Sidebar.IsHitTestVisible = !hidden;
        UpdateScrim();
        // наложения над картой (легенда, баннер, архив) и подписи карты плавно занимают освободившееся место — синхронно с панелью
        AnimateSidebarSpace(hidden ? 0 : 1, Sidebar.Transitions == null);
        ToolTip.SetTip(SidebarToggle, hidden ? "Показать боковую панель" : "Скрыть боковую панель");
        SidebarToggle.Classes.Set("on", !hidden);
        MirrorSidebarChanged();
    }

    private double _sideK = 1, _sideFrom = 1, _sideTo = 1;
    private long _sideStart;
    private Avalonia.Threading.DispatcherTimer? _sideTimer;

    /// <summary>Доля места, занятого панелью (0…1): тот же закон и длительность, что у выезда самой панели (CubicEaseOut, 0.38 с).</summary>
    private void AnimateSidebarSpace(double to, bool instant)
    {
        // шторка лежит поверх карты и места у неё не отнимает: покадровый пересчёт (с перерисовкой всей карты) не нужен
        if (instant || DrawerMode) { _sideTimer?.Stop(); _sideK = _sideFrom = _sideTo = to; ApplySidebarSpace(); return; }
        (_sideFrom, _sideTo, _sideStart) = (_sideK, to, System.Diagnostics.Stopwatch.GetTimestamp());
        _sideTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
        {
            double t = Math.Min(1, System.Diagnostics.Stopwatch.GetElapsedTime(_sideStart).TotalSeconds / 0.38);
            double e = 1 - Math.Pow(1 - t, 3);
            _sideK = _sideFrom + (_sideTo - _sideFrom) * e;
            ApplySidebarSpace();
            if (t >= 1) _sideTimer!.Stop();
        });
        _sideTimer.Start();
    }

    private bool _fitting;

    /// <summary>
    /// Раскладка плавающих панелей под размер окна: верхние баннеры, нижняя центральная панель
    /// (загрузка истории / архив) и легенда не должны налезать друг на друга.
    /// </summary>
    private static void Place(Control p, Avalonia.Layout.HorizontalAlignment h, double left, double bottom)
    {
        p.HorizontalAlignment = h;
        var m = new Avalonia.Thickness(left, 0, 0, bottom);
        if (p.Margin != m) p.Margin = m;
    }

    internal void FitOverlays()
    {
        if (_fitting) return;
        _fitting = true;
        try
        {
            double ow = Overlays.Bounds.Width, oh = Overlays.Bounds.Height;
            if (ow <= 0 || oh <= 0) return;
            const double Edge = 12, Gap = 8;
            TopStack.MaxWidth = Math.Max(0, ow - 2 * Edge);

            // баннер «нет связи» прозрачен, но место в стеке занимает всегда — учитываем его только при обрыве;
            // баннер выбора регионов — когда виден
            bool offline = _vm?.IsOffline == true;
            double top = BarHeight + Safe.Top + 12 + (offline && TopStack.Bounds.Height > 0.5 ? TopStack.Bounds.Height + Gap
                : PickBanner.IsVisible && PickBanner.Bounds.Height > 0.5 ? PickBanner.Bounds.Height + Gap : 0);
            HiddenChip.MaxWidth = Math.Max(0, ow - 2 * Edge);
            var chipMargin = new Avalonia.Thickness(0, top, 0, 0);
            if (HiddenChip.Margin != chipMargin) HiddenChip.Margin = chipMargin;
            if (HiddenChip.IsVisible) top += HiddenChip.Bounds.Height + Gap;
            bool legendOpen = LegendPanel.IsVisible;
            double legendW = legendOpen ? LegendPanel.Width : LegendButton.Bounds.Width;
            double legendRight = Edge + legendW + Edge;

            // нижние панели: на широком экране — по центру над линейкой масштаба (или правее легенды); на узком —
            // во всю ширину с полями 12. Воспроизведение (постоянная панель) стоит внизу, а легенда и линейка — над ним;
            // временный индикатор загрузки истории, наоборот, всплывает НАД строкой легенды и линейки, ничего не сдвигая
            bool fullWidth = ow < 600;
            double legendH = legendOpen ? 0 : (LegendButton.Bounds.Height > 0 ? LegendButton.Bounds.Height : LegendButton.DesiredSize.Height);
            double legendBottom = Edge;
            double PanelH(Control p) => p.Bounds.Height > 0 ? p.Bounds.Height : p.DesiredSize.Height;
            if (ReplayPanel.IsVisible)
            {
                double pw = ReplayPanel.Bounds.Width > 0 ? ReplayPanel.Bounds.Width : ReplayPanel.DesiredSize.Width;
                double centered = (ow - pw) / 2;
                if (!fullWidth && centered >= legendRight)
                    Place(ReplayPanel, Avalonia.Layout.HorizontalAlignment.Center, 0, 40);
                else if (!fullWidth && legendRight + pw <= ow - Edge)
                    Place(ReplayPanel, Avalonia.Layout.HorizontalAlignment.Left, legendRight, 40);
                else
                {
                    Place(ReplayPanel, Avalonia.Layout.HorizontalAlignment.Center, 0, Edge);
                    legendBottom = Edge + PanelH(ReplayPanel) + Gap;
                }
            }
            if (HistoryPanel.IsVisible)
            {
                double pw = HistoryPanel.Bounds.Width > 0 ? HistoryPanel.Bounds.Width : HistoryPanel.DesiredSize.Width;
                if (!fullWidth && (ow - pw) / 2 >= legendRight)
                    Place(HistoryPanel, Avalonia.Layout.HorizontalAlignment.Center, 0, legendBottom == Edge ? 40 : legendBottom);
                else
                    // над строкой «обозначения + линейка»
                    Place(HistoryPanel, Avalonia.Layout.HorizontalAlignment.Center, 0, legendBottom + Math.Max(legendH, 20) + Gap);
            }

            // линейка масштаба — на одной линии со строкой легенды (низ к низу)
            double lift = legendBottom - Edge;
            double inset = legendRight - Edge;
            if (Map.HudLift != lift || Map.HudLeftInset != inset) { Map.HudLift = lift; Map.HudLeftInset = inset; Map.InvalidateVisual(); }
            LegendPanel.Margin = new Avalonia.Thickness(Edge, 0, 0, legendBottom);
            LegendButton.Margin = new Avalonia.Thickness(Edge, 0, 0, legendBottom);
            LegendPanel.MaxHeight = Math.Clamp(oh - top - legendBottom, 120, 640);
            LegendPanel.Classes.Set("compact", oh - top - legendBottom < 520);
        }
        finally { _fitting = false; }
    }

    private void ApplySidebarSpace()
    {
        // шторка поверх карты (телефон) места у карты не отнимает
        double w = DrawerMode ? 0 : SidebarWidth * _sideK;
        Overlays.Margin = new Avalonia.Thickness(Safe.Left, 0, w + (DrawerMode ? Safe.Right : 0), Safe.Bottom);
        var obscured = new Avalonia.Thickness(Safe.Left, BarHeight + Safe.Top + 1, w + Safe.Right, Safe.Bottom);
        if (Map.Obscured == obscured) return;
        Map.Obscured = obscured;
        Map.InvalidateVisual();
    }

    private void ResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_chrome is not { Framed: true } || sender is not Control { Tag: string tag }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = tag switch
        {
            "N" => WindowEdge.North, "S" => WindowEdge.South, "W" => WindowEdge.West, "E" => WindowEdge.East,
            "NW" => WindowEdge.NorthWest, "NE" => WindowEdge.NorthEast, "SW" => WindowEdge.SouthWest, _ => WindowEdge.SouthEast,
        };
        _chrome.BeginResize(edge, e);
        e.Handled = true;
    }

    private void ZoomIn(object? sender, RoutedEventArgs e) => Map.ZoomBy(1);
    private void ZoomOut(object? sender, RoutedEventArgs e) => Map.ZoomBy(-1);
    private void Home(object? sender, RoutedEventArgs e) => Map.GoHome();
    private void Refresh(object? sender, RoutedEventArgs e) => _vm?.PollNow();

    private void ToggleSimulatedOffline()
    {
        Radar.Services.TelegramSource.SimulateOffline = !Radar.Services.TelegramSource.SimulateOffline;
        _vm?.PollNow(); // сразу опрашиваем каналы, не дожидаясь паузы между попытками
    }

    private void TestNotificationClick(object? sender, RoutedEventArgs e) => _vm?.TestNotification();
    private void ClearRegionsClick(object? sender, RoutedEventArgs e) => _vm?.ClearPickedRegions();
    private void RadiusDownClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.DataContext is WatchPlaceRow r) r.Step(-1); }
    private void RadiusUpClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.DataContext is WatchPlaceRow r) r.Step(+1); }
    /// <summary>Переименование пункта: Enter — сохранить, Esc — отменить (фокус уходит из поля).</summary>
    private void RenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Escape && tb.DataContext is WatchPlaceRow row) tb.Text = row.Name;
        e.Handled = true;
        Map.Focus();
    }

    private void FinishPickingClick(object? sender, RoutedEventArgs e) { if (_vm != null) _vm.PickingOnMap = false; }
    private void RegionChipClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WatchChoice c) _vm?.RemoveArea(c.Area);
    }
    private void CheckUpdatesClick(object? sender, RoutedEventArgs e) => _vm?.CheckForUpdates();
    private void OpenDataFolderClick(object? sender, RoutedEventArgs e) => _vm?.OpenDataFolder();
    private void DownloadUpdateClick(object? sender, RoutedEventArgs e) => _vm?.OpenUpdate();

    private void InstallUpdateClick(object? sender, RoutedEventArgs e) => _vm?.InstallUpdate();

    private void RestartUpdateClick(object? sender, RoutedEventArgs e) => _vm?.RestartForUpdate();
    private void SyncArchiveClick(object? sender, RoutedEventArgs e) => _ = _vm?.SyncArchiveAsync();
    private void CastClick(object? sender, RoutedEventArgs e) => _vm?.OpenCastSettings();
    private void DonateClick(object? sender, RoutedEventArgs e) => _vm?.OpenDonate();
    private void FeedbackClick(object? sender, RoutedEventArgs e) => _vm?.OpenFeedback();
    private void ProjectClick(object? sender, RoutedEventArgs e) => _vm?.OpenProject();

    private void ChannelToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { DataContext: ChannelRow row } tb)
        {
            _vm?.ToggleChannel(row);
            tb.IsChecked = row.Enabled; // OneWay: значение ведёт модель
        }
        e.Handled = true;
    }

    private void ChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChannelRow row && Uri.TryCreate(row.Url, UriKind.Absolute, out var uri))
            OpenUri(uri);
    }

    private void FeedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: FeedItem item } && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
            OpenUri(uri);
    }
}
