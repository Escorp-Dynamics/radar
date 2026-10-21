namespace Radar.Services;

public static class AppPaths
{
    /// <summary>Каталог данных; мобильная платформа задаёт свой (внутреннее хранилище приложения) до первого обращения.</summary>
    public static string? Override { get; set; }
    private static string? _root;
    public static string Root => _root ??= Override is { Length: > 0 } o ? Ensure(o) : Init();

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    /// <summary>Кеш геокодера; RADAR_GEOCODE_CACHE — свой файл (аудит архива — с зафиксированным снимком, а не с растущим кешем пользователя).</summary>
    public static string GeocodeCache =>
        Environment.GetEnvironmentVariable("RADAR_GEOCODE_CACHE") is { Length: > 0 } p ? p : Path.Combine(Root, "geocode.tsv");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string MessageCache => Path.Combine(Root, "messages.tsv");
    public static string FrontCache => Path.Combine(Root, "frontline.txt");

    private const string LegacyDirName = "RadarRF";

    private static string Init()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        var dir = Path.Combine(baseDir, "Radar");
        try
        {
            MigrateLegacy(Path.Combine(baseDir, LegacyDirName), dir);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // каталог данных недоступен — работаем из временного (настройки и кеши не сохранятся между запусками)
            dir = Path.Combine(Path.GetTempPath(), "Radar-" + Environment.UserName);
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    /// <summary>
    /// Каталог данных прежней версии («RadarRF»): настройки и кеши переносятся в «Radar» один раз. Если новый каталог уже есть —
    /// ничего не трогаем; старый остаётся нетронутым (его можно удалить вручную), чтобы откат версии не терял данные.
    /// </summary>
    internal static void MigrateLegacy(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
            CopyTree(oldDir, newDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"перенос данных: {ex.Message}");
            // неполная копия хуже её отсутствия: следующий запуск попробует заново
            try { if (Directory.Exists(newDir)) Directory.Delete(newDir, true); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
