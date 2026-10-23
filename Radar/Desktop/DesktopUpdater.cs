using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Radar.Services;

namespace Radar.Desktop;

/// <summary>
/// Установка обновления на десктопе: пакет релиза распаковывается рядом с программой, файлы заменяются на месте, старые
/// остаются с суффиксом .old (запущенный exe на Windows переименовать можно, перезаписать — нет) и удаляются при следующем
/// запуске. На macOS бандл .app меняется целиком (иначе нарушится его подпись). Новая версия начинает работать после
/// перезапуска. Любой сбой откатывает уже заменённые файлы.
/// </summary>
public sealed class DesktopUpdater : IUpdateInstaller
{
    public const string OldSuffix = ".old";

    /// <summary>Каталог, который заменяется: папка программы или бандл .app на macOS.</summary>
    public string InstallRoot { get; }
    /// <summary>Подкаталог пакета, из которого берутся файлы («Радар.app» на macOS, корень пакета — иначе).</summary>
    private readonly string? _packageSub;

    /// <summary>
    /// Путь программы на момент запуска. После замены файлов /proc/self/exe (Linux) и путь процесса на macOS указывают уже на
    /// переименованный «.old», а запускать надо новую версию по прежнему пути.
    /// </summary>
    public static string? LaunchPath { get; private set; }

    public DesktopUpdater(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath ?? "";
        LaunchPath ??= exe.Length > 0 ? exe : null;
        var dir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        // …/Радар.app/Contents/MacOS/Radar: заменяется весь бандл (Info.plist, иконка, подпись)
        if (OperatingSystem.IsMacOS() && Path.GetFileName(dir) == "MacOS" && Path.GetDirectoryName(dir) is { } contents
            && Path.GetFileName(contents) == "Contents" && Path.GetDirectoryName(contents) is { } app && app.EndsWith(".app", StringComparison.Ordinal))
        {
            InstallRoot = app;
            _packageSub = Path.GetFileName(app);
        }
        else InstallRoot = dir;
    }

    private string? _unavailable;
    private bool _checked;

    /// <summary>Проверяется один раз: проба записи в «Программы» на каждом обращении привязки — лишний ввод-вывод.</summary>
    public string? Unavailable
    {
        get
        {
            if (_checked) return _unavailable;
            _checked = true;
            return _unavailable = Probe();
        }
    }

    private string? Probe()
    {
        if (!Updater.IsReleaseBuild) return "сборка разработчика";
        // запуск через «dotnet Radar.dll» — не своя папка программы
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return "запуск через dotnet";
        // бандл меняется переименованием — нужна запись в папку, где он лежит (обычно «Программы»)
        var dir = _packageSub != null ? Path.GetDirectoryName(InstallRoot)! : InstallRoot;
        return CanWrite(dir) ? null : "нет прав на запись в папку программы";
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".radar-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool CanInstallUnattended => true;

    public Task<UpdateInstall> InstallAsync(string package, CancellationToken ct) => Task.Run(() =>
    {
        // бандл переносится целиком переименованием, а оно не работает между томами — распаковка рядом с ним
        var staging = _packageSub != null
            ? Path.Combine(Path.GetDirectoryName(InstallRoot)!, ".radar-update")
            : Path.Combine(Path.GetDirectoryName(package)!, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Extract(package, staging);
        var from = _packageSub != null ? Path.Combine(staging, _packageSub) : staging;
        if (!Directory.Exists(from)) throw new InvalidDataException($"в пакете нет {_packageSub}");
        if (_packageSub != null) SwapDirectory(from, InstallRoot);
        else Apply(from, InstallRoot, ct, JournalPath);
        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return UpdateInstall.RestartToApply;
    }, ct);

    /// <summary>Распаковка zip или tar.gz; пути, ведущие за пределы каталога, отклоняются.</summary>
    public static void Extract(string package, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        string Safe(string name)
        {
            var full = Path.GetFullPath(Path.Combine(dest, name));
            if (!full.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"недопустимый путь в пакете: {name}");
            return full;
        }
        if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(package);
            foreach (var e in zip.Entries)
            {
                // служебные записи ditto (метаданные Finder): в бандле они нарушили бы его подпись
                if (e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || e.Name.StartsWith("._", StringComparison.Ordinal)) continue;
                var path = Safe(e.FullName);
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, overwrite: true);
                // права из zip (ditto на macOS сохраняет их в старших битах): без +x бинарник бандла не запустится
                int mode = (e.ExternalAttributes >> 16) & 0x1FF;
                if (!OperatingSystem.IsWindows() && mode != 0) File.SetUnixFileMode(path, (UnixFileMode)mode);
            }
            return;
        }
        using var fs = File.OpenRead(package);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            if (e.Name is "." or "./") continue;
            var path = Safe(e.Name);
            switch (e.EntryType)
            {
                case TarEntryType.Directory: Directory.CreateDirectory(path); break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    e.ExtractToFile(path, overwrite: true);
                    break;
                default: throw new InvalidDataException($"неподдерживаемая запись пакета: {e.Name} ({e.EntryType})");
            }
        }
    }

    /// <summary>Журнал замены (относительные пути): пока он есть, установка не завершена — при запуске она откатывается.</summary>
    public string JournalPath => Path.Combine(InstallRoot, ".radar-update.journal");
    private string DoneMarker => JournalPath + ".done";

    /// <summary>
    /// Замена файлов: каждый существующий файл назначения сначала переименовывается в .old, затем на его место
    /// перемещается новый. При сбое уже перемещённые откатываются. Лишние старые файлы не трогаются.
    /// Журнал (<paramref name="journal"/>) пишется до первой замены: прерванную установку (питание, kill) откатит
    /// <see cref="Recover"/> при следующем запуске, а завершённую — удалит <see cref="CleanupOld"/>.
    /// </summary>
    public static void Apply(string from, string to, CancellationToken ct = default, string? journal = null)
    {
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(from, f)).ToList();
        if (journal != null)
        {
            File.Delete(journal + ".done");
            File.WriteAllLines(journal, files);
        }
        var done = new List<(string Dest, string? Old)>();
        try
        {
            foreach (var rel in files)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(from, rel);
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string? old = null;
                if (File.Exists(dest))
                {
                    old = dest + OldSuffix;
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(dest, old);
                }
                done.Add((dest, old));
                File.Move(src, dest);
            }
            // установка целиком на месте: дальше .old из журнала — мусор для CleanupOld
            if (journal != null) File.WriteAllText(journal + ".done", "");
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (dest, old) = done[i];
                try
                {
                    if (File.Exists(dest) && (old == null || File.Exists(old))) File.Delete(dest);
                    if (old != null && File.Exists(old)) File.Move(old, dest);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine($"откат обновления {dest}: {ex.Message}"); }
            }
            if (journal != null) try { File.Delete(journal); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Прерванная установка (журнал без отметки завершения): вернуть .old на место. Новый файл, рядом с которым есть .old,
    /// удаляется; файл без .old (добавленный пакетом) остаётся — старая версия его не знает.
    /// </summary>
    public static void Recover(string root, string journal)
    {
        if (!File.Exists(journal) || File.Exists(journal + ".done")) return;
        foreach (var rel in File.ReadAllLines(journal))
        {
            if (rel.Length == 0) continue;
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            if (!dest.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var old = dest + OldSuffix;
            try { if (File.Exists(old)) File.Move(old, dest, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine($"восстановление {dest}: {ex.Message}"); }
        }
        File.Delete(journal);
    }

    /// <summary>Замена каталога целиком: прежний — в «имя.old» (работающий процесс держит его файлы и после переименования).</summary>
    public static void SwapDirectory(string from, string to)
    {
        var old = to + OldSuffix;
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.Move(to, old);
        try { Directory.Move(from, to); }
        catch
        {
            try { Directory.Move(old, to); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine($"откат обновления {to}: {ex.Message}"); }
            throw;
        }
    }

    /// <summary>
    /// При запуске: прерванная установка откатывается, после завершённой удаляются её .old — только файлы из журнала
    /// (папкой программы может быть «Загрузки» с чужими *.old). Бандл macOS — прежний «Радар.app.old».
    /// </summary>
    public void CleanupOld()
    {
        if (_packageSub != null)
        {
            try { if (Directory.Exists(InstallRoot + OldSuffix)) Directory.Delete(InstallRoot + OldSuffix, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        try
        {
            Recover(InstallRoot, JournalPath);
            if (!File.Exists(JournalPath)) return;
            foreach (var rel in File.ReadAllLines(JournalPath))
            {
                if (rel.Length == 0) continue;
                var old = Path.GetFullPath(Path.Combine(InstallRoot, rel)) + OldSuffix;
                if (!old.StartsWith(Path.GetFullPath(InstallRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                // exe прежней версии ещё может быть занят ею (Windows) — тогда удалится при следующем запуске
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            }
            File.Delete(JournalPath);
            File.Delete(DoneMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine($"очистка обновления: {ex.Message}"); }
    }

    /// <summary>Запуск новой версии с флагом ожидания: она дождётся выхода этого процесса (один экземпляр).</summary>
    public static void StartNewInstance(IEnumerable<string> args)
    {
        var exe = LaunchPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("путь программы неизвестен");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(RestartFlag);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    public const string RestartFlag = "--after-update";
    /// <summary>Перезапуск из трея: окно не показывать.</summary>
    public const string HiddenFlag = "--hidden";

    /// <summary>Запущен после обновления: дождаться завершения прежнего процесса (до 30 с) и вернуть оставшиеся аргументы.</summary>
    public static string[] AwaitPrevious(string[] args)
    {
        if (args.Length < 2 || args[0] != RestartFlag) return args;
        if (int.TryParse(args[1], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(30_000); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return args[2..];
    }
}
