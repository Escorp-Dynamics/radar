using System.Diagnostics;
using Xunit;

namespace Radar.Tests;

/// <summary>
/// Офлайн-снимок окна (`Radar --render-ui`, им сделан docs/screenshot-main.png): лента заполнена всеми карточками,
/// нет плашки «Подключение … 0%». Тесты без Avalonia — приложение собирается из текущих исходников и запускается
/// отдельным процессом.
/// </summary>
public class UiSnapshotTests
{
    /// <summary>
    /// Собирает приложение из текущих исходников в отдельный каталог рядом с тестами (сборка инкрементальная —
    /// без изменений занимает секунды), чтобы снимок не зависел от устаревшей сборки в Radar/bin.
    /// RADAR_UI_TEST_BIN — готовая сборка без пересборки. null — dotnet недоступен.
    /// </summary>
    private static string? BuildApp(string root)
    {
        if (Environment.GetEnvironmentVariable("RADAR_UI_TEST_BIN") is { Length: > 0 } bin) return File.Exists(bin) ? bin : null;
        var outDir = Path.Combine(AppContext.BaseDirectory, "uiapp");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var a in new[] { "build", Path.Combine(root, "Radar", "Radar.csproj"), "-c", "Debug", "-o", outDir, "--nologo", "-v", "q" })
            psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        using (p)
        {
            var err = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(600_000), "сборка приложения не завершилась за 10 минут");
            Assert.True(p.ExitCode == 0, "сборка приложения не удалась:\n" + output + err.Result);
        }
        var app = Path.Combine(outDir, OperatingSystem.IsWindows() ? "Radar.exe" : "Radar");
        Assert.True(File.Exists(app), "после сборки нет " + app);
        return app;
    }

    private static string Render(string app, string corpus, bool feed)
    {
        var png = Path.Combine(Path.GetTempPath(), $"radar-ui-test-{Guid.NewGuid():N}.png");
        var psi = new ProcessStartInfo(app) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--render-ui");
        psi.ArgumentList.Add(png);
        psi.Environment["RADAR_DEMO_CORPUS"] = corpus;
        psi.Environment["RADAR_UI_FREEZE"] = "1";
        if (feed) psi.Environment["RADAR_UI_FEED"] = "1";
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(180_000), "снимок не завершился за 3 минуты");
            Assert.True(p.ExitCode == 0, output + err.Result);
            Assert.True(File.Exists(png) && new FileInfo(png).Length > 10_000, "PNG не создан");
            return output;
        }
        finally { File.Delete(png); }
    }

    [Fact]
    public void WindowSnapshotShowsFeedWithoutConnecting()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Radar.Tests"))) root = Path.GetDirectoryName(root);
        Assert.True(root != null, "корень решения не найден выше " + AppContext.BaseDirectory);
        var app = BuildApp(root);
        // дисплей не нужен (рендер без окна); без dotnet в PATH собрать приложение нельзя — тест пропускается
        if (app == null) { Assert.Skip("dotnet не найден или RADAR_UI_TEST_BIN указывает на несуществующий файл"); return; }
        var corpus = Path.Combine(AppContext.BaseDirectory, "Data", "ui_corpus.json");

        var ok = Render(app, corpus, feed: true);
        var m = System.Text.RegularExpressions.Regex.Match(ok, @"лента: событий (\d+), карточек (\d+); подключение: (\S+); загрузка: (\S+)");
        Assert.True(m.Success, ok);
        int events = int.Parse(m.Groups[1].Value), cards = int.Parse(m.Groups[2].Value);
        Assert.True(events >= 5, ok);
        // все карточки ленты созданы (раньше без окна — только первая)
        Assert.Equal(events, cards);
        Assert.Equal("нет", m.Groups[3].Value);
        Assert.Equal("нет", m.Groups[4].Value);

        // без ленты (RADAR_UI_FEED не задан) — окно как при запуске: идёт подключение
        var connecting = Render(app, corpus, feed: false);
        Assert.Contains("подключение: да", connecting);
    }
}
