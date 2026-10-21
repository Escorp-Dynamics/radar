using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

public class MessageCacheTests
{
    [Fact]
    public void RoundTripKeepsTextAndDropsOld()
    {
        var path = Path.Combine(Path.GetTempPath(), $"radar-cache-{Guid.NewGuid():N}.tsv");
        try
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var c = new MessageCache(path);
            c.Load(now.AddDays(-1));
            var text = "Белгород\tБПЛА\nОпасность \\ атаки";
            c.Append([new RawMessage("a", 1, now.AddHours(-30), "старое"), new RawMessage("a", 2, now.AddMinutes(-5), text)]);
            c.Append([new RawMessage("a", 2, now.AddMinutes(-5), text)]); // дубль не дописывается
            Assert.Equal(2, File.ReadAllLines(path).Length);

            var loaded = new MessageCache(path).Load(now.AddDays(-1));
            var m = Assert.Single(loaded);
            Assert.Equal(text, m.Text);
            Assert.Equal(now.AddMinutes(-5), m.Time);
            Assert.Single(File.ReadAllLines(path)); // устаревшее вычищено из файла
        }
        finally { File.Delete(path); }
    }
}
