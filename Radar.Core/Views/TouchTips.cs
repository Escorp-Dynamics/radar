using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Radar.Views;

/// <summary>
/// Подсказки на сенсорном экране: у пальца нет наведения, поэтому подсказка элемента (ToolTip.Tip) открывается
/// долгим нажатием — как в системе Android — и закрывается сама через несколько секунд или при следующем касании.
/// Касание после долгого нажатия не срабатывает как щелчок.
/// </summary>
internal static class TouchTips
{
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(4);
    private const double MoveTolerance = 12;

    /// <summary>Подсказка открыта (true) или закрыта у элемента — для зеркала на внешнем экране.</summary>
    public static event Action<Control, bool>? Toggled;

    private static IDisposable? _hold, _hide;
    private static Control? _target, _open;
    private static Point _start;
    private static bool _swallowRelease;

    public static void Attach(Control root)
    {
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => CancelHold(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return;
        Close();
        CancelHold();
        _target = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => ToolTip.GetTip(c) is string { Length: > 0 } or Control);
        if (_target == null || sender is not Visual root) return;
        _start = e.GetPosition(root);
        var target = _target;
        _hold = DispatcherTimer.RunOnce(() => Open(target), HoldDelay);
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch || _hold == null || sender is not Visual root) return;
        var d = e.GetPosition(root) - _start;
        if (Math.Abs(d.X) + Math.Abs(d.Y) > MoveTolerance) CancelHold();
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return;
        CancelHold();
        // палец отпущен после показа подсказки — это не щелчок по элементу
        if (_swallowRelease) { _swallowRelease = false; e.Handled = true; }
    }

    private static void Open(Control c)
    {
        _hold = null;
        if (!c.IsEffectivelyVisible) return;
        _open = c;
        _swallowRelease = true;
        ToolTip.SetPlacement(c, PlacementMode.Top);
        ToolTip.SetIsOpen(c, true);
        Toggled?.Invoke(c, true);
        _hide?.Dispose();
        _hide = DispatcherTimer.RunOnce(Close, ShowFor);
    }

    private static void Close()
    {
        _hide?.Dispose();
        _hide = null;
        if (_open != null) { ToolTip.SetIsOpen(_open, false); Toggled?.Invoke(_open, false); }
        _open = null;
    }

    private static void CancelHold()
    {
        _hold?.Dispose();
        _hold = null;
    }
}
