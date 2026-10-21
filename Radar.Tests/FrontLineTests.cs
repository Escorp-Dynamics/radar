using Radar.Models;
using Radar.Services;
using Xunit;

namespace Radar.Tests;

/// <summary>Актуальная линия соприкосновения из открытых данных и её кеш.</summary>
[Collection("Engine")]
public class FrontLineTests
{
    private static (List<GeoPoint[]> Lines, List<GeoPoint[]> Occupied) Sample() =>
        FrontLineSource.Extract(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "front_sample.json")));

    [Fact]
    public void ExtractsFrontNotStateBorderOrCoast()
    {
        var (lines, occ) = Sample();
        Assert.NotEmpty(lines);
        Assert.NotEmpty(occ);
        double km = lines.Sum(l => Enumerable.Range(1, l.Length - 1).Sum(i => Geo.DistanceKm(l[i - 1], l[i])));
        // фронт от Курской/Сумской обл. до Днепра — порядка тысячи км с изгибами; не весь периметр зоны (несколько тысяч)
        Assert.InRange(km, 700, 2200);
        // ни одной вершины на госгранице с Ростовской областью (восточный край ЛНР/ДНР) и на побережье Азова
        Assert.DoesNotContain(lines.SelectMany(l => l), p => p.Lon > 39.7 && p.Lat < 48.5);
        Assert.DoesNotContain(lines.SelectMany(l => l), p => p.Lat < 46.5 && p.Lon > 34.0 && p.Lon < 38.0);
    }

    [Fact]
    public void SideByActualFront()
    {
        var (lines, occ) = Sample();
        try
        {
            Borderline.SetFront(lines, occ);
            Assert.True(Borderline.IsLive);
            Assert.True(Borderline.IsEnemySide(new GeoPoint(48.72, 37.56), "dnr"));       // Краматорск
            Assert.False(Borderline.IsEnemySide(new GeoPoint(48.015, 37.80), "dnr"));     // Донецк
            Assert.True(Borderline.IsEnemySide(new GeoPoint(47.84, 35.14), "zaporozhye")); // Запорожье
            Assert.False(Borderline.IsEnemySide(new GeoPoint(46.84, 35.37), "zaporozhye")); // Мелитополь
            // глубина Донецка — от актуальной линии, а не от границы Днепропетровской области
            Assert.InRange(Borderline.DistanceKm(new GeoPoint(48.015, 37.80)), 15, 120);
        }
        finally { Borderline.ResetFront(); }
    }

    [Fact]
    public void CacheRoundTrip()
    {
        var (lines, occ) = Sample();
        var path = Path.Combine(Path.GetTempPath(), $"radar-front-{Guid.NewGuid():N}.txt");
        try
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
            var data = DateTimeOffset.FromUnixTimeSeconds(1_789_990_000);
            FrontLineSource.WriteCache(path, lines, occ, at, data);
            Assert.True(FrontLineSource.TryReadCache(path, out var l2, out var o2, out var at2, out var data2));
            Assert.Equal(at, at2);
            Assert.Equal(data, data2);
            Assert.Equal(lines.Count, l2.Count);
            Assert.Equal(occ.Count, o2.Count);
            Assert.Equal(lines[0][0].Lat, l2[0][0].Lat, 4);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DataTimeFromDeepStateId()
    {
        long id = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeSeconds();
        var t = FrontLineSource.ReadDataTime(System.Text.Encoding.UTF8.GetBytes($"{{\"id\":{id},\"map\":{{}}}}"));
        Assert.Equal(id, t?.ToUnixTimeSeconds());
        // не время (идентификатор вне правдоподобного диапазона) — не показываем
        Assert.Null(FrontLineSource.ReadDataTime(System.Text.Encoding.UTF8.GetBytes("{\"id\":12345,\"map\":{}}")));
    }
}
