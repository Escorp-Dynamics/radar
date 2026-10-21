namespace Radar.Services;

/// <summary>
/// Растровая маска «точка внутри контуров»: быстрый ответ для большинства точек и признак «нужна точная проверка»
/// у границ. Ячейки, которых касается хоть одно ребро контура (с запасом в соседние), помечаются пограничными —
/// ответ по маске даётся только для остальных, где вся ячейка целиком по одну сторону границы. Результат совпадает
/// с точным обходом контуров (чётно-нечётное правило по каждому объекту, объединение объектов).
/// </summary>
public sealed class RegionMask
{
    public const byte Outside = 0, Inside = 1, Border = 2;

    private readonly byte[] _cells;
    private readonly int _w, _h;
    private readonly double _minX, _minY, _cell;

    private RegionMask(byte[] cells, int w, int h, double minX, double minY, double cell)
        => (_cells, _w, _h, _minX, _minY, _cell) = (cells, w, h, minX, minY, cell);

    /// <summary>Строит маску по объектам (каждый — набор контуров) с шагом сетки <paramref name="cell"/>.</summary>
    public static RegionMask Build(IReadOnlyList<(double X, double Y)[][]> features, double cell)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var f in features)
            foreach (var ring in f)
                foreach (var (x, y) in ring)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (minX > maxX) return new RegionMask([], 0, 0, 0, 0, cell);
        // запас в две ячейки: пограничная окрестность не выходит за сетку
        minX -= 2 * cell; minY -= 2 * cell; maxX += 2 * cell; maxY += 2 * cell;
        int w = (int)Math.Ceiling((maxX - minX) / cell) + 1, h = (int)Math.Ceiling((maxY - minY) / cell) + 1;
        var cells = new byte[w * h];

        foreach (var f in features)
        {
            // пересечения горизонтали через центр строки с рёбрами объекта — чётно-нечётное правило, как в точной проверке
            var rows = new List<double>?[h];
            foreach (var ring in f)
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    var (ax, ay) = ring[i];
                    var (bx, by) = ring[j];
                    if (ay == by) continue;
                    double lo = Math.Min(ay, by), hi = Math.Max(ay, by);
                    int r0 = Math.Max(0, (int)Math.Ceiling((lo - minY) / cell - 0.5));
                    int r1 = Math.Min(h - 1, (int)Math.Floor((hi - minY) / cell - 0.5));
                    for (int r = r0; r <= r1; r++)
                    {
                        double yc = minY + (r + 0.5) * cell;
                        if ((ay > yc) == (by > yc)) continue;
                        (rows[r] ??= []).Add((bx - ax) * (yc - ay) / (by - ay) + ax);
                    }
                }
            for (int r = 0; r < h; r++)
            {
                if (rows[r] is not { Count: > 1 } xs) continue;
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = Math.Max(0, (int)Math.Ceiling((xs[k] - minX) / cell - 0.5));
                    int c1 = Math.Min(w - 1, (int)Math.Floor((xs[k + 1] - minX) / cell - 0.5));
                    for (int c = c0; c <= c1; c++) cells[r * w + c] = Inside;
                }
            }
        }

        // пограничные ячейки: вдоль каждого ребра шагом в полячейки, вместе с восемью соседними
        foreach (var f in features)
            foreach (var ring in f)
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    var (ax, ay) = ring[i];
                    var (bx, by) = ring[j];
                    double len = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
                    int steps = Math.Max(1, (int)Math.Ceiling(len / (cell * 0.5)));
                    for (int s = 0; s <= steps; s++)
                    {
                        double t = (double)s / steps;
                        int cx = (int)Math.Floor((ax + (bx - ax) * t - minX) / cell), cy = (int)Math.Floor((ay + (by - ay) * t - minY) / cell);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int x = cx + dx, y = cy + dy;
                                if ((uint)x < (uint)w && (uint)y < (uint)h) cells[y * w + x] = Border;
                            }
                    }
                }
        return new RegionMask(cells, w, h, minX, minY, cell);
    }

    /// <summary>Внутри, снаружи или «пограничная ячейка» (тогда ответ даёт только точная проверка).</summary>
    public byte Query(double x, double y)
    {
        if (_w == 0) return Outside;
        double fx = (x - _minX) / _cell, fy = (y - _minY) / _cell;
        // вне сетки — вне рамок всех объектов, значит заведомо снаружи
        if (fx < 0 || fy < 0 || fx >= _w || fy >= _h) return Outside;
        return _cells[(int)fy * _w + (int)fx];
    }
}
