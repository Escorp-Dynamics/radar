using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Исправления сервисов по аудиту: кеш сообщений, архив, сводка статистики.</summary>
public class ServicesAuditFixTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    [Fact]
    public void CacheFormatKeepsCarriageReturnAndCleansChannel()
    {
        var m = new RawMessage("ch\tx", 5, T0, "строка\r\nвторая \\ конец\\");
        var back = MessageCache.Parse(MessageCache.Format(m))!;
        Assert.Equal(m.Text, back.Text);
        Assert.Equal("ch_x", back.Channel);
        // старые файлы (без \r) читаются так же
        Assert.Equal("a\nb\\c", MessageCache.Parse("ch\t1\t0\ta\\nb\\\\c")!.Text);
    }

    [Fact]
    public void AppendAfterTornLineDoesNotGlueMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"radar-cache-{Guid.NewGuid():N}.tsv");
        try
        {
            // прошлая запись оборвалась без перевода строки
            File.WriteAllText(path, MessageCache.Format(new RawMessage("a", 1, T0.AddMinutes(-2), "первое")));
            var c = new MessageCache(path);
            c.Append([new RawMessage("a", 2, T0.AddMinutes(-1), "второе")]);
            var loaded = new MessageCache(path).Load(T0.AddDays(-1));
            Assert.Equal(["первое", "второе"], loaded.Select(x => x.Text));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PackFromEmptySourceKeepsExistingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "radar-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src", "chan");
            Directory.CreateDirectory(src);
            File.WriteAllLines(Path.Combine(src, "2025-03.tsv"), [MessageCache.Format(new RawMessage("chan", 1, new DateTimeOffset(2025, 3, 2, 0, 0, 0, TimeSpan.Zero), "текст"))]);
            var dst = Path.Combine(root, "pack");
            ArchivePack.Pack(Path.Combine(root, "src"), dst);
            Assert.Throws<InvalidDataException>(() => ArchivePack.Pack(Path.Combine(root, "нет-такого"), dst));
            Assert.Single(ArchivePack.ReadManifest(dst)!.Months);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChannelDirsLoadWithoutRepeats()
    {
        var root = Path.Combine(Path.GetTempPath(), "radar-arch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = Path.Combine(root, "chan");
            Directory.CreateDirectory(d);
            var line = MessageCache.Format(new RawMessage("chan", 7, new DateTimeOffset(2025, 3, 2, 0, 0, 0, TimeSpan.Zero), "текст"));
            // прерванная докачка дописала то же сообщение дважды
            File.WriteAllLines(Path.Combine(d, "2025-03.tsv"), [line, line]);
            Assert.Single(ArchiveFetcher.Load(root, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyPeriodHasNoPeakDay()
    {
        var r = StatsSummary.Build([], [], T0.AddDays(-7), T0, null, [], 15);
        Assert.Equal(0, r.PeakDay.Count);
        Assert.Equal(default, r.PeakDay.Day);
    }
}
