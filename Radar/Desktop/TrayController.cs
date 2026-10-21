using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Radar.Desktop;

/// <summary>Значок в области уведомлений: клик — показать окно, меню — «Открыть» и «Выход».</summary>
public sealed class TrayController
{
    private readonly Window _window;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private TrayIcon? _icon;

    public TrayController(Window window, IClassicDesktopStyleApplicationLifetime desktop) { _window = window; _desktop = desktop; }

    /// <summary>Выход из меню трея: окно закрывается по-настоящему, а не прячется.</summary>
    public bool Exiting { get; private set; }

    public static WindowIcon LoadIcon() => new(AssetLoader.Open(new Uri("avares://Radar.Core/Assets/radar.png")));

    public void Install(Application app)
    {
        var open = new NativeMenuItem("Открыть Радар");
        open.Click += (_, _) => Show();
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => Exit();
        _icon = new TrayIcon { Icon = LoadIcon(), ToolTipText = "Радар", Menu = new NativeMenu { open, new NativeMenuItemSeparator(), exit } };
        _icon.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(app, [_icon]);
    }

    public void Show()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Show); return; }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Exit()
    {
        Exiting = true;
        _icon?.Dispose();
        _desktop.Shutdown();
    }
}
