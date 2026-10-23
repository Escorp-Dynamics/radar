using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Radar.Desktop;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Автообновление: подпись манифеста, проверка скачанного файла, распаковка и замена файлов с откатом.</summary>
public class UpdaterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("radar-upd-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // манифест и подпись из tools/release/sign_update.py ключом релизов: формат скрипта и проверка в приложении совпадают
    private const string ScriptManifest = "ewogInZlcnNpb24iOiAiMS4wLjkiLAogImFzc2V0cyI6IFsKICB7CiAgICJuYW1lIjogIlJhZGFyLTEuMC45LW9zeC1hcm02NC56aXAiLAogICAic2hhMjU2IjogIjNiYjZjNjMxZDQ4NjgyY2UyZWY2ZmUxODgxODg5ZjNjMTZkOGUyOGRlZTE4NzYxYTE1NTIzY2M4NjE0M2NkMTQiLAogICAic2l6ZSI6IDQKICB9LAogIHsKICAgIm5hbWUiOiAiUmFkYXItMS4wLjktd2luLXg2NC56aXAiLAogICAic2hhMjU2IjogIjk4ZWE2ZTRmMjE2ZjJmYjRiNjlmZmY5YjNhNDQ4NDJjMzg2ODZjYTY4NWYzZjU1ZGM0OGM1ZDNmYjExMDdiZTQiLAogICAic2l6ZSI6IDMKICB9LAogIHsKICAgIm5hbWUiOiAiUmFkYXItMS4wLjkuYXBrIiwKICAgInNoYTI1NiI6ICJlYjM4ZThiNWE5NmI2ZTgzZmU5Y2IxYTg0MTEzZmVlOGJjZTQxNzEwODgzNzg2Mjg2NzIxMDU1NTRjZTg2NTY1IiwKICAgInNpemUiOiA0CiAgfQogXQp9";
    private const string ScriptSignature = "MEUCIQCb+caIWXGmRi8fJ5jnnSUEsmNqABMIKD7vySr9LtOeOAIgXuwOR87v4zObUcGDPdsnX40Po8thwxzuWJyHHMqlH7U=";

    [Fact]
    public void ManifestFromReleaseScriptVerifiesWithBuiltInKey()
    {
        var m = Updater.Verify(Convert.FromBase64String(ScriptManifest), Convert.FromBase64String(ScriptSignature));
        Assert.NotNull(m);
        Assert.Equal("1.0.9", m!.Version);
        Assert.Equal(3, m.Assets.Count);
        Assert.NotNull(Updater.Select(m, "v1.0.9", "Radar-1.0.9.apk"));
    }

    [Fact]
    public void TamperedManifestIsRejected()
    {
        var json = Convert.FromBase64String(ScriptManifest);
        json[json.Length / 2] ^= 1;
        Assert.Null(Updater.Verify(json, Convert.FromBase64String(ScriptSignature)));
    }

    [Fact]
    public void ManifestSignedByOtherKeyIsRejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Convert.FromBase64String(ScriptManifest);
        var sig = other.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Null(Updater.Verify(json, sig));
    }

    [Fact]
    public void OldSignedManifestCannotPassForNewRelease()
    {
        var m = Updater.Verify(Convert.FromBase64String(ScriptManifest), Convert.FromBase64String(ScriptSignature))!;
        // подписанный манифест 1.0.9, выданный за релиз 1.1.0, — не принимается
        Assert.Null(Updater.Select(m, "v1.1.0", "Radar-1.0.9.apk"));
        Assert.Null(Updater.Select(m, "v1.0.9", "Radar-1.0.9-linux-x64.tar.gz"));
    }

    private static (ECDsa Key, string Pub) NewKey()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    private sealed class FileHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var d)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(d) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static UpdatePlan Plan(string name, byte[] data, string? sha = null, long? size = null) =>
        new("v9.9.9", "https://x/" + name, new UpdateAsset { Name = name, Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(data)), Size = size ?? data.Length });

    [Fact]
    public async Task DownloadChecksHashAndSize()
    {
        var data = Encoding.UTF8.GetBytes("новая версия");
        using var http = new HttpClient(new FileHandler(new() { ["pkg.zip"] = data }));
        var dir = Path.Combine(_root, "dl");
        var file = await Updater.DownloadAsync(http, Plan("pkg.zip", data), dir, null, TestContext.Current.CancellationToken);
        Assert.Equal(data, File.ReadAllBytes(file));

        var bad = Plan("pkg.zip", data, sha: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, bad, dir, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, Plan("pkg.zip", data, size: data.Length - 1), dir, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.DownloadAsync(http, Plan("pkg.zip", data, size: data.Length + 1), dir, null, TestContext.Current.CancellationToken));
        // неудачная загрузка не оставляет недокачанный файл
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    [Fact]
    public async Task PlanRequiresValidSignature()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var json = Encoding.UTF8.GetBytes("""{"version":"2.0.0","assets":[{"name":"Radar-2.0.0-linux-x64.tar.gz","sha256":"%","size":5}]}""".Replace("%", new string('a', 64)));
            var sig = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var m = Updater.Verify(json, sig, pub);
            Assert.NotNull(m);
            Assert.NotNull(Updater.Select(m!, "v2.0.0", "Radar-2.0.0-linux-x64.tar.gz"));
            Assert.Null(Updater.Verify(json, sig));   // ключ релизов другой
        }
        // релиз без манифеста — автоустановки нет (кнопка «Скачать»)
        var rel = new GitHubRelease { Tag = "v2.0.0", Assets = [new() { Name = "Radar-2.0.0-linux-x64.tar.gz", Url = "u" }, new() { Name = "Radar-2.0.0-win-x64.zip", Url = "u" }, new() { Name = "Radar-2.0.0-osx-x64.zip", Url = "u" }, new() { Name = "Radar-2.0.0-osx-arm64.zip", Url = "u" }, new() { Name = "Radar-2.0.0-linux-arm64.tar.gz", Url = "u" }, new() { Name = "Radar-2.0.0-win-arm64.zip", Url = "u" }] };
        using var http = new HttpClient(new FileHandler([]));
        Assert.Null(await Updater.PlanAsync(http, rel, TestContext.Current.CancellationToken));

        // общий манифест есть, но подпись не от ключа релизов — подмена, а не «ещё не собрано»
        var json2 = Encoding.UTF8.GetBytes("""{"version":"2.0.0","assets":[]}""");
        var rel2 = new GitHubRelease { Tag = "v2.0.0", Assets = [.. rel.Assets, new() { Name = Updater.ManifestName, Url = "https://x/m" }, new() { Name = Updater.SignatureName, Url = "https://x/s" },
            new() { Name = Updater.MacManifestName, Url = "https://x/m" }, new() { Name = Updater.MacSignatureName, Url = "https://x/s" }] };
        using var http2 = new HttpClient(new FileHandler(new() { ["m"] = json2, ["s"] = [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.PlanAsync(http2, rel2, TestContext.Current.CancellationToken));
    }

    private string Tree(string name, Dictionary<string, string> files)
    {
        var dir = Path.Combine(_root, name);
        foreach (var (rel, text) in files)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return dir;
    }

    [Fact]
    public void ApplyReplacesFilesAndKeepsOldForCleanup()
    {
        var app = Tree("app", new() { ["Radar"] = "v1", ["lib.so"] = "lib1", ["extra.txt"] = "keep", ["notes/config.old"] = "чужой" });
        var staging = Tree("staging", new() { ["Radar"] = "v2", ["lib.so"] = "lib2", ["sub/new.bin"] = "n" });
        var updater = new DesktopUpdater(Path.Combine(app, "Radar"));
        DesktopUpdater.Apply(staging, app, default, updater.JournalPath);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "Radar")));
        Assert.Equal("lib2", File.ReadAllText(Path.Combine(app, "lib.so")));
        Assert.Equal("n", File.ReadAllText(Path.Combine(app, "sub", "new.bin")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(app, "extra.txt")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "Radar.old")));

        updater.CleanupOld();
        Assert.False(File.Exists(Path.Combine(app, "Radar.old")));
        Assert.False(File.Exists(Path.Combine(app, "lib.so.old")));
        Assert.False(File.Exists(updater.JournalPath));
        // чужие *.old в папке программы (её распаковали в «Загрузки») не трогаются
        Assert.Equal("чужой", File.ReadAllText(Path.Combine(app, "notes", "config.old")));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "Radar")));
    }

    [Fact]
    public void InterruptedInstallIsRolledBackOnNextStart()
    {
        var app = Tree("app", new() { ["Radar"] = "v1", ["lib.so"] = "lib1" });
        var updater = new DesktopUpdater(Path.Combine(app, "Radar"));
        // процесс убит посреди замены: журнал записан, Radar уже переименован в .old, а новый ещё не на месте; lib.so — заменён
        File.WriteAllLines(updater.JournalPath, ["Radar", "lib.so"]);
        File.Move(Path.Combine(app, "Radar"), Path.Combine(app, "Radar.old"));
        File.Move(Path.Combine(app, "lib.so"), Path.Combine(app, "lib.so.old"));
        File.WriteAllText(Path.Combine(app, "lib.so"), "lib2");

        updater.CleanupOld();
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "Radar")));
        Assert.Equal("lib1", File.ReadAllText(Path.Combine(app, "lib.so")));
        Assert.Empty(Directory.GetFiles(app, "*.old"));
        Assert.False(File.Exists(updater.JournalPath));
    }

    [Fact]
    public void CleanupWithoutJournalTouchesNothing()
    {
        var app = Tree("app", new() { ["Radar"] = "v1", ["backup.old"] = "мой" });
        new DesktopUpdater(Path.Combine(app, "Radar")).CleanupOld();
        Assert.Equal("мой", File.ReadAllText(Path.Combine(app, "backup.old")));
    }

    [Fact]
    public void ApplyCancelledBeforeStartChangesNothing()
    {
        var app = Tree("app", new() { ["a"] = "a1", ["b"] = "b1" });
        var staging = Tree("staging", new() { ["a"] = "a2", ["b"] = "b2" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DesktopUpdater.Apply(staging, app, cts.Token));
        Assert.Equal("a1", File.ReadAllText(Path.Combine(app, "a")));
        Assert.Equal("b1", File.ReadAllText(Path.Combine(app, "b")));
    }

    [Fact]
    public void ApplyRollsBackWhenSecondFileFails()
    {
        var app = Tree("app", new() { ["a"] = "a1", ["b"] = "b1" });
        var staging = Tree("staging", new() { ["a"] = "a2", ["b"] = "b2" });
        // назначение второго файла — каталог: перемещение старого в .old не удастся
        File.Delete(Path.Combine(app, "b"));
        Directory.CreateDirectory(Path.Combine(app, "b"));
        File.WriteAllText(Path.Combine(app, "b", "x"), "");
        Assert.ThrowsAny<IOException>(() => DesktopUpdater.Apply(staging, app));
        Assert.Equal("a1", File.ReadAllText(Path.Combine(app, "a")));
        Assert.False(File.Exists(Path.Combine(app, "a.old")));
    }

    [Fact]
    public void ExtractZipAndTarAndRejectTraversal()
    {
        var zipPath = Path.Combine(_root, "p.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("Радар.app/Contents/MacOS/Radar");
            e.ExternalAttributes = 0b111_101_101 << 16;
            using (var w = new StreamWriter(e.Open())) w.Write("bin");
        }
        var outZip = Path.Combine(_root, "z");
        DesktopUpdater.Extract(zipPath, outZip);
        var bin = Path.Combine(outZip, "Радар.app", "Contents", "MacOS", "Radar");
        Assert.Equal("bin", File.ReadAllText(bin));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(bin).HasFlag(UnixFileMode.UserExecute));

        var tgz = Path.Combine(_root, "p.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            var f = new PaxTarEntry(TarEntryType.RegularFile, "./Radar") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("elf")), Mode = (UnixFileMode)0b111_101_101 };
            tar.WriteEntry(f);
        }
        var outTar = Path.Combine(_root, "t");
        DesktopUpdater.Extract(tgz, outTar);
        Assert.Equal("elf", File.ReadAllText(Path.Combine(outTar, "Radar")));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(outTar, "Radar")).HasFlag(UnixFileMode.UserExecute));

        var evil = Path.Combine(_root, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("../outside.txt").Open())) w.Write("x");
        Assert.Throws<InvalidDataException>(() => DesktopUpdater.Extract(evil, Path.Combine(_root, "e")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void AwaitPreviousStripsRestartArguments()
    {
        Assert.Equal(["--hidden"], DesktopUpdater.AwaitPrevious([DesktopUpdater.RestartFlag, "999999999", "--hidden"]));
        Assert.Equal(["--replay"], DesktopUpdater.AwaitPrevious(["--replay"]));
    }
}
