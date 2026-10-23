using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Radar.Services;

namespace Radar.Android;

/// <summary>
/// Установка обновления на Android через PackageInstaller. Приложение, поставленное из apk, обновляет само себя:
/// Android 12+ без вопросов, если установщиком числится оно же (после первого самообновления это так),
/// иначе — одно окно «Обновить» от системы. Разрешение «установка из этого источника» запрашивается один раз.
/// </summary>
public sealed class AndroidUpdater(Context ctx) : IUpdateInstaller
{
    public const string ActionStatus = "ru.escorp.radar.UPDATE_STATUS";

    public string? Unavailable => Updater.IsReleaseBuild ? null : "сборка разработчика";

    /// <summary>
    /// Автоустановка: без разрешения «установка из этого источника» настройки сами не открываются (только по кнопке),
    /// а на экране приложение не перезапускается — установка ждёт, пока его свернут (следующая проверка).
    /// </summary>
    public bool CanInstallUnattended => ctx.PackageManager?.CanRequestPackageInstalls() == true && AndroidPlatform.Current?.InForeground != true;

    public async Task<UpdateInstall> InstallAsync(string package, CancellationToken ct)
    {
        // ошибки системы приходят Java-исключениями — для общей части это сбой ввода-вывода
        try { return await InstallCoreAsync(package, ct); }
        catch (Java.Lang.Exception ex) { throw new IOException(ex.Message, ex); }
    }

    private async Task<UpdateInstall> InstallCoreAsync(string package, CancellationToken ct)
    {
        var pm = ctx.PackageManager ?? throw new InvalidOperationException("PackageManager недоступен");
        if (!pm.CanRequestPackageInstalls())
        {
            // разрешение даётся в системных настройках; после возврата пользователь жмёт «Обновить» ещё раз
            var intent = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources)
                .SetData(global::Android.Net.Uri.Parse("package:" + ctx.PackageName))
                .AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
            throw new InvalidOperationException("разрешите установку обновлений для Радара и нажмите «Обновить» снова");
        }
        var installer = pm.PackageInstaller;
        var p = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        p.SetAppPackageName(ctx.PackageName);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) p.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);
        int id = installer.CreateSession(p);
        using var session = installer.OpenSession(id);
        try
        {
            await using (var src = File.OpenRead(package))
            await using (var dst = session.OpenWrite("radar.apk", 0, src.Length))
            {
                await src.CopyToAsync(dst, ct);
                session.Fsync(dst);
            }
            var status = new Intent(ctx, typeof(UpdateStatusReceiver)).SetAction(ActionStatus).SetPackage(ctx.PackageName);
            // система дописывает в интент статус установки: с Android 12 изменяемость надо указать явно (до него — по умолчанию)
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var pi = PendingIntent.GetBroadcast(ctx, id, status, flags)!;
            session.Commit(pi.IntentSender);
        }
        catch
        {
            session.Abandon();
            throw;
        }
        // успех означает замену процесса: приложение перезапускается системой, ответа этот код не дождётся
        return UpdateInstall.AwaitingUser;
    }
}

/// <summary>Итог сессии установки: нужно подтверждение — показать окно системы; ошибка — в строку статуса настроек.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class UpdateStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
#pragma warning disable CA1422 // до Android 13 — единственный вариант
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422
                if (confirm == null) break;
                confirm.AddFlags(ActivityFlags.NewTask);
                // из фона окно системы не откроется (запрет запуска активностей с Android 10) — тогда уведомлением
                if (AndroidPlatform.Current is { InForeground: false } platform)
                    platform.NotifyAction("Обновление Радара готово", "Нажмите, чтобы установить новую версию.", confirm);
                else context.StartActivity(confirm);
                break;
            case PackageInstallStatus.Success:
                Updater.ClearDownloads();
                break;
            default:
                var msg = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
                Updater.Report(status == PackageInstallStatus.FailureAborted ? "Установка обновления отменена" : $"Обновление не установлено: {msg ?? status.ToString()}", failed: true);
                break;
        }
    }
}
