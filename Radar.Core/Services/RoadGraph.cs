using System.IO.Compression;
using System.Text;
using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Офлайн-граф автодорог приграничья (Assets/roadgraph.bin.gz, tools/build_roadgraph.py, OSM ODbL):
/// кратчайший путь по дорогам между двумя точками — для сообщений «по трассе Рыльск — Хомутовка»,
/// «по дороге на Геройское».
/// </summary>
public static class RoadGraph
{
    private sealed class Graph
    {
        public required GeoPoint[] V;
        public required int[] EA, EB;
        public required byte[] Cls;
        public required GeoPoint[][] Mid;
        public required double[] Len;
        public required List<int>[] Adj;
        // сетка вершин для поиска ближайшей (ячейка ~0.05°)
        public required Dictionary<(int, int), List<int>> Grid;
    }

    private static readonly Lazy<Graph?> _g = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool Available => _g.Value != null;

    public static void Preload() => Task.Run(() => _g.Value);

    private const double Cell = 0.05;

    private static Graph? Load()
    {
        using var res = typeof(RoadGraph).Assembly.GetManifestResourceStream("Radar.roadgraph.bin.gz");
        if (res == null) return null;
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var d = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (d.Length < 8 || Encoding.ASCII.GetString(d[..4]) != "RRG1") return null;
        int pos = 4;
        int nv = (int)BitConverter.ToUInt32(d.Slice(pos, 4)); pos += 4;
        var v = new GeoPoint[nv];
        for (int i = 0; i < nv; i++) { v[i] = new GeoPoint(BitConverter.ToSingle(d.Slice(pos, 4)), BitConverter.ToSingle(d.Slice(pos + 4, 4))); pos += 8; }
        int ne = (int)BitConverter.ToUInt32(d.Slice(pos, 4)); pos += 4;
        var ea = new int[ne]; var eb = new int[ne]; var cls = new byte[ne]; var mid = new GeoPoint[ne][]; var len = new double[ne];
        var adj = new List<int>[nv];
        for (int i = 0; i < nv; i++) adj[i] = [];
        for (int e = 0; e < ne; e++)
        {
            ea[e] = (int)BitConverter.ToUInt32(d.Slice(pos, 4));
            eb[e] = (int)BitConverter.ToUInt32(d.Slice(pos + 4, 4));
            cls[e] = d[pos + 8];
            int n = BitConverter.ToUInt16(d.Slice(pos + 9, 2));
            pos += 11;
            var m = new GeoPoint[n];
            for (int k = 0; k < n; k++) { m[k] = new GeoPoint(BitConverter.ToSingle(d.Slice(pos, 4)), BitConverter.ToSingle(d.Slice(pos + 4, 4))); pos += 8; }
            mid[e] = m;
            double l = 0;
            var prev = v[ea[e]];
            foreach (var p in m) { l += Geo.DistanceKm(prev, p); prev = p; }
            l += Geo.DistanceKm(prev, v[eb[e]]);
            len[e] = l;
            adj[ea[e]].Add(e);
            adj[eb[e]].Add(e);
        }
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < nv; i++)
        {
            var key = ((int)Math.Floor(v[i].Lat / Cell), (int)Math.Floor(v[i].Lon / Cell));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i);
        }
        return new Graph { V = v, EA = ea, EB = eb, Cls = cls, Mid = mid, Len = len, Adj = adj, Grid = grid };
    }

    /// <summary>Ближайшие вершины графа к точке (в пределах <paramref name="maxKm"/>), по возрастанию расстояния.</summary>
    private static List<(int V, double Km)> NearVertices(Graph g, GeoPoint p, double maxKm, int take)
    {
        int r = (int)Math.Ceiling(maxKm / 111.0 / Cell) + 1;
        int cy = (int)Math.Floor(p.Lat / Cell), cx = (int)Math.Floor(p.Lon / Cell);
        var res = new List<(int, double)>();
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (g.Grid.TryGetValue((cy + dy, cx + dx), out var list))
                    foreach (var i in list)
                    {
                        double km = Geo.DistanceKm(p, g.V[i]);
                        if (km <= maxKm) res.Add((i, km));
                    }
        res.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return res.Count > take ? res.GetRange(0, take) : res;
    }

    /// <summary>
    /// Путь по дорогам от <paramref name="a"/> до <paramref name="b"/> (A*, вес — длина с поправкой на класс дороги:
    /// магистрали предпочтительнее). null — нет графа, точки далеко от дорог или путь неправдоподобно длинный.
    /// </summary>
    public static List<GeoPoint>? Route(GeoPoint a, GeoPoint b, double maxDetour = 1.8)
    {
        var g = _g.Value;
        if (g == null) return null;
        double direct = Geo.DistanceKm(a, b);
        if (direct < 2 || direct > 400) return null;
        var sa = NearVertices(g, a, 6, 4);
        var sb = NearVertices(g, b, 6, 4);
        if (sa.Count == 0 || sb.Count == 0) return null;
        var goals = new Dictionary<int, double>();
        foreach (var (v, km) in sb) goals[v] = km;

        var dist = new Dictionary<int, double>();
        var prevE = new Dictionary<int, int>();
        var open = new PriorityQueue<int, double>();
        foreach (var (v, km) in sa) { dist[v] = km * 1.3; open.Enqueue(v, km * 1.3 + MinWeight * Geo.DistanceKm(g.V[v], b)); prevE[v] = -1; }
        double limit = direct * maxDetour + 10;
        int found = -1;
        double foundCost = double.MaxValue;
        int expanded = 0;
        while (open.TryDequeue(out var u, out var pri))
        {
            if (pri > foundCost || pri > limit * 1.35) break;
            if (++expanded > 200_000) break;
            double du = dist[u];
            if (goals.TryGetValue(u, out var tail) && du + tail * 1.3 < foundCost) { foundCost = du + tail * 1.3; found = u; }
            foreach (var e in g.Adj[u])
            {
                int w = g.EA[e] == u ? g.EB[e] : g.EA[e];
                double cost = du + g.Len[e] * ClassWeight(g.Cls[e]);
                if (dist.TryGetValue(w, out var dw) && dw <= cost) continue;
                dist[w] = cost;
                prevE[w] = e;
                open.Enqueue(w, cost + MinWeight * Geo.DistanceKm(g.V[w], b));
            }
        }
        if (found < 0) return null;

        // восстановление геометрии
        var chain = new List<GeoPoint>();
        int cur = found;
        var segs = new List<(int E, bool Forward)>();
        while (prevE.TryGetValue(cur, out var e) && e >= 0)
        {
            bool fwd = g.EB[e] == cur;
            segs.Add((e, fwd));
            cur = fwd ? g.EA[e] : g.EB[e];
        }
        segs.Reverse();
        chain.Add(a);
        chain.Add(g.V[cur]);
        foreach (var (e, fwd) in segs)
        {
            if (fwd) { chain.AddRange(g.Mid[e]); chain.Add(g.V[g.EB[e]]); }
            else { for (int k = g.Mid[e].Length - 1; k >= 0; k--) chain.Add(g.Mid[e][k]); chain.Add(g.V[g.EA[e]]); }
        }
        chain.Add(b);
        double len = Geo.PathLength(chain);
        if (len > direct * maxDetour + 6) return null;
        return chain;
    }

    // эвристика A* не больше реальной стоимости: самое «дешёвое» ребро — магистраль
    private const double MinWeight = 0.85;

    // магистрали — «дешевле»: объект, идущий «по трассе», скорее держится главной дороги
    private static double ClassWeight(byte c) => c switch { 0 => 0.85, 1 => 0.88, 2 => 0.95, 3 => 1.0, _ => 1.12 };
}
