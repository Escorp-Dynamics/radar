using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Radar.Map;

/// <summary>
/// Визуальный язык интерфейса — «тактический прибор»: акрил (размытие карты под панелью, плотная тонировка, зерно),
/// срезанные углы, волосяная кромка, акцентная засечка и метки по углам.
/// Используется и панелями разметки (<see cref="Radar.Views.GlassPanel"/>), и карточками, которые рисует карта.
/// </summary>
public static class Glass
{
    /// <summary>
    /// Облегчённая отрисовка для слабых видеочипов (Adreno 5xx/6xx младших серий, Mali-G5x/G6x, PowerVR):
    /// стекло без размытия (плотная тонировка); фоновая анимация карты и помехи — с частотой «экономно».
    /// Определяется по строке рендерера OpenGL при первом кадре на видеочипе.
    /// </summary>
    public static bool LowPower { get; private set; }
    private static bool _gpuProbed;

    internal static void ProbeGpu(GRRecordingContext? gpu)
    {
        if (_gpuProbed || gpu == null) return;
        _gpuProbed = true;
        string renderer = GlRenderer();
        bool weak = Environment.GetEnvironmentVariable("RADAR_LOWPOWER") is { Length: > 0 } force ? force == "1" : IsWeakGpu(renderer);
        if (!weak) return;
        LowPower = true;
    }

    /// <summary>Слабые мобильные видеочипы: Adreno до 620 (кроме 618/619 у флагманов средних лет — тоже слабые для полноэкранного стекла), Mali-G5x/G6x/T, PowerVR.</summary>
    public static bool IsWeakGpu(string renderer)
    {
        var m = System.Text.RegularExpressions.Regex.Match(renderer, @"Adreno\D*(\d{3})");
        if (m.Success) return int.Parse(m.Groups[1].Value) < 630;
        if (System.Text.RegularExpressions.Regex.IsMatch(renderer, @"Mali-(T\d|G[1-6]\d\b|G[1-7]\d MP[1-3]\b)")) return true;
        return renderer.Contains("PowerVR", StringComparison.Ordinal);
    }

    [System.Runtime.InteropServices.DllImport("libGLESv2", EntryPoint = "glGetString")]
    private static extern IntPtr GlGetString(int name);

    private static string GlRenderer()
    {
        try { return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(GlGetString(0x1F01)) ?? ""; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }
    public static readonly Color Tint = Color.FromArgb(196, 8, 10, 14);
    public static readonly Color Accent = Color.Parse("#7CD4FF");
    public static readonly IBrush Hairline = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    public const double BlurSigma = 24;

    /// <summary>Контур панели: прямоугольник со срезанными верхним левым и нижним правым углами.</summary>
    public static Geometry Shape(Rect r, double chamfer)
    {
        if (chamfer <= 0.5) return new RectangleGeometry(r);
        double c = Math.Min(chamfer, Math.Min(r.Width, r.Height) / 3);
        var g = new StreamGeometry();
        using (var x = g.Open())
        {
            x.BeginFigure(new Point(r.X + c, r.Y), true);
            x.LineTo(new Point(r.Right, r.Y));
            x.LineTo(new Point(r.Right, r.Bottom - c));
            x.LineTo(new Point(r.Right - c, r.Bottom));
            x.LineTo(new Point(r.X, r.Bottom));
            x.LineTo(new Point(r.X, r.Y + c));
            x.EndFigure(true);
        }
        return g;
    }

    /// <summary>
    /// Акриловая панель: размытие подложки, плотная тонировка, зерно и волосяная кромка — без бликов и подсветок.
    /// <paramref name="brackets"/> — тонкая акцентная засечка на верхней кромке и метки по углам.
    /// </summary>
    public static void Draw(DrawingContext ctx, Rect rect, double chamfer, Color? tint = null, double sigma = BlurSigma,
        bool border = true, bool brackets = false, Color? accent = null)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        if (!LowPower) ctx.Custom(new BackdropOp(rect, chamfer, sigma));
        var shape = Shape(rect, chamfer);
        // без размытия тонировка плотнее: карта под панелью не должна мешать чтению
        var t = tint ?? Tint;
        if (LowPower) t = Color.FromArgb((byte)Math.Max(t.A, (byte)236), t.R, t.G, t.B);
        ctx.DrawGeometry(new SolidColorBrush(t), null, shape);
        if (border) ctx.DrawGeometry(null, new Pen(Hairline, 1), Shape(rect.Deflate(0.5), Math.Max(0, chamfer - 0.2)));
        if (brackets) DrawTicks(ctx, rect.Deflate(0.5), chamfer, accent ?? Accent);
    }

    /// <summary>Акцентная засечка у среза на верхней кромке и короткие метки по прямым углам.</summary>
    public static void DrawTicks(DrawingContext ctx, Rect r, double chamfer, Color accent)
    {
        double c = chamfer > 0.5 ? Math.Min(chamfer, Math.Min(r.Width, r.Height) / 3) : 0;
        var strong = new Pen(new SolidColorBrush(Color.FromArgb(230, accent.R, accent.G, accent.B)), 1.5);
        ctx.DrawLine(strong, new Point(r.X + c + 2, r.Y), new Point(r.X + c + Math.Min(28, r.Width / 4), r.Y));
        var faint = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
        const double l = 5;
        ctx.DrawLine(faint, new Point(r.Right - l, r.Y), new Point(r.Right, r.Y));
        ctx.DrawLine(faint, new Point(r.Right, r.Y), new Point(r.Right, r.Y + l));
        ctx.DrawLine(faint, new Point(r.X, r.Bottom - l), new Point(r.X, r.Bottom));
        ctx.DrawLine(faint, new Point(r.X, r.Bottom), new Point(r.X + l, r.Bottom));
    }

    private static SKImage? _noise;
    private static readonly object NoiseLock = new();
    private const int NoiseN = 96;

    // вытесненные из кэшей растры освобождаются не сразу: параллельный кадр мог уже взять их и ещё рисует
    private static readonly List<(SKImage Image, long At)> Retired = [];

    private static void Retire(SKImage img)
    {
        long now = Environment.TickCount64;
        lock (Retired)
        {
            Retired.RemoveAll(r => now - r.At > 15_000 && DisposeImage(r.Image));
            Retired.Add((img, now));
        }
    }

    private static bool DisposeImage(SKImage i) { i.Dispose(); return true; }

    /// <summary>Мелкое зерно, как у акрила: размытая карта не выглядит «мыльной».</summary>
    private static SKImage NoiseImage()
    {
        lock (NoiseLock)
        {
        if (_noise != null) return _noise;
        const int n = NoiseN;
        using var bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(7);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // SetPixel принимает цвет без умножения на альфу: белый с альфой 0…6 даёт светлое зерно
                // (прежнее (0…6, 0…6, 0…6, 6) после умножения на альфу обращалось в ровное затемнение без зерна)
                byte a = (byte)(rnd.Next(256) * 6 / 255);
                bmp.SetPixel(x, y, new SKColor(255, 255, 255, a));
            }
        return _noise = SKImage.FromBitmap(bmp);
        }
    }

    private static SKPath SkShape(Rect rect, double chamfer)
    {
        var p = new SKPath();
        float x = (float)rect.X, y = (float)rect.Y, r = (float)rect.Right, b = (float)rect.Bottom;
        float c = (float)Math.Min(chamfer, Math.Min(rect.Width, rect.Height) / 3);
        if (c <= 0.5f) { p.AddRect(new SKRect(x, y, r, b)); return p; }
        p.MoveTo(x + c, y); p.LineTo(r, y); p.LineTo(r, b - c); p.LineTo(r - c, b); p.LineTo(x, b); p.LineTo(x, y + c); p.Close();
        return p;
    }

    /// <summary>Тень строго по контуру панели (со срезанными углами) и только снаружи — без прямоугольных «подложек» в срезах.</summary>
    public static void DrawShadow(DrawingContext ctx, Rect rect, double chamfer, BoxShadows shadows)
    {
        if (shadows.Count == 0 || rect.Width < 1 || rect.Height < 1) return;
        double pad = 0;
        foreach (var sh in shadows) pad = Math.Max(pad, sh.Blur * 1.5 + Math.Abs(sh.OffsetX) + Math.Abs(sh.OffsetY) + sh.Spread);
        ctx.Custom(new ShadowOp(rect, rect.Inflate(pad), chamfer, shadows));
    }

    private sealed class ShadowOp(Rect rect, Rect bounds, double chamfer, BoxShadows shadows) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            using var shape = SkShape(rect, chamfer);
            int save = canvas.Save();
            canvas.ClipPath(shape, SKClipOperation.Difference, true);
            // размытая маска тени дорогая, а панель перерисовывается с каждым кадром карты под ней: растр тени — один на размер
            var img = ShadowImage(rect.Size, bounds, rect, chamfer, shadows, out var origin);
            canvas.DrawImage(img, (float)(bounds.X + origin.X), (float)(bounds.Y + origin.Y));
            canvas.RestoreToCount(save);
        }
    }

    private static readonly Dictionary<string, SKImage> _shadows = [];

    private static SKImage ShadowImage(Size size, Rect bounds, Rect rect, double chamfer, BoxShadows shadows, out Point origin)
    {
        origin = default;
        var key = FormattableString.Invariant($"{size.Width:0.#}x{size.Height:0.#}|{rect.X - bounds.X:0.#},{rect.Y - bounds.Y:0.#}|{chamfer:0.#}|{shadows}");
        lock (_shadows)
        {
            if (_shadows.TryGetValue(key, out var img)) return img;
            if (_shadows.Count > 64) { foreach (var i in _shadows.Values) Retire(i); _shadows.Clear(); }
            int w = Math.Max(1, (int)Math.Ceiling(bounds.Width)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height));
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            c.Translate((float)-bounds.X, (float)-bounds.Y);
            foreach (var sh in shadows)
            {
                if (sh.IsInset) continue;
                using var path = SkShape(rect.Inflate(sh.Spread), chamfer + sh.Spread * 0.4);
                path.Offset((float)sh.OffsetX, (float)sh.OffsetY);
                using var mf = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.1, sh.Blur / 2));
                using var p = new SKPaint { Color = new SKColor(sh.Color.R, sh.Color.G, sh.Color.B, sh.Color.A), MaskFilter = mf, IsAntialias = true };
                c.DrawPath(path, p);
            }
            return _shadows[key] = surface.Snapshot();
        }
    }

    private static readonly Dictionary<(SKRectI, int), (SKImage Image, long Stamp, int Version)> _cache = [];
    private static int _version;
    private static readonly TimeSpan CacheAge = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Содержимое под стеклом заметно изменилось (сдвиг или масштаб карты, новые зоны, анимация появления):
    /// размытые копии пересчитываются в следующем кадре, не дожидаясь срока.
    /// </summary>
    public static void InvalidateBackdrop() => Interlocked.Increment(ref _version);

    private sealed class BackdropOp(Rect rect, double chamfer, double sigma) : ICustomDrawOperation
    {
        public Rect Bounds => rect;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) =>
            other is BackdropOp o && o.Bounds == rect && Math.Abs(o.Chamfer - chamfer) < 0.01 && Math.Abs(o.Sigma - sigma) < 0.01;
        private double Chamfer => chamfer;
        private double Sigma => sigma;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            var r = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
            using var path = SkShape(rect, chamfer);
            int save = canvas.Save();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
            // размытие + немного насыщенности: цвета угроз под стеклом остаются узнаваемыми
            var surface = lease.SkSurface;
            ProbeGpu(surface?.Context as GRRecordingContext);
            if (surface == null || !DrawCheapBackdrop(canvas, surface, r, (float)sigma))
                DrawLayerBackdrop(canvas, r, (float)sigma);
            // зерно копиями готового растра: тайловый шейдер на всю панель считает каждый пиксель отдельно
            // на видеочипе — один вызов с повторяющимся шейдером; на процессоре плитки дешевле попиксельного шейдера
            var g = NoiseImage();
            if (surface?.Context != null)
            {
                using var sh = g.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateTranslation(r.Left, r.Top));
                using var gp = new SKPaint { Shader = sh };
                canvas.DrawRect(r, gp);
            }
            else
                for (float y = r.Top; y < r.Bottom; y += NoiseN)
                    for (float x = r.Left; x < r.Right; x += NoiseN)
                        canvas.DrawImage(g, x, y);
            canvas.RestoreToCount(save);
        }

        private static readonly float[] Saturate = SaturationMatrix(1.15f);

        private static float[] SaturationMatrix(float s)
        {
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            return
            [
                lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0, 0,
                lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0, 0,
                lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0, 0,
                0, 0, 0, 1, 0,
            ];
        }

        /// <summary>
        /// Дешёвое стекло: снимок области под панелью с поверхности, уменьшение в 8 раз, размытие уменьшенной картинки
        /// и вывод с увеличением. Слой с фильтром подложки Skia на процессоре копирует и фильтрует всю область в полном
        /// разрешении — с десятком панелей над картой это была основная нагрузка кадра.
        /// </summary>
        private static bool DrawCheapBackdrop(SKCanvas canvas, SKSurface surface, SKRect r, float sigma)
        {
            var m = canvas.TotalMatrix;
            if (m.SkewX != 0 || m.SkewY != 0) return false;
            // область панели в пикселях устройства, уже обрезанная по поверхности и текущему клипу (контур панели)
            var src = canvas.DeviceClipBounds;
            src.Intersect(SKRectI.Round(m.MapRect(r)));
            if (src.Width < 4 || src.Height < 4) return true;
            var key = (src, (int)Math.Round(sigma));
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_cache)
            {
                // карта под панелью не двигалась — размытая копия годится ещё четверть секунды: фоновая пульсация
                // и пунктиры под стеклом всё равно расплываются, а снимок с размытием — самая дорогая часть панели
                if (_cache.TryGetValue(key, out var hit) && hit.Version == Volatile.Read(ref _version) &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(hit.Stamp, now) < CacheAge)
                {
                    DrawDevice(canvas, hit.Image, src);
                    return true;
                }
            }
            using var snap = surface.Snapshot(src);
            if (snap == null) return false;
            const int k = 8;
            int w = Math.Max(1, src.Width / k), h = Math.Max(1, src.Height / k);
            // промежуточные поверхности — там же, где кадр (на видеочипе): без копирования снимка в память процессора и обратно
            var gpu = surface.Context as GRRecordingContext;
            using var small = Make(gpu, w, h);
            if (small == null) return false;
            using (var p = new SKPaint())
                small.Canvas.DrawImage(snap, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
            using var smallImg = small.Snapshot();
            using var blur = SKImageFilter.CreateBlur(sigma / k, sigma / k, SKShaderTileMode.Clamp);
            using var cf = SKColorFilter.CreateColorMatrix(Saturate);
            using var paint = new SKPaint { ImageFilter = blur, ColorFilter = cf };
            using var blurred = Make(gpu, w, h);
            if (blurred == null) return false;
            blurred.Canvas.DrawImage(smallImg, 0, 0, paint);
            var outImg = blurred.Snapshot();
            DrawDevice(canvas, outImg, src);
            lock (_cache)
            {
                if (_cache.Remove(key, out var old)) Retire(old.Image);
                if (_cache.Count > 48) { foreach (var v in _cache.Values) Retire(v.Image); _cache.Clear(); }
                _cache[key] = (outImg, now, Volatile.Read(ref _version));
            }
            return true;
        }

        private static SKSurface? Make(GRRecordingContext? gpu, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
            return (gpu != null ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
        }

        /// <summary>Размытая копия — в координатах устройства, поверх текущей трансформации.</summary>
        private static void DrawDevice(SKCanvas canvas, SKImage img, SKRectI src)
        {
            int save = canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(img, new SKRect(src.Left, src.Top, src.Right, src.Bottom), new SKSamplingOptions(SKFilterMode.Linear), null);
            canvas.RestoreToCount(save);
        }

        private static void DrawLayerBackdrop(SKCanvas canvas, SKRect r, float sigma)
        {
            const float k = 4;
            using var down = SKImageFilter.CreateMatrix(SKMatrix.CreateScale(1 / k, 1 / k), new SKSamplingOptions(SKFilterMode.Linear));
            using var blur = SKImageFilter.CreateBlur(sigma / k, sigma / k, SKShaderTileMode.Clamp, down);
            using var cf = SKColorFilter.CreateColorMatrix(Saturate);
            using var sat = SKImageFilter.CreateColorFilter(cf, blur);
            using var backdrop = SKImageFilter.CreateMatrix(SKMatrix.CreateScale(k, k), new SKSamplingOptions(SKFilterMode.Linear), sat);
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = r, Backdrop = backdrop });
            canvas.Restore();
        }
    }
}
