using System.IO.Compression;
using System.Text;
using Avalonia;

namespace Radar.Map;

/// <summary>Подпись населённого пункта: мировые координаты, население, класс (0 — столица/центр региона, 1 — центр района, 2 — прочие).</summary>
public readonly record struct PlaceLabel(string Name, Point World, int Population, byte Class)
{
    /// <summary>Важность для порядка и порога появления: население, у центров без данных — условное.</summary>
    public int Weight => Math.Max(Population, Class == 0 ? 15000 : Class == 1 ? 3000 : 0);
}

/// <summary>
/// Слой подписей населённых пунктов (Assets/labels.bin.gz, tools/build_labels.py): ~200 тыс. пунктов,
/// упорядочены по важности. Для отрисовки — сеточный индекс: выборка видимых пунктов не перебирает весь список.
/// </summary>
public static class PlaceLabels
{
    private const double Cell = 0.5; // мировых единиц (256 — весь мир) ≈ 55 км по долготе
    private static readonly Lazy<(PlaceLabel[] All, Dictionary<(int, int), int[]> Grid)> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Preload() => Task.Run(() => _data.Value);

    public static IReadOnlyList<PlaceLabel> All => _data.Value.All;

    /// <summary>Пункты в прямоугольнике с весом не ниже порога — по убыванию важности.</summary>
    public static List<PlaceLabel> InView(Rect view, int minWeight)
    {
        var (all, grid) = _data.Value;
        var idx = new List<int>();
        int x0 = (int)Math.Floor(view.Left / Cell), x1 = (int)Math.Floor(view.Right / Cell);
        int y0 = (int)Math.Floor(view.Top / Cell), y1 = (int)Math.Floor(view.Bottom / Cell);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!grid.TryGetValue((x, y), out var cell)) continue;
                foreach (var i in cell)
                {
                    // в ячейке индексы по возрастанию = по убыванию важности
                    if (all[i].Weight < minWeight) break;
                    if (view.Contains(all[i].World)) idx.Add(i);
                }
            }
        idx.Sort();
        var res = new List<PlaceLabel>(idx.Count);
        foreach (var i in idx) res.Add(all[i]);
        return res;
    }

    private static (PlaceLabel[], Dictionary<(int, int), int[]>) Load()
    {
        using var res = typeof(PlaceLabels).Assembly.GetManifestResourceStream("Radar.labels.bin.gz");
        if (res == null) return ([], []);
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var d = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (d.Length < 8 || Encoding.ASCII.GetString(d[..4]) != "RLB1") return ([], []);
        int n = BitConverter.ToInt32(d.Slice(4, 4));
        var all = new PlaceLabel[n];
        int pos = 8;
        for (int i = 0; i < n; i++)
        {
            float lat = BitConverter.ToSingle(d.Slice(pos, 4)), lon = BitConverter.ToSingle(d.Slice(pos + 4, 4));
            int pop = BitConverter.ToInt32(d.Slice(pos + 8, 4));
            byte cls = d[pos + 12];
            int len = BitConverter.ToUInt16(d.Slice(pos + 13, 2));
            pos += 15;
            var name = Encoding.UTF8.GetString(d.Slice(pos, len));
            pos += len;
            all[i] = new PlaceLabel(name, Basemap.Project(lat, lon), pop, cls);
        }
        // файл упорядочен по важности; на всякий случай — устойчиво по весу
        var order = Enumerable.Range(0, n).OrderByDescending(i => all[i].Weight).ThenBy(i => i).ToArray();
        all = order.Select(i => all[i]).ToArray();
        var lists = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < all.Length; i++)
        {
            var key = ((int)Math.Floor(all[i].World.X / Cell), (int)Math.Floor(all[i].World.Y / Cell));
            if (!lists.TryGetValue(key, out var l)) lists[key] = l = [];
            l.Add(i);
        }
        return (all, lists.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }
}
