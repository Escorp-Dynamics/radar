namespace Radar.Services;

/// <summary>
/// Подпись вдоль линии, как на контурных картах: буквы повторяют изгиб линии и стоят рядом с ней — над или под, на
/// постоянном зазоре, — а не на самой линии и не по хорде. Чистая геометрия в экранных координатах.
/// Результат — базовая линия текста (ломаная, читается слева направо), по которой рисуется надпись.
/// </summary>
public static class FrontLabel
{
    private const double Step = 4;             // шаг передискретизации, px
    private const double MaxTurnPerStep = 0.12; // поворот на шаг (рад): круче — буквы налезают друг на друга
    private const double Gap = 5;               // зазор между линией и ближайшим краем текста, px
    private const double Clear = 2.5;           // меньше этого расстояния от текста до линии — пересечение
    private const double AscentShare = 0.75, DescentShare = 0.28; // доли высоты текста над и под базовой линией

    /// <param name="runs">Непрерывные участки линии (экранные координаты).</param>
    /// <param name="length">Длина надписи вдоль линии, px.</param>
    /// <param name="area">Область, в которой надпись должна лежать целиком (left, top, right, bottom).</param>
    /// <param name="textHeight">Высота строки текста, px.</param>
    /// <param name="blocked">Точка занята (выноска, плашка): надпись с такой точкой не подходит. null — всё свободно.</param>
    /// <summary>Запас в длине, заложенный вызывающим (w + 12): сам текст обязан поместиться целиком.</summary>
    public const double Slack = 10;

    public static List<(double X, double Y)>? Pick(IReadOnlyList<IReadOnlyList<(double X, double Y)>> runs, double length,
        (double L, double T, double R, double B) area, double textHeight, Func<double, double, bool>? blocked = null)
    {
        double cx = (area.L + area.R) / 2, cy = (area.T + area.B) / 2;
        var cands = new List<(double Cost, List<(double X, double Y)> Slice)>();
        int win = (int)Math.Ceiling(length / Step) + 1;
        foreach (var run in runs)
        {
            var line = Resample(run, Step);
            if (line.Count < win) continue;
            // исходная линия зубчатая (контуры полигонов): кривая под текст — её сглаженная версия
            var smooth = Resample(Smooth(line, 6, 3), Step);   // сглаживание укорачивает шаг: пересэмплируем, чтобы окно давало нужную длину
            for (int s = 0; s + win <= smooth.Count; s += 2)
            {
                double turn = 0, maxTurn = 0;
                for (int i = s + 1; i + 1 < s + win; i++)
                {
                    double t = Math.Abs(Turn(smooth[i - 1], smooth[i], smooth[i + 1]));
                    turn += t; maxTurn = Math.Max(maxTurn, t);
                }
                if (maxTurn > MaxTurnPerStep) continue;
                var mid = smooth[s + win / 2];
                // плавность важнее; из равных — ближе к центру области
                cands.Add((turn + 0.0004 * Math.Sqrt((mid.X - cx) * (mid.X - cx) + (mid.Y - cy) * (mid.Y - cy)), smooth.GetRange(s, win)));
            }
        }
        cands.Sort((a, b) => a.Cost.CompareTo(b.Cost));

        var segs = new List<((double X, double Y) A, (double X, double Y) B)>();
        foreach (var run in runs)
            for (int i = 1; i < run.Count; i++) segs.Add((run[i - 1], run[i]));

        int tried = 0;
        foreach (var (_, slice0) in cands)
        {
            if (++tried > 250) break;
            var slice = Orient(slice0);
            foreach (bool above in new[] { true, false })
            {
                var baseline = Offset(slice, above ? Gap + DescentShare * textHeight : -(Gap + AscentShare * textHeight));
                // со стороны внутреннего изгиба смещённая кривая короче исходной: надпись обязана на ней поместиться целиком,
                // иначе рисование по пути обрежет первые буквы («я соприкосновения»)
                if (Length(baseline) < length - Slack) continue;
                if (Fits(baseline, textHeight, area, segs, blocked)) return baseline;
            }
        }
        return null;
    }

    /// <summary>Длина ломаной, px.</summary>
    public static double Length(IReadOnlyList<(double X, double Y)> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += Math.Sqrt((p[i].X - p[i - 1].X) * (p[i].X - p[i - 1].X) + (p[i].Y - p[i - 1].Y) * (p[i].Y - p[i - 1].Y));
        return s;
    }

    /// <summary>Текст читается слева направо; у почти вертикальной линии — снизу вверх.</summary>
    private static List<(double X, double Y)> Orient(List<(double X, double Y)> pts)
    {
        var a = pts[0]; var b = pts[^1];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (dx < -1e-6 || (Math.Abs(dx) <= 1e-6 && dy > 0)) { var r = new List<(double X, double Y)>(pts); r.Reverse(); return r; }
        return pts;
    }

    /// <summary>Единичная нормаль «вверх» по отношению к направлению чтения (там, где стоят верхушки букв).</summary>
    private static (double X, double Y) Up(List<(double X, double Y)> p, int i)
    {
        var a = p[Math.Max(0, i - 1)]; var b = p[Math.Min(p.Count - 1, i + 1)];
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (0, -1) : (dy / len, -dx / len);
    }

    private static List<(double X, double Y)> Offset(List<(double X, double Y)> p, double up)
    {
        var res = new List<(double X, double Y)>(p.Count);
        for (int i = 0; i < p.Count; i++)
        {
            var u = Up(p, i);
            res.Add((p[i].X + u.X * up, p[i].Y + u.Y * up));
        }
        return res;
    }

    private static bool Fits(List<(double X, double Y)> baseline, double h, (double L, double T, double R, double B) area,
        List<((double X, double Y) A, (double X, double Y) B)> segs, Func<double, double, bool>? blocked)
    {
        // тело текста: от выносных элементов до верхушек; проверяются несколько уровней по высоте
        double[] levels = [-DescentShare * h, 0, 0.5 * AscentShare * h, AscentShare * h];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var body = new List<(double X, double Y)>();
        for (int i = 0; i < baseline.Count; i += 2)
        {
            var u = Up(baseline, i);
            foreach (var t in levels)
            {
                double x = baseline[i].X + u.X * t, y = baseline[i].Y + u.Y * t;
                if (x < area.L || x > area.R || y < area.T || y > area.B || (blocked?.Invoke(x, y) ?? false)) return false;
                body.Add((x, y));
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        // сегменты исходной линии, которые вообще могут оказаться рядом
        double m = Clear + 2;
        var near = new List<((double X, double Y) A, (double X, double Y) B)>();
        foreach (var s in segs)
            if (Math.Max(s.A.X, s.B.X) >= minX - m && Math.Min(s.A.X, s.B.X) <= maxX + m && Math.Max(s.A.Y, s.B.Y) >= minY - m && Math.Min(s.A.Y, s.B.Y) <= maxY + m)
                near.Add(s);
        foreach (var p in body)
            foreach (var s in near)
                if (DistSq(p, s.A, s.B) < Clear * Clear) return false;
        return true;
    }

    private static double DistSq((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        double qx = a.X + dx * t - p.X, qy = a.Y + dy * t - p.Y;
        return qx * qx + qy * qy;
    }

    /// <summary>Равномерная передискретизация ломаной с шагом <paramref name="step"/>.</summary>
    internal static List<(double X, double Y)> Resample(IReadOnlyList<(double X, double Y)> pts, double step)
    {
        var res = new List<(double X, double Y)>();
        if (pts.Count == 0) return res;
        res.Add(pts[0]);
        double carry = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double seg = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (seg < 1e-9) continue;
            double pos = step - carry;
            while (pos <= seg)
            {
                double t = pos / seg;
                res.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                pos += step;
            }
            carry = seg - (pos - step);
        }
        return res;
    }

    /// <summary>Сглаживание скользящим средним (радиус в отсчётах), несколько проходов; концы остаются на месте.</summary>
    internal static List<(double X, double Y)> Smooth(List<(double X, double Y)> pts, int radius, int passes)
    {
        var cur = pts;
        for (int p = 0; p < passes; p++)
        {
            var next = new List<(double X, double Y)>(cur.Count);
            for (int i = 0; i < cur.Count; i++)
            {
                int r = Math.Min(radius, Math.Min(i, cur.Count - 1 - i));
                double sx = 0, sy = 0;
                for (int k = i - r; k <= i + r; k++) { sx += cur[k].X; sy += cur[k].Y; }
                next.Add((sx / (2 * r + 1), sy / (2 * r + 1)));
            }
            cur = next;
        }
        return cur;
    }

    private static double Turn((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double a1 = Math.Atan2(b.Y - a.Y, b.X - a.X), a2 = Math.Atan2(c.Y - b.Y, c.X - b.X);
        double d = a2 - a1;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }
}
