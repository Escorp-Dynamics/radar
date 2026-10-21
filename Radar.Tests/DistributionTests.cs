using System.Net;
using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

public class DistributionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "radar-dist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Pack()
    {
        var src = Path.Combine(_root, "src");
        var t0 = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        foreach (var (ch, id, dt, month) in new[] { ("chan_a", 5L, 0, "2025-03"), ("chan_a", 9L, 1, "2025-03"), ("chan_b", 3L, 30, "2025-04") })
        {
            var d = Path.Combine(src, ch);
            Directory.CreateDirectory(d);
            File.AppendAllLines(Path.Combine(d, month + ".tsv"), [MessageCache.Format(new RawMessage(ch, id, t0.AddDays(dt), "БПЛА Курск"))]);
        }
        var pack = Path.Combine(_root, "pack");
        ArchivePack.Pack(src, pack);
        return pack;
    }

    [Fact]
    public void UnpackRestoresChannelDirsAndFetchState()
    {
        var pack = Pack();
        var raw = Path.Combine(_root, "raw");
        Assert.Equal(3, ArchivePack.Unpack(pack, raw));
        Assert.Equal(ArchiveFetcher.Months(pack), ArchiveFetcher.Months(raw));
        Assert.Equal("oldest=5\nnewest=9\ndone=1\n", File.ReadAllText(Path.Combine(raw, "chan_a", ".state")));
        // туда-обратно — те же пакеты (SHA-256 содержимого)
        var again = ArchivePack.Pack(raw, Path.Combine(_root, "pack2"));
        Assert.Equal(ArchivePack.ReadManifest(pack)!.Months.Select(m => m.Sha256), again.Months.Select(m => m.Sha256));
    }

    /// <summary>Отдаёт файлы каталога пакетов как релиз; считает запросы.</summary>
    private sealed class DirHandler(string dir) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public Func<string, byte[], byte[]>? Tamper;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            Requests.Add(name);
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var data = File.ReadAllBytes(path);
            if (Tamper != null) data = Tamper(name, data);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    [Fact]
    public async Task SyncDownloadsOnlyChangedMonths()
    {
        var pack = Pack();
        var local = Path.Combine(_root, "local");
        var h = new DirHandler(pack);
        using var http = new HttpClient(h);
        var r1 = await ArchiveSync.SyncAsync(http, local, "https://x/archive/", null, default);
        Assert.Equal((2, 2, true), (r1.Downloaded, r1.Total, r1.Changed));
        Assert.Empty(ArchivePack.Verify(local));

        h.Requests.Clear();
        var r2 = await ArchiveSync.SyncAsync(http, local, "https://x/archive/", null, default);
        Assert.Equal((0, false), (r2.Downloaded, r2.Changed));
        Assert.Equal([ArchivePack.ManifestName], h.Requests);
    }

    [Fact]
    public async Task SyncRejectsCorruptPackAndKeepsOldManifest()
    {
        var pack = Pack();
        var local = Path.Combine(_root, "local");
        using var http = new HttpClient(new DirHandler(pack) { Tamper = (n, d) => n.EndsWith(".gz") ? Gz("подмена") : d });
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveSync.SyncAsync(http, local, "https://x/archive/", null, default));
        Assert.Null(ArchivePack.ReadManifest(local));
    }

    private static byte[] Gz(string s)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(System.Text.Encoding.UTF8.GetBytes(s));
        return ms.ToArray();
    }

    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v0.9.9", "1.0.0", false)]
    [InlineData("1.2", "1.1.9", true)]
    [InlineData("archive", "1.0.0", false)]
    public void UpdateVersionCompare(string tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(tag, Version.Parse(current)));

    [Fact]
    public void ReleaseAssetPicksPlatformFile()
    {
        var r = new GitHubRelease { Tag = "v1.0.1", Assets = [new() { Name = "Radar-1.0.1-linux-x64.tar.gz", Url = "l" }, new() { Name = "Radar-1.0.1-win-x64.zip", Url = "w" }, new() { Name = "Radar-1.0.1.apk", Url = "a" }] };
        Assert.Equal(OperatingSystem.IsWindows() ? "w" : "l", UpdateChecker.AssetFor(r)!.Url);
    }
}
