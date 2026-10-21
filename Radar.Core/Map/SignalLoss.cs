using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Radar.Map;

/// <summary>
/// «Потеря сигнала» в духе старого аналогового монитора — поверх уже нарисованной карты:
/// выцветание (обесцвечивание и подъём чёрного), зерно, строки развёртки, медленная светлая полоса,
/// случайные срывы строк (горизонтальный сдвиг полосы кадра) и короткие провалы яркости.
/// Лёгкий фон помех есть всегда (стиль «экрана»); при обрыве связи (loss 0…1) помехи усиливаются
/// примерно вдесятеро, картинка становится чёрно-белой и почти гаснет. Время задаёт рисунок помех.
/// </summary>
internal static class SignalLoss
{
    /// <summary>Последний кадр помех рисовался на видеокарте — тогда эффект можно обновлять с частотой экрана.</summary>
    public static volatile bool GpuBacked;

    /// <param name="ambient">Сила фона помех в обычном режиме (0 — выключен в настройках, 1 — как задумано).</param>
    /// <param name="grainRate">Сколько раз в секунду меняется рисунок зерна.</param>
    public static void Draw(DrawingContext ctx, Rect area, double loss, double time, double ambient = 1, double grainRate = 12)
    {
        if (area.Width < 2 || area.Height < 2) return;
        if (loss < 0.001 && ambient < 0.001) return;
        ctx.Custom(new Op(area, Math.Clamp(loss, 0, 1), time, (float)Math.Clamp(ambient, 0, 1), grainRate));
    }

    // зерно: готовый тайл 160×160 (белый с альфой = яркости), рисуется копиями без шейдера — растеризатор
    // кладёт изображение блоками, а тайловый шейдер на весь экран считает каждый пиксель отдельно
    private const int GrainN = 160;
    private static SKImage? _grain;
    private static SKImage Grain()
    {
        if (_grain != null) return _grain;
        // N32 — родной порядок байт поверхности: при выводе нет перестановки каналов
        using var bmp = new SKBitmap(new SKImageInfo(GrainN, GrainN, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        var rnd = new Random(11);
        for (int y = 0; y < GrainN; y++)
            for (int x = 0; x < GrainN; x++)
            {
                byte v = (byte)rnd.Next(256);
                bool on = rnd.Next(256) >= 150;
                // белый с альфой = яркости (после умножения на альфу цвет = альфа): обычное наложение SrcOver даёт
                // ровно то же, что режим Screen (src + dst·(1 − src)), но идёт по быстрому пути растеризатора.
                // SetPixel принимает цвет без умножения на альфу — поэтому белый, а не (k, k, k)
                byte k = on ? v : (byte)0;
                bmp.SetPixel(x, y, new SKColor(255, 255, 255, k));
            }
        return _grain = SKImage.FromBitmap(bmp);
    }

    private static SKImage? _scan, _scanTile;

    /// <summary>Тайл строк развёртки 1×3: одна тёмная строка, две прозрачные.</summary>
    private static SKImage ScanTile()
    {
        if (_scanTile != null) return _scanTile;
        using var bmp = new SKBitmap(new SKImageInfo(1, 3, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        bmp.SetPixel(0, 0, SKColors.Black);
        bmp.SetPixel(0, 1, SKColors.Transparent);
        bmp.SetPixel(0, 2, SKColors.Transparent);
        return _scanTile = SKImage.FromBitmap(bmp);
    }

    private static SKImage Scanlines(int w, int h)
    {
        if (_scan is { } img && img.Width >= w && img.Height >= h) return img;
        _scan?.Dispose();
        // с запасом: при изменении размера окна растр не пересоздаётся на каждый пиксель
        w = Math.Max(w, 64) + 256; h = Math.Max(h, 64) + 256;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (int y = 0; y < h; y += 3) c.DrawRect(new SKRect(0, y, w, y + 1), p);
        return _scan = surface.Snapshot();
    }

    private static readonly object OverlayLock = new();
    private static SKImage? _overlay;
    private static (int W, int H, uint Frame, uint Dim, byte Grain, byte Scan) _overlayKey;

    /// <summary>Готовый слой «затемнение + зерно + строки развёртки» для спокойного режима; пересоздаётся только со сменой рисунка помех.</summary>
    private static SKImage Overlay(int w, int h, uint frame, SKColor? dim, byte grainA, byte scanA, int ox, int oy)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var key = (w, h, frame, dim is { } d ? (uint)d : 0u, grainA, scanA);
        lock (OverlayLock)
        {
            if (_overlay != null && _overlayKey == key) return _overlay;
            // предыдущий слой на экране уже заменён этим: вызовы идут из одного потока отрисовки, освобождаем сразу
            _overlay?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            var cv = surface.Canvas;
            cv.Clear(SKColors.Transparent);
            if (dim is { } dc) using (var p = new SKPaint { Color = dc, IsAntialias = false }) cv.DrawRect(new SKRect(0, 0, w, h), p);
            using (var p = new SKPaint { Color = new SKColor(255, 255, 255, grainA) })
            {
                var g = Grain();
                for (float y = -oy; y < h; y += GrainN)
                    for (float x = -ox; x < w; x += GrainN)
                        cv.DrawImage(g, x, y, p);
            }
            using (var p = new SKPaint { Color = new SKColor(0, 0, 0, scanA), IsAntialias = false })
                cv.DrawImage(Scanlines(w, h), 0, 0, p);
            _overlayKey = key;
            return _overlay = surface.Snapshot();
        }
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }
    private static double Rand(uint seed) => Hash(seed) / (double)uint.MaxValue;

    private sealed class Op(Rect area, double strength, double time, float amb, double grainRate) : ICustomDrawOperation
    {
        public Rect Bounds => area;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null) return;
            using var l = lease.Lease();
            var c = l.SkCanvas;
            GpuBacked = l.GrContext != null;
            float s = (float)strength; // 0 — обычный фон помех, 1 — полная потеря сигнала
            // значение «в покое» тянется к нейтральному с ослаблением фона: без фона в покое эффекта нет вовсе
            float MixN(float neutral, float calm, float lost, float l) { float c0 = neutral + (calm - neutral) * amb; return c0 + (lost - c0) * l; }
            float Mix(float calm, float lost, float l) => MixN(0, calm, lost, l);
            var r = new SKRect((float)area.X, (float)area.Y, (float)area.Right, (float)area.Bottom);
            int save = c.Save();
            c.ClipRect(r);

            // 1. выцветание: в обычном режиме цвета почти не трогаем, при обрыве — чёрно-белая и тёмная картинка
            float sat = MixN(1, 0.94f, 0, Math.Min(1, s / 0.8f)); // линейно к ч/б, без порога
            float lift = Mix(0.006f, 0.035f, s), dim = MixN(1, 0.98f, 0.2f, s); // сдвиг в матрице Skia — в долях 0…1
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float[] m =
            [
                (lr * (1 - sat) + sat) * dim, lg * (1 - sat) * dim, lb * (1 - sat) * dim, 0, lift,
                lr * (1 - sat) * dim, (lg * (1 - sat) + sat) * dim, lb * (1 - sat) * dim, 0, lift,
                lr * (1 - sat) * dim, lg * (1 - sat) * dim, (lb * (1 - sat) + sat) * dim, 0, lift,
                0, 0, 0, 1, 0,
            ];
            SKColor? calmDim = null;
            if (s < 0.01f)
            {
                // связь есть: обесцвечивание на несколько процентов незаметно, а слой-подложка (backdrop) — самая
                // дорогая операция кадра. Остаются затемнение и подъём чёрного: SrcOver серым с альфой (1 − dim)
                // даёт dst·dim + lift — то же, что матрица без насыщенности
                float a = 1 - dim;
                if (a > 0.001f)
                {
                    float g = Math.Clamp(lift / a, 0, 1);
                    calmDim = new SKColor((byte)(g * 255), (byte)(g * 255), (byte)(g * 255), (byte)Math.Round(a * 255));
                }
            }
            else
                using (var cf = SKColorFilter.CreateColorMatrix(m))
                using (var f = SKImageFilter.CreateColorFilter(cf))
                {
                    c.SaveLayer(new SKCanvasSaveLayerRec { Bounds = r, Backdrop = f });
                    c.Restore();
                }

            // 2. срывы строк: несколько горизонтальных полос кадра сдвигаются вбок на мгновение
            uint frame = (uint)(time * 12); // срывы строк — ~12 раз в секунду, независимо от частоты кадров
            uint gframe = (uint)(time * grainRate);
            int tears = Rand(frame * 7 + 1) < Mix(0.04f, 0.6f, s) ? 1 + (int)(Rand(frame * 7 + 2) * Mix(1.5f, 5, s)) : 0;
            for (int i = 0; i < tears; i++)
            {
                float y = r.Top + (float)(Rand(frame * 31 + (uint)i * 5 + 3) * r.Height);
                float h = 3 + (float)(Rand(frame * 31 + (uint)i * 5 + 4) * 26);
                float dx = (float)((Rand(frame * 31 + (uint)i * 5 + 5) - 0.5) * Mix(14, 140, s));
                var band = new SKRect(r.Left, y, r.Right, Math.Min(r.Bottom, y + h));
                using var shift = SKImageFilter.CreateOffset(dx, 0);
                int sv = c.Save();
                c.ClipRect(band);
                c.SaveLayer(new SKCanvasSaveLayerRec { Bounds = band, Backdrop = shift });
                c.Restore();
                // цветная окантовка срыва — как расхождение лучей
                // (при обрыве — бесцветная, картинка чёрно-белая)
                byte ea = (byte)Mix(26, 60, s);
                using (var p = new SKPaint { Color = s > 0.3f ? new SKColor(200, 200, 200, ea) : new SKColor(255, 80, 90, ea), BlendMode = SKBlendMode.Screen })
                    c.DrawRect(new SKRect(band.Left, band.Top, band.Right, band.Top + 1), p);
                using (var p = new SKPaint { Color = s > 0.3f ? new SKColor(160, 160, 160, ea) : new SKColor(90, 220, 255, ea), BlendMode = SKBlendMode.Screen })
                    c.DrawRect(new SKRect(band.Left, band.Bottom - 1, band.Right, band.Bottom), p);
                c.RestoreToCount(sv);
            }

            byte grainA = (byte)Mix(16, 130, s), scanA = (byte)Mix(34, 120, s);
            int gox = (int)(Rand(gframe * 3 + 9) * GrainN), goy = (int)(Rand(gframe * 3 + 10) * GrainN);
            if (GpuBacked)
            {
                // видеокарта: зерно и строки развёртки — по одному прямоугольнику с повторяющимся шейдером
                // (сотня отдельных копий тайла на телефоне съедала половину кадра)
                if (calmDim is { } cd0) using (var p = new SKPaint { Color = cd0, IsAntialias = false }) c.DrawRect(r, p);
                using (var sh = Grain().ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateTranslation(r.Left - gox, r.Top - goy)))
                using (var p = new SKPaint { Shader = sh, Color = new SKColor(255, 255, 255, grainA) })
                    c.DrawRect(r, p);
                using (var sh = ScanTile().ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateTranslation(0, r.Top)))
                using (var p = new SKPaint { Shader = sh, Color = new SKColor(0, 0, 0, scanA), IsAntialias = false })
                    c.DrawRect(r, p);
            }
            else if (s < 0.01f)
            {
                // связь есть: затемнение, зерно и строки развёртки — один готовый слой на кадр помех (рисунок меняется
                // ~5 раз в секунду, а кадров при движении карты десятки): вместо четырёх полноэкранных проходов — один
                c.DrawImage(Overlay((int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height), gframe, calmDim, grainA, scanA, gox, goy), r.Left, r.Top);
            }
            else
            {
                if (calmDim is { } cd) using (var p = new SKPaint { Color = cd, IsAntialias = false }) c.DrawRect(r, p);
                // 3. зерно: сдвигается каждый кадр помех
                using (var p = new SKPaint { Color = new SKColor(255, 255, 255, grainA) })
                {
                    var g = Grain();
                    // сдвиг рисунка каждый кадр помех — целыми пикселями (без субпиксельной выборки)
                    for (float y = r.Top - goy; y < r.Bottom; y += GrainN)
                        for (float x = r.Left - gox; x < r.Right; x += GrainN)
                            c.DrawImage(g, x, y, p);
                }

                // 4. строки развёртки: готовый растр на размер области (сотни отдельных полос на кадр — заметная доля времени),
                // сила — прозрачностью кисти
                using (var p = new SKPaint { Color = new SKColor(0, 0, 0, scanA), IsAntialias = false })
                    c.DrawImage(Scanlines((int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)), r.Left, r.Top, p);
            }

            // 5. медленная светлая полоса, ползущая вниз
            float barH = Math.Max(60, r.Height * 0.12f);
            float barY = r.Top + (float)((time * 0.09) % 1.0) * (r.Height + barH) - barH;
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, barY), new SKPoint(0, barY + barH),
                       [SKColors.Transparent, new SKColor(215, 220, 220, (byte)Mix(10, 60, s)), SKColors.Transparent], SKShaderTileMode.Clamp))
            using (var p = new SKPaint { Shader = sh, BlendMode = SKBlendMode.Screen })
                c.DrawRect(new SKRect(r.Left, barY, r.Right, barY + barH), p);

            // 6. короткие провалы и вспышки яркости всего кадра
            // плавное «дыхание» яркости (сглаженный шум ~1.5 Гц) вместо резких вспышек
            double ft = time * 1.5; uint fi = (uint)ft; double fu = ft - fi; fu = fu * fu * (3 - 2 * fu);
            double wob = Rand(fi * 13 + 77) * (1 - fu) + Rand((fi + 1) * 13 + 77) * fu; // 0…1
            byte fa = (byte)(Mix(6, 46, s) * wob * wob);
            if (fa > 0)
                using (var p = new SKPaint { Color = new SKColor(0, 0, 0, fa) }) c.DrawRect(r, p);

            c.RestoreToCount(save);
        }
    }

}
