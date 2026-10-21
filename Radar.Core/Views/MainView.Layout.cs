using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Radar.Views;

/// <summary>
/// Адаптивная раскладка под любой экран: десктоп, планшет, телефон, портрет и ландшафт.
/// Ширина — в логических пикселях (dp): плотность экрана уже учтена платформой, поэтому одни и те же пороги
/// работают на любом DPI.
/// <list type="bullet">
/// <item>Широкий экран (≥ 1000): боковая панель — колонка справа, карта сдвигается.</item>
/// <item>Средний и узкий: панель — шторка поверх карты (с затемнением), по ширине экрана на телефоне.</item>
/// <item>Шапка: на сенсорных экранах выше (крупные цели касания), без кнопок окна и масштаба.</item>
/// <item>Статистика: две колонки на широком экране, одна колонка с прокруткой на узком.</item>
/// <item>Отступы под вырез камеры, системные панели и клавиатуру — от платформы (safe area).</item>
/// </list>
/// </summary>
public partial class MainView
{
    /// <summary>Ширина колонки боковой панели на широком экране.</summary>
    private const double WideSidebar = 412;
    /// <summary>Ниже этой ширины панель перестаёт быть колонкой и становится шторкой поверх карты.</summary>
    private const double DrawerBelow = 1000;
    /// <summary>Ниже этой ширины — телефонная раскладка (шторка во всю ширину, статистика в одну колонку).</summary>
    private const double CompactBelow = 600;

    /// <summary>Компактная раскладка (телефон, узкое окно): шторка во всю ширину, крупные элементы.</summary>
    public bool Compact { get; private set; }
    /// <summary>Боковая панель — шторка поверх карты, а не колонка.</summary>
    public bool DrawerMode { get; private set; }
    /// <summary>Сенсорный экран без мыши (мобильная платформа): крупные цели касания, без наведения.</summary>
    public static bool DefaultTouch { get; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || Environment.GetEnvironmentVariable("RADAR_UI_MOBILE") == "1";

    /// <summary>
    /// Представление на внешнем экране (трансляция с телефона на телевизор или монитор): раскладка как у десктопа —
    /// в разрешении того экрана, без сенсорных стилей, состояние не сохраняется (им управляет экран телефона).
    /// </summary>
    public bool IsExternal { get; }
    /// <summary>Сенсорный экран без мыши: крупные цели касания, без наведения. Внешний экран — не сенсорный.</summary>
    public bool TouchFirst => DefaultTouch && !IsExternal;

    /// <summary>Отладка снимков: RADAR_UI_SAFE=лево,верх,право,низ — имитация выреза и системных панелей.</summary>
    internal void DebugSafeArea(Thickness t) { Safe = t; _layoutDone = false; TitleBar.Height = BarHeight + t.Top; TitleBarGrid.Margin = new Thickness(t.Left, t.Top, t.Right, 0); LoadLine.Margin = new Thickness(0, BarHeight + t.Top, 0, 0); ApplyLayout(Bounds.Width, Bounds.Height); }

    /// <summary>Высота полосы заголовка: на сенсорном экране — 44 dp (минимальная цель касания), на десктопе — 28.</summary>
    public double BarHeight => TouchFirst ? 44 : 28;

    private double SidebarWidth { get; set; } = WideSidebar;

    /// <summary>Отступы безопасной зоны: вырез камеры, строка состояния, жестовая панель, клавиатура.</summary>
    private Thickness Safe { get; set; }

    /// <summary>Колонки карточек сводки статистики (3 на широком, 2 на узком).</summary>
    public static readonly StyledProperty<int> KpiColumnsProperty = AvaloniaProperty.Register<MainView, int>(nameof(KpiColumns), 3);
    public int KpiColumns { get => GetValue(KpiColumnsProperty); set => SetValue(KpiColumnsProperty, value); }

    private TopLevel? _top;
    private IInsetsManager? _insets;
    private IInputPane? _pane;

    /// <summary>Представление, показанное сейчас (на телефоне — одно): для вызовов из активности.</summary>
    private static MainView? _current;

    /// <summary>Сохранить камеру и панели (активность уходит в фон).</summary>
    public static void SaveCurrent() => _current?.SaveState();

    /// <summary>Открыть ленту тревог (нажатие на уведомление).</summary>
    public static void OpenFeedCurrent()
    {
        if (_current is not { } v) return;
        if (v._vm is { } vm && !vm.IsLive) vm.Mode = ViewModels.AppMode.Live;
        v.SideTabs.SelectedIndex = 0;
        v._sidebarAutoHidden = false;
        v.SetSidebarHidden(false);
    }

    /// <summary>Начальная настройка раскладки (из конструктора): высота шапки, сенсорный режим.</summary>
    private void InitLayout()
    {
        TitleBar.Height = BarHeight;
        LoadLine.Margin = new Thickness(0, BarHeight, 0, 0);
        StatsBoard.Margin = new Thickness(0, BarHeight + 1, 0, 0);
        ModeDismiss.Margin = new Thickness(0, BarHeight + 1, 0, 0);
        TopStack.Margin = new Thickness(0, BarHeight + 12, 0, 0);
        Sidebar.Margin = new Thickness(0, BarHeight + 12, 12, 12);
        if (TouchFirst)
        {
            Classes.Add("touch");
            TouchTips.Attach(this);
            // масштаб — жестом двух пальцев, кнопки окна не нужны
            ZoomInButton.IsVisible = ZoomOutButton.IsVisible = false;
        }
        // кнопки окна и рамка — только у окна десктопа (его MainWindow включает их, задавая Chrome)
        WinButtons.IsVisible = ResizeGrips.IsVisible = WindowFrame.IsVisible = false;
        SizeChanged += (_, e) => { if (_insets != null) UpdateSafeArea(); ApplyLayout(e.NewSize.Width, e.NewSize.Height); };
        StatsHeader.SizeChanged += (_, _) =>
        {
            var l = StatsLeftScroll.Margin;
            StatsLeftScroll.Margin = new Thickness(l.Left, StatsTop(), l.Right, l.Bottom);
            StatsRightScroll.Margin = new Thickness(0, StatsTop(), 0, 0);
        };
    }

    private void AttachPlatform(TopLevel? top)
    {
        _top = top;
        if (top == null || IsExternal) return;
        _current = this;
        // отступы безопасной зоны раскладка расставляет сама (шапка и карта уходят под строку состояния);
        // автоматический отступ платформы сдвинул бы всё представление вниз второй раз
        TopLevel.SetAutoSafeAreaPadding(this, false);
        TopLevel.SetAutoSafeAreaPadding(top, false);
        _insets = top.InsetsManager;
        if (_insets != null)
        {
            // рисуем под системными панелями (карта во весь экран), а элементы управления отодвигаем на безопасную зону
            _insets.DisplayEdgeToEdgePreference = true;
            _insets.SystemBarColor = Avalonia.Media.Color.Parse("#07090C");
            _insets.SafeAreaChanged += OnSafeAreaChanged;
        }
        _pane = top.InputPane;
        if (_pane != null) _pane.StateChanged += OnInputPane;
        top.BackRequested += OnBackRequested;
        // отступы безопасной зоны платформа пересчитывает в dp по текущему масштабу: при первом чтении он ещё 1,
        // и вырез в 107 px выглядел как 107 dp — перечитываем при смене масштаба и размера
        top.ScalingChanged += OnScalingChanged;
        UpdateSafeArea();
    }

    private void DetachPlatform()
    {
        if (_insets != null) _insets.SafeAreaChanged -= OnSafeAreaChanged;
        if (_pane != null) _pane.StateChanged -= OnInputPane;
        if (_top != null) { _top.BackRequested -= OnBackRequested; _top.ScalingChanged -= OnScalingChanged; }
        _insets = null; _pane = null;
        if (_current == this) _current = null;
        _top = null;
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => UpdateSafeArea();
    private void OnScalingChanged(object? sender, EventArgs e) => UpdateSafeArea();
    private void OnInputPane(object? sender, InputPaneStateEventArgs e) => UpdateSafeArea();

    private void UpdateSafeArea()
    {
        var safe = _insets?.SafeAreaPadding ?? default;
        // открытая клавиатура перекрывает низ экрана: поле поиска и списки поднимаются над ней
        if (_pane is { State: InputPaneState.Open } pane && pane.OccludedRect.Height > 0)
            safe = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, pane.OccludedRect.Height));
        if (safe == Safe && _layoutDone) return;
        Safe = safe;
        // шапка уходит под строку состояния: её фон тянется до края экрана, содержимое — ниже выреза
        TitleBar.Height = BarHeight + safe.Top;
        TitleBarGrid.Margin = new Thickness(safe.Left, safe.Top, safe.Right, 0);
        // линия загрузки — по нижней кромке шапки, а не поперёк неё (шапка выросла на высоту строки состояния)
        LoadLine.Margin = new Thickness(0, BarHeight + safe.Top, 0, 0);
        ApplyLayout(Bounds.Width, Bounds.Height);
    }

    private bool _layoutDone;
    private double _lastW = -1, _lastH = -1;

    /// <summary>Раскладка под размер: вызывается при смене размера, поворота, выреза, клавиатуры.</summary>
    private void ApplyLayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        bool force = !_layoutDone;
        _layoutDone = true;
        bool sizeChanged = width != _lastW || height != _lastH;
        _lastW = width; _lastH = height;
        var safe = Safe;
        double top = BarHeight + safe.Top;

        bool wasDrawer = DrawerMode;
        DrawerMode = width < DrawerBelow;
        Compact = width < CompactBelow;
        Classes.Set("compact", Compact);
        Classes.Set("drawer", DrawerMode);

        // ---- боковая панель ----
        // шторка на телефоне — во всю ширину (за вычетом выреза); на планшете — 412 dp, но не шире 88% экрана
        double usable = width - safe.Left - safe.Right;
        SidebarWidth = !DrawerMode ? WideSidebar : Compact ? usable : Math.Min(WideSidebar + 28, usable * 0.88);
        Root.ColumnDefinitions[1].Width = new GridLength(DrawerMode ? 0 : WideSidebar);
        Grid.SetColumn(Sidebar, DrawerMode ? 0 : 1);
        Grid.SetColumnSpan(Sidebar, DrawerMode ? 2 : 1);
        Sidebar.HorizontalAlignment = DrawerMode ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        Sidebar.Width = DrawerMode ? SidebarWidth - (Compact ? 0 : 12) : double.NaN;
        // отступ панели от шапки и краёв — один и тот же (12), от нижнего края — над жестовой панелью
        Sidebar.Margin = Compact
            ? new Thickness(safe.Left, top, safe.Right, 0)
            : new Thickness(0, top + 12, 12 + safe.Right, 12 + safe.Bottom);
        Sidebar.Chamfer = Compact ? 0 : 10;
        // нижняя строка статуса шторки — над жестовой панелью
        Sidebar.Padding = Compact ? new Thickness(0, 0, 0, safe.Bottom) : default;
        SidebarScrim.Margin = new Thickness(0, top, 0, 0);
        // плашки над картой — под шапкой, которая на телефоне выросла на высоту строки состояния
        TopStack.Margin = new Thickness(0, top + 12, 0, 0);

        // переход колонка ↔ шторка: на узком экране шторка при открытии закрыта (на виду карта)
        if (force || wasDrawer != DrawerMode)
        {
            if (DrawerMode && !_sidebarHidden && !_userOpenedDrawer) SetSidebarHiddenInstant(true);
            else if (!DrawerMode && _sidebarHidden && !_userHidSidebar && _state is { SidebarHidden: false }) SetSidebarHiddenInstant(false);
        }
        else if (_sidebarHidden && sizeChanged)
            // ширина шторки поменялась (поворот) — спрятанная уезжает за новый край
            Sidebar.RenderTransform = SlideX(SidebarWidth + 24 + safe.Right);
        ToolTip.SetTip(SidebarToggle, _sidebarHidden ? "Показать ленту и настройки" : "Скрыть ленту и настройки");

        // ---- шапка ----
        BrandText.IsVisible = width >= 420;
        // без кнопок окна (телефон) правый разделитель повис бы у края, а левый край марки отстоит на 12 —
        // справа тот же отступ, что слева: шапка симметрична
        bool chrome = _chrome != null;
        MapSepRight.IsVisible = chrome;
        // последняя кнопка 44 dp с значком 12 по центру: от значка до края 16 — столько же слева до марки
        MapButtons.Margin = default;
        Brand.Margin = new Thickness(chrome ? 12 : 16, 0, 0, 0);
        UpdateTelemetryLayout(TitleBar.Bounds.Width > 0 ? TitleBar.Bounds.Width : width);

        // ---- плавающие панели над картой ----
        // плавающие панели на узком экране — во всю ширину с полями 12 с обеих сторон (как у легенды)
        double panelW = Math.Max(200, usable - 24);
        ReplayPanel.Width = Math.Min(460, panelW);
        HistoryPanel.Width = Math.Min(300, panelW);
        LegendPanel.Width = Math.Min(340, panelW);
        // баннер выбора зон: на узком экране переключатель и «Готово» — вторым рядом
        bool pickNarrow = usable < 760;
        PickBanner.MaxWidth = panelW;
        Grid.SetRow(PickModeSeg, pickNarrow ? 1 : 0);
        Grid.SetColumn(PickModeSeg, pickNarrow ? 1 : 2);
        Grid.SetRow(PickDone, pickNarrow ? 1 : 0);
        // во втором ряду переключатель занимает всё место до «Готово» — подписи не обрезаются при крупном шрифте
        Grid.SetColumnSpan(PickModeSeg, pickNarrow ? 2 : 1);
        PickModeSeg.Width = pickNarrow ? double.NaN : 196;
        PickModeSeg.HorizontalAlignment = HorizontalAlignment.Stretch;

        // ---- статистика ----
        LayoutStats(width, safe);

        ApplySidebarSpace();
        FitOverlays();
    }

    private bool _userOpenedDrawer, _userHidSidebar;

    private void SetSidebarHiddenInstant(bool hidden)
    {
        var tr = Sidebar.Transitions;
        Sidebar.Transitions = null;
        SetSidebarHidden(hidden);
        AnimateSidebarSpace(hidden ? 0 : 1, instant: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Sidebar.Transitions = tr, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Затемнение под открытой шторкой — только в режиме шторки.</summary>
    private void UpdateScrim()
    {
        bool on = DrawerMode && !_sidebarHidden;
        if (on) SidebarScrim.IsVisible = true;
        SidebarScrim.Opacity = on ? 1 : 0;
        SidebarScrim.IsHitTestVisible = on;
        if (!on) Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (!(DrawerMode && !_sidebarHidden)) SidebarScrim.IsVisible = false; }, TimeSpan.FromMilliseconds(320));
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseDrawer();
        e.Handled = true;
    }

    private void CloseDrawer()
    {
        if (!DrawerMode || _sidebarHidden) return;
        _userOpenedDrawer = false;
        SetSidebarHidden(true);
    }

    /// <summary>
    /// Системная кнопка/жест «назад»: сначала закрывается открытое (меню режимов, выбор месяца, шторка, выбор зон),
    /// затем — возврат из истории или статистики в реальное время. Только потом приложение уходит в фон.
    /// </summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (vm.ModeMenuOpen) { vm.ModeMenuOpen = false; e.Handled = true; return; }
        if (vm.MonthPickerOpen) { vm.MonthPickerOpen = false; e.Handled = true; return; }
        if (vm.PickingOnMap) { vm.PickingOnMap = false; e.Handled = true; return; }
        if (DrawerMode && !_sidebarHidden) { CloseDrawer(); e.Handled = true; return; }
        if (!vm.IsLive) { vm.Mode = ViewModels.AppMode.Live; e.Handled = true; }
    }

    /// <summary>Статистика: две колонки на широком экране, одна общая прокрутка на узком.</summary>
    private void LayoutStats(double width, Thickness safe)
    {
        // две колонки — только когда левой после рейтингов (400) остаётся место под пары блоков без обрезки подписей
        bool narrow = width < 1100;
        KpiColumns = width < 520 ? 2 : 3;
        StatsBoard.Margin = new Thickness(safe.Left, BarHeight + 1 + safe.Top, safe.Right, safe.Bottom);
        // шапка дашборда: на узком экране — заголовок, затем период (и даты своего диапазона), затем регион
        bool headNarrow = width < 760;
        // пустые строки сетки в одну строку не должны добавлять межстрочный зазор (лишний низ шапки)
        StatsHeaderGrid.RowSpacing = headNarrow ? 8 : 0;
        Grid.SetRow(StatsPeriodBox, headNarrow ? 1 : 0);
        Grid.SetColumn(StatsPeriodBox, headNarrow ? 0 : 3);
        Grid.SetColumnSpan(StatsPeriodBox, headNarrow ? 4 : 1);
        StatsPeriodBox.HorizontalAlignment = headNarrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetRow(StatsRegionBox, headNarrow ? 2 : 0);
        Grid.SetColumn(StatsRegionBox, headNarrow ? 0 : 2);
        Grid.SetColumnSpan(StatsRegionBox, headNarrow ? 4 : 1);
        StatsRegionHint.Text = TouchFirst ? "Все регионы · касание региона на карте — фильтр" : "Все регионы · клик по региону на карте — фильтр";
        StatsRegionHint.TextWrapping = headNarrow ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
        // свой диапазон: на телефоне даты — строкой под списком периодов
        StatsPeriodBox.Orientation = width < 520 ? Orientation.Vertical : Orientation.Horizontal;
        if (StatsPeriodBox.Orientation == Orientation.Vertical && StatsPeriodBox.Children[0] is not ComboBox)
            StatsPeriodBox.Children.Move(0, 1);
        else if (StatsPeriodBox.Orientation == Orientation.Horizontal && StatsPeriodBox.Children[0] is ComboBox)
            StatsPeriodBox.Children.Move(0, 1);
        // даты периода в заголовке — на телефоне не помещаются рядом со словом «статистика»
        StatsRangeText.IsVisible = width >= 520;

        // правая колонка рейтингов: на узком экране — продолжение общей ленты блоков
        if (narrow && StatsRight.Parent == StatsRightScroll)
        {
            StatsRightScroll.Content = null;
            StatsLeft.Children.Add(_statsRightHost ??= new ContentControl());
            _statsRightHost.Content = StatsRight;
        }
        else if (!narrow && StatsRight.Parent != StatsRightScroll && _statsRightHost != null)
        {
            _statsRightHost.Content = null;
            StatsLeft.Children.Remove(_statsRightHost);
            StatsRightScroll.Content = StatsRight;
        }
        // скрытая правая колонка не должна оставлять зазор сетки справа
        Grid.SetColumnSpan(StatsLeftScroll, narrow ? 2 : 1);
        StatsRightScroll.Width = narrow ? 0 : 400;
        if (narrow) StatsRightScroll.IsVisible = false;
        else StatsRightScroll.Bind(IsVisibleProperty, Avalonia.Data.CompiledBinding.Create((ViewModels.StatsViewModel s) => s.HasData));
        StatsGrid.ColumnSpacing = narrow ? 0 : 10;
        StatsGrid.Margin = new Thickness(width < CompactBelow ? 8 : 12);
        StatsLeftScroll.Margin = new Thickness(0, StatsTop(), 0, 0);
        StatsRightScroll.Margin = new Thickness(0, StatsTop(), 0, 0);
        // попарные блоки: рядом на широком, друг под другом на узком — с одинаковым зазором 10
        double leftW = width - safe.Left - safe.Right - 2 * StatsGrid.Margin.Left - (narrow ? 0 : 410);
        bool stackPairs = leftW < 740;
        foreach (var (row, second, wide) in new[] { (StatsWeekRow, (Control)StatsSectorPanel, 270.0), (StatsKindsRow, StatsLaunchPanel, double.NaN) })
        {
            row.ColumnDefinitions[1].Width = stackPairs ? new GridLength(0) : double.IsNaN(wide) ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            row.ColumnSpacing = stackPairs ? 0 : 10;
            row.RowSpacing = stackPairs ? 10 : 0;
            Grid.SetColumn(second, stackPairs ? 0 : 1);
            Grid.SetRow(second, stackPairs ? 1 : 0);
            second.Width = stackPairs || double.IsNaN(wide) ? double.NaN : wide;
        }
    }

    private ContentControl? _statsRightHost;

    /// <summary>Сдвиг по горизонтали без разбора строки: в русской локали дробь «440,2» в строке трансформации ломала разбор.</summary>
    private static Avalonia.Media.Transformation.TransformOperations SlideX(double x)
    {
        var b = Avalonia.Media.Transformation.TransformOperations.CreateBuilder(1);
        b.AppendTranslate(x, 0);
        return b.Build();
    }

    /// <summary>Отступ блоков статистики под шапкой дашборда: по фактической высоте шапки (на телефоне она в три строки).</summary>
    private double StatsTop() => Math.Max(64, (StatsHeader.Bounds.Height > 1 ? StatsHeader.Bounds.Height : StatsHeader.DesiredSize.Height) + 10);

    /// <summary>Открыть ссылку (канал, сообщение) во внешнем приложении: браузере или Telegram.</summary>
    private void OpenUri(Uri uri)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } l) _ = l.LaunchUriAsync(uri);
    }
}
