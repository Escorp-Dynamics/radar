using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Radar.Android;

/// <summary>
/// Служба переднего плана: работает, пока включено слежение, и держит процесс живым, когда приложение свёрнуто или закрыто (смахнуто из недавних), —
/// опрос каналов и оповещения продолжаются, как у мессенджера. Если процесс поднят системой без окна (перезагрузка,
/// обновление, перезапуск убитой службы), модель и опрос запускаются здесь же. В шторке — тихое постоянное уведомление.
/// Тип specialUse, а не dataSync: у dataSync с Android 15 лимит 6 ч в сутки и запрет запуска после загрузки.
/// </summary>
[Service(Exported = false, ForegroundServiceType = (ForegroundService)0x40000000)]
[MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "Непрерывное слежение за публичными каналами воздушных тревог и оповещение о новых тревогах")]
public sealed class WatchService : Service
{
    private const int NotificationId = 1;
    private const ForegroundService SpecialUse = (ForegroundService)0x40000000;
    private static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(10);
    private PowerManager.WakeLock? _wake;

    public static bool Running { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var platform = AndroidPlatform.Current;
        var n = new Notification.Builder(this, AndroidPlatform.ServiceChannel)
            .SetSmallIcon(Resource.Drawable.ic_notify)
            .SetContentTitle("Радар следит за обстановкой")
            .SetContentText("Оповещения о тревогах придут, даже когда приложение закрыто")
            .SetOngoing(true)
            .SetShowWhen(false)
            .SetContentIntent(platform?.OpenAppIntent(feed: false))
            .Build();
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake) StartForeground(NotificationId, n, SpecialUse);
            else StartForeground(NotificationId, n);
        }
        catch (Exception ex)
        {
            // запуск из фона запрещён (Android 12+ без исключения из оптимизации батареи) — без службы
            System.Diagnostics.Trace.WriteLine($"фоновая служба: {ex.Message}");
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        Running = true;
        // процесс поднят без окна — модель, опрос и оповещения запускаются здесь
        var vm = App.EnsureSharedModel();
        if (!vm.Settings.BackgroundWatch)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        // частичная блокировка сна: опрос по таймеру не замирает при выключенном экране
        if (_wake == null && GetSystemService(PowerService) is PowerManager pm)
        {
            _wake = pm.NewWakeLock(WakeLockFlags.Partial, "radar:watch");
            _wake?.SetReferenceCounted(false);
            _wake?.Acquire();
        }
        // сторож: если прошивка убьёт процесс без перезапуска «липкой» службы, будильник поднимет её снова
        AndroidPlatform.ScheduleRestart(this, Watchdog);
        return StartCommandResult.Sticky;
    }

    /// <summary>Приложение смахнули из недавних: некоторые прошивки при этом завершают процесс — служба перезапустится.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        if (App.SharedViewModel?.Settings.BackgroundWatch == false) return;
        AndroidPlatform.ScheduleRestart(this, TimeSpan.FromSeconds(3));
    }

    public override void OnDestroy()
    {
        Running = false;
        if (_wake is { IsHeld: true }) _wake.Release();
        _wake = null;
        base.OnDestroy();
    }
}

/// <summary>Запуск слежения после перезагрузки телефона и после обновления приложения — без открытия окна.</summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionLockedBootCompleted, Intent.ActionMyPackageReplaced, "android.intent.action.QUICKBOOT_POWERON"])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;
        if (!Radar.Services.SettingsStore.Load().BackgroundWatch) return;
        AndroidPlatform.StartWatch(context);
    }
}
