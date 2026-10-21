using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Radar.Models;
using Radar.Services;
using Radar.ViewModels;
using Radar.Views;

namespace Radar;

public partial class App : Application
{
    /// <summary>
    /// Десктопная обвязка окна (трей, один экземпляр, сворачивание при закрытии) — задаётся головой Radar.
    /// Возвращает false, если приложение должно завершиться (уже запущен другой экземпляр).
    /// </summary>
    public static Func<IClassicDesktopStyleApplicationLifetime, MainWindow, MainViewModel, AppSettings, bool>? DesktopShell { get; set; }

    /// <summary>Мобильная платформа: сервисы ОС (уведомления, фоновая служба, ссылки). Задаётся головой Radar.Android.</summary>
    public static IPlatformServices? Platform { get; set; }

    /// <summary>Модель представления — одна на процесс: мобильная активность пересоздаётся при повороте и возврате из фона.</summary>
    public static MainViewModel? SharedViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ScrollReveal.Register();
    }

    private static void ConfigureShared()
    {
        ThreatCatalog.IconPathResolver = k => Radar.Map.MapIcons.Paths.TryGetValue(k, out var d) ? d : Radar.Map.MapIcons.Paths["alert"];
        // уведомления карточек тревог — в поток интерфейса (движок меняет тревоги из фоновых потоков)
        AlertEvent.NotifyDispatcher = a =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) a();
            else Avalonia.Threading.Dispatcher.UIThread.Post(a);
        };
        MainViewModel.ConfigureGeometry();
        // демо-сценарий (снимки, замеры) без звуков: иначе сыплются сигналы на каждую «новую» тревогу
        if (Environment.GetEnvironmentVariable("RADAR_DEMO") == "1") Notifier.Muted = true;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConfigureShared();
            var settings = SettingsStore.Load();
            var vm = SharedViewModel = new MainViewModel(settings);
            var window = new MainWindow { DataContext = vm, Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Radar.Core/Assets/radar.png"))) };
            window.RestoreState(settings);

            if (DesktopShell is { } shell) { if (!shell(desktop, window, vm, settings)) { Environment.Exit(0); return; } }
            else desktop.MainWindow = window;
            // ShutdownRequested приходит только при выходе из сеанса/по сигналу ОС, а не при закрытии главного окна —
            // поэтому завершение (остановка опроса, запись настроек) ещё и по Exit; выполняется один раз
            bool down = false;
            void Down() { if (down) return; down = true; vm.Shutdown(); }
            desktop.ShutdownRequested += (_, _) => { window.SaveState(); Down(); };
            desktop.Exit += (_, _) => Down();
            // RADAR_IDLE_CORPUS=<архив.json>: замер простоя — обстановка из архива на момент последнего сообщения,
            // «на связи», без опроса каналов; окно живое, анимации появления уже закончились
            if (Environment.GetEnvironmentVariable("RADAR_IDLE_CORPUS") is { Length: > 0 } idle)
            {
                Notifier.Muted = true;
                window.View.ShowIdleScenario(idle);
                return;
            }
            vm.Start();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // каждая активность получает новое представление над общей моделью
            activity.MainViewFactory = CreateMobileView;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = CreateMobileView();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Модель и опрос каналов без окна: фоновая служба поднимает их сама, если процесс запущен системой
    /// (после перезагрузки телефона, обновления или перезапуска службы), — оповещения идут и при закрытом приложении.
    /// </summary>
    public static MainViewModel EnsureSharedModel()
    {
        if (SharedViewModel == null)
        {
            ConfigureShared();
            var settings = SettingsStore.Load();
            SharedViewModel = new MainViewModel(settings) { WindowActive = false };
            SharedViewModel.Start();
        }
        return SharedViewModel;
    }

    /// <summary>Представление для внешнего экрана: та же модель, раскладка в разрешении того экрана.</summary>
    public static MainView CreateExternalView()
    {
        var vm = EnsureSharedModel();
        var view = new MainView(external: true) { DataContext = vm };
        view.RestoreState(vm.Settings);
        return view;
    }

    private static MainView CreateMobileView()
    {
        EnsureSharedModel();
        var view = new MainView { DataContext = SharedViewModel };
        view.RestoreState(SharedViewModel.Settings);
        return view;
    }
}
