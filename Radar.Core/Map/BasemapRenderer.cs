using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using Radar.Services;

namespace Radar.Map;

/// <summary>Параметры кадра подложки: центр и масштаб камеры, размер окна, масштаб экрана, запас по краям.</summary>
public readonly record struct BaseFrame(Point Center, double Zoom, Size Size, double Scaling, double Margin, bool ShowFront,
    IReadOnlySet<string> TrackLabels, int FrontVersion)
{
    /// <summary>Мировые точки активных отметок: рядом с ними второстепенные пункты не подписываются (место для подписей тревог).</summary>
    public IReadOnlyList<Point> KeepOut { get; init; } = [];
    /// <summary>Доля разрешения растра (1 — полное; 0,5 — во время жеста: вчетверо меньше пикселей, чёткий кадр — после остановки).</summary>
    public double Resolution { get; init; } = 1;
    /// <summary>Подписи и значки верхнего слоя: мировая точка левого верхнего угла и размер на экране. Под ними подложка не подписывает ничего.</summary>
    public IReadOnlyList<(Point World, Size Size)> KeepOutRects { get; init; } = [];
}

/// <summary>
/// Отрисовка подложки (суша, регионы, реки, границы, линия фронта, подписи) напрямую через SkiaSharp.
/// Не использует объекты Avalonia (они привязаны к UI-потоку) — может работать в фоновом потоке,
/// пока на экране растягивается прежний кадр. Результат — растр в формате экрана (BGRA, premul).
/// </summary>
public static class BasemapRenderer
{
    private const double CacheMargin = 0.35;

    /// <summary>Нарисованная подпись населённого пункта: область (точка + текст) в мировых координатах, сам пункт.</summary>
    public readonly record struct LabelHit(Rect World, string Name, Point Pos);

    public sealed class Result : IDisposable
    {
        public required SKBitmap Bitmap { get; init; }
        public required BaseFrame Frame { get; init; }
        /// <summary>Подписи пунктов РФ на кадре (для выбора пункта кликом по подписи).</summary>
        public IReadOnlyList<LabelHit> Labels { get; init; } = [];
        public void Dispose() => Bitmap.Dispose();
    }

    // ---- шрифты: IBM Plex Sans из ресурсов приложения (тот же, что в интерфейсе) ----
    private static readonly Lazy<(SKTypeface Regular, SKTypeface Medium)> Faces = new(() =>
    {
        SKTypeface Load(string file, SKFontStyle fallback)
        {
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Radar.Core/Assets/Fonts/{file}"));
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                using var data = SKData.CreateCopy(ms.ToArray());
                return SKTypeface.FromData(data) ?? SKTypeface.FromFamilyName(null, fallback);
            }
            catch
            {
                return SKTypeface.FromFamilyName(null, fallback);
            }
        }
        return (Load("IBMPlexSans-Regular.ttf", SKFontStyle.Normal),
            Load("IBMPlexSans-Medium.ttf", new SKFontStyle(SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)));
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Загружает шрифты заранее (в UI-потоке: загрузчик ресурсов Avalonia).</summary>
    public static void Prepare()
    {
        _ = Faces.Value;
    }

    private static SKColor C(IBrush b)
    {
        var c = ((ISolidColorBrush)b).Color;
        double o = ((ISolidColorBrush)b).Opacity;
        return new SKColor(c.R, c.G, c.B, (byte)Math.Round(c.A * o));
    }

    // палитра читается из темы один раз (кисти Avalonia — только в UI-потоке)
    private sealed record Palette(SKColor Sea, SKColor ForeignLand, SKColor RussiaLand, SKColor Lake, SKColor Coast, SKColor River,
        SKColor RegionBorder, SKColor ForeignBorder, SKColor RuBorder, SKColor FrontLine, SKColor FrontText, SKColor CityDot,
        SKColor CityText, SKColor MinorCityText, SKColor ForeignCityText, SKColor ForeignMinorText, SKColor RegionText, SKColor CountryText, SKColor SeaText, SKColor Halo);

    private static Palette? _palette;

    public static void PreparePalette() => _palette ??= new Palette(C(MapTheme.Sea), C(MapTheme.ForeignLand), C(MapTheme.RussiaLand), C(MapTheme.Lake),
        C(MapTheme.Coast), C(MapTheme.River), C(MapTheme.RegionBorder), C(MapTheme.ForeignBorder), C(MapTheme.RuBorder), C(MapTheme.FrontLine),
        C(MapTheme.FrontText), C(MapTheme.CityDot), C(MapTheme.CityText), C(MapTheme.MinorCityText), C(MapTheme.ForeignCityText), C(MapTheme.ForeignMinorCityText),
        C(MapTheme.RegionText), C(MapTheme.CountryText), C(MapTheme.SeaText), C(MapTheme.TextHalo));

    // ---- кэш спрайтов подписей (текст с ореолом) — потокобезопасный ----
    private sealed record Sprite(SKImage Image, float Width, float Height, float Pad);
    private static readonly ConcurrentDictionary<(string Text, float Size, int Face, uint Color, float Scale), Sprite> Sprites = new();

    private static readonly List<(SKImage Image, long At)> RetiredSprites = [];

    private static bool DisposeSprite(SKImage i) { i.Dispose(); return true; }

    private static SKFont Font(int face, float size)
    {
        var f = Faces.Value;
        // курсив (face 2) — наклоном, как синтетический курсив Avalonia (курсивное начертание не встраиваем)
        return new SKFont(face == 1 ? f.Medium : f.Regular, size) { Subpixel = true, Edging = SKFontEdging.Antialias, SkewX = face == 2 ? -0.2f : 0 };
    }

    // размеры подписей зависят только от текста, начертания и кегля, а не от позиции: измеряются один раз
    private static readonly ConcurrentDictionary<(string Text, int Face, float Size), (float W, float H)> Measured = new();

    private static (float W, float H) Measure(string text, int face, float size)
    {
        var key = (text, face, size);
        if (Measured.TryGetValue(key, out var hit)) return hit;
        if (Measured.Count > 60_000) Measured.Clear();
        using var font = Font(face, size);
        float w = font.MeasureText(text);
        var m = font.Metrics;
        return Measured[key] = (w, m.Descent - m.Ascent);
    }

    private static readonly (float X, float Y)[] HaloOffsets =
        [(-1.3f, 0), (1.3f, 0), (0, -1.3f), (0, 1.3f), (-0.95f, -0.95f), (0.95f, -0.95f), (-0.95f, 0.95f), (0.95f, 0.95f)];

    private static Sprite GetSprite(string text, int face, float size, SKColor color, SKColor halo, float scale)
    {
        var key = (text, size, face, (uint)color, scale);
        if (Sprites.TryGetValue(key, out var sp)) return sp;
        if (Sprites.Count > 30000)
        {
            // растры подписей освобождаются с отсрочкой: параллельный кадр мог взять спрайт и ещё рисует его
            lock (RetiredSprites)
            {
                long now = Environment.TickCount64;
                RetiredSprites.RemoveAll(r => now - r.At > 15_000 && DisposeSprite(r.Image));
                foreach (var old in Sprites.Values) RetiredSprites.Add((old.Image, now));
            }
            Sprites.Clear();
        }
        using var font = Font(face, size * scale);
        var m = font.Metrics;
        float w = font.MeasureText(text), h = m.Descent - m.Ascent;
        float pad = 2 * scale;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Ceiling(w + 2 * pad)), Math.Max(1, (int)Math.Ceiling(h + 2 * pad)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = halo };
        float bx = pad, by = pad - m.Ascent;
        foreach (var (dx, dy) in HaloOffsets) c.DrawText(text, bx + dx * scale, by + dy * scale, font, paint);
        paint.Color = color;
        c.DrawText(text, bx, by, font, paint);
        sp = new Sprite(surface.Snapshot(), w / scale, h / scale, pad / scale);
        Sprites[key] = sp;
        return sp;
    }

    /// <summary>Рисует подложку. Безопасно вызывать из фонового потока (после <see cref="Prepare"/>/<see cref="PreparePalette"/>).</summary>
    /// <param name="background">Фоновая перерисовка: одной полосой. Каждая полоса заново разбирает рёбра всех контуров
    /// (материк — сотни тысяч точек), так что параллельность ускоряет кадр, но умножает общую работу процессора;
    /// в фоне задержка не видна (показывается растянутый прежний кадр).</param>
    public static Result Render(BaseFrame f, CancellationToken ct = default, bool background = false)
    {
        var pal = _palette ?? throw new InvalidOperationException("PreparePalette() не вызван");
        double mx = MarginOf(f.Size.Width, f.Margin, f.Scaling), my = MarginOf(f.Size.Height, f.Margin, f.Scaling);
        double fullW = f.Size.Width + 2 * mx, fullH = f.Size.Height + 2 * my;
        int pw = Math.Max(1, (int)Math.Ceiling(fullW * f.Scaling * f.Resolution)), ph = Math.Max(1, (int)Math.Ceiling(fullH * f.Scaling * f.Resolution));
        var bmp = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
        // при отмене или ошибке битмап (десятки МБ) освобождаем сразу, а не ждём финализатора
        try { return RenderInto(bmp, f, pal, mx, my, fullW, fullH, pw, ph, ct, background); }
        catch { bmp.Dispose(); throw; }
    }

    private static Result RenderInto(SKBitmap bmp, BaseFrame f, Palette pal, double mx, double my, double fullW, double fullH, int pw, int ph, CancellationToken ct, bool background)
    {
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(pal.Sea);
        canvas.Scale((float)(pw / fullW), (float)(ph / fullH));
        canvas.Translate((float)mx, (float)my);

        double scale = Math.Pow(2, f.Zoom);
        // центр — по пикселю устройства, как в MapControl (иначе при нечётном окне всё сдвинуто на 0,5 px)
        double hw = MapControl.SnapHalf(f.Size.Width, f.Scaling), hh = MapControl.SnapHalf(f.Size.Height, f.Scaling);
        Point ToScreen(Point w) => new((w.X - f.Center.X) * scale + hw, (w.Y - f.Center.Y) * scale + hh);
        Point ToWorld(Point s) => new((s.X - hw) / scale + f.Center.X, (s.Y - hh) / scale + f.Center.Y);
        var view = new Rect(ToWorld(new Point(-mx, -my)), ToWorld(new Point(f.Size.Width + mx, f.Size.Height + my)));
        int lod = f.Zoom < 6.3 ? 0 : 1;

        long pt0 = System.Diagnostics.Stopwatch.GetTimestamp();
        void PT(int i) { if (Phase != null) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); Phase[i] += System.Diagnostics.Stopwatch.GetElapsedTime(pt0, t).TotalMilliseconds; pt0 = t; } }

        // геометрия — параллельно горизонтальными полосами: у каждой своя канва над своими строками общего битмапа
        // и своя видимая область (пути обрезаются по полосе). Пути и маска только читаются
        int bands = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        if (ph < 256 || background) bands = 1;
        int bandH = (ph + bands - 1) / bands;
        nint basePtr = bmp.GetPixels();
        int rowBytes = bmp.RowBytes;
        float sx = (float)(pw / fullW), sy = (float)(ph / fullH);
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands, CancellationToken = ct }, band =>
        {
            int y0 = band * bandH, y1 = Math.Min(ph, y0 + bandH);
            if (y1 <= y0) return;
            using var part = new SKBitmap();
            part.InstallPixels(new SKImageInfo(pw, y1 - y0, SKColorType.Bgra8888, SKAlphaType.Premul), basePtr + y0 * rowBytes, rowBytes);
            using var bc = new SKCanvas(part);
            bc.Translate(0, -y0);
            bc.Scale(sx, sy);
            bc.Translate((float)mx, (float)my);
            // полоса в логических координатах экрана (с запасом на толщину линий) → в мировые
            double top = y0 / sy - my - 4, bottom = y1 / sy - my + 4;
            var bview = new Rect(ToWorld(new Point(-mx, top)), ToWorld(new Point(f.Size.Width + mx, bottom)));
            DrawGeometry(bc, f, pal, bview, scale, hw, hh, lod, ct);
        });
        ct.ThrowIfCancellationRequested();
        PT(0);
        float sc = (float)f.Scaling;
        using var text = new SKPaint { IsAntialias = true };
        using var img = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);

        void DrawPlain(string s, int face, float size, SKColor color, double x, double y)
        {
            using var font = Font(face, size);
            text.Color = color;
            canvas.DrawText(s, (float)x, (float)(y - font.Metrics.Ascent), font, text);
        }
        void DrawSprite(Sprite sp, double x, double y)
        {
            // по пиксельной сетке — спрайт остаётся чётким
            float sx = (float)(Math.Round((x - sp.Pad) * sc) / sc), sy = (float)(Math.Round((y - sp.Pad) * sc) / sc);
            canvas.DrawImage(sp.Image, SKRect.Create(sx, sy, sp.Image.Width / sc, sp.Image.Height / sc), sampling, img);
        }

        // верхний слой (выноски, плашки, значки) — абсолютный запрет, в том числе для крупных городов
        var hardOut = new RectGrid();
        foreach (var (w, sz) in f.KeepOutRects) hardOut.Add(new Rect(ToScreen(w), sz).Inflate(4));

        // подпись вдоль линии фронта — по самому плавному её участку, текст следует изгибу линии
        Rect? frontLabelBox = null;
        if (f.ShowFront && f.Zoom >= 6.2)
        {
            const string label = "линия соприкосновения";
            var (w, h) = Measure(label, 2, 10);
            var screenRuns = FrontRuns(view.Inflate(0.0)).Select(r => (IReadOnlyList<(double X, double Y)>)r.Pts.Select(q => { var s = ToScreen(q); return (s.X, s.Y); }).ToList()).ToList();
            // подпись целиком в видимой области, не под выносками и плашками верхнего слоя
            var spot = FrontLabel.Pick(screenRuns, w + 12, (24, 40, f.Size.Width - 24, f.Size.Height - 24), h,
                (x, y) => hardOut.Overlaps(new Rect(x - 2, y - 2, 4, 4)));
            if (spot != null)
            {
                using var path = new SKPath();
                path.MoveTo((float)spot[0].X, (float)spot[0].Y);
                for (int k = 1; k < spot.Count; k++) path.LineTo((float)spot[k].X, (float)spot[k].Y);
                using var font = Font(2, 10);
                using var paint = new SKPaint { IsAntialias = true, Color = pal.FrontText };
                // над линией, по центру отрезка
                canvas.DrawTextOnPath(label, path, (float)((PathLen(spot) - w) / 2), 0f, font, paint);
                // подписи городов не ложатся на надпись: её рамка — по крайним точкам участка с запасом на высоту текста
                frontLabelBox = new Rect(new Point(spot.Min(q => q.X) - 3, spot.Min(q => q.Y) - h - 3), new Point(spot.Max(q => q.X) + 3, spot.Max(q => q.Y) + 3));
            }
        }

        // моря и страны
        foreach (var wt in Waters)
        {
            if (f.Zoom < wt.MinZoom || f.Zoom > wt.MaxZoom) continue;
            var p = Basemap.Project(wt.Lat, wt.Lon);
            if (!view.Contains(p)) continue;
            float size = f.Zoom < 6 ? 10.5f : 12;
            var (w, h) = Measure(wt.Name, 2, size);
            var s = ToScreen(p);
            if (hardOut.Overlaps(new Rect(s.X - w / 2, s.Y - h / 2, w, h))) continue;
            DrawPlain(wt.Name, 2, size, pal.SeaText, s.X - w / 2, s.Y - h / 2);
        }
        if (f.Zoom <= 8)
            foreach (var cn in Countries)
            {
                var p = Basemap.Project(cn.Lat, cn.Lon);
                if (!view.Contains(p)) continue;
                float size = f.Zoom < 6 ? 10 : 12;
                var name = Spaced(cn.Name);
                var (w, h) = Measure(name, 1, size);
                var s = ToScreen(p);
                if (hardOut.Overlaps(new Rect(s.X - w / 2, s.Y - h / 2, w, h))) continue;
                DrawPlain(name, 1, size, pal.CountryText, s.X - w / 2, s.Y - h / 2);
            }
        ct.ThrowIfCancellationRequested();

        // подписи регионов и населённых пунктов
        var placed = new RectGrid();
        if (frontLabelBox is { } flb) placed.Add(flb);
        if (f.Zoom is >= 5.6 and <= 9)
            foreach (var r in Basemap.Of(FeatureKind.Region, 1).Where(x => view.Contains(x.Label)))
            {
                if (r.Bounds.Width * scale < 90) continue;
                var name = Spaced(ShortRegion(r.Name).ToUpperInvariant());
                var (w, h) = Measure(name, 1, 9);
                var p = ToScreen(r.Label);
                var rc = new Rect(p.X - w / 2, p.Y - h / 2, w, h);
                if (placed.Overlaps(rc) || hardOut.Overlaps(rc)) continue;
                DrawPlain(name, 1, 9, pal.RegionText, rc.X, rc.Y);
                placed.Add(rc.Inflate(4));
            }

        int maxRank = (int)Math.Floor(f.Zoom - 1.5);
        var cities = Basemap.Of(FeatureKind.City, 0)
            .Where(x => x.Id is not ("RUS" or "UKR") && x.Rank <= maxRank && view.Contains(x.Label))
            .Select(x => (x.Name, Pos: x.Label, Tier: x.Rank <= 3 ? 0 : x.Rank <= 6 ? 1 : 2, Ru: false))
            .ToList();
        foreach (var l in PlaceLabels.InView(view, LabelMinWeight(f.Zoom)))
            cities.Add((l.Name, l.World, l.Weight >= 400_000 || (l.Class == 0 && l.Weight >= 100_000) ? 0 : l.Weight >= 40_000 ? 1 : 2, Basemap.InRussia(l.World)));
        // устойчиво: внутри уровня — порядок по важности (крупные пункты занимают место первыми)
        cities = cities.OrderBy(x => x.Tier).ToList();

        // вокруг отметок тревог — место для их подписей: там подписываются только крупные города
        var keepOut = new RectGrid();
        double ko = f.Zoom < 6.3 ? 34 : f.Zoom < 8 ? 28 : 22;
        foreach (var w in f.KeepOut)
        {
            var s = ToScreen(w);
            keepOut.Add(new Rect(s.X - ko, s.Y - ko * 0.75, 2 * ko + 60, 1.5 * ko));
        }

        using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        // адаптивная плотность: экран делится на ячейки, в ячейке — не больше одной подписи (крупные города — всегда);
        // вокруг мелких подписей — пустое поле. Без этого раскладка «без наложений» заполняет каждый просвет и карта рябит
        double cellPx = DensityCell(f.Zoom);
        var occupied = new HashSet<(int, int)>();
        double gapMid = cellPx * 0.18, gapMinor = cellPx * 0.3;
        int n = 0, drawn = 0;
        var hits = new List<LabelHit>();
        foreach (var c in cities)
        {
            if ((++n & 255) == 0) ct.ThrowIfCancellationRequested();
            if (f.TrackLabels.Contains(Gazetteer.Normalize(c.Name))) continue;
            var s = ToScreen(c.Pos);
            bool major = c.Tier == 0, mid = c.Tier == 1;
            // ячейки привязаны к миру, а не к экрану: при сдвиге карты набор подписей не «прыгает»
            var cell = ((int)Math.Floor(c.Pos.X * scale / cellPx), (int)Math.Floor(c.Pos.Y * scale / cellPx));
            if (!major && occupied.Contains(cell)) continue;
            float fs = major ? 12 : mid ? 11 : 10;
            int face = major ? 1 : 0;
            // пункты за пределами РФ — заметно тусклее: на карте сразу видно, где своя территория
            var color = !c.Ru ? (major ? pal.ForeignCityText : pal.ForeignMinorText) : major || mid ? pal.CityText : pal.MinorCityText;
            double r = major ? 2.6 : 1.8;
            var dot = new Rect(s.X - r, s.Y - r, 2 * r, 2 * r);
            if (placed.Overlaps(dot)) continue;
            if (!major && keepOut.Overlaps(dot)) continue;
            if (hardOut.Overlaps(dot)) continue;
            var (w, h) = Measure(c.Name, face, fs);
            // справа, слева, сверху, снизу — первая свободная позиция
            Rect? labelRect = null;
            foreach (var cand in (ReadOnlySpan<Rect>)[
                         new Rect(s.X + r + 4, s.Y - h / 2, w, h),
                         new Rect(s.X - r - 4 - w, s.Y - h / 2, w, h),
                         new Rect(s.X - w / 2, s.Y - r - 2 - h, w, h),
                         new Rect(s.X - w / 2, s.Y + r + 1, w, h)])
                if (!hardOut.Overlaps(cand) && !placed.Overlaps(major ? cand : cand.Inflate(mid ? gapMid : gapMinor))) { labelRect = cand; break; }
            if (labelRect is not { } lr) continue;
            occupied.Add(cell);
            if (s.X >= 0 && s.Y >= 0 && s.X <= f.Size.Width && s.Y <= f.Size.Height) drawn++;
            dotPaint.Color = c.Ru ? pal.CityDot : pal.ForeignMinorText;
            if (major) canvas.DrawRect(SKRect.Create((float)dot.X, (float)dot.Y, (float)dot.Width, (float)dot.Height), dotPaint);
            else canvas.DrawCircle((float)s.X, (float)s.Y, (float)r, dotPaint);
            DrawSprite(GetSprite(c.Name, face, fs, color, pal.Halo, sc), lr.X, lr.Y);
            if (c.Ru)
            {
                var u = lr.Union(dot).Inflate(2);
                hits.Add(new LabelHit(new Rect(ToWorld(u.TopLeft), ToWorld(u.BottomRight)), c.Name, c.Pos));
            }
            placed.Add(lr.Inflate(3));
            placed.Add(dot.Inflate(3));
        }
        canvas.Flush();
        PT(1);
        LastCityLabels = drawn;
        LastHardOut = f.KeepOutRects.Count;
        return new Result { Bitmap = bmp, Frame = f, Labels = hits };
    }

    // ---- линия фронта (мировые координаты) ----
    private static SKPath? _frontPath;
    private static int _frontVersion = -1;
    private static readonly Lock FrontLock = new();
    // замененные пути освобождаются не сразу — их может ещё рисовать параллельный кадр — а при следующей смене, спустя паузу
    private static readonly List<(SKPath Path, long At)> RetiredFronts = [];

    private static bool Dispose(SKPath p) { p.Dispose(); return true; }

    private static SKPath? FrontPath()
    {
        lock (FrontLock)
        {
            if (_frontPath == null || _frontVersion != Borderline.Version)
            {
                _frontVersion = Borderline.Version;
                var p = new SKPath();
                foreach (var part in Borderline.FrontLines)
                {
                    if (part.Length < 2) continue;
                    var a = Basemap.Project(part[0]);
                    p.MoveTo((float)a.X, (float)a.Y);
                    for (int i = 1; i < part.Length; i++) { var b = Basemap.Project(part[i]); p.LineTo((float)b.X, (float)b.Y); }
                }
                long now = Environment.TickCount64;
                RetiredFronts.RemoveAll(r => now - r.At > 30_000 && Dispose(r.Path));
                if (_frontPath != null) RetiredFronts.Add((_frontPath, now));
                _frontPath = p;
            }
            return _frontPath;
        }
    }

    /// <summary>Геометрия подложки (суша, регионы, озёра, реки, границы, фронт) в пределах <paramref name="view"/>.</summary>
    private static void DrawGeometry(SKCanvas canvas, BaseFrame f, Palette pal, Rect view, double scale, double hw, double hh, int lod, CancellationToken ct)
    {
        IEnumerable<BasemapFeature> Visible(FeatureKind kind) => Basemap.Of(kind, lod).Where(x => x.Bounds.Intersects(view));
        var lands = Visible(FeatureKind.Land).ToList();
        var regions = Visible(FeatureKind.Region).ToList();

        canvas.Save();
        canvas.Translate((float)hw, (float)hh);
        canvas.Scale((float)scale);
        canvas.Translate((float)-f.Center.X, (float)-f.Center.Y);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
        float px(double w) => (float)(w / scale);
        // под подписями верхнего слоя все линии подложки (реки, границы, берег, фронт) вырезаются, а не просто
        // притеняются: надпись лежит на чистой заливке
        using var mask = new SKPath();
        foreach (var (w, sz) in f.KeepOutRects)
        {
            var r = new Rect(w, new Size(sz.Width / scale, sz.Height / scale)).Inflate(new Thickness(5 / scale, 3 / scale));
            mask.AddRoundRect(new SKRect((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom), px(4), px(4));
        }
        bool masked = !mask.IsEmpty;
        var strokeView = view.Inflate(px(4));
        // обводка — только по видимым участкам контура: Skia обводит путь целиком, даже за кадром
        void StrokeFeature(BasemapFeature x)
        {
            if (x.StrokePathIn(scale, strokeView) is { } part) { using (part) canvas.DrawPath(part, stroke); }
            else canvas.DrawPath(x.PathAt(scale), stroke);
        }
        // берег: обводка суши вдвое шире и ДО заливки — заливка соседних контуров закрывает внутреннюю половину линии,
        // и на сухопутной границе (общей у двух контуров суши) линии нет, а у моря остаётся наружная половина
        canvas.Save();
        if (masked) canvas.ClipPath(mask, SKClipOperation.Difference, antialias: true);
        stroke.StrokeCap = SKStrokeCap.Butt;
        stroke.Color = pal.Coast; stroke.StrokeWidth = px(1.8);
        foreach (var x in lands) StrokeFeature(x);
        stroke.StrokeCap = SKStrokeCap.Round;
        canvas.Restore();
        fill.Color = pal.ForeignLand;
        foreach (var x in lands) canvas.DrawPath(x.PathAt(scale), fill);
        stroke.Color = pal.ForeignBorder; stroke.StrokeWidth = px(0.8);
        canvas.Save();
        if (masked) canvas.ClipPath(mask, SKClipOperation.Difference, antialias: true);
        foreach (var x in Visible(FeatureKind.ForeignRegion)) StrokeFeature(x);
        canvas.Restore();
        fill.Color = pal.RussiaLand;
        foreach (var x in regions) canvas.DrawPath(x.PathAt(scale), fill);
        fill.Color = pal.Lake;
        foreach (var x in Visible(FeatureKind.Lake)) canvas.DrawPath(x.PathAt(scale), fill);
        canvas.Save();
        if (masked) canvas.ClipPath(mask, SKClipOperation.Difference, antialias: true);
        stroke.Color = pal.River;
        foreach (var x in Visible(FeatureKind.River))
        {
            if (x.Rank > 5 && f.Zoom < 7) continue;
            stroke.StrokeWidth = px(x.Rank <= 3 ? 1.3 : x.Rank <= 5 ? 0.9 : 0.6);
            StrokeFeature(x);
        }
        ct.ThrowIfCancellationRequested();
        stroke.StrokeCap = SKStrokeCap.Butt;
        stroke.Color = pal.RegionBorder; stroke.StrokeWidth = px(0.9);
        foreach (var x in regions) StrokeFeature(x);
        stroke.StrokeCap = SKStrokeCap.Round;
        stroke.Color = pal.RuBorder; stroke.StrokeWidth = px(1.3);
        foreach (var x in Visible(FeatureKind.RussiaOutline)) StrokeFeature(x);
        if (f.ShowFront && FrontPath() is { } fp && fp.Bounds.IntersectsWith(new SKRect((float)view.Left, (float)view.Top, (float)view.Right, (float)view.Bottom)))
        {
            stroke.StrokeCap = SKStrokeCap.Butt;
            stroke.Color = pal.FrontLine; stroke.StrokeWidth = px(1.4);
            float on = px(1.4 * 5), off = px(1.4 * 3);
            // пунктир только по видимым участкам: Skia иначе режет на штрихи всю линию фронта (на крупном зуме —
            // сотни тысяч штрихов). Фаза — от начала линии, чтобы штрихи не прыгали при сдвиге карты и на стыке полос
            foreach (var (pts, startLen) in FrontRuns(strokeView))
            {
                using var run = new SKPath();
                run.MoveTo((float)pts[0].X, (float)pts[0].Y);
                for (int i = 1; i < pts.Count; i++) run.LineTo((float)pts[i].X, (float)pts[i].Y);
                using var dash = SKPathEffect.CreateDash([on, off], (float)(startLen % (on + off)));
                stroke.PathEffect = dash;
                canvas.DrawPath(run, stroke);
            }
            stroke.PathEffect = null;
        }
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>Непрерывные видимые участки линии фронта (мировые координаты) и длина линии до начала участка.</summary>
    private static List<(List<Point> Pts, double StartLen)> FrontRuns(Rect view)
    {
        var runs = new List<(List<Point>, double)>();
        foreach (var part in Borderline.FrontLines)
        {
            if (part.Length < 2) continue;
            double len = 0;
            List<Point>? cur = null;
            var a = Basemap.Project(part[0]);
            for (int i = 1; i < part.Length; i++)
            {
                var b = Basemap.Project(part[i]);
                var seg = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
                if (seg.Intersects(view) || view.Contains(a))
                {
                    if (cur == null) { cur = [a]; runs.Add((cur, len)); }
                    cur.Add(b);
                }
                else cur = null;
                len += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                a = b;
            }
        }
        return runs;
    }

    private static double PathLen(List<(double X, double Y)> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
        return s;
    }

    private static (Point, Point)? LongestFrontSegment(Rect view)
    {
        (Point, Point)? best = null;
        double bestLen = 0;
        foreach (var part in Borderline.FrontLines)
            for (int i = 0; i + 1 < part.Length; i++)
            {
                var a = Basemap.Project(part[i]);
                var b = Basemap.Project(part[i + 1]);
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (!view.Contains(mid)) continue;
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len > bestLen) { bestLen = len; best = (a, b); }
            }
        return best;
    }

    private static readonly (string Name, double Lat, double Lon, double MinZoom, double MaxZoom)[] Waters =
    [
        ("Чёрное море", 43.3, 34.4, 4.5, 9),
        ("Азовское море", 46.1, 36.6, 6, 10),
        ("Каспийское море", 42.6, 50.6, 4.5, 9),
        ("Балтийское море", 56.6, 19.3, 4.5, 9),
        ("Белое море", 65.6, 37.8, 4.5, 9),
        ("Баренцево море", 71.5, 42, 3.5, 8),
        ("Финский залив", 60.0, 26.6, 6.3, 10),
        ("Керченский пролив", 45.25, 36.55, 8.5, 12),
    ];

    private static readonly (string Name, double Lat, double Lon)[] Countries =
    [
        ("УКРАИНА", 49.3, 31.6), ("БЕЛАРУСЬ", 53.5, 28.0), ("КАЗАХСТАН", 48.5, 63), ("ГРУЗИЯ", 42.2, 43.6),
        ("ТУРЦИЯ", 39.6, 34.5), ("ПОЛЬША", 52.2, 19.4), ("ФИНЛЯНДИЯ", 63.5, 26.5), ("ЛАТВИЯ", 56.9, 25.6),
        ("ЛИТВА", 55.3, 23.9), ("ЭСТОНИЯ", 58.7, 25.6), ("РУМЫНИЯ", 45.9, 24.9), ("МОЛДОВА", 47.2, 28.5),
        ("АЗЕРБАЙДЖАН", 40.4, 47.7), ("АРМЕНИЯ", 40.2, 45.0),
    ];

    private static string Spaced(string s) => string.Join('\u2009', s.ToCharArray());

    /// <summary>Сколько подписей пунктов нарисовано в последнем кадре (отладка плотности).</summary>
    public static int LastCityLabels;
    /// <summary>Замер фаз (бенчмарк): геометрия, подписи.</summary>
    public static double[]? Phase;

    /// <summary>Запас кэша по краю — целое число пикселей устройства: начало растра совпадает с пиксельной сеткой экрана.</summary>
    public static double MarginOf(double size, double margin, double scaling)
    {
        if (scaling <= 0) scaling = 1;
        return Math.Ceiling(size * margin * scaling) / scaling;
    }
    /// <summary>Отладка: сколько прямоугольников верхнего слоя подложка обошла в последнем кадре.</summary>
    public static int LastHardOut;

    /// <summary>
    /// Сторона ячейки плотности, px: на обзоре — редкие опорные города, при приближении ячейка уменьшается
    /// и проявляются районные центры, затем сёла.
    /// </summary>
    private static double DensityCell(double zoom) =>
        zoom < 5 ? 180 : zoom < 6 ? 165 : zoom < 7 ? 150 : zoom < 8 ? 135 : zoom < 9 ? 120 : zoom < 10 ? 104 : zoom < 11 ? 88 : zoom < 12 ? 72 : 60;

    /// <summary>
    /// Минимальная «важность» (население) подписываемого пункта на масштабе: ~100 тыс. на общем виде (z≈5,4),
    /// ~2,5 тыс. на уровне области (z7,5), все пункты — с z10,4. Плотность дополнительно ограничивает раскладка без наложений.
    /// </summary>
    private static int LabelMinWeight(double zoom) => zoom >= 10.4 ? 0 : (int)Math.Pow(10, 6.4 - (zoom - 3.5) * 0.75);

    private static string ShortRegion(string name) => name
        .Replace(" область", "")
        .Replace("Автономная Республика ", "")
        .Replace("Республика ", "")
        .Replace("Республика", "")
        .Replace(" край", "")
        .Replace(" автономный округ", " АО")
        .Replace("Еврейская автономная", "ЕАО")
        .Replace("-Алания", "")
        .Trim();

    /// <summary>Занятые подписями прямоугольники с сеточным индексом: проверка пересечения не перебирает все подписи.</summary>
    private sealed class RectGrid
    {
        private const double Cell = 96;
        private readonly Dictionary<(int, int), List<Rect>> _cells = [];

        public void Add(Rect r)
        {
            for (int x = (int)Math.Floor(r.Left / Cell); x <= (int)Math.Floor(r.Right / Cell); x++)
                for (int y = (int)Math.Floor(r.Top / Cell); y <= (int)Math.Floor(r.Bottom / Cell); y++)
                {
                    if (!_cells.TryGetValue((x, y), out var l)) _cells[(x, y)] = l = [];
                    l.Add(r);
                }
        }

        public bool Overlaps(Rect r)
        {
            for (int x = (int)Math.Floor(r.Left / Cell); x <= (int)Math.Floor(r.Right / Cell); x++)
                for (int y = (int)Math.Floor(r.Top / Cell); y <= (int)Math.Floor(r.Bottom / Cell); y++)
                    if (_cells.TryGetValue((x, y), out var l))
                        foreach (var p in l) if (p.Intersects(r)) return true;
            return false;
        }
    }
}
