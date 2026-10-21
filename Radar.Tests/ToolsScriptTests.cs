using System.Diagnostics;
using Xunit;

namespace Radar.Tests;

/// <summary>Python-тесты скриптов аудита (tools/test_*.py) — в общем прогоне dotnet test.</summary>
public class ToolsScriptTests
{
    [Fact]
    public void AuditScriptsUnitTests()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "tools"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "каталог tools/ не найден выше " + AppContext.BaseDirectory);
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in new[] { "-B", "-m", "unittest", "discover", "-s", "tools", "-p", "test_*.py" }) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        // python3 нет — скрипты аудита на этой машине не запускаются; тест виден в отчёте как пропущенный
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("python3 не найден — тесты tools/ пропущены"); return; }
        using (p)
        {
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "unittest не завершился за минуту");
            Assert.True(p.ExitCode == 0, output + err.Result);
        }
    }
}
