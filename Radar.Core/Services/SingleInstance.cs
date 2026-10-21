using System.IO.Pipes;

namespace Radar.Services;

/// <summary>
/// Один экземпляр приложения на пользователя. Второй запуск будит первый (показать окно) и завершается:
/// иначе приложение, живущее в трее, плодило бы копии при каждом щелчке по ярлыку.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static CancellationTokenSource? _stop;
    private static string Key => "Radar-" + Environment.UserName;

    /// <summary>true — этот процесс первый (или первый недоступен и копии не избежать); false — активный экземпляр разбужен, выходим.</summary>
    public static bool TryAcquire(Action onWake)
    {
        try
        {
            _mutex = new Mutex(true, Key, out bool first);
            if (first) { Listen(onWake); return true; }
            return !Wake();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AbandonedMutexException or WaitHandleCannotBeOpenedException)
        {
            return true;
        }
    }

    private static bool Wake()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Key, PipeDirection.Out);
            c.Connect(1500);
            c.WriteByte(1);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Listen(Action onWake)
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var s = new NamedPipeServerStream(Key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync(token);
                    if (await s.ReadAsync(new byte[1], token) > 0) onWake();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500, CancellationToken.None); }
            }
        });
    }

    public static void Release()
    {
        _stop?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
    }
}
