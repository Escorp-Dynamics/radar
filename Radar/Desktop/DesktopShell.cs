using Avalonia.Controls.ApplicationLifetimes;
using Radar.Services;
using Radar.ViewModels;
using Radar.Views;

namespace Radar.Desktop;

/// <summary>Обвязка окна десктопа: значок трея, один экземпляр, сворачивание при закрытии, скрытый запуск с системой.</summary>
public static class DesktopShell
{
    public static bool Install(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm, AppSettings settings)
    {
        // замеры и демо-сценарии идут без трея и без защиты от второго экземпляра
        bool service = Environment.GetEnvironmentVariable("RADAR_DEMO") == "1" || Environment.GetEnvironmentVariable("RADAR_IDLE_CORPUS") is { Length: > 0 };
        bool autostart = Environment.GetCommandLineArgs().Contains(Autostart.Flag);
        bool hidden = !service && autostart && settings.StartWithSystem && settings.StartMinimized;
        if (service) { desktop.MainWindow = window; return true; }
        TrayController? tray = null;
        // второй запуск будит первый и завершается: окно в трее иначе не достать, а копии дублировали бы опрос каналов
        if (!SingleInstance.TryAcquire(() => tray?.Show())) return false;
        tray = new TrayController(window, desktop);
        tray.Install(Avalonia.Application.Current!);
        // окно можно спрятать, поэтому приложение завершается только явно: закрытием окна без трея или «Выход» в меню
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        window.Closing += (_, e) =>
        {
            // кнопка «закрыть» своей шапки зовёт Close() — это «программное» закрытие, но для пользователя оно обычное;
            // выход из системы и завершение приложения (в т. ч. «Выход» в меню трея) пропускаются
            if (tray.Exiting || e.CloseReason is Avalonia.Controls.WindowCloseReason.ApplicationShutdown or Avalonia.Controls.WindowCloseReason.OSShutdown) return;
            if (!settings.CloseToTray) return;
            e.Cancel = true;
            window.SaveState();
            vm.WindowActive = false;
            window.Hide();
        };
        window.Closed += (_, _) => desktop.Shutdown();
        desktop.Exit += (_, _) => SingleInstance.Release();
        // свёрнутый запуск: главное окно не назначается (его показ — часть старта), оно откроется из трея
        desktop.MainWindow = hidden ? null : window;
        return true;
    }
}
