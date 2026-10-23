using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Radar.Services;

namespace Radar.Android;

/// <summary>
/// Сервисы Android: каналы уведомлений (тревоги — со звуком и вибрацией, служба — тихий), звук сигналов через MediaPlayer,
/// фоновая служба опроса, запрос разрешения на уведомления (Android 13+).
/// </summary>
public sealed class AndroidPlatform : IPlatformServices
{
    public const string AlertChannel = "alerts";
    public const string ServiceChannel = "watch";
    public const string ExtraOpen = "radar.open";

    public static AndroidPlatform? Current { get; private set; }

    private readonly Context _ctx;
    private int _nextId = 100;
    private bool _foreground;

    public AndroidPlatform(Context ctx)
    {
        _ctx = ctx;
        Current = this;
        CreateChannels();
    }

    public string DataDirectory => _ctx.FilesDir?.AbsolutePath ?? AppPaths.Root;

    private NotificationManager? Manager => (NotificationManager?)_ctx.GetSystemService(Context.NotificationService);

    private void CreateChannels()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O || Manager is not { } nm) return;
        var alerts = new NotificationChannel(AlertChannel, "Тревоги", NotificationImportance.High)
        {
            Description = "Новые тревоги в выбранных регионах и зонах",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        alerts.EnableVibration(true);
        alerts.SetVibrationPattern([0, 250, 150, 250]);
        nm.CreateNotificationChannel(alerts);
        var watch = new NotificationChannel(ServiceChannel, "Слежение в фоне", NotificationImportance.Low)
        {
            Description = "Постоянное уведомление, пока Радар опрашивает каналы в фоне",
        };
        watch.SetShowBadge(false);
        nm.CreateNotificationChannel(watch);
    }

    internal PendingIntent OpenAppIntent(bool feed)
    {
        var intent = new Intent(_ctx, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        if (feed) intent.PutExtra(ExtraOpen, "feed");
        return PendingIntent.GetActivity(_ctx, feed ? 1 : 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    public void Notify(string title, string body, bool urgent)
    {
        if (Manager is not { } nm) return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            _ctx.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) return;
        var n = new Notification.Builder(_ctx, AlertChannel)
            .SetSmallIcon(Resource.Drawable.ic_notify)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new Notification.BigTextStyle().BigText(body))
            .SetCategory(urgent ? Notification.CategoryAlarm : Notification.CategoryStatus)
            .SetColor(unchecked((int)0xFFFF4D5A))
            .SetAutoCancel(true)
            .SetContentIntent(OpenAppIntent(feed: true))
            .Build();
        nm.Notify(Interlocked.Increment(ref _nextId), n);
    }

    /// <summary>Уведомление, открывающее заданную активность (окно установки обновления).</summary>
    public void NotifyAction(string title, string body, Intent target)
    {
        if (Manager is not { } nm) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _ctx.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) return;
        var pi = PendingIntent.GetActivity(_ctx, 2, target, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var n = new Notification.Builder(_ctx, ServiceChannel)
            .SetSmallIcon(Resource.Drawable.ic_notify)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetAutoCancel(true)
            .SetContentIntent(pi)
            .Build();
        nm.Notify(UpdateNotificationId, n);
    }

    public const int UpdateNotificationId = 90;

    public void PlaySound(string wavPath)
    {
        try
        {
            var mp = new MediaPlayer();
            mp.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Notification)!.SetContentType(AudioContentType.Sonification)!.Build());
            mp.SetDataSource(wavPath);
            mp.Completion += (_, _) => mp.Release();
            mp.Prepare();
            mp.Start();
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"звук: {ex.Message}"); }
    }

    public void SetBackgroundService(bool on)
    {
        if (!on) { StopService(); return; }
        StartWatch(_ctx);
        if (_activity?.TryGetTarget(out var a) == true) RequestBatteryExemption(a);
    }

    // ---------------- жизненный цикл ----------------

    private WeakReference<Activity>? _activity;

    public void OnActivityCreated(Activity activity)
    {
        _activity = new WeakReference<Activity>(activity);
        // Android 13+: уведомления требуют разрешения — спрашиваем один раз при первом запуске
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            activity.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            activity.RequestPermissions([Manifest.Permission.PostNotifications], 1);
        else if (App.SharedViewModel?.Settings.BackgroundWatch == true) RequestBatteryExemption(activity);
    }

    public void OnForeground()
    {
        _foreground = true;
        // служба работает всё время, пока включено слежение (и при открытом окне): остановка запущенной через
        // startForegroundService службы до её startForeground роняет процесс, а окно могут закрыть в любой момент
        if (App.SharedViewModel?.Settings.BackgroundWatch == true) StartWatch(_ctx);
    }

    public void OnBackground()
    {
        _foreground = false;
        if (App.SharedViewModel?.Settings.BackgroundWatch == true) StartWatch(_ctx);
    }

    /// <summary>Запустить службу слежения (из окна, после загрузки, по таймеру перезапуска).</summary>
    public static void StartWatch(Context ctx)
    {
        try
        {
            var intent = new Intent(ctx, typeof(WatchService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
            else ctx.StartService(intent);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"фоновая служба: {ex.Message}"); }
    }

    /// <summary>Перезапуск службы через <paramref name="after"/> (процесс завершён прошивкой при смахивании из недавних).</summary>
    public static void ScheduleRestart(Context ctx, TimeSpan after)
    {
        try
        {
            var intent = new Intent(ctx, typeof(WatchService));
            var pi = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? PendingIntent.GetForegroundService(ctx, 7, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
                : PendingIntent.GetService(ctx, 7, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (pi != null && ctx.GetSystemService(Context.AlarmService) is AlarmManager am)
                am.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, SystemClock.ElapsedRealtime() + (long)after.TotalMilliseconds, pi);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"перезапуск службы: {ex.Message}"); }
    }

    public static void CancelRestart(Context ctx)
    {
        var intent = new Intent(ctx, typeof(WatchService));
        var pi = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? PendingIntent.GetForegroundService(ctx, 7, intent, PendingIntentFlags.Immutable | PendingIntentFlags.NoCreate)
            : PendingIntent.GetService(ctx, 7, intent, PendingIntentFlags.Immutable | PendingIntentFlags.NoCreate);
        if (pi != null && ctx.GetSystemService(Context.AlarmService) is AlarmManager am) am.Cancel(pi);
    }

    /// <summary>
    /// Оптимизация батареи останавливает сеть и таймеры фоновых приложений (режим Doze) и запрещает перезапуск
    /// службы из фона — для оповещений, как у мессенджера, приложение просит исключить его из оптимизации (один раз).
    /// </summary>
    public void RequestBatteryExemption(Activity activity)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M) return;
        if (activity.GetSystemService(Context.PowerService) is not PowerManager pm || pm.IsIgnoringBatteryOptimizations(activity.PackageName)) return;
        var prefs = activity.GetSharedPreferences("radar", FileCreationMode.Private);
        if (prefs == null || prefs.GetBoolean("battery_asked", false)) return;
        prefs.Edit()?.PutBoolean("battery_asked", true)?.Apply();
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
                .SetData(global::Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"оптимизация батареи: {ex.Message}"); }
    }

    private void StopService()
    {
        CancelRestart(_ctx);
        try { _ctx.StopService(new Intent(_ctx, typeof(WatchService))); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"фоновая служба: {ex.Message}"); }
    }

    public bool InForeground => _foreground;

    // ---------------- внешний экран ----------------

    private ExternalScreen? _external;
    public string? ExternalScreen => _external?.Current;
    public event Action? ExternalScreenChanged;

    /// <summary>Слежение за внешними экранами — пока жива активность (презентация привязана к ней).</summary>
    public void AttachExternal(Activity activity)
    {
        _external?.Release();
        _external = new ExternalScreen(activity);
        _external.Changed += () => ExternalScreenChanged?.Invoke();
        ExternalScreenChanged?.Invoke();
    }

    public void DetachExternal(Activity activity)
    {
        _external?.Release();
        _external = null;
        ExternalScreenChanged?.Invoke();
    }

    /// <summary>
    /// Системный выбор экрана трансляции. На разных прошивках он называется по-разному — по очереди:
    /// «Беспроводной дисплей», «Трансляция», затем общий экран подключений.
    /// </summary>
    public void OpenCastSettings()
    {
        foreach (var action in new[] { "android.settings.WIFI_DISPLAY_SETTINGS", "android.settings.CAST_SETTINGS", global::Android.Provider.Settings.ActionWirelessSettings })
        {
            try
            {
                var intent = new Intent(action).AddFlags(ActivityFlags.NewTask);
                if (intent.ResolveActivity(_ctx.PackageManager!) == null) continue;
                _ctx.StartActivity(intent);
                return;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"трансляция: {ex.Message}"); }
        }
    }
}
