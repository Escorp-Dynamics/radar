using System.Diagnostics;
using System.Text;

namespace Radar.Services;

/// <summary>Запуск вместе с системой. Запись всегда несёт <see cref="Flag"/>: по нему приложение понимает, что его запустила система.</summary>
public static class Autostart
{
    public const string Flag = "--autostart";
    private const string Name = "Radar";

    /// <summary>Команда запуска: исполняемый файл и его аргументы (для запуска через «dotnet Radar.dll» — сама библиотека).</summary>
    public static (string Exe, string[] Args) Command()
    {
        var exe = Environment.ProcessPath ?? "Radar";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Environment.GetCommandLineArgs() is { Length: > 0 } a && a[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return (exe, [a[0], Flag]);
        return (exe, [Flag]);
    }

    /// <summary>Строка Exec для .desktop: аргумент с пробелами или спецсимволами берётся в кавычки; внутри кавычек
    /// <c>" ` $ \</c> экранируются обратной косой, а сама она в значении ключа удваивается (два уровня по спецификации).</summary>
    public static string DesktopExec(string exe, IEnumerable<string> args)
    {
        static string Q(string s)
        {
            if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+=:,".Contains(c))) return s;
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '%') sb.Append("%%");
                else if (c == '\\') sb.Append("\\\\\\\\");
                else if (c is '"' or '`' or '$') sb.Append("\\\\").Append(c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
        return string.Join(' ', new[] { exe }.Concat(args).Select(Q));
    }

    public static string DesktopFile(string exe, IEnumerable<string> args) =>
        $"[Desktop Entry]\nType=Application\nName={Name}\nComment=Карта воздушных тревог\nExec={DesktopExec(exe, args)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    public static string LaunchAgentPlist(string exe, IEnumerable<string> args)
    {
        static string X(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>\n<key>Label</key><string>app.radar</string>\n<key>ProgramArguments</key><array>\n");
        foreach (var s in new[] { exe }.Concat(args)) sb.Append("<string>").Append(X(s)).Append("</string>\n");
        return sb.Append("</array>\n<key>RunAtLoad</key><true/>\n</dict></plist>\n").ToString();
    }

    private static string LinuxFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "radar.desktop");
    private static string MacFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "app.radar.plist");

    /// <summary>Включает или выключает автозапуск. false — не удалось (нет прав, неподдерживаемая система).</summary>
    public static bool Set(bool on)
    {
        try
        {
            var (exe, args) = Command();
            if (OperatingSystem.IsLinux()) Write(LinuxFile, on, DesktopFile(exe, args));
            else if (OperatingSystem.IsMacOS()) Write(MacFile, on, LaunchAgentPlist(exe, args));
            else if (OperatingSystem.IsWindows()) SetWindows(on, string.Join(' ', new[] { exe }.Concat(args).Select(s => "\"" + s + "\"")));
            else return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static void Write(string path, bool on, string content)
    {
        if (!on) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindows(bool on, string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (on) key.SetValue(Name, command); else key.DeleteValue(Name, false);
    }
}
