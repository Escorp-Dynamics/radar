using Avalonia.Controls;
using Avalonia.Input;

namespace Radar.Views;

/// <summary>Окно десктопа вокруг <see cref="MainView"/>: перетаскивание за шапку, кнопки окна, рамка, сохранение геометрии.</summary>
public interface IWindowChrome
{
    /// <summary>Обычное (не развёрнутое) окно: видна рамка и зоны изменения размера.</summary>
    bool Framed { get; }
    void BeginMove(PointerPressedEventArgs e);
    void BeginResize(WindowEdge edge, PointerPressedEventArgs e);
    void Minimize();
    void ToggleMaximize();
    void Close();
    void SaveState(bool onlyIfChanged);
}
