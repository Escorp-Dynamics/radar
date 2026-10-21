using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Radar.Services;

namespace Radar.Views;

/// <summary>
/// Окно десктопа: своя рамка без системного заголовка вокруг <see cref="MainView"/>. Хранит геометрию окна между запусками,
/// возвращает окно с отключённого монитора; всё содержимое — в представлении (общем с мобильной версией).
/// </summary>
public sealed class MainWindow : Window, IWindowChrome
{
    public MainView View { get; }

    public MainWindow()
    {
        Title = "Радар — воздушная обстановка";
        Width = 1440; Height = 880; MinWidth = 960; MinHeight = 600;
        WindowDecorations = WindowDecorations.None;
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#07090C"));
        View = new MainView();
        Content = View;
        View.Chrome = this;
    }

    public bool Framed => WindowState == WindowState.Normal;
    public void BeginMove(PointerPressedEventArgs e) => BeginMoveDrag(e);
    public void BeginResize(WindowEdge edge, PointerPressedEventArgs e) => BeginResizeDrag(edge, e);
    public void Minimize() => WindowState = WindowState.Minimized;
    public void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void IWindowChrome.Close() => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        // свёрнутое окно сохраняется в том виде, каким было до сворачивания (развёрнутым или обычным)
        if (WindowState != WindowState.Minimized) _wasMaximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        // восстановленное окно могло остаться на отключённом мониторе
        if (WindowState == WindowState.Normal) Avalonia.Threading.Dispatcher.UIThread.Post(EnsureOnScreen, Avalonia.Threading.DispatcherPriority.Background);
        View.OnWindowStateChanged(WindowState == WindowState.Normal, WindowState is WindowState.Maximized or WindowState.FullScreen, WindowState == WindowState.Minimized);
    }

    // ---------------- состояние между запусками ----------------

    private AppSettings? _state;
    private PixelPoint _normalPos;
    private Size _normalSize;
    private bool _posKnown, _wasMaximized;
    private string? _savedStateKey;

    /// <summary>Применяет сохранённые геометрию окна, камеру карты и состояние панелей. Вызывать до показа окна.</summary>
    public void RestoreState(AppSettings s)
    {
        _state = s;
        if (s.WindowWidth is double ww && s.WindowHeight is double wh && ww >= MinWidth && wh >= MinHeight)
        {
            Width = ww;
            Height = wh;
        }
        if (s.WindowX is int x && s.WindowY is int y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _posKnown = true;
        }
        _normalPos = Position;
        _normalSize = new Size(Width, Height);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        View.RestoreState(s);

        // до показа окна и сразу после него платформа может сообщать нулевую позицию — доверяем только перемещениям после открытия
        bool opened = false;
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            opened = true;
            // первый запуск: окно поставлено платформой (по центру) — запоминаем фактическое место, а не (0, 0)
            if (!_posKnown && WindowState == WindowState.Normal) { _normalPos = Position; _posKnown = true; }
        }, Avalonia.Threading.DispatcherPriority.Background);
        PositionChanged += (_, e) => { if (opened && WindowState == WindowState.Normal) { _normalPos = e.Point; _posKnown = true; } };
        Resized += (_, _) => { if (WindowState == WindowState.Normal) _normalSize = ClientSize; };
        Opened += (_, _) => EnsureOnScreen();
        Closing += (_, _) => SaveState();
        // аварийное завершение (сигнал, выход из сеанса) не вызывает Closing — сохраняемся и при уходе фокуса
        Deactivated += (_, _) => { SaveState(onlyIfChanged: true); if (DataContext is ViewModels.MainViewModel vm) vm.WindowActive = false; };
        Activated += (_, _) => { if (DataContext is ViewModels.MainViewModel vm) vm.WindowActive = true; };
    }

    /// <summary>Окно, сохранённое на отключённом мониторе, возвращается на основной экран.</summary>
    private void EnsureOnScreen()
    {
        if (Screens.All.Count == 0 || WindowState != WindowState.Normal) return;
        double scaling = DesktopScaling;
        var rect = new PixelRect(Position, PixelSize.FromSize(ClientSize, scaling));
        // окно, сохранённое на большом мониторе, не крупнее рабочей области экрана, на котором оказалось
        var host = Screens.ScreenFromPoint(rect.Center) ?? Screens.Primary ?? Screens.All[0];
        double maxW = host.WorkingArea.Width / scaling, maxH = host.WorkingArea.Height / scaling;
        if (Width > maxW || Height > maxH)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, maxW));
            Height = Math.Max(MinHeight, Math.Min(Height, maxH));
            rect = new PixelRect(Position, PixelSize.FromSize(new Size(Width, Height), scaling));
            if (!host.WorkingArea.Contains(rect))
                // окно (по MinWidth/MinHeight) может быть больше рабочей области: Math.Clamp при max < min бросает исключение
                Position = new PixelPoint(Math.Max(host.WorkingArea.X, Math.Min(rect.X, host.WorkingArea.Right - rect.Width)), Math.Max(host.WorkingArea.Y, Math.Min(rect.Y, host.WorkingArea.Bottom - rect.Height)));
            _normalPos = Position;
            _posKnown = true;
            return;
        }
        // достаточно, чтобы полоса заголовка хотя бы частично была видна на каком-либо экране
        var grip = new PixelRect(rect.X + 40, rect.Y, Math.Max(1, rect.Width - 80), (int)(28 * scaling));
        if (Screens.All.Any(sc => sc.WorkingArea.Intersects(grip))) return;
        var area = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        int w = Math.Min(rect.Width, area.Width), h = Math.Min(rect.Height, area.Height);
        Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
        _normalPos = Position;
        _posKnown = true;
    }

    /// <summary>
    /// Сохраняет геометрию окна, камеру и панели. <paramref name="onlyIfChanged"/> — при уходе фокуса: запись на диск
    /// синхронная, поэтому файл пишется, только если с прошлого сохранения что-то изменилось.
    /// </summary>
    public void SaveState(bool onlyIfChanged = false)
    {
        if (_state is not { } s) return;
        if (WindowState == WindowState.Normal) _normalSize = ClientSize;
        bool maximized = WindowState == WindowState.Minimized ? _wasMaximized : WindowState is WindowState.Maximized or WindowState.FullScreen;
        var key = FormattableString.Invariant($"{maximized}|{_posKnown}|{_normalPos.X},{_normalPos.Y}|{_normalSize.Width:0.#},{_normalSize.Height:0.#}|{View.StateKey}");
        if (onlyIfChanged && key == _savedStateKey) return;
        s.WindowMaximized = maximized;
        if (_posKnown) (s.WindowX, s.WindowY) = (_normalPos.X, _normalPos.Y);
        (s.WindowWidth, s.WindowHeight) = (_normalSize.Width, _normalSize.Height);
        View.WriteState(s);
        if (onlyIfChanged) SettingsStore.SaveDeferred(s); else SettingsStore.Save(s);
        _savedStateKey = key;
    }
}
