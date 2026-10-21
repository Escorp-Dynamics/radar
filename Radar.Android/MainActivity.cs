using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace Radar.Android;

[Activity(
    Label = "Радар",
    Theme = "@style/RadarTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    // поворот, складной экран, многооконный режим, смена плотности — без пересоздания активности (карта не перезагружается)
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidPlatform.Current?.OnActivityCreated(this);
        AndroidPlatform.Current?.AttachExternal(this);
    }

    protected override void OnDestroy()
    {
        AndroidPlatform.Current?.DetachExternal(this);
        base.OnDestroy();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (App.SharedViewModel is { } vm) vm.WindowActive = true;
        AndroidPlatform.Current?.OnForeground();
    }

    protected override void OnPause()
    {
        if (App.SharedViewModel is { } vm) vm.WindowActive = false;
        // камера, панели, вкладка — сохраняются при уходе в фон: процесс могут завершить без предупреждения
        Radar.Views.MainView.SaveCurrent();
        base.OnPause();
    }

    protected override void OnStop()
    {
        AndroidPlatform.Current?.OnBackground();
        base.OnStop();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // после ответа про уведомления — запрос исключения из оптимизации батареи (иначе оба окна сразу)
        if (requestCode == 1 && App.SharedViewModel?.Settings.BackgroundWatch == true) AndroidPlatform.Current?.RequestBatteryExemption(this);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // нажатие на уведомление о тревоге — открыть ленту
        if (intent?.GetStringExtra(AndroidPlatform.ExtraOpen) == "feed") Radar.Views.MainView.OpenFeedCurrent();
    }
}
