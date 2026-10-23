using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Radar.Android;

[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        // как на десктопе (InvariantGlobalization): числа в разметке, кешах и строках трансформаций — с точкой,
        // русская локаль телефона не должна менять разбор «440.2» на «440,2»
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        // данные — во внутреннем хранилище приложения; платформенные сервисы — до создания модели
        Radar.Services.AppPaths.Override = FilesDir?.AbsolutePath;
        var services = new AndroidPlatform(this);
        App.Platform = services;
        Radar.Services.Notifier.Platform = services;
        Radar.Services.Updater.Installer = new AndroidUpdater(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            // те же шрифты, что на десктопе (встроены в Radar.Core)
            .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://Radar.Core/Assets/Fonts#IBM Plex Sans" })
            .LogToTrace();
}
