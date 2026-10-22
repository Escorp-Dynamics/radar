using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Статистика из CI: пакет месяцев принимается клиентом с тем же архивом и кодом, без прогона движка.</summary>
[Collection("Engine")]
public class StatsPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "radar-statspack-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset March = new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public StatsPackTests()
    {
        var t0 = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var src = Path.Combine(_root, "src", "chan_a");
        Directory.CreateDirectory(src);
        File.WriteAllLines(Path.Combine(src, "2025-03.tsv"), new[]
        {
            new RawMessage("chan_a", 1, t0, "Белгородская область\nОпасность по БПЛА"),
            new RawMessage("chan_a", 2, t0.AddMinutes(40), "Белгородская область\nОтбой опасности по БПЛА"),
        }.Select(MessageCache.Format));
        // два клиента с одинаковым архивом: «CI» и «устройство»
        ArchivePack.Pack(Path.Combine(_root, "src"), Ci);
        ArchivePack.Pack(Path.Combine(_root, "src"), Client);
    }

    private string Ci => Path.Combine(_root, "ci");
    private string Client => Path.Combine(_root, "client");
    private string Out => Path.Combine(_root, "stats");

    public void Dispose()
    {
        Clock.StopSimulation();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Pack_ImportedByClient_WithoutCompute()
    {
        var m = StatsPack.Pack(Ci, Out);
        Assert.Single(m.Months);
        Assert.False(ArchiveStats.IsReady(Client, March));
        Assert.Equal(1, StatsPack.ImportDir(Client, Out));
        Assert.True(ArchiveStats.IsReady(Client, March));
        Assert.Equal(ArchiveStats.Month(Ci, March).Events.Count, ArchiveStats.Month(Client, March).Events.Count);
        // уже готовый месяц второй раз не принимается
        Assert.Equal(0, StatsPack.ImportDir(Client, Out));
    }

    [Fact]
    public void OtherArchive_Rejected()
    {
        StatsPack.Pack(Ci, Out);
        // у клиента в месяце другие сообщения — итоги CI ему не подходят
        var src = Path.Combine(_root, "src", "chan_a", "2025-03.tsv");
        File.AppendAllLines(src, [MessageCache.Format(new RawMessage("chan_a", 3, March.AddDays(20), "Курская область\nОпасность по БПЛА"))]);
        ArchivePack.Pack(Path.Combine(_root, "src"), Client);
        Assert.Equal(0, StatsPack.ImportDir(Client, Out));
        Assert.False(ArchiveStats.IsReady(Client, March));
    }

    [Fact]
    public void Peek_ReturnsStaleCache()
    {
        var before = ArchiveStats.Month(Client, March);
        var src = Path.Combine(_root, "src", "chan_a", "2025-03.tsv");
        File.AppendAllLines(src, [MessageCache.Format(new RawMessage("chan_a", 3, March.AddDays(20), "Курская область\nОпасность по БПЛА"))]);
        ArchivePack.Pack(Path.Combine(_root, "src"), Client);
        Assert.False(ArchiveStats.IsReady(Client, March));
        var stale = ArchiveStats.Peek(Client, March);
        Assert.NotNull(stale);
        Assert.Equal(before.Events.Count, stale.Value.Events.Count);
    }

    [Fact]
    public void CorruptedFile_Rejected()
    {
        var m = StatsPack.Pack(Ci, Out);
        var f = Path.Combine(Out, m.Months[0].File);
        var bytes = File.ReadAllBytes(f);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(f, bytes);
        Assert.Equal(0, StatsPack.ImportDir(Client, Out));
        Assert.False(ArchiveStats.IsReady(Client, March));
    }
}
