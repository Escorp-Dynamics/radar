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
        // после обновления из трея окно остаётся скрытым, как было до перезапуска
        bool hidden = !service && (autostart && settings.StartWithSystem && settings.StartMinimized || Environment.GetCommandLineArgs().Contains(DesktopUpdater.HiddenFlag));
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
        bool restart = false, restartVisible = false;
        desktop.Exit += (_, _) =>
        {
            SingleInstance.Release();
            // новая версия ждёт выхода этого процесса (DesktopUpdater.AwaitPrevious) и сразу берёт «один экземпляр»
            if (restart)
                try { DesktopUpdater.StartNewInstance(restartVisible ? [] : [DesktopUpdater.HiddenFlag]); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { System.Diagnostics.Trace.WriteLine($"перезапуск: {ex.Message}"); }
        };
        void Restart()
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(Restart); return; }
            restart = true;
            // к моменту Exit окно уже закрыто — видимость запоминается сейчас
            restartVisible = window.IsVisible;
            window.SaveState();
            tray.Exit();
        }
        Updater.Restart = Restart;
        // окно спрятано в трей — перезапуск пользователь не заметит; открытое окно не закрываем без спроса
        Updater.RestartIfIdle = () => { if (window.IsVisible) return false; Restart(); return true; };
        // свёрнутый запуск: главное окно не назначается (его показ — часть старта), оно откроется из трея
        desktop.MainWindow = hidden ? null : window;
        return true;
    }
}
