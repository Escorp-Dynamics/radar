using System.IO.Compression;
using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

public class ArchivePackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "radar-pack-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "pack");

    public ArchivePackTests()
    {
        var t0 = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        Write("chan_a", "2025-03", new RawMessage("chan_a", 1, t0, "Ракетная опасность\tБелгород"),
                                    new RawMessage("chan_a", 2, t0.AddMinutes(30), "Отбой\nБелгород"));
        Write("chan_b", "2025-03", new RawMessage("chan_b", 7, t0.AddMinutes(10), "БПЛА на Курск"),
                                    new RawMessage("chan_b", 7, t0.AddMinutes(10), "БПЛА на Курск"));
        Write("chan_b", "2025-04", new RawMessage("chan_b", 8, t0.AddDays(25), "Отбой Курск \\ область"));
    }

    private void Write(string ch, string month, params RawMessage[] msgs)
    {
        var d = Path.Combine(Src, ch);
        Directory.CreateDirectory(d);
        File.AppendAllLines(Path.Combine(d, month + ".tsv"), msgs.Select(MessageCache.Format));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void PackedArchiveReadsLikeChannelDirs()
    {
        var man = ArchivePack.Pack(Src, Dst);
        Assert.Equal(["2025-03", "2025-04"], man.Months.Select(m => m.Month));
        Assert.Equal(3, man.Months[0].Messages);
        Assert.Equal(2, man.Months[0].Channels["chan_a"]);

        var from = DateTimeOffset.MinValue; var to = DateTimeOffset.MaxValue;
        var packed = ArchiveFetcher.Load(Dst, from, to);
        var plain = ArchiveFetcher.Load(Src, from, to).DistinctBy(m => (m.Channel, m.Id)).ToList();
        Assert.Equal(plain.Select(m => (m.Channel, m.Id, m.Time, m.Text)), packed.Select(m => (m.Channel, m.Id, m.Time, m.Text)));
        Assert.Equal(ArchiveFetcher.Months(Src), ArchiveFetcher.Months(Dst));
        Assert.Empty(ArchivePack.Verify(Dst));

        // интервал отсекает второй месяц
        var march = ArchiveFetcher.Load(Dst, from, new DateTimeOffset(2025, 3, 31, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(3, march.Count);
    }

    [Fact]
    public void RepackKeepsUnchangedMonthsAndVerifyCatchesDamage()
    {
        var first = ArchivePack.Pack(Src, Dst);
        var april = Path.Combine(Dst, first.Months.Single(m => m.Month == "2025-04").File);
        var stamp = File.GetLastWriteTimeUtc(april);
        Write("chan_a", "2025-03", new RawMessage("chan_a", 3, new DateTimeOffset(2025, 3, 11, 0, 0, 0, TimeSpan.Zero), "Отбой"));
        var man = ArchivePack.Pack(Src, Dst);
        Assert.Equal(4, man.Months[0].Messages);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(april));

        // подмена содержимого пакета
        using (var fs = File.Create(april))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            gz.Write("x\t1\t1\ty\n"u8);
        Assert.Contains(ArchivePack.Verify(Dst), e => e.StartsWith("2025-04"));
        File.WriteAllText(april, "мусор");
        Assert.Contains(ArchivePack.Verify(Dst), e => e.Contains("повреждён"));
    }

    [Fact]
    public void TruncatedPackThrowsAndMissingMonthIsKept()
    {
        ArchivePack.Pack(Src, Dst);
        // источник без апреля (неполная копия) — апрель в архиве остаётся
        File.Delete(Path.Combine(Src, "chan_b", "2025-04.tsv"));
        var man = ArchivePack.Pack(Src, Dst);
        Assert.Contains(man.Months, m => m.Month == "2025-04");
        // оборванный пакет: строк меньше, чем в манифесте
        var march = Path.Combine(Dst, man.Months.Single(m => m.Month == "2025-03").File);
        using (var fs = File.Create(march))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            gz.Write(System.Text.Encoding.UTF8.GetBytes(MessageCache.Format(new RawMessage("chan_a", 1, new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero), "x")) + "\n"));
        Assert.Throws<InvalidDataException>(() => ArchiveFetcher.Load(Dst, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void RepackWithMissingChannelKeepsItsMessages()
    {
        var first = ArchivePack.Pack(Src, Dst);
        int march = first.Months.Single(m => m.Month == "2025-03").Messages;
        // неполная копия: нет каталога одного канала — его сообщения марта остаются в архиве
        File.Delete(Path.Combine(Src, "chan_b", "2025-03.tsv"));
        var man = ArchivePack.Pack(Src, Dst);
        Assert.Equal(march, man.Months.Single(m => m.Month == "2025-03").Messages);
        // прежние файлы пакетов, на которые манифест больше не ссылается, удалены
        var files = Directory.GetFiles(Dst, "*.tsv.gz").Select(f => Path.GetFileName(f)!).ToHashSet();
        Assert.Equal(man.Months.Select(m => m.File).ToHashSet(), files);
        Assert.Equal(march, ArchiveFetcher.Load(Dst, DateTimeOffset.MinValue, new DateTimeOffset(2025, 3, 31, 0, 0, 0, TimeSpan.Zero)).Count);
    }

    [Fact]
    public void BrokenManifestStopsPackAndKeepsArchive()
    {
        ArchivePack.Pack(Src, Dst);
        var path = Path.Combine(Dst, ArchivePack.ManifestName);
        File.WriteAllText(path, "{ битый");
        var before = Directory.GetFiles(Dst).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
        Assert.Throws<InvalidDataException>(() => ArchivePack.Pack(Src, Dst));
        Assert.Equal("{ битый", File.ReadAllText(path));
        Assert.Equal(before, Directory.GetFiles(Dst).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList());
    }

    [Theory]
    // сверху: новые выше newest — да, уже сохранённые — нет
    [InlineData(120, false, 50, 100, true)]
    [InlineData(80, false, 50, 100, false)]
    // сверху при обрыве листания вглубь: страница ушла ниже oldest — тоже сохраняется (иначе дыра)
    [InlineData(40, false, 50, 100, true)]
    // первый запуск (oldest=0): всё выше newest
    [InlineData(10, false, 0, 0, true)]
    // вглубь: только ниже oldest
    [InlineData(40, true, 50, 100, true)]
    [InlineData(60, true, 50, 100, false)]
    public void FetchKeepsMessagesBelowOldestWhenCatchingUp(long id, bool deep, long oldest, long newest, bool fresh) =>
        Assert.Equal(fresh, ArchiveFetcher.IsFresh(id, deep, oldest, newest));
}
