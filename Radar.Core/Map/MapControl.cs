using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Radar.Models;
using Radar.Services;

namespace Radar.Map;

/// <summary>
/// Векторная карта (Web Mercator) на встроенных данных Natural Earth.
/// Подложка рендерится в кэш-битмап с запасом по краям: при перетаскивании кадр просто сдвигается,
/// полная перерисовка — только при изменении масштаба или выходе за запас. Тревоги рисуются поверх каждый кадр.
/// </summary>
public sealed class MapControl : Control
{
    private const double MinZoom = 3.0, MaxZoom = 12;
    // запас кэша относительно размера окна (каждая сторона). Меньший запас (0,2) замерен: пикселей в перерисовке меньше,
    // но при сдвиге она нужна чаще — нагрузка не снижается
    private const double CacheMargin = 0.35;
    private static readonly GeoPoint HomeCenter = new(49.6, 38.4);
    private const double HomeZoom = 6.1;

    private double _zoom;
    private Point _center; // мировые координаты (0..256)
    private Point? _dragStart;
    private Point _dragLast;
    private bool _dragged;
    // общая фаза анимаций (1 = 4 с) — растёт непрерывно и не обнуляется: при обнулении все волны с нецелым
    // множителем фазы обрывались бы посередине
    private double _phase;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDashStyle> DashCache = new();

    /// <summary>Неизменяемый штрих без смещения: один экземпляр на рисунок (слой зон рисуется и в фоновом потоке).</summary>
    private static IDashStyle Dash(params double[] d) =>
        DashCache.GetOrAdd(string.Join(',', d.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            _ => new Avalonia.Media.Immutable.ImmutableDashStyle(d, 0));

    private static readonly IDashStyle[] LaunchDashes = [.. Enumerable.Range(0, 7).Select(o => (IDashStyle)new Avalonia.Media.Immutable.ImmutableDashStyle([7, 3, 2, 4], o))];

    private static IDashStyle LaunchDash(int offset) => LaunchDashes[Math.Clamp(offset, 0, 6)];

    private long _lastFxTs;
    private static readonly long PhaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
    // время предметной области (возраст тревог, прогноз, ETA); анимации — по реальным часам
    private DateTimeOffset _dnow = DateTimeOffset.UtcNow;
    private Point? _hover;
    /// <summary>Подсказка, закреплённая касанием (у пальца нет наведения): держится до следующего касания или жеста.</summary>
    private bool _touchTip;
    private AlertEvent? _hoverEvent;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settle;
    private static readonly Typeface Regular = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface Medium = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    /// <summary>Заголовки на карте (подсказки тревог): тот же шрифт, что в заголовках интерфейса.</summary>
    private static readonly Typeface Head = new(new FontFamily("avares://Radar.Core/Assets/Fonts#Exo 2"), FontStyle.Normal, FontWeight.SemiBold);
    private readonly Dictionary<(string, double, uint, double, int, bool), FormattedText> _textCache = [];
    private readonly Dictionary<string, Paint> _paints = [];
    private readonly MapAnimator _anim = new();
    /// <summary>Пункты, которые подписывает слой треков — подложка их не дублирует.</summary>
    private HashSet<string> _trackLabels = new(StringComparer.Ordinal);
    private List<Point> _keepOut = [];

    private Bitmap? _base;
    private SkiaSharp.SKImage? _baseImage;
    private int _baseFrontVersion = -1;
    // фоновая перерисовка подложки: на экране тем временем растягивается прежний кадр
    private Task? _baseJob;
    private CancellationTokenSource? _baseJobCts;
    private BaseFrame? _baseJobFrame;
    private bool _wantStrict;
    private Point _baseCenter;
    private double _baseZoom = double.NaN;
    private Size _baseSize;
    private double _baseScaling, _baseRes = 1;
    private bool _baseDirty = true;

    // Кэш статичного слоя зон (заливки, штриховка, контуры, подписи районов, круги пунктов) — в тех же
    // координатах, что и подложка. Каждый кадр поверх рисуются только анимируемые части.
    private enum Pass { All, Static, Dynamic }
    private Pass _pass = Pass.All;
    private Bitmap? _areas;
    private bool _areasDirty = true;
    private long _areasStamp;
    private int _baseVersion, _areasBaseVersion = -1;
    private bool _areasShown;
    private double _cullMargin;
    private bool DrawStatic => _pass != Pass.Dynamic;
    private bool DrawDynamic => _pass != Pass.Static;

    private sealed record Paint(Color Color, IBrush Solid, IBrush Line, IBrush Zone, IBrush ZoneStrong)
    {
        public Color HatchColor => Color.FromArgb(55, Color.R, Color.G, Color.B);
    }

    public IReadOnlyList<AlertEvent> Events { get; private set; } = [];
    public IReadOnlyList<Track> Tracks { get; private set; } = [];
    public AlertEvent? Selected { get; private set; }

    public event Action<AlertEvent?>? EventClicked;

    // ---- режим выбора регионов для уведомлений: клик по региону добавляет/убирает его ----
    private bool _pickRegions;
    private IReadOnlySet<string> _pickedRegions = new HashSet<string>();
    private string? _pickHover;
    /// <summary>Клик по региону в режиме выбора (идентификатор региона).</summary>
    public event Action<string>? RegionPicked;
    /// <summary>Клик по подписи населённого пункта в режиме выбора: название и координаты.</summary>
    public event Action<string, GeoPoint>? PlacePicked;
    /// <summary>Клик в произвольном месте карты в режиме «ТОЧКА» (своя точка: дача, участок).</summary>
    public event Action<GeoPoint>? PointPicked;
    /// <summary>Режим «ТОЧКА»: клик ставит свою точку в любом месте (регионы и подписи не выбираются).</summary>
    public bool PickPoint { get; set { field = value; _pickHover = null; _pickPlace = null; InvalidateVisual(); } }

    /// <summary>Радиус пункта изменён перетаскиванием края круга (зона, новый радиус в км) — по отпусканию.</summary>
    public event Action<WatchArea, int>? PlaceRadiusChanged;

    // ---- радиус пункта: перетаскивание края круга в режиме выбора ----
    private WatchArea? _radiusArea, _radiusHover;
    private double _radiusDragKm;
    private const double RadiusGrabPx = 7;
    private StandardCursorType _pickCursor = StandardCursorType.Arrow;
    private const int RadiusMinKm = 3, RadiusMaxKm = 100;

    private double PlaceKm(WatchArea a) => _radiusArea == a ? _radiusDragKm : a.RadiusKm > 0 ? a.RadiusKm : WatchRadiusKm;

    /// <summary>Пункт, у края круга которого находится курсор (в пределах нескольких пикселей).</summary>
    private WatchArea? RadiusEdgeAt(Point p)
    {
        WatchArea? best = null;
        double bestD = RadiusGrabPx;
        foreach (var a in _watch)
        {
            if (a.Kind != WatchArea.PlaceKind) continue;
            var g = new GeoPoint(a.Lat, a.Lon);
            double r = PlaceKm(a) * PxPerKm(g);
            if (r < 12) continue; // слишком маленький круг — тянуть неудобно, его край совпадает с точкой
            double d = Math.Abs(Distance(ToScreen(g), p) - r);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    /// <summary>Радиус под курсором при перетаскивании: шаг 1 км до 20 км, дальше — 5 км.</summary>
    private double RadiusFromPointer(WatchArea a, Point p)
    {
        var g = new GeoPoint(a.Lat, a.Lon);
        double km = Distance(ToScreen(g), p) / PxPerKm(g);
        km = km < 20 ? Math.Round(km) : Math.Round(km / 5) * 5;
        return Math.Clamp(km, RadiusMinKm, RadiusMaxKm);
    }

    private IReadOnlyList<BasemapRenderer.LabelHit> _baseLabels = [];
    private BasemapRenderer.LabelHit? _pickPlace;

    /// <summary>Подпись пункта под точкой экрана (на текущей подложке).</summary>
    private BasemapRenderer.LabelHit? PlaceLabelAt(Point p)
    {
        var w = ScreenToWorld(p);
        foreach (var l in _baseLabels)
            if (l.World.Contains(w)) return l;
        return null;
    }

    private WatchArea? PickedPlace(BasemapRenderer.LabelHit l) =>
        _watch.FirstOrDefault(a => a.Kind == WatchArea.PlaceKind && Geo.DistanceKm(new GeoPoint(a.Lat, a.Lon), Basemap.Unproject(l.Pos)) < 0.5);

    /// <summary>Режим выбора регионов: карта подсвечивает выбранные и наведённый регион, клик переключает выбор.</summary>
    public bool PickRegions
    {
        get => _pickRegions;
        set { if (_pickRegions == value) return; _pickRegions = value; if (value) CloseSpider(); _pickHover = null; _pickPlace = null; _radiusHover = null; _radiusArea = null; _pickCursor = StandardCursorType.Arrow; Cursor = CursorOf(StandardCursorType.Arrow); InvalidateVisual(); }
    }

    /// <summary>Выбранные регионы (подсвечиваются в режиме выбора).</summary>
    public IReadOnlySet<string> PickedRegions { get => _pickedRegions; set { _pickedRegions = value; InvalidateVisual(); } }

    private IReadOnlyList<WatchArea> _watch = [];
    private HashSet<string> _watchDistricts = new(StringComparer.Ordinal);
    /// <summary>Все зоны наблюдения: регионы, районы, населённые пункты.</summary>
    public IReadOnlyList<WatchArea> WatchAreas
    {
        get => _watch;
        set
        {
            _watch = [.. value];
            _watchDistricts = _watch.Where(a => a.Kind == WatchArea.DistrictKind).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            InvalidateVisual();
        }
    }
    /// <summary>Радиус окрестности выбранного населённого пункта, км.</summary>
    public double WatchRadiusKm { get; set { field = value; InvalidateVisual(); } } = 15;

    /// <summary>Выбранные регионы показываются постоянно (тонкий контур, ореол у их тревог).</summary>
    public bool HighlightPicked { get; set { field = value; InvalidateVisual(); } } = true;

    /// <summary>Событие касается выбранных регионов (сам регион, пункт в нём, регион, куда идёт угроза).</summary>
    private bool IsMine(AlertEvent e)
    {
        if (_watch.Count == 0 || !HighlightPicked || e.Status == AlertStatus.AllClear) return false;
        return WatchMatcher.Matches(e, _watch, WatchRadiusKm, Basemap.DistrictsOf, Basemap.DistrictAt);
    }

    private static readonly IBrush MineLine = new SolidColorBrush(Color.Parse("#8C7CD4FF")).ToImmutable();

    /// <summary>Тонкий постоянный контур выбранных регионов (вне режима выбора).</summary>
    private void DrawPickedOutlines(DrawingContext ctx)
    {
        if (_pickRegions || !HighlightPicked || _watch.Count == 0) return;
        int lod = CurrentLod;
        double s = Scale;
        var view = CullWorld();
        using (ctx.PushTransform(WorldTransform()))
            foreach (var id in _pickedRegions.Concat(_watchDistricts))
                if (AreaFeature(id, lod) is { } f && f.Bounds.Intersects(view))
                    ctx.DrawGeometry(null, new Pen(MineLine, 1.1 / s, Dash(6, 3)) { LineJoin = PenLineJoin.Round }, f.GeometryAt(s));
        DrawWatchPlaces(ctx, strong: false);
    }

    // ---- статистика: тепловая раскраска регионов ----

    private IReadOnlyDictionary<string, double>? _heat;
    private string? _heatSelected;
    /// <summary>Интенсивность тревог по регионам (0…1) — раскраска карты в режиме статистики; null — выключено.</summary>
    public IReadOnlyDictionary<string, double>? Heat { get => _heat; set { _heat = value; InvalidateVisual(); } } // рисуется вживую (DrawHeat), подложка не меняется
    /// <summary>Выбранный в статистике регион — выделяется контуром.</summary>
    public string? HeatSelected { get => _heatSelected; set { _heatSelected = value; InvalidateVisual(); } }
    /// <summary>Клик по региону в режиме статистики.</summary>
    public event Action<string?>? HeatRegionClicked;

    private static Color HeatColor(double v)
    {
        // холодный → тёплый: сине-серый, янтарный, красный
        v = Math.Clamp(v, 0, 1);
        (byte r, byte g, byte b) lo = (0x3A, 0x5A, 0x7A), mid = (0xD9, 0xA4, 0x41), hi = (0xE5, 0x48, 0x4D);
        var (a, z, t) = v < 0.5 ? (lo, mid, v / 0.5) : (mid, hi, (v - 0.5) / 0.5);
        return Color.FromArgb((byte)(40 + 130 * v), (byte)(a.r + (z.r - a.r) * t), (byte)(a.g + (z.g - a.g) * t), (byte)(a.b + (z.b - a.b) * t));
    }

    private void DrawHeat(DrawingContext ctx)
    {
        if (_heat is not { Count: > 0 } heat) return;
        int lod = CurrentLod;
        double s = Scale;
        var view = CullWorld();
        using (ctx.PushTransform(WorldTransform()))
        {
            foreach (var (id, v) in heat)
                if (Basemap.RegionById(id, lod) is { } f && f.Bounds.Intersects(view))
                    ctx.DrawGeometry(new ImmutableSolidColorBrush(HeatColor(v)), null, f.GeometryAt(s));
            if (_heatSelected != null && Basemap.RegionById(_heatSelected, lod) is { } sf)
                ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Color.FromRgb(0x7C, 0xD4, 0xFF)), 2 / s) { LineJoin = PenLineJoin.Round }, sf.GeometryAt(s));
        }
    }

    private bool IsPickedArea(string id) => id.Contains('|') ? _watchDistricts.Contains(id) : _pickedRegions.Contains(id);

    /// <summary>Выбранные населённые пункты: окружность зоны наблюдения (радиус в км) и точка с подписью.</summary>
    private void DrawWatchPlaces(DrawingContext ctx, bool strong)
    {
        foreach (var a in _watch)
        {
            if (a.Kind != WatchArea.PlaceKind) continue;
            var g = new GeoPoint(a.Lat, a.Lon);
            var c = ToScreen(g);
            double km = PlaceKm(a);
            double r = km * PxPerKm(g);
            if (c.X < -r - 40 || c.Y < -r - 40 || c.X > Bounds.Width + r + 40 || c.Y > Bounds.Height + r + 40) continue;
            bool active = strong && (_radiusArea == a || _radiusHover == a);
            if (r > 6)
            {
                if (strong) ctx.DrawEllipse(PickHoverFill, null, c, r, r);
                ctx.DrawEllipse(null, active ? new Pen(PickLine, 2.2) : new Pen(strong ? PickLine : MineLine, strong ? 1.4 : 1.1, Dash(5, 3)), c, r, r);
            }
            if (active)
            {
                // ручка на краю — в сторону курсора; рядом — значение радиуса
                var dir = _hover is { } hp2 && Distance(hp2, c) > 1 ? (hp2 - c) / Distance(hp2, c) : new Vector(1, 0);
                var knob = c + dir * r;
                ctx.DrawLine(new Pen(MineLine, 1, Dash(2, 3)), c, knob);
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(PickLine, 1.6), knob, 5, 5);
                var kt = Text($"{km:0} км", 11, MapTheme.CityText, Medium);
                var kr = new Rect(knob.X + (dir.X >= 0 ? 10 : -10 - kt.Width - 12), knob.Y - kt.Height / 2 - 3, kt.Width + 12, kt.Height + 6);
                ctx.DrawRectangle(MapTheme.PanelBg, new Pen(PickLine, 1), kr);
                ctx.DrawText(kt, new Point(kr.X + 6, kr.Y + 3));
            }
            ctx.DrawEllipse(PickLine, new Pen(MapTheme.PanelBg, 1.2), c, 3.2, 3.2);
            if (strong || _zoom >= 6)
            {
                var t = Text(strong ? $"{a.Name} · {km:0} км" : a.Name, 10.5, PickLine, Medium);
                DrawHaloText(ctx, t, new Point(c.X - t.Width / 2, c.Y + 6));
            }
        }
    }

    private static readonly IBrush ApproachLine = new SolidColorBrush(Color.Parse("#E5484D")).ToImmutable();

    /// <summary>
    /// Связь «мой пункт — угроза, идущая к нему»: тонкая пунктирная линия от пункта (своей точки) к расчётному положению
    /// угрозы, бегущая к пункту, и плашка с расстоянием и временем посередине. Только для угроз, чей курс проходит через
    /// радиус пункта, не дальше 150 км; у каждого пункта — до трёх ближайших.
    /// </summary>
    private void DrawApproachLinks(DrawingContext ctx, DateTimeOffset now)
    {
        if (!HighlightPicked || _pickRegions || !_watch.Any(a => a.Kind == WatchArea.PlaceKind)) return;
        var links = new List<(WatchArea Place, TrackVisual V, Proximity.Report R, double Alpha)>();
        foreach (var v in _anim.Tracks)
        {
            var t = v.Track;
            if (t.Cleared || !t.IsMoving || t.StateAt(_dnow) != TrackState.Active || _plan.Hidden.Contains(v)) continue;
            double alpha = TrackAlpha(v, now);
            if (alpha < 0.05) continue;
            var e = t.Events.OrderByDescending(x => x.Time).FirstOrDefault();
            if (e == null) continue;
            foreach (var a in _watch)
            {
                if (a.Kind != WatchArea.PlaceKind) continue;
                if (Proximity.Compute(e, t, [a], WatchRadiusKm, _dnow) is { Motion: Proximity.Motion.Approaching } r)
                    links.Add((a, v, r, alpha));
            }
        }
        if (links.Count == 0) return;
        double dash = (_phase * 14) % 9;
        foreach (var g in links.GroupBy(l => l.Place))
            foreach (var (place, v, r, alpha) in g.OrderBy(l => l.R.DistanceKm).Take(3))
            {
                var from = ToScreen(new GeoPoint(place.Lat, place.Lon));
                var to = ToScreen(v.Head);
                double len = Distance(from, to);
                if (len < 24) continue;
                var dir = (to - from) / len;
                // у пункта — от точки, у угрозы — не заходя под значок
                var a0 = from + dir * 5;
                var a1 = to - dir * 13;
                using (ctx.PushOpacity(alpha))
                {
                    ctx.DrawLine(new Pen(Argb(70, 0, 0, 0), 3.2), a0, a1);
                    // штрихи бегут от угрозы к пункту — «идёт к вам»
                    ctx.DrawLine(new Pen(ApproachLine, 1.3, new DashStyle([5, 4], dash)) { LineCap = PenLineCap.Round }, a0, a1);
                    ctx.DrawEllipse(null, new Pen(ApproachLine, 1.2), from, 5.5, 5.5);
                    if (len < 70) continue;
                    var label = $"{Math.Round(r.DistanceKm):0} км" + (r.Eta is { } eta ? $" · ~{Math.Max(1, Math.Round(eta.TotalMinutes)):0} мин" : "");
                    var kt = Text(label, 10.5, MapTheme.CityText, Medium);
                    var mid = from + (to - from) * 0.45;
                    var kr = new Rect(mid.X - kt.Width / 2 - 6, mid.Y - kt.Height / 2 - 2, kt.Width + 12, kt.Height + 4);
                    ctx.DrawRectangle(MapTheme.PanelBg, new Pen(ApproachLine, 1), kr);
                    ctx.DrawText(kt, new Point(kr.X + 6, kr.Y + 2));
                }
            }
    }

    /// <summary>Мягкий ореол акцентного цвета под значком тревоги «моего» региона, медленно дышит.</summary>
    private void DrawMineHalo(DrawingContext ctx, Point c, double r)
    {
        double ph = 0.5 + 0.5 * Math.Sin(_phase * Math.PI * 2 * 0.6);
        var g = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(70 + 40 * ph), 0x7C, 0xD4, 0xFF), 0.35),
                new GradientStop(Color.FromArgb(0, 0x7C, 0xD4, 0xFF), 1),
            },
        };
        double rr = r * (1.9 + 0.15 * ph);
        ctx.DrawEllipse(g, null, c, rr, rr);
        ctx.DrawEllipse(null, new Pen(Argb((byte)(120 + 60 * ph), 0x7C, 0xD4, 0xFF), 1.1), c, r + 3.5, r + 3.5);
    }

    /// <summary>Масштаб, начиная с которого клик в режиме выбора берёт район, а не регион целиком.</summary>
    private const double PickDistrictZoom = 6.6;

    /// <summary>Регион (на обзоре) или район (при приближении) под точкой экрана.</summary>
    private string? RegionAtScreen(Point p, bool districts = true)
    {
        var w = ScreenToWorld(p);
        var region = Basemap.Features.FirstOrDefault(f => f.Kind == FeatureKind.Region && f.Lod == CurrentLod && !Gazetteer.IsForeign(f.Id) &&
                                                          f.Bounds.Contains(w) && f.Geometry.FillContains(w))?.Id;
        if (region == null || !districts || _zoom < PickDistrictZoom) return region;
        return Basemap.DistrictAt(Basemap.Unproject(w)) is { } d && d.StartsWith(region + "|", StringComparison.Ordinal) ? d : region;
    }

    private static BasemapFeature? AreaFeature(string id, int lod) =>
        id.Contains('|') ? Basemap.DistrictById(id, lod) : Basemap.RegionById(id, lod);

    private static readonly IBrush PickFill = new SolidColorBrush(Color.Parse("#2E7CD4FF")).ToImmutable();
    private static readonly IBrush PickHoverFill = new SolidColorBrush(Color.Parse("#1A7CD4FF")).ToImmutable();
    private static readonly IBrush PickLine = new SolidColorBrush(Color.Parse("#7CD4FF")).ToImmutable();

    /// <summary>Подсветка выбранных регионов и региона под курсором; название наведённого — рядом с курсором.</summary>
    private void DrawRegionPicking(DrawingContext ctx, Size size)
    {
        if (!_pickRegions) return;
        int lod = CurrentLod;
        double s = Scale;
        var view = CullWorld();
        using (ctx.PushTransform(WorldTransform()))
        {
            foreach (var id in _pickedRegions.Concat(_watchDistricts))
                if (AreaFeature(id, lod) is { } f && f.Bounds.Intersects(view))
                    ctx.DrawGeometry(PickFill, new Pen(PickLine, 1.6 / s) { LineJoin = PenLineJoin.Round }, f.GeometryAt(s));
            if (_pickHover is { } hov && AreaFeature(hov, lod) is { } hf)
                ctx.DrawGeometry(IsPickedArea(hov) ? null : PickHoverFill, new Pen(PickLine, 1.2 / s, Dash(4, 3)) { LineJoin = PenLineJoin.Round }, hf.GeometryAt(s));
        }
        DrawWatchPlaces(ctx, strong: true);
        if (_radiusHover != null || _radiusArea != null) return;
        // подпись пункта под курсором — рамка вокруг подписи и плашка
        if (_pickPlace is { } pl && _hover is { } php)
        {
            var tl = WorldToScreen(pl.World.TopLeft);
            var br = WorldToScreen(pl.World.BottomRight);
            var picked = PickedPlace(pl);
            bool pon = picked != null;
            double km = picked is { RadiusKm: > 0 } pa ? pa.RadiusKm : WatchRadiusKm;
            ctx.DrawRectangle(PickHoverFill, new Pen(PickLine, 1), new Rect(tl, br).Inflate(1));
            DrawPickTip(ctx, size, php, pl.Name, (pon ? "нажмите, чтобы убрать" : "нажмите, чтобы добавить") + $" пункт · {km:0} км вокруг", pon);
            return;
        }
        if (PickPoint && _hover is { } pp)
        {
            // прицел и подсказка: точка ставится там, где курсор; клик по своей точке — убрать
            var mine = _watch.FirstOrDefault(a => a.IsPoint && Distance(ToScreen(new GeoPoint(a.Lat, a.Lon)), pp) < 10);
            var g = Basemap.Unproject(ScreenToWorld(pp));
            double rk = WatchRadiusKm * PxPerKm(g);
            if (mine == null && rk > 6) ctx.DrawEllipse(null, new Pen(PickLine, 1, Dash(3, 4)), pp, rk, rk);
            DrawPickTip(ctx, size, pp, mine?.Name ?? "Своя точка",
                mine != null ? "нажмите, чтобы убрать точку" : $"нажмите, чтобы поставить · {WatchRadiusKm:0} км вокруг", mine != null);
            return;
        }
        // название региона под курсором — плашкой рядом с курсором
        if (_pickHover is not { } hid || _hover is not { } hp) return;
        var name = hid.Contains('|') ? Basemap.DistrictById(hid, lod)?.Name ?? hid : Gazetteer.Get(hid)?.Name ?? Basemap.RegionById(hid, lod)?.Name ?? hid;
        bool on = IsPickedArea(hid);
        DrawPickTip(ctx, size, hp, name, (on ? "нажмите, чтобы убрать" : "нажмите, чтобы добавить") + (hid.Contains('|') ? " район" : _zoom < PickDistrictZoom ? " · приблизьте для районов" : " регион"), on);
    }

    private void DrawPickTip(DrawingContext ctx, Size size, Point hp, string name, string hint, bool on)
    {
        var t = Text(name, 11.5, MapTheme.CityText, Medium);
        var sub = Text(hint, 10, PickLine, Regular);
        double w = Math.Max(t.Width, sub.Width) + 20, h = t.Height + sub.Height + 10;
        double x = hp.X + 16, y = hp.Y + 18;
        if (x + w > size.Width - 8) x = hp.X - 16 - w;
        if (y + h > size.Height - 8) y = hp.Y - 12 - h;
        x = Math.Max(8, x);
        y = Math.Max(8, y);
        var r = new Rect(x, y, w, h);
        ctx.DrawRectangle(MapTheme.PanelBg, new Pen(on ? PickLine : MapTheme.PanelBorder, 1), r);
        ctx.DrawRectangle(PickLine, null, new Rect(r.X, r.Y + 3, 2, r.Height - 6));
        ctx.DrawText(t, new Point(r.X + 10, r.Y + 4));
        ctx.DrawText(sub, new Point(r.X + 10, r.Y + 5 + t.Height));
    }

    private double _lastAmbient = double.MinValue;
    private long _glassAreasStamp;
    private int _glassBaseVersion = -1;

    // частоты фоновой анимации по настройке «экономно / обычно / плавно»
    private static readonly double[] ActiveFps = [8, 15, 30], InactiveFps = [4, 8, 15], NoiseFps = [3, 5, 10];
    // на видеокарте кадр помех почти бесплатен: зерно и полоса развёртки идут с частотой экрана, а не рывками
    private static readonly double[] GpuNoiseFps = [30, 60, 60];
    private double NoiseRate => (SignalLoss.GpuBacked ? GpuNoiseFps : NoiseFps)[EffectiveQuality];
    /// <summary>Качество анимации с учётом слабого видеочипа: не выше «экономно».</summary>
    private int EffectiveQuality => Glass.LowPower ? 0 : AnimationQuality;
    private int _animQuality = 1;
    /// <summary>0 — экономно, 1 — обычно, 2 — плавно.</summary>
    public int AnimationQuality { get => _animQuality; set => _animQuality = Math.Clamp(value, 0, 2); }

    /// <summary>Период фоновой анимации в секундах; null — окно свёрнуто или скрыто.</summary>
    private double? AmbientInterval()
    {
        if (!IsEffectivelyVisible) return null;
        if (TopLevel.GetTopLevel(this) is Window w)
        {
            if (w.WindowState == WindowState.Minimized) return null;
            if (!w.IsActive) return 1 / InactiveFps[AnimationQuality];
        }
        // каждый кадр карты на процессоре — ~3 % ядра: «обычно» (15 кадров/с) хватает пульсации и бегущим пунктирам
        return 1 / ActiveFps[EffectiveQuality];
    }

    private bool _noiseFrameRequested;

    private bool WantsVsyncNoise() => (_retro > 0 || _signalLoss > 0) && SignalLoss.GpuBacked && AmbientInterval() is { } iv
        && NoiseRate * (iv > 1 / ActiveFps[AnimationQuality] + 1e-9 ? 0.6 : 1) >= 30;

    private void RequestNoiseFrame()
    {
        if (_noiseFrameRequested || TopLevel.GetTopLevel(this) is not { } top) return;
        _noiseFrameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _noiseFrameRequested = false;
            if (!WantsVsyncNoise()) return;
            _phase = System.Diagnostics.Stopwatch.GetElapsedTime(PhaseStart).TotalSeconds * 0.25;
            _noiseLayer.InvalidateVisual();
            // следующий кадр — сразу по сигналу экрана: таймер диспетчера на телефоне тикает реже 60 раз в секунду
            RequestNoiseFrame();
        });
    }

    private DispatcherTimer? _wakeTimer;

    /// <summary>Таймер кадров встаёт до ближайшего кадра фона (пульсация, смена рисунка помех) или до явного пробуждения.</summary>
    private void Sleep(double? ambient, double noiseFps)
    {
        double wait = double.PositiveInfinity;
        if ((Events.Count > 0 || Tracks.Count > 0) && ambient is { } iv) wait = Math.Max(0, iv - (_phase - _lastAmbient) * 4);
        if (noiseFps > 0 && (_retro > 0 || _signalLoss > 0)) wait = Math.Min(wait, 1 / noiseFps);
        if (wait < 0.03) return;
        _timer.Stop();
        if (double.IsPositiveInfinity(wait)) return;
        _wakeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(wait), DispatcherPriority.Render, (_, _) => { _wakeTimer!.Stop(); Wake(); });
        _wakeTimer.Interval = TimeSpan.FromSeconds(wait);
        _wakeTimer.Start();
    }

    /// <summary>Будит таймер кадров: ввод, новые данные, перерисовка, смена настроек.</summary>
    private void Wake()
    {
        if (_timer is null || _timer.IsEnabled || !IsAttached) return;
        _wakeTimer?.Stop();
        _lastFxTs = 0;
        _timer.Start();
    }

    private bool IsAttached => TopLevel.GetTopLevel(this) != null;

    /// <summary>Любая просьба перерисовать карту будит таймер: анимации переходов, камеры и угасания идут от него.</summary>
    public new void InvalidateVisual()
    {
        base.InvalidateVisual();
        Wake();
    }

    public MapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = CursorOf(StandardCursorType.Arrow);
        BasemapRenderer.Prepare();
        BasemapRenderer.PreparePalette();
        CenterOn(HomeCenter, HomeZoom);
        _noiseLayer = new NoiseLayer(this);
        VisualChildren.Add(_noiseLayer);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _phase = System.Diagnostics.Stopwatch.GetElapsedTime(PhaseStart).TotalSeconds * 0.25;
            _anim.Tick(DateTimeOffset.UtcNow);
            bool ui = StepTransitions();
            StepCamera();
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            double dtTick = _lastFxTs == 0 ? 0.016 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastFxTs, nowTs).TotalSeconds, 0.001, 0.1);
            _lastFxTs = nowTs;
            _signalLoss = Math.Clamp(_signalLoss + (_signalLost && SignalLossEffect ? dtTick / 2.5 : -dtTick / 2.0), 0, 1);
            bool retroMoving = Math.Abs(_retro - _retroTarget) > 1e-3;
            _retro = retroMoving ? _retro + Math.Clamp(_retroTarget - _retro, -dtTick / 0.6, dtTick / 0.6) : _retroTarget;
            if (_signalLost) _heldSeverity = CurrentSeverity; // на спаде держим силу, с которой обрыв закончился
            // фон помех живёт всегда, но каждая смена рисунка — перерисовка всей карты: в активном окне 5 раз в секунду,
            // в неактивном — 3, в свёрнутом не меняется
            var ambient = AmbientInterval();
            double noiseFps = ambient is { } a0 ? NoiseRate * (a0 > 1 / ActiveFps[AnimationQuality] + 1e-9 ? 0.6 : 1) : 0;
            // частые кадры помех — по сигналу кадра экрана (без дрожания таймера относительно vsync)
            if (noiseFps >= 30 && WantsVsyncNoise()) { RequestNoiseFrame(); noiseFps = 0; }
            long noiseFrame = noiseFps > 0 ? (long)(_phase * 4 * noiseFps) : _noiseFrame;
            bool noise = noiseFrame != _noiseFrame; _noiseFrame = noiseFrame;
            // нарастание и спад обрыва — плавно, с полной частотой; установившийся обрыв меняется только с рисунком помех
            bool lossMoving = _signalLoss > 0 && _signalLoss < 1;
            bool lossNoise = _signalLoss > 0 && noise;
            if (_dragPending && System.Diagnostics.Stopwatch.GetElapsedTime(_dragFrameAt).TotalSeconds >= 1 / DragFps[AnimationQuality] - 0.004)
            {
                _dragPending = false;
                _dragFrameAt = System.Diagnostics.Stopwatch.GetTimestamp();
                Glass.InvalidateBackdrop();
                InvalidateVisual();
                _lastAmbient = _phase;
            }
            else if (_anim.IsAnimating || ui || SpiderAnimating)
            {
                // заметное изменение под панелями — стекло пересчитывается сразу
                Glass.InvalidateBackdrop();
                InvalidateVisual();
                _lastAmbient = _phase;
            }
            else if ((Events.Count > 0 || Tracks.Count > 0) && ambient is { } iv && (_phase - _lastAmbient) * 4 >= iv)
            {
                // пульсация и бегущие пунктиры: 15 к/с, в неактивном окне — 8, в свёрнутом не рисуются
                InvalidateVisual();
                _lastAmbient = _phase;
            }
            // помехи — свой слой поверх карты: смена их рисунка не перезаписывает саму карту
            if (lossMoving || lossNoise || (noise && _retro > 0) || retroMoving)
            {
                if (lossMoving || retroMoving) Glass.InvalidateBackdrop();
                _noiseLayer.InvalidateVisual();
            }
            // ничего не движется: таймер (60 тиков в секунду) засыпает до следующего кадра фона — на слабых телефонах
            // пустые тики в покое съедали основную долю потока интерфейса
            bool busy = _dragPending || _anim.IsAnimating || ui || SpiderAnimating || CameraAnimating || lossMoving || retroMoving
                || _tipFade is > 0.001 and < 0.999 || AreasFading || noiseFps >= 30 && (_retro > 0 || _signalLoss > 0) && !WantsVsyncNoise();
            if (!busy) Sleep(ambient, noiseFps);
        });
        // после окончания жеста перерисовываем подложку в полном качестве
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            if (_dragStart == null && !CameraAnimating && !IsBaseValid(strict: true)) { _wantStrict = true; InvalidateVisual(); }
        });
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // первый раз с настоящим размером: стартовый вид — в центре видимой (не перекрытой панелями) части
        if (!_homeSized && e.NewSize.Width > 1)
        {
            _homeSized = true;
            if (_fly == null) _center = Basemap.Project(HomeCenter) + new Vector((Obscured.Right - Obscured.Left) / 2 / Scale, (Obscured.Bottom - Obscured.Top) / 2 / Scale);
        }
        ClampView();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _wakeTimer?.Stop();
        _settle.Stop();
        _baseJobCts?.Cancel();
        _base?.Dispose();
        _base = null;
        _areas?.Dispose();
        _areas = null;
        _areasPrev?.Dispose();
        _areasPrev = null;
        _areasShown = false;
        _areasJobCts?.Cancel();
        RetireBaseImage();
        foreach (var b in _badgeSprites.Values) b.Dispose();
        _badgeSprites.Clear();
        foreach (var b in _iconSprites.Values) b.Dispose();
        _iconSprites.Clear();
        _vignette?.Dispose();
        _vignette = null;
        foreach (var b in _hatchTiles.Values) b.Dispose();
        _hatchTiles.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    // ---------------- API ----------------

    public void SetEvents(IReadOnlyList<AlertEvent> events, IReadOnlyList<Track> tracks)
    {
        Events = events;
        Tracks = tracks;
        _anim.SetData(events, tracks, DateTimeOffset.UtcNow);
        Wake();
        _labelKeepUrgentUntil = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(System.Diagnostics.Stopwatch.Frequency * (MapAnimator.FadeIn.TotalSeconds + 0.3));
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tracks.Where(t => t.IsMoving || t.Points.Count > 1))
        {
            foreach (var p in t.Points) if (!string.IsNullOrEmpty(p.Label)) labels.Add(Gazetteer.Normalize(p.Label));
        }
        if (!labels.SetEquals(_trackLabels)) { _trackLabels = labels; _baseDirty = true; }
        // места активных отметок: подложка освобождает вокруг них место для подписей тревог
        var keep = events.Where(e => e.Status != AlertStatus.AllClear)
            .SelectMany(e => e.Points.Where(p => !p.IsRegionLevel).Select(p => p.Pos).Concat(e.Origin is { } o ? [o] : []))
            .Concat(tracks.Where(t => t.Points.Count > 0 && (t.IsMoving || t.Points.Count > 1)).Select(t => t.Points[^1].Pos))
            .Select(g => Basemap.Project(g)).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (!keep.SequenceEqual(_keepOut)) { _keepOut = keep; _baseDirty = true; }
        // выделение снятого события сбрасывается; модель снимает своё выделение сама (MainViewModel), поэтому без EventClicked
        if (Selected != null && !events.Contains(Selected)) { Selected = null; _areasDirty = true; }
        if (_hoverEvent != null && !events.Contains(_hoverEvent))
        {
            _hoverEvent = null;
            _areasDirty = true;
            Cursor = CursorOf(StandardCursorType.Arrow);
        }
        // веер показывает снимок группы: если её события сняты (отбой, истечение), он закрывается
        if (_spider is { Closing: false } sp && sp.Items.Any(i => !events.Contains(i.Event))) CloseSpider();
        // слой зон перерисовывается, только если изменилось то, что на нём видно
        var sig = AreasSignature(events, tracks);
        if (sig != _areasSig) { _areasSig = sig; _areasDirty = true; }
        InvalidateVisual();
    }

    private int _areasSig;

    private static int AreasSignature(IReadOnlyList<AlertEvent> events, IReadOnlyList<Track> tracks)
    {
        var h = new HashCode();
        // события движущихся треков не подсвечивают районы — состав треков тоже влияет на слой зон
        foreach (var t in tracks)
        {
            if (!t.IsMoving) continue;
            foreach (var e in t.Events) h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e));
        }
        foreach (var e in events)
        {
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e));
            h.Add(e.EffectiveLevel);
            h.Add(e.Status);
            h.Add(e.Kind);
            h.Add(e.ColorHex);
            h.Add(e.Nearby);
            h.Add(e.Origin);
            h.Add(e.Target);
            h.Add(e.Time);
            // содержимое, а не только количества: уточнение места при том же числе точек тоже меняет слой зон
            foreach (var p in e.Points)
            {
                h.Add(p.Pos);
                h.Add(p.RadiusKm);
                h.Add(p.IsRegionLevel);
                h.Add(p.DistrictRegion);
                h.Add(p.PartRegion);
                h.Add(p.PartBearing);
            }
            foreach (var r in e.WholeRegionIds) h.Add(r);
            foreach (var r in e.ExpectedRegionIds) h.Add(r);
            foreach (var r in e.RegionIds) h.Add(r);
            h.Add(e.PartialClears.Count);
        }
        return h.ToHashCode();
    }

    public void Select(AlertEvent? ev, bool focus)
    {
        Selected = ev;
        _areasDirty = true;
        if (focus && ev != null)
        {
            var pts = ev.Points.Select(p => p.Pos).ToList();
            if (ev.Origin is { } o) pts.Add(o);
            if (ev.Target is { } t) pts.Add(t);
            if (pts.Count > 0) FlyTo(new GeoPoint(pts.Average(p => p.Lat), pts.Average(p => p.Lon)), Math.Max(_zoom, 7));
        }
        InvalidateVisual();
    }

    /// <summary>Мгновенно ставит камеру (без анимации).</summary>
    public void CenterOn(GeoPoint p, double zoom)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(p.Lat) || !double.IsFinite(p.Lon)) return;
        StopCamera();
        _zoom = Math.Clamp(zoom, MinZoomNow, MaxZoom);
        _center = Basemap.Project(p);
        ClampView();
        ViewChanged();
    }

    /// <summary>
    /// Часть окна, перекрытая стеклянными панелями (боковая лента справа): перелёты и «домой» центрируют точку
    /// в видимой части карты, а не под панелью.
    /// </summary>
    public Thickness Obscured { get; set; }
    /// <summary>Высота нижних панелей над картой (история на телефоне): линейка масштаба и координаты — над ними.</summary>
    public double HudLift { get; set; }
    /// <summary>Правый край кнопки легенды на той же строке: линейка масштаба не заходит под неё.</summary>
    public double HudLeftInset { get; set; }
    private bool _homeSized;

    /// <summary>Плавный перелёт камеры (сглаженное ускорение и торможение).</summary>
    public void FlyTo(GeoPoint p, double zoom, double seconds = 0.6)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(p.Lat) || !double.IsFinite(p.Lon)) return;
        StopCamera();
        double z = Math.Clamp(zoom, MinZoomNow, MaxZoom);
        // центр камеры — так, чтобы точка оказалась в центре видимой (не перекрытой) области
        double zs = Math.Pow(2, z);
        FlyCamera(Basemap.Project(p) + new Vector((Obscured.Right - Obscured.Left) / 2 / zs, (Obscured.Bottom - Obscured.Top) / 2 / zs), z, seconds);
    }

    /// <summary>Перелёт к центру камеры <paramref name="to"/> (мировые координаты).</summary>
    private void FlyCamera(Point to, double z, double seconds)
    {
        StopCamera();
        if (Distance(new Point(to.X * Scale, to.Y * Scale), new Point(_center.X * Scale, _center.Y * Scale)) < 1 && Math.Abs(z - _zoom) < 0.01) return;
        _fly = (_center, _zoom, to, z, 0, seconds);
        Wake();
        InvalidateVisual();
    }

    public void GoHome() => FlyTo(HomeCenter, HomeZoom);

    /// <summary>Центр кадра (без поправки на панели) и масштаб — для сохранения между запусками.</summary>
    public (GeoPoint Center, double Zoom) Camera => _fly is { } f
        ? (Basemap.Unproject(f.ToC), f.ToZ)
        : (Basemap.Unproject(_center), double.IsNaN(_zoomTarget) ? _zoom : _zoomTarget);

    /// <summary>Восстанавливает сохранённую камеру; стартовый вид «домой» после этого не применяется.</summary>
    public void RestoreCamera(GeoPoint center, double zoom)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(center.Lat) || !double.IsFinite(center.Lon)) return;
        StopCamera();
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _center = Basemap.Project(center);
        _homeSized = true;
        ClampView();
        ViewChanged();
    }

    public IEnumerable<string> DebugGeometry()
    {
        foreach (var v in VisibleTracks())
        {
            var sh = Shape(v);
            var pts = string.Join(" ", v.Track.Points.Select(p => { var q = ToScreen(p.Pos); return $"{p.Label}({q.X:F0},{q.Y:F0})"; }));
            yield return $"#{v.Track.Id} {v.Track.Threat}: fixes {pts} | lastFix ({sh.LastFix.X:F0},{sh.LastFix.Y:F0}) icon ({sh.Icon.X:F0},{sh.Icon.Y:F0}) end {(sh.End is { } e ? $"({e.X:F0},{e.Y:F0})" : "-")} tail={sh.HasTail} confirmedPts={sh.Confirmed.Count} reveal={v.Reveal:F2} launch={(sh.Launch is { } la ? $"({la.X:F0},{la.Y:F0}) {v.Track.LaunchLabel}" : "-")} pre={sh.Pre.Count} heading={v.Track.HeadingHint?.ToString("F0") ?? "-"} target={v.Track.TargetLabel ?? "-"} speed={v.Track.SpeedKmh:F0}";
        }
    }

    /// <summary>Прокручивает анимации вперёд (для офлайн-рендера).</summary>
    public void AdvanceAnimations(TimeSpan by)
    {
        if (_signalLost) _signalLoss = 1;
        var start = DateTimeOffset.UtcNow;
        for (var t = TimeSpan.Zero; t <= by; t += TimeSpan.FromMilliseconds(50)) _anim.Tick(start + t, simulated: true);
        _anim.Rebase(start + by);
    }

    /// <summary>Проверка камеры без окна: эмуляция колеса, броска и перелёта по шагам таймера.</summary>
    public IEnumerable<string> CameraSelfTest()
    {
        string St() => $"zoom {_zoom:0.000} center {Basemap.Unproject(_center).Lat:0.00},{Basemap.Unproject(_center).Lon:0.00} anim {CameraAnimating}";
        void Run(int frames) { for (int i = 0; i < frames; i++) { _camT = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency / 60; StepCamera(); } }
        yield return $"min zoom for window {Bounds.Width}x{Bounds.Height}: {MinZoomNow:0.000}";
        CenterOn(new GeoPoint(55, 60), 2);
        yield return "after CenterOn(z=2): " + St();
        SmoothZoom(new Point(Bounds.Width / 2, Bounds.Height / 2), -3);
        Run(60);
        yield return "after wheel -3: " + St();
        CenterOn(new GeoPoint(48.3, 39.9), 7);
        var w0 = ScreenToWorld(new Point(300, 200));
        SmoothZoom(new Point(300, 200), 1.2);
        Run(3);
        yield return "wheel +1.2, 3 frames: " + St();
        Run(60);
        var w1 = ScreenToWorld(new Point(300, 200));
        yield return $"wheel +1.2 settled: {St()} anchor drift {Distance(new Point(w0.X * Scale, w0.Y * Scale), new Point(w1.X * Scale, w1.Y * Scale)):0.00} px";
        var c0 = _center;
        _velocity = new Vector(2000, 0);
        Run(10);
        yield return $"fling 2000 px/s, 10 frames: moved {(c0.X - _center.X) * Scale:0} px, v {_velocity.X:0}";
        Run(120);
        yield return $"fling settled: moved {(c0.X - _center.X) * Scale:0} px, anim {CameraAnimating}";
        _velocity = new Vector(-200000, 0);
        Run(200);
        yield return "fling far east (clamped): " + St();
        FlyTo(new GeoPoint(50.6, 36.6), 9);
        Run(20);
        yield return "flyTo 20 frames: " + St();
        Run(40);
        yield return "flyTo done: " + St();
    }

    /// <summary>Сбрасывает кэш подложки (для замеров).</summary>
    public void InvalidateBase() { _baseDirty = true; InvalidateVisual(); }
    public void InvalidateAreas() { _areasDirty = true; InvalidateVisual(); }


    public void PanBy(Vector screenDelta)
    {
        _center = new Point(_center.X + screenDelta.X / Scale, _center.Y + screenDelta.Y / Scale);
        ClampView();
        ViewChanged();
    }

    /// <summary>Зум относительно центра окна: плавно (кнопки, клавиши) или сразу (замеры).</summary>
    public void ZoomBy(double delta, bool animate = true)
    {
        var anchor = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (!animate) { ZoomAt(anchor, delta); return; }
        SmoothZoom(anchor, delta);
    }

    // ---------------- плавные переходы: выделение/наведение, подсказка, смена кэша ----------------

    private readonly Dictionary<object, double> _hl = new(ReferenceEqualityComparer.Instance);
    private double _selAny;          // 0..1 — насколько «приглушено» всё невыделенное
    private double _tipFade;         // 0..1 — проявление подсказки
    private AlertEvent? _tipEvent;   // подсказка дорисовывается, пока гаснет
    private Point _tipPos;
    private long _trT;
    private const double FadeSeconds = 0.15;

    /// <summary>Плавный уровень выделения объекта (событие или трек): 0 — обычный, 1 — выделен/под курсором.</summary>
    private double Hl(object o) => _hl.TryGetValue(o, out var v) ? v : 0;

    private double HlTrack(Track t) => Math.Max(Hl(t), t.Events.Count == 0 ? 0 : t.Events.Max(e => Hl(e)));

    private static bool Settled(double v) => v is 0 or 1;

    private bool StepTransitions()
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _trT == 0 ? 0.016 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_trT, t).TotalSeconds, 0.001, 0.05);
        _trT = t;
        double k = 1 - Math.Exp(-dt / FadeSeconds * 3);
        static double Approach(double v, double target, double k) => Math.Abs(target - v) < 0.01 ? target : v + (target - v) * k;

        bool active = false;
        var targets = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (Selected != null) targets.Add(Selected);
        if (_hoverEvent != null) targets.Add(_hoverEvent);
        foreach (var o in targets) _hl.TryAdd(o, 0);
        foreach (var o in _hl.Keys.ToList())
        {
            double v = Approach(_hl[o], targets.Contains(o) ? 1 : 0, k);
            if (v == 0 && !targets.Contains(o)) _hl.Remove(o); else _hl[o] = v;
            if (!Settled(v)) active = true;
        }
        double sa = Approach(_selAny, Selected != null ? 1 : 0, k);
        double tf = Approach(_tipFade, _hoverEvent != null && _hover != null ? 1 : 0, k * 1.4);
        if (_hoverEvent != null && _hover is { } hp) { _tipEvent = _hoverEvent; _tipPos = hp; }
        if (tf == 0) _tipEvent = null;
        active |= sa != _selAny || tf != _tipFade || AreasFading;
        _selAny = sa;
        _tipFade = tf;
        return active;
    }

    // смена содержимого кэша зон (выделение, новые тревоги, перерисовка после масштаба) — кроссфейд старого и нового кадра
    private Bitmap? _areasPrev;
    private (Point Center, double Zoom, Size Size) _areasGeo, _areasPrevGeo;
    private long _areasFadeStart;
    private double AreasFadeT => _areasPrev == null ? 1 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_areasFadeStart).TotalSeconds / FadeSeconds, 0, 1);
    private bool AreasFading => AreasFadeT < 1;

    /// <summary>Экранный прямоугольник кэша (с запасом по краям), отрисованного при центре/масштабе/размере окна.</summary>
    private Rect CacheDest(Point center, double zoom, Size size)
    {
        var bs = Math.Pow(2, zoom);
        var off = WorldToScreen(new Point(center.X - SnapHalf(size.Width, _baseScaling) / bs, center.Y - SnapHalf(size.Height, _baseScaling) / bs));
        var k = Math.Pow(2, _zoom - zoom);
        double mx = BasemapRenderer.MarginOf(size.Width, CacheMargin, _baseScaling), my = BasemapRenderer.MarginOf(size.Height, CacheMargin, _baseScaling);
        return new Rect(off.X - mx * k, off.Y - my * k, (size.Width + 2 * mx) * k, (size.Height + 2 * my) * k);
    }

    // ---------------- камера: плавный зум, инерция, перелёты ----------------

    private double _zoomTarget = double.NaN;   // цель плавного зума (колесо, кнопки)
    private Point _zoomAnchor;                 // экранная точка, остающаяся на месте при зуме
    private Vector _velocity;                  // инерция панорамирования, px/с
    private readonly List<(long T, Point P)> _dragSamples = [];
    private (Point FromC, double FromZ, Point ToC, double ToZ, double Elapsed, double Dur)? _fly;
    private long _camT;
    private Rect _russiaWorld;

    private bool CameraAnimating => !double.IsNaN(_zoomTarget) || _velocity != default || _fly != null;

    private void StopCamera()
    {
        _zoomTarget = double.NaN;
        _velocity = default;
        _fly = null;
    }

    /// <summary>Территория РФ (мировые координаты) — граница отдаления и панорамирования.</summary>
    private Rect RussiaWorld
    {
        get
        {
            if (_russiaWorld.Width <= 0)
            {
                var regions = Basemap.Of(FeatureKind.Region, 0);
                if (regions.Count == 0) return new Rect(0, 0, Basemap.WorldSize, Basemap.WorldSize);
                var r = regions[0].Bounds;
                foreach (var f in regions) r = r.Union(f.Bounds);
                _russiaWorld = r;
            }
            return _russiaWorld;
        }
    }

    /// <summary>Минимальный масштаб: дальше отдалять незачем — вся территория РФ уже помещается в окне (с небольшим полем).</summary>
    private double MinZoomNow
    {
        get
        {
            if (Bounds.Width < 1 || Bounds.Height < 1) return MinZoom;
            var r = RussiaWorld;
            double fit = Math.Log2(Math.Min(Bounds.Width / (r.Width * 1.06), Bounds.Height / (r.Height * 1.06)));
            return Math.Clamp(fit, MinZoom, MaxZoom - 1);
        }
    }

    /// <summary>Не даёт отдалиться дальше «вся РФ в окне» и увести карту от РФ.</summary>
    private void ClampView()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        _zoom = Math.Clamp(_zoom, MinZoomNow, MaxZoom);
        var r = RussiaWorld.Inflate(new Thickness(RussiaWorld.Width * 0.04, RussiaWorld.Height * 0.04));
        double hw = Bounds.Width / 2 / Scale, hh = Bounds.Height / 2 / Scale;
        // окно шире территории — она целиком в кадре; уже — кадр не выходит за её пределы
        double minX = hw * 2 >= r.Width ? r.Right - hw : r.Left + hw, maxX = hw * 2 >= r.Width ? r.Left + hw : r.Right - hw;
        double minY = hh * 2 >= r.Height ? r.Bottom - hh : r.Top + hh, maxY = hh * 2 >= r.Height ? r.Top + hh : r.Bottom - hh;
        double cx = Math.Clamp(_center.X, Math.Min(minX, maxX), Math.Max(minX, maxX));
        double cy = Math.Clamp(_center.Y, Math.Min(minY, maxY), Math.Max(minY, maxY));
        if (cx != _center.X) _velocity = new Vector(0, _velocity.Y);
        if (cy != _center.Y) _velocity = new Vector(_velocity.X, 0);
        _center = new Point(cx, cy);
    }

    private void SmoothZoom(Point anchor, double delta)
    {
        _fly = null;
        double from = double.IsNaN(_zoomTarget) ? _zoom : _zoomTarget;
        _zoomTarget = Math.Clamp(from + delta, MinZoomNow, MaxZoom);
        _zoomAnchor = anchor;
        if (Math.Abs(_zoomTarget - _zoom) < 1e-6) _zoomTarget = double.NaN;
        InvalidateVisual();
    }

    /// <summary>Шаг анимации камеры (вызывается таймером кадров).</summary>
    private void StepCamera()
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _camT == 0 ? 0.016 : Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_camT, t).TotalSeconds, 0.001, 0.05);
        _camT = t;
        if (!CameraAnimating) return;
        if (_fly is { } f)
        {
            double el = f.Elapsed + dt;
            _fly = f with { Elapsed = el };
            double x = Math.Clamp(el / f.Dur, 0, 1);
            double e = x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
            _zoom = f.FromZ + (f.ToZ - f.FromZ) * e;
            _center = new Point(f.FromC.X + (f.ToC.X - f.FromC.X) * e, f.FromC.Y + (f.ToC.Y - f.FromC.Y) * e);
            if (x >= 1) _fly = null;
        }
        if (!double.IsNaN(_zoomTarget))
        {
            // предел отдаления зависит от размера окна: после его изменения цель могла стать недостижимой
            _zoomTarget = Math.Clamp(_zoomTarget, MinZoomNow, MaxZoom);
            // экспоненциальное приближение к цели: быстро в начале, мягкая остановка
            double rest = _zoomTarget - _zoom;
            double step = Math.Abs(rest) < 0.003 ? rest : rest * (1 - Math.Exp(-dt * 14));
            ZoomAt(_zoomAnchor, step, clamp: false);
            if (Math.Abs(rest) < 0.003) _zoomTarget = double.NaN;
        }
        if (_velocity != default)
        {
            _center = new Point(_center.X - _velocity.X * dt / Scale, _center.Y - _velocity.Y * dt / Scale);
            _velocity *= Math.Exp(-dt * 4.2); // трение: быстрый бросок проезжает дальше
            if (Math.Sqrt(_velocity.X * _velocity.X + _velocity.Y * _velocity.Y) < 10) _velocity = default;
        }
        ClampView();
        ViewChanged();
    }

    // ---------------- координаты ----------------

    private double Scale => Math.Pow(2, _zoom);

    /// <summary>
    /// Экранный центр карты, выровненный по пикселю устройства: при нечётном размере окна «честная» середина
    /// попадает на полпикселя, и все линии/подписи/кэши сдвигаются на 0,5 px — размываются сглаживанием.
    /// </summary>
    private double HalfW => SnapHalf(Bounds.Width, _textScaling);
    private double HalfH => SnapHalf(Bounds.Height, _textScaling);
    internal static double SnapHalf(double size, double scaling)
    {
        if (scaling <= 0) scaling = 1;
        return Math.Floor(size * scaling / 2) / scaling;
    }

    private Point WorldToScreen(Point w) => new((w.X - _center.X) * Scale + HalfW, (w.Y - _center.Y) * Scale + HalfH);

    private Point ToScreen(GeoPoint p) => WorldToScreen(Basemap.Project(p));

    private Point ScreenToWorld(Point s) => new((s.X - HalfW) / Scale + _center.X, (s.Y - HalfH) / Scale + _center.Y);

    private Rect ViewWorld() => new(ScreenToWorld(new Point(0, 0)), ScreenToWorld(new Point(Bounds.Width, Bounds.Height)));

    private Matrix WorldTransform() =>
        Matrix.CreateTranslation(-_center.X, -_center.Y) * Matrix.CreateScale(Scale, Scale) *
        Matrix.CreateTranslation(HalfW, HalfH);

    /// <summary>Камера изменилась (жест, перелёт, колесо): центр и масштаб — для повторения на внешнем экране.</summary>
    public event Action<GeoPoint, double>? CameraMoved;
    private (Point C, double Z) _lastCamera;

    /// <summary>
    /// Ведомая камера (карта на внешнем экране повторяет телефон): тот же географический центр и тот же охват по
    /// меньшей стороне экрана — на большом экране в нативном разрешении видно ту же область, только детальнее.
    /// </summary>
    public void FollowCamera(GeoPoint center, double zoom, double leaderShortSide)
    {
        if (!double.IsFinite(zoom) || Bounds.Width < 1 || Bounds.Height < 1) return;
        StopCamera();
        double z = zoom + Math.Log2(Math.Min(Bounds.Width, Bounds.Height) / Math.Max(1, leaderShortSide));
        _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        _center = Basemap.Project(center);
        _homeSized = true;
        ClampView();
        if (!IsBaseValid(strict: false)) _baseDirty = true;
        _settle?.Stop();
        _settle?.Start();
        InvalidateVisual();
    }

    private void ViewChanged()
    {
        if (!IsBaseValid(strict: false)) _baseDirty = true;
        _settle?.Stop();
        _settle?.Start();
        InvalidateVisual();
    }

    private bool IsBaseValid(bool strict)
    {
        if (_base == null || _baseDirty || _baseSize != Bounds.Size || _baseFrontVersion != Borderline.Version) return false;
        // во время жеста масштабирования допускаем растяжение старого кадра; кадр, нарисованный заранее для конечного
        // масштаба отдаления, годится на всём пути к нему
        bool forGoal = !strict && ZoomOutGoal() is { } goal && Math.Abs(goal.Zoom - _baseZoom) < 1e-6;
        if (!forGoal && (strict ? Math.Abs(_baseZoom - _zoom) > 1e-6 : Math.Abs(_baseZoom - _zoom) > 0.75)) return false;
        // кадр половинного разрешения (нарисован во время жеста) после остановки заменяется чётким
        if (strict && _baseRes < 1) return false;
        var off = BaseOffset();
        var s = Math.Pow(2, _zoom - _baseZoom);
        double mx = BasemapRenderer.MarginOf(_baseSize.Width, CacheMargin, _baseScaling), my = BasemapRenderer.MarginOf(_baseSize.Height, CacheMargin, _baseScaling);
        double w = _baseSize.Width, h = _baseSize.Height;
        // видимая область в координатах кэша должна поместиться в кэш
        double left = -off.X / s, top = -off.Y / s, right = left + w / s, bottom = top + h / s;
        return left >= -mx && top >= -my && right <= w + mx && bottom <= h + my;
    }

    private static bool Covers(Rect d, Size size) =>
        d.Left <= 0.5 && d.Top <= 0.5 && d.Right >= size.Width - 0.5 && d.Bottom >= size.Height - 0.5;

    /// <summary>Растянутый прежний кадр подложки ещё закрывает всё окно.</summary>
    private bool BaseCoversScreen()
    {
        // размер окна не важен: кадр привязан к миру, а не к окну; после смены размера он держится на месте, пока готовится новый
        if (_base == null) return false;
        var d = CacheDest(_baseCenter, _baseZoom, _baseSize);
        return d.Left <= 0.5 && d.Top <= 0.5 && d.Right >= Bounds.Width - 0.5 && d.Bottom >= Bounds.Height - 0.5 && Math.Abs(_baseZoom - _zoom) < 2.5;
    }

    private Point BaseOffset()
    {
        // экранная позиция левого верхнего угла рабочей области кэша
        var bs = Math.Pow(2, _baseZoom);
        return WorldToScreen(new Point(_baseCenter.X - SnapHalf(_baseSize.Width, _baseScaling) / bs, _baseCenter.Y - SnapHalf(_baseSize.Height, _baseScaling) / bs));
    }

    // ---------------- ввод ----------------

    // ---- касания: два пальца — масштаб и сдвиг вокруг середины между ними ----
    private readonly Dictionary<IPointer, Point> _touches = [];
    private double _pinchDist;
    private Point _pinchMid;
    private bool _pinching;
    private long _lastTapAt;
    private Point _lastTapPos;

    /// <summary>Касание: учитывается в жесте двух пальцев. Возвращает true, если событие поглощено жестом.</summary>
    private bool TouchPressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return false;
        _touches[e.Pointer] = e.GetPosition(this);
        if (_touches.Count < 2) return false;
        // второй палец: перетаскивание одним пальцем превращается в масштабирование
        var (a, b) = TwoTouches();
        _pinchDist = Math.Max(1, Distance(a, b));
        _pinchMid = Mid(a, b);
        _pinching = true;
        _dragStart = null;
        _dragged = true; // отпускание не будет «щелчком» по отметке
        _velocity = default;
        _fly = null;
        e.Pointer.Capture(this);
        e.Handled = true;
        return true;
    }

    private bool TouchMoved(PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch || !_touches.ContainsKey(e.Pointer)) return false;
        _touches[e.Pointer] = e.GetPosition(this);
        if (!_pinching || _touches.Count < 2) return false;
        var (a, b) = TwoTouches();
        double dist = Math.Max(1, Distance(a, b));
        var mid = Mid(a, b);
        // сдвиг вслед за серединой и масштаб вокруг неё
        _center = new Point(_center.X - (mid.X - _pinchMid.X) / Scale, _center.Y - (mid.Y - _pinchMid.Y) / Scale);
        ZoomAt(mid, Math.Log2(dist / _pinchDist), clamp: false);
        _pinchDist = dist;
        _pinchMid = mid;
        ClampView();
        ViewChanged();
        if (!IsBaseValid(strict: false)) _baseDirty = true;
        _settle?.Stop();
        _settle?.Start();
        _dragPending = true;
        e.Handled = true;
        return true;
    }

    private bool TouchReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return false;
        _touches.Remove(e.Pointer);
        if (!_pinching) return false;
        if (_touches.Count < 2) _pinching = false;
        // оставшийся палец продолжает перетаскивание с текущего места, без скачка
        if (_touches.Count == 1)
        {
            var rest = _touches.Values.First();
            _dragStart = rest;
            _dragLast = rest;
            _dragged = true;
            _dragSamples.Clear();
        }
        else _dragStart = null;
        _settle?.Stop();
        _settle?.Start();
        InvalidateVisual();
        e.Handled = true;
        return true;
    }

    /// <summary>Двойное касание пустого места карты — приблизить на шаг к этой точке.</summary>
    private bool TouchDoubleTap(Point p)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool dbl = System.Diagnostics.Stopwatch.GetElapsedTime(_lastTapAt, now).TotalMilliseconds < 320 && Distance(p, _lastTapPos) < 40;
        _lastTapAt = dbl ? 0 : now;
        _lastTapPos = p;
        if (!dbl) return false;
        SmoothZoom(p, 1);
        return true;
    }

    private (Point, Point) TwoTouches()
    {
        using var it = _touches.Values.GetEnumerator();
        it.MoveNext(); var a = it.Current;
        it.MoveNext(); var b = it.Current;
        return (a, b);
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Wake();
        base.OnPointerPressed(e);
        if (_signalLost) return; // без связи карта не реагирует: обстановка устарела
        if (TouchPressed(e)) return;
        if (e.Pointer.Type == PointerType.Touch) ClearTouchTip();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_pickRegions && RadiusEdgeAt(e.GetPosition(this)) is { } ra)
        {
            _radiusArea = ra;
            _radiusDragKm = ra.RadiusKm > 0 ? ra.RadiusKm : WatchRadiusKm;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragLast = _dragStart.Value;
        _dragged = false;
        _velocity = default;
        _fly = null;
        _dragSamples.Clear();
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Wake();
        base.OnPointerMoved(e);
        if (_signalLost) return;
        if (TouchMoved(e)) return;
        var pos = e.GetPosition(this);
        // палец не «наводится»: подсказки по наведению — только для мыши и пера
        _hover = e.Pointer.Type == PointerType.Touch && _dragStart == null ? null : pos;
        if (_radiusArea is { } dragA)
        {
            _radiusDragKm = RadiusFromPointer(dragA, pos);
            InvalidateVisual();
            return;
        }
        if (_dragStart == null && _pickRegions && RadiusEdgeAt(pos) is { } edge)
        {
            if (_radiusHover != edge) { _radiusHover = edge; _pickHover = null; _pickPlace = null; }
            if (_pickCursor != StandardCursorType.SizeAll) { _pickCursor = StandardCursorType.SizeAll; Cursor = CursorOf(StandardCursorType.SizeAll); }
            InvalidateVisual();
            return;
        }
        if (_radiusHover != null) { _radiusHover = null; InvalidateVisual(); }
        if (_dragStart == null && _pickRegions && PickPoint)
        {
            _pickHover = null;
            _pickPlace = null;
            if (_pickCursor != StandardCursorType.Cross) { _pickCursor = StandardCursorType.Cross; Cursor = CursorOf(StandardCursorType.Cross); }
            InvalidateVisual();
            return;
        }
        if (_dragStart == null && _pickRegions)
        {
            var place = PlaceLabelAt(pos);
            var rid = place == null ? RegionAtScreen(pos) : null;
            _pickHover = rid;
            _pickPlace = place;
            var want = rid != null || place != null ? StandardCursorType.Hand : StandardCursorType.Arrow;
            if (want != _pickCursor) { _pickCursor = want; Cursor = CursorOf(want); }
            InvalidateVisual();
            return;
        }
        if (_dragStart is not { } start)
        {
            var h = HitEvent(pos);
            if (h != _hoverEvent)
            {
                _hoverEvent = h;
                _areasDirty = true;
                Cursor = CursorOf(h != null ? StandardCursorType.Hand : StandardCursorType.Arrow);
            }
            InvalidateVisual();
            return;
        }
        var d = pos - start;
        // палец дрожит сильнее мыши: порог начала перетаскивания больше, иначе касание отметки не срабатывает
        if (!_dragged && Math.Abs(d.X) + Math.Abs(d.Y) < (e.Pointer.Type == PointerType.Touch ? 10 : 4)) return;
        _dragged = true;
        // приращениями — совместимо с идущей анимацией зума
        var step = pos - _dragLast;
        _dragLast = pos;
        _center = new Point(_center.X - step.X / Scale, _center.Y - step.Y / Scale);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _dragSamples.Add((now, pos));
        _dragSamples.RemoveAll(x => System.Diagnostics.Stopwatch.GetElapsedTime(x.T, now).TotalMilliseconds > 100);
        ClampView();
        // мышь шлёт 60–1000 событий в секунду, каждый кадр карты на процессоре дорогой: при перетаскивании кадр
        // запрашивает таймер не чаще DragFps (центр уже сдвинут, кадр покажет последнее положение)
        if (!IsBaseValid(strict: false)) _baseDirty = true;
        _settle?.Stop();
        _settle?.Start();
        _dragPending = true;
    }

    /// <summary>
    /// Касание вместо наведения: подсказка выбранной отметки (как при наведении мышью) или название региона
    /// в режиме выбора зон — у точки касания, до следующего касания.
    /// </summary>
    /// <summary>Закреплённая касанием подсказка: точка на карте или null — снята (для зеркала на внешнем экране).</summary>
    public event Action<GeoPoint?>? TouchTipChanged;

    /// <summary>Ведомая карта: подсказка у той же географической точки, что на телефоне.</summary>
    public void ShowTouchTip(GeoPoint? at)
    {
        if (at is { } g) PinTouchTip(WorldToScreen(Basemap.Project(g)));
        else ClearTouchTip();
    }

    private void PinTouchTip(Point p)
    {
        _touchTip = true;
        TouchTipChanged?.Invoke(Basemap.Unproject(ScreenToWorld(p)));
        _hover = p;
        if (_pickRegions)
        {
            if (!PickPoint)
            {
                _pickPlace = PlaceLabelAt(p);
                _pickHover = _pickPlace == null ? RegionAtScreen(p) : null;
            }
        }
        else if (_heat == null && _spider == null && Selected != null)
        {
            _hoverEvent = Selected;
            _areasDirty = true;
        }
        else _hoverEvent = null;
        InvalidateVisual();
    }

    private void ClearTouchTip()
    {
        if (!_touchTip) return;
        _touchTip = false;
        TouchTipChanged?.Invoke(null);
        _hover = null;
        if (_hoverEvent != null) _areasDirty = true;
        _hoverEvent = null;
        _pickHover = null;
        _pickPlace = null;
        InvalidateVisual();
    }

    private bool _dragPending;
    private long _dragFrameAt;
    private static readonly double[] DragFps = [30, 30, 60];

    protected override void OnPointerExited(PointerEventArgs e)
    {
        Wake();
        base.OnPointerExited(e);
        // палец «уходит» с карты при каждом отпускании — закреплённая касанием подсказка остаётся
        if (e.Pointer.Type == PointerType.Touch && _touchTip) return;
        _hover = null;
        if (_hoverEvent != null) _areasDirty = true;
        _hoverEvent = null;
        if (_dragStart == null && _radiusArea == null)
        {
            _pickHover = null; _pickPlace = null; _radiusHover = null;
            if (_pickCursor != StandardCursorType.Arrow) { _pickCursor = StandardCursorType.Arrow; Cursor = CursorOf(StandardCursorType.Arrow); }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Wake();
        base.OnPointerReleased(e);
        if (TouchReleased(e)) return;
        // жест начинается только левой кнопкой (OnPointerPressed) — отпускание другой кнопки его не завершает
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_radiusArea is { } ra)
        {
            int km = (int)Math.Round(_radiusDragKm);
            int was = ra.RadiusKm > 0 ? ra.RadiusKm : (int)Math.Round(WatchRadiusKm);
            _radiusArea = null;
            e.Pointer.Capture(null);
            if (km != was) PlaceRadiusChanged?.Invoke(ra, km);
            InvalidateVisual();
            return;
        }
        if (_dragStart != null && !_dragged)
        {
            var tp = e.GetPosition(this);
            // касание: двойное — приблизить; одиночное — как щелчок (но после зума по двойному не открывает отметку)
            if (!(e.Pointer.Type == PointerType.Touch && !_pickRegions && TouchDoubleTap(tp))) HitTestClick(tp);
            if (e.Pointer.Type == PointerType.Touch) PinTouchTip(tp);
        }
        else if (_dragStart != null) StartInertia();
        _dragStart = null;
        _dragged = false;
        // последний сдвиг виден сразу, не дожидаясь таймера; ретро-помехи возвращаются
        _dragPending = false;
        InvalidateVisual();
        e.Pointer.Capture(null);
        _settle.Stop();
        _settle.Start();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        Wake();
        base.OnPointerCaptureLost(e);
        _touches.Remove(e.Pointer);
        if (_touches.Count < 2) _pinching = false;
        // захват отобран (окно потеряло фокус, всплывающее окно): жест отменяется без клика и без инерции
        if (_dragStart == null && _radiusArea == null) return;
        _dragStart = null;
        _dragged = false;
        _radiusArea = null;
        _dragSamples.Clear();
        _settle.Stop();
        _settle.Start();
        InvalidateVisual();
    }

    /// <summary>Инерция после броска: скорость по последним ~80 мс движения; если перед отпусканием палец замер — без инерции.</summary>
    private void StartInertia()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _dragSamples.RemoveAll(x => System.Diagnostics.Stopwatch.GetElapsedTime(x.T, now).TotalMilliseconds > 80);
        if (_dragSamples.Count < 2) return;
        var (t0, p0) = _dragSamples[0];
        var (t1, p1) = _dragSamples[^1];
        double dt = System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalSeconds;
        if (dt < 0.008) return;
        var v = (p1 - p0) / dt;
        double speed = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (speed < 120) return;
        if (speed > 5000) v *= 5000 / speed;
        _velocity = v;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Wake();
        base.OnPointerWheelChanged(e);
        if (_signalLost) { e.Handled = true; return; }
        SmoothZoom(e.GetPosition(this), e.Delta.Y * 0.4);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_signalLost) return;
        double step = 120 / Scale;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomBy(0.5); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(-0.5); break;
            case Key.Left: PanSmooth(-step, 0); break;
            case Key.Right: PanSmooth(step, 0); break;
            case Key.Up: PanSmooth(0, -step); break;
            case Key.Down: PanSmooth(0, step); break;
            case Key.Home: GoHome(); break;
            case Key.Escape when _spider is { Closing: false }: CloseSpider(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ZoomAt(Point screen, double delta, bool clamp = true)
    {
        var nz = Math.Clamp(_zoom + delta, MinZoomNow, MaxZoom);
        if (Math.Abs(nz - _zoom) < 1e-9) return;
        var w = ScreenToWorld(screen);
        _zoom = nz;
        _center = new Point(w.X - (screen.X - HalfW) / Scale, w.Y - (screen.Y - HalfH) / Scale);
        if (!clamp) return;
        ClampView();
        ViewChanged();
    }

    private void PanSmooth(double dx, double dy)
    {
        var from = _fly?.ToC ?? _center;
        FlyCamera(new Point(from.X + dx, from.Y + dy), _fly?.ToZ ?? _zoom, 0.28);
    }

    // ---------------- веер: раскрытие группы отметок/объектов по щелчку ----------------

    /// <summary>Элемент веера: событие или трек; <see cref="Pos"/> — его настоящее место на карте (к нему ведёт выноска).</summary>
    private sealed record SpiderItem(AlertEvent Event, TrackVisual? Track, GeoPoint Pos, string Label);

    private sealed class Spider
    {
        public required Point Anchor;          // мировые координаты центра группы
        public required List<SpiderItem> Items;
        public required double Zoom;           // масштаб при открытии (при заметном зуме веер закрывается)
        public long Opened = System.Diagnostics.Stopwatch.GetTimestamp();
        public long ClosedAt;
        public bool Closing;
        public int Hidden;                     // не поместились в веер
    }

    private Spider? _spider;
    private readonly List<(Point Pos, List<(AlertEvent Ev, string? Label)> Items)> _clusterHits = [];
    private const double SpiderSeconds = 0.22;
    private const int SpiderMax = 24;

    private static double SpiderRadius(int n) => n <= 8 ? Math.Max(38, n * 8.5) : 38 + 6.5 * Math.Sqrt(n) * 2.2;

    private double SpiderT()
    {
        if (_spider is not { } s) return 0;
        double open = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(s.Opened).TotalSeconds / SpiderSeconds, 0, 1);
        if (!s.Closing) return EaseOut(open);
        double close = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(s.ClosedAt).TotalSeconds / (SpiderSeconds * 0.7), 0, 1);
        return EaseOut(open) * (1 - EaseOut(close));
    }

    public bool SpiderAnimating => _spider != null && (_spider.Closing ||
        System.Diagnostics.Stopwatch.GetElapsedTime(_spider.Opened).TotalSeconds < SpiderSeconds);

    /// <summary>Группа под курсором: кластер отметок (2+ события) или объединённые «головы» треков.</summary>
    private (Point At, List<SpiderItem> Items)? GroupHit(Point p)
    {
        foreach (var (lead, members) in _plan.Members)
        {
            if (members.Count < 2) continue;
            var at = Shape(lead).Icon;
            if (Distance(at, p) > 16) continue;
            var items = members
                .Select(m => new SpiderItem(m.Track.Events.OrderByDescending(e => e.Time).FirstOrDefault()!, m, m.Head,
                    (m.Track.Points[^1].Label is { Length: > 0 } l ? l : m.Track.Info.Short) + (m.Track.Count > 1 ? $" ×{m.Track.Count}" : "")))
                .Where(i => i.Event != null).ToList();
            if (items.Count >= 2) return (at, items);
        }
        foreach (var (at, list) in _clusterHits)
        {
            if (Distance(at, p) > 14) continue;
            var items = list.GroupBy(i => i.Ev)
                .Select(g =>
                {
                    var ev = g.Key;
                    var pos = ev.Points.FirstOrDefault(pt => pt.Label == g.First().Label)?.Pos ?? ev.Target ?? ev.Points[0].Pos;
                    var label = g.Select(i => i.Label).FirstOrDefault(l => l != null) ?? ev.RegionNames;
                    return new SpiderItem(ev, null, pos, label);
                })
                .OrderByDescending(i => Severity(i.Event)).ThenByDescending(i => i.Event.Time).ToList();
            if (items.Count >= 2) return (at, items);
        }
        return null;
    }

    private void OpenSpider((Point At, List<SpiderItem> Items) g)
    {
        var items = g.Items.OrderByDescending(i => Severity(i.Event)).ThenByDescending(i => i.Event.Time).ToList();
        _spider = new Spider
        {
            Anchor = ScreenToWorld(g.At), Items = items.Take(SpiderMax).ToList(), Zoom = _zoom, Hidden = Math.Max(0, items.Count - SpiderMax),
        };
        if (_hoverEvent != null) _areasDirty = true; // наведённое подсвечено и в кэше зон
        _hoverEvent = null;
        InvalidateVisual();
    }

    private void CloseSpider()
    {
        if (_spider is not { Closing: false } s) return;
        s.Closing = true;
        s.ClosedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        InvalidateVisual();
    }

    private void SelectFromSpider(SpiderItem item)
    {
        Selected = item.Event;
        _areasDirty = true;
        EventClicked?.Invoke(item.Event);
        InvalidateVisual();
    }

    /// <summary>Положения элементов веера на экране: кольцо (до 8) или спираль, с раскрытием от центра на долю <paramref name="t"/>.</summary>
    private List<Point> SpiderLayout(Spider s, double t)
    {
        var c = WorldToScreen(s.Anchor);
        int n = s.Items.Count;
        var res = new List<Point>(n);
        if (n <= 8)
        {
            double r = SpiderRadius(n) * t;
            for (int i = 0; i < n; i++)
            {
                double a = -Math.PI / 2 + 2 * Math.PI * i / n;
                res.Add(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
            }
        }
        else
        {
            // спираль Архимеда с равным шагом вдоль кривой
            double ang = 0;
            for (int i = 0; i < n; i++)
            {
                double r = (30 + 4.2 * ang) * t;
                res.Add(new Point(c.X + r * Math.Cos(ang - Math.PI / 2), c.Y + r * Math.Sin(ang - Math.PI / 2)));
                ang += 30 / Math.Max(20, 30 + 4.2 * ang);
            }
        }
        return res;
    }

    /// <summary>Радиус подложки веера (кольцо или конец спирали + поле) — та же граница и для клика «мимо».</summary>
    private double SpiderOuterRadius(Spider s) =>
        (s.Items.Count <= 8 ? SpiderRadius(s.Items.Count) : Distance(WorldToScreen(s.Anchor), SpiderLayout(s, 1)[^1])) + 26;

    private SpiderItem? SpiderHit(Point p)
    {
        if (_spider is not { Closing: false } s) return null;
        var pts = SpiderLayout(s, 1);
        int best = -1;
        double bd = 14;
        for (int i = 0; i < pts.Count; i++)
        {
            double d = Distance(pts[i], p);
            if (d < bd) { bd = d; best = i; }
        }
        return best >= 0 ? s.Items[best] : null;
    }

    private void DrawSpider(DrawingContext ctx)
    {
        if (_spider is not { } s) return;
        if (!s.Closing && Math.Abs(_zoom - s.Zoom) > 0.6) CloseSpider();
        double t = SpiderT();
        if (s.Closing && t <= 0.001) { _spider = null; return; }
        var c = WorldToScreen(s.Anchor);
        var pts = SpiderLayout(s, t);
        double R = SpiderOuterRadius(s);

        // подложка: затемнение вокруг группы, чтобы веер читался поверх треков и зон
        var bg = new RadialGradientBrush
        {
            Center = new RelativePoint(c, RelativeUnit.Absolute), GradientOrigin = new RelativePoint(c, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(R * Math.Max(t, 0.3), RelativeUnit.Absolute), RadiusY = new RelativeScalar(R * Math.Max(t, 0.3), RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(200 * t), 0x0B, 0x0E, 0x13), 0),
                new GradientStop(Color.FromArgb((byte)(170 * t), 0x0B, 0x0E, 0x13), 0.75),
                new GradientStop(Color.FromArgb(0, 0x0B, 0x0E, 0x13), 1),
            },
        };
        ctx.DrawEllipse(bg, null, c, R * Math.Max(t, 0.3), R * Math.Max(t, 0.3));
        ctx.DrawEllipse(null, new Pen(A(Colors.White, 0.35 * t), 1), c, 3, 3);

        for (int i = 0; i < s.Items.Count; i++)
        {
            var it = s.Items[i];
            var paint = P(it.Event.ColorHex);
            var at = pts[i];
            bool sel = it.Event == Selected || it.Event == _hoverEvent;
            // выноска к настоящему месту объекта
            var real = ToScreen(it.Pos);
            ctx.DrawLine(new Pen(A(paint.Color, 0.55 * t), 1), real, at);
            ctx.DrawEllipse(paint.Solid, null, real, 2, 2);
            using (LayerAlpha(ctx, t))
            {
                if (it.Track is { } tv)
                {
                    double ang = MapIcons.IsDirectional(tv.Track.Info.Icon) && !tv.Track.UncertainHeading ? tv.Bearing * Math.PI / 180 : 0;
                    ctx.DrawEllipse(MapTheme.PanelBg, new Pen(sel ? paint.Solid : MapTheme.PanelBorder, sel ? 1.6 : 1), at, 12, 12);
                    DrawTrackIconCached(ctx, tv.Track.Info, paint, at, 15, ang, tv.Track.StateAt(_dnow) == TrackState.Stale);
                }
                else DrawKindBadgeCached(ctx, at, it.Event.Info, paint, sel ? 11 : 10, it.Event.EffectiveLevel);
                if (sel) ctx.DrawEllipse(null, new Pen(paint.Solid, 1.6), at, 15, 15);

                // подпись наружу от центра: тип угрозы (цветом значка, тусклее) и место
                var kind = Text(it.Event.Info.Short, 10, paint.Line, Bold);
                var name = Text(it.Label, 10.5, HoverText(Hl(it.Event)), Medium);
                bool right = at.X >= c.X - 1;
                double w = kind.Width + 5 + name.Width;
                double x = right ? at.X + 16 : at.X - 16 - w;
                double y = at.Y - name.Height / 2;
                if (s.Items.Count <= 8 || t > 0.95)
                {
                    DrawHaloText(ctx, kind, new Point(x, y + (name.Height - kind.Height) / 2));
                    DrawHaloText(ctx, name, new Point(x + kind.Width + 5, y));
                }
            }
        }
        if (s.Hidden > 0 && t > 0.95)
        {
            var more = Text($"ещё {s.Hidden} — приблизьте карту", 10, MapTheme.HudText, Medium);
            DrawHaloText(ctx, more, new Point(c.X - more.Width / 2, c.Y + R - 8));
        }
    }

    /// <summary>Отладка (--render-demo): щелчок в точке экрана (центры групп для щелчка — <see cref="DebugGroups"/>).</summary>
    public void DebugClick(Point p) => HitTestClick(p);

    /// <summary>Отладка: имитация перетаскивания карты на (dx, dy) пикселей без отпускания; кадр рисует вызывающий.</summary>
    public void DebugDragBy(Point from, Vector by)
    {
        _dragStart = from; _dragLast = from; _dragged = false;
        var pos = from + by;
        var d = pos - from;
        _dragged = true;
        _center = new Point(_center.X - d.X / Scale, _center.Y - d.Y / Scale);
        ClampView();
        if (!IsBaseValid(strict: false)) _baseDirty = true;
    }

    public void DebugDragEnd() { _dragStart = null; _dragged = false; }
    /// <summary>Отладка: перетаскивание края круга пункта с одного расстояния (км) на другое по направлению на восток.</summary>
    public string DebugDragRadius(string placeName, double toKm, bool release)
    {
        var a = _watch.FirstOrDefault(x => x.Kind == WatchArea.PlaceKind && x.Name == placeName);
        if (a == null) return "нет пункта";
        var g = new GeoPoint(a.Lat, a.Lon);
        var c = ToScreen(g);
        double ppk = PxPerKm(g);
        var edge = new Point(c.X + PlaceKm(a) * ppk, c.Y);
        var grab = RadiusEdgeAt(edge);
        if (grab == null) return $"край не захватывается (r={PlaceKm(a) * ppk:0.0} px)";
        _radiusArea = grab;
        _radiusDragKm = PlaceKm(grab);
        _hover = new Point(c.X + toKm * ppk, c.Y);
        _radiusDragKm = RadiusFromPointer(grab, _hover.Value);
        string res = $"тянем: {_radiusDragKm:0} км";
        if (release)
        {
            int km = (int)Math.Round(_radiusDragKm);
            _radiusArea = null;
            PlaceRadiusChanged?.Invoke(grab, km);
            _radiusHover = grab;
        }
        return res;
    }

    public IEnumerable<(Point At, int Count)> DebugGroups() =>
        _plan.Members.Where(m => m.Value.Count > 1).Select(m => (Shape(m.Key).Icon, m.Value.Count))
            .Concat(_clusterHits.Select(c => (c.Pos, c.Items.Select(i => i.Ev).Distinct().Count())));

    public bool DebugSpiderOpen => _spider is { Closing: false };

    private AlertEvent? HitEvent(Point p)
    {
        if (SpiderHit(p) is { } si) return si.Event;
        foreach (var (r, ev) in _regionTagHits)
            if (r.Contains(p)) return ev;
        AlertEvent? best = null;
        double bestDist = 20;
        var now = DateTimeOffset.UtcNow;
        // иконки треков — там же, где их рисует DrawTrackHeads: по маршруту, только видимые и не скрытые в группе
        foreach (var v in VisibleTracks())
        {
            if (_plan.Hidden.Contains(v) || TrackAlpha(v, now) < 0.01) continue;
            var d = Distance(BuildShape(v).Icon, p);
            if (d < bestDist) { bestDist = d; best = v.Track.Events.OrderByDescending(e => e.Time).FirstOrDefault(); }
        }
        var tracked = _anim.Tracks.Where(v => v.Track.IsMoving || v.Track.Points.Count > 1).SelectMany(v => v.Track.Events).ToHashSet();
        int lod = CurrentLod;
        foreach (var ev in Events)
        {
            // у события трека значков-маркеров нет — его точки рисуются ромбами на линии трека
            var pts = tracked.Contains(ev) ? ev.Points.Where(q => !q.IsRegionLevel).Select(q => q.Pos) : AllMarkers(ev, lod);
            foreach (var pt in pts)
            {
                var d = Distance(ToScreen(pt), p);
                if (d < bestDist) { bestDist = d; best = ev; }
            }
        }
        return best;
    }

    private void HitTestClick(Point p)
    {
        if (_pickRegions && PickPoint)
        {
            PointPicked?.Invoke(Basemap.Unproject(ScreenToWorld(p)));
            InvalidateVisual();
            return;
        }
        if (_heat != null)
        {
            // статистика ведётся по регионам: район на крупном масштабе ей не нужен
            // промах в море или за границу не сбрасывает выбранный регион (сброс — отдельной кнопкой)
            if (RegionAtScreen(p, districts: false) is { } hr) HeatRegionClicked?.Invoke(hr);
            return;
        }
        if (_pickRegions)
        {
            if (PlaceLabelAt(p) is { } pl) PlacePicked?.Invoke(pl.Name, Basemap.Unproject(pl.Pos));
            else if (RegionAtScreen(p) is { } rid) RegionPicked?.Invoke(rid);
            InvalidateVisual();
            return;
        }
        // открытый веер: щелчок по элементу выбирает его, по центру или мимо — закрывает
        if (_spider is { Closing: false } sp)
        {
            var at = WorldToScreen(sp.Anchor);
            if (SpiderHit(p) is { } item)
            {
                SelectFromSpider(item);
                return;
            }
            CloseSpider();
            if (Distance(at, p) <= SpiderOuterRadius(sp)) { InvalidateVisual(); return; }
        }
        // группа (несколько отметок или объектов в одном месте) — раскрывается веером
        if (GroupHit(p) is { } group)
        {
            OpenSpider(group);
            return;
        }
        var best = HitEvent(p);
        if (best == null)
        {
            var w = ScreenToWorld(p);
            var region = Basemap.Features.FirstOrDefault(f => f.Kind == FeatureKind.Region && f.Lod == CurrentLod &&
                                                               f.Bounds.Contains(w) && f.Geometry.FillContains(w));
            if (region != null)
                best = Events.Where(e => e.Status != AlertStatus.AllClear && e.WholeRegionIds.Contains(region.Id))
                    .OrderByDescending(e => e.Time).FirstOrDefault();
            if (best == null)
                best = Events.Where(e => e.Status != AlertStatus.AllClear)
                    .FirstOrDefault(e => e.Points.Any(pt => pt.DistrictRegion is { } dr && Basemap.District(dr, pt.Label, CurrentLod) is { } d
                                                            && d.Bounds.Contains(w) && d.Geometry.FillContains(w)
                                                            || pt.PartRegion is { } pr && Basemap.PartShape(pr, pt.PartBearing, CurrentLod) is { } pd
                                                                && pd.Bounds.Contains(w) && pd.Geometry.FillContains(w)));
        }
        Selected = best;
        _areasDirty = true;
        EventClicked?.Invoke(best);
        InvalidateVisual();
    }

    /// <summary>Точки маркеров события — те же, что получают кластеры (<see cref="BuildClusters"/>).</summary>
    private static IEnumerable<(GeoPoint Pos, string? Label)> MarkerPoints(AlertEvent ev, int lod)
    {
        // районы с контуром подписаны в самом районе — отдельный маркер не нужен
        foreach (var p in ev.Points)
            if (!p.IsRegionLevel && !HasArea(p, lod)) yield return (p.Pos, p.Label);
        if (ev.Target is { } t && ev.Origin == null) yield return (t, null);
    }

    /// <summary>Всё, что рисуется значком события: маркеры, значки регионов, место пуска.</summary>
    private static IEnumerable<GeoPoint> AllMarkers(AlertEvent ev, int lod)
    {
        if (ev.Status == AlertStatus.Launch)
        {
            if (ev.Origin is { } o) yield return o;
            yield break;
        }
        foreach (var (pos, _) in MarkerPoints(ev, lod)) yield return pos;
        foreach (var p in ev.Points) if (p.IsRegionLevel) yield return p.Pos;
    }


    private int CurrentLod => _zoom < 6.3 ? 0 : 1;

    // ================= отрисовка =================

    public override void Render(DrawingContext ctx)
    {
        if (CameraMoved != null && (_center, _zoom) != _lastCamera)
        {
            _lastCamera = (_center, _zoom);
            CameraMoved(Basemap.Unproject(_center), _zoom);
        }
        // смена подложки или слоя зон (данные, выделение, наведение) — стекло над картой пересчитывается
        if (_baseVersion != _glassBaseVersion || _areasStamp != _glassAreasStamp || _areasDirty)
        {
            _glassBaseVersion = _baseVersion;
            _glassAreasStamp = _areasStamp;
            Glass.InvalidateBackdrop();
        }
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (Math.Abs(scaling - _baseScaling) > 1e-6) _baseDirty = true;
        _textScaling = scaling;
        Prof(-1);
        bool background = ForceBackgroundBase || TopLevel.GetTopLevel(this) != null;
        if (_base == null || (!background && !IsBaseValid(strict: false))) RenderBaseSync(size, scaling);
        else if (background)
        {
            // во время жеста — при выходе за допуск растяжения/запас; после остановки — в точном масштабе
            bool stale = !IsBaseValid(strict: false);
            bool settle = _wantStrict && !CameraAnimating && _dragStart == null && !IsBaseValid(strict: true);
            if (stale || settle) RequestBase(size, scaling);
            // фон не успевает, а кадр уже не покрывает экран (сильное отдаление/быстрый бросок) — лучше задержка, чем пустое море
            // фоновая подложка для конечного масштаба отдаления почти готова (каждый шаг колеса — 0,1 масштаба за ~60 мс):
            // растянутый прежний кадр с краями моря на пару кадров лучше синхронной перерисовки на 25–30 мс
            bool goalJob = _baseJob is { IsCompleted: false } && ZoomOutGoal() is { } g && _baseJobFrame is { } jf && Math.Abs(jf.Zoom - g.Zoom) < 1e-6;
            if (stale && !goalJob && !BaseCoversScreen()) RenderBaseSync(size, scaling);
        }

        ctx.FillRectangle(MapTheme.Sea, new Rect(size));
        var k = Math.Pow(2, _zoom - _baseZoom);
        var dest = CacheDest(_baseCenter, _baseZoom, _baseSize);
        // во время жеста масштабирования кэш растягивается — билинейной фильтрации достаточно (кадр временный)
        using var _ro = Math.Abs(k - 1) > 1e-6 ? ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality }) : default;
        _dnow = Clock.Now;
        var now = DateTimeOffset.UtcNow;
        _shapes.Clear();
        // слой зон запекается поверх копии подложки — за кадр выводится один битмап, а не два
        // (при растяжении во время зума это вдвое дешевле); во время проявления/угасания зон кэш бесполезен
        bool cachedAreas = !_anim.AreasAnimating;
        // после проявления/угасания в кэше лежит слой, записанный до анимации: показ такого кадра даёт повторное
        // «мигание» объектов. До готовности свежего слоя зоны рисуются вживую (без кроссфейда).
        if (cachedAreas && _wasAreasAnimating)
        {
            _areasStale = true;
            _areasFreshSeq = _areasSeq + 1;
            _areasDirty = true;
        }
        _wasAreasAnimating = !cachedAreas;
        if (cachedAreas)
        {
            var refresh = TimeSpan.FromSeconds(Clock.IsSimulated ? 1 : 5); // прозрачность зависит от давности тревоги
            // кроссфейд — только при видимой смене содержимого (выделение, новые данные), не при плановом обновлении
            bool contentChanged = _areasDirty || !BakedTracksCurrent(now);
            bool baseChanged = _areasBaseVersion != _baseVersion;
            if (contentChanged || baseChanged || _areas == null || System.Diagnostics.Stopwatch.GetElapsedTime(_areasStamp) > refresh)
            {
                // смена одной лишь подложки не плавный переход: растворение старого (растянутого, размытого) кадра в чёткий выглядело как мыло
                if (background && _areas != null && _baseImage != null) RequestAreas(now, crossfade: contentChanged && !baseChanged);
                else RenderAreas(now, crossfade: contentChanged && !baseChanged);
            }
        }
        if (_areasStale) cachedAreas = false;
        // кэш зон относится к прежней подложке и после резкого сдвига/отдаления уже не закрывает окно — по краям
        // открылось бы пустое море (тёмные блоки). Пока свежий слой не готов, кадр — из актуальной подложки, зоны — вживую
        var areasDest = _areas != null ? CacheDest(_areasGeo.Center, _areasGeo.Zoom, _areasGeo.Size) : default;
        if (cachedAreas && (_areas == null || !Covers(areasDest, size))) cachedAreas = false;
        if (cachedAreas && AreasFading && _areasPrev != null)
        {
            var prevDest = CacheDest(_areasPrevGeo.Center, _areasPrevGeo.Zoom, _areasPrevGeo.Size);
            if (!Covers(prevDest, size)) DrawCached(ctx, _base!, dest, size);
            DrawCached(ctx, _areasPrev, prevDest, size);
            using (ctx.PushOpacity(EaseOut(AreasFadeT))) DrawCached(ctx, _areas!, areasDest, size);
        }
        else if (cachedAreas) DrawCached(ctx, _areas!, areasDest, size);
        else DrawCached(ctx, _base!, dest, size);
        // кроссфейд закончился — прежний кадр зон больше не нужен (полноэкранный битмап с запасом)
        if (_areasPrev != null && !AreasFading) { _areasPrev.Dispose(); _areasPrev = null; }
        _areasShown = cachedAreas;
        Prof(0);
        if (cachedAreas) _pass = Pass.Dynamic;
        _useBakedTracks = cachedAreas;
        _hatchView = ViewWorld().Inflate(8 / Scale);
        DrawHeat(ctx);
        DrawActiveRegions(ctx, now);
        DrawExpectedRegions(ctx, now);
        Prof(1);
        DrawLocalZones(ctx, now);
        _pass = Pass.All;
        DrawPickedOutlines(ctx);
        Prof(2);

        _plan = BuildTrackPlan(now);
        DrawTracks(ctx, now);
        Prof(3);
        var tracked = _anim.Tracks.Where(v => v.Track.IsMoving || v.Track.Points.Count > 1).SelectMany(v => v.Track.Events).ToHashSet();
        var shown = _anim.Events.Select(v => v.Event).Where(e => !tracked.Contains(e) && e.Status != AlertStatus.Launch)
            .OrderBy(e => e == Selected || e == _hoverEvent).ThenBy(e => Severity(e)).ThenBy(e => e.Time).ToList();
        DrawLaunches(ctx, now);
        DrawEventMarkers(ctx, shown, now);
        Prof(4);
        DrawApproachLinks(ctx, now);
        DrawTrackHeads(ctx, now);
        FlushLabels(ctx);
        DrawRegionPicking(ctx, size);
        Prof(5);
        if (Vignette) DrawVignette(ctx, size);
        ProfX(0);
        ProfX(1);
        DrawSpider(ctx);
        DrawHud(ctx, size);
        ProfX(2);
        Prof(6);
        if (_tipEvent != null && _tipFade > 0.01)
            using (LayerAlpha(ctx, EaseOut(_tipFade))) DrawTooltip(ctx, _tipEvent, _tipPos, size);
        if (DebugTips != null)
            for (int i = 0; i < DebugTips.Count; i++)
                DrawTooltip(ctx, DebugTips[i], new Point(i == 0 ? 20 : size.Width / 2 - 40, i == 0 ? 20 : size.Height - 60), size);
    }

    private readonly NoiseLayer _noiseLayer;

    /// <summary>
    /// Помехи «аналогового экрана» — отдельный слой над картой (и её подписями, линейкой, подсказками — как настоящая
    /// развёртка). Рисунок помех меняется с частотой экрана, а запись кадра карты — дорогая (десятки миллисекунд на
    /// телефоне): у слоя своя запись, поэтому смена помех не перезаписывает карту.
    /// </summary>
    private sealed class NoiseLayer : Control
    {
        private readonly MapControl _map;
        public NoiseLayer(MapControl map) { _map = map; IsHitTestVisible = false; }

        public override void Render(DrawingContext ctx)
        {
            var m = _map;
            var size = Bounds.Size;
            double ease = m._signalLoss * m._signalLoss * (3 - 2 * m._signalLoss); // плавный вход и выход без рывков на краях
            // фон ретро-помех рисуется всегда, и при перетаскивании и масштабировании тоже: пропадающий на время движения
            // эффект выглядел как сбой. Дорогие части (слой-подложка) в обычном режиме не используются
            double retro = m._retro * m._retro * (3 - 2 * m._retro);
            SignalLoss.Draw(ctx, new Rect(0, 29, size.Width, Math.Max(0, size.Height - 29)), ease * m._heldSeverity, m._phase * 4, retro,
                SignalLoss.GpuBacked ? m.NoiseRate : 12);
        }
    }

    /// <summary>Отладка (--render-demo, RADAR_DEMO_TIP): всплывающие карточки этих событий рисуются сразу.</summary>
    public IReadOnlyList<AlertEvent>? DebugTips { get; set; }

    // ---- затенение по краям вьюпорта: мягкий спад к рамке, взгляд собирается в центре ----
    private static readonly Color VignetteColor = Color.FromRgb(0x05, 0x07, 0x0A);
    private static readonly IBrush VignetteTop = EdgeBrush(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative));
    private static readonly IBrush VignetteBottom = EdgeBrush(new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));
    private static readonly IBrush VignetteLeft = EdgeBrush(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));
    private static readonly IBrush VignetteRight = EdgeBrush(new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));

    private static IBrush EdgeBrush(RelativePoint from, RelativePoint to) => new ImmutableLinearGradientBrush(
    [
        new ImmutableGradientStop(0, Color.FromArgb(150, VignetteColor.R, VignetteColor.G, VignetteColor.B)),
        new ImmutableGradientStop(0.35, Color.FromArgb(70, VignetteColor.R, VignetteColor.G, VignetteColor.B)),
        new ImmutableGradientStop(1, Color.FromArgb(0, VignetteColor.R, VignetteColor.G, VignetteColor.B)),
    ], startPoint: from, endPoint: to);

    private RenderTargetBitmap? _vignette;
    private (Size Size, double Scale) _vignetteKey;

    /// <summary>
    /// Затенение краёв: четыре градиента растеризуются один раз на размер окна в растр, дальше кадр только
    /// выводит готовую картинку (градиенты во весь край в каждом кадре стоили ~7 мс).
    /// </summary>
    private void DrawVignette(DrawingContext ctx, Size size)
    {
        // верхний край — под полосой заголовка, остальные — по рамке окна
        const double top = 29;
        double d = Math.Clamp(Math.Min(size.Width, size.Height) * 0.09, 48, 96);
        double h = size.Height - top;
        if (size.Width < 2 * d || h < 2 * d) return;
        double sc = _textScaling > 0 ? _textScaling : 1;
        if (_vignette == null || _vignetteKey != (size, sc))
        {
            _vignette?.Dispose();
            var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * sc)), Math.Max(1, (int)Math.Ceiling(size.Height * sc)));
            _vignette = new RenderTargetBitmap(px, new Vector(96 * sc, 96 * sc));
            using (var vc = _vignette.CreateDrawingContext())
            {
                vc.FillRectangle(VignetteTop, new Rect(0, top, size.Width, d));
                vc.FillRectangle(VignetteBottom, new Rect(0, size.Height - d, size.Width, d));
                vc.FillRectangle(VignetteLeft, new Rect(0, top, d, h));
                vc.FillRectangle(VignetteRight, new Rect(size.Width - d, top, d, h));
            }
            _vignetteKey = (size, sc);
        }
        // середина растра пустая — выводим только четыре полосы краёв
        foreach (var r in (ReadOnlySpan<Rect>)[new Rect(0, top, size.Width, d), new Rect(0, size.Height - d, size.Width, d),
                     new Rect(0, top + d, d, h - 2 * d), new Rect(size.Width - d, top + d, d, h - 2 * d)])
            ctx.DrawImage(_vignette, new Rect(r.X * sc, r.Y * sc, r.Width * sc, r.Height * sc), r);
    }

    // ---- отладочный профиль кадра (только для --bench-map) ----
    public static bool Profiling;
    /// <summary>Фоновая перерисовка подложки и без окна (проверка в --camera-test).</summary>
    public static bool ForceBackgroundBase;
    /// <summary>Отладка (--render-ui): подписи пунктов на подложке и эмуляция наведения/клика в режиме выбора.</summary>
    public IReadOnlyList<BasemapRenderer.LabelHit> DebugLabels => _baseLabels;
    public Point DebugWorldToScreen(Point w) => WorldToScreen(w);
    public void DebugHover(Point p)
    {
        _hover = p;
        _pickPlace = PlaceLabelAt(p);
        _pickHover = _pickPlace == null ? RegionAtScreen(p) : null;
    }
    /// <summary>Отладка (--render-ui): наведение на отметку события с пунктом <paramref name="label"/> — карточка без анимации.</summary>
    public string DebugHoverEvent(string label)
    {
        foreach (var e in Events)
            foreach (var pt in e.Points)
            {
                if (!pt.Label.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                var at = WorldToScreen(Basemap.Project(pt.Pos));
                if (HitEvent(at) is not { } h) continue;
                _hover = at;
                _hoverEvent = _tipEvent = h;
                _tipPos = at;
                _tipFade = 1;
                _hl[h] = 1;
                return $"карточка: {h.Title} у «{pt.Label}» {at.X:0},{at.Y:0}";
            }
        return $"карточка: отметка «{label}» не найдена";
    }
    public double DebugBaseZoom => _baseZoom;
    public Task? DebugBaseJob => _baseJob;
    public Task? DebugAreasJob => _areasJob;
    public static readonly double[] ProfMs = new double[8];
    private static long _profT;
    public static readonly double[] ProfX_ = new double[3];
    private static long _profXT;
    private static void ProfX(int i)
    {
        if (!Profiling) return;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfX_[i] += System.Diagnostics.Stopwatch.GetElapsedTime(i == 0 ? _profT : _profXT, t).TotalMilliseconds;
        _profXT = t;
    }

    private static void Prof(int i)
    {
        if (!Profiling) return;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        if (i >= 0) ProfMs[i] += System.Diagnostics.Stopwatch.GetElapsedTime(_profT, t).TotalMilliseconds;
        _profT = t;
    }

    /// <summary>Выводит только видимую часть кэша (кэш почти втрое больше экрана из-за запаса по краям).</summary>
    private void DrawCached(DrawingContext ctx, Bitmap bmp, Rect dest, Size size)
    {
        double sc = _baseScaling > 0 ? _baseScaling : 1;
        double nw = bmp.PixelSize.Width / sc, nh = bmp.PixelSize.Height / sc;
        // без растяжения кэш выводится строго 1:1 по пиксельной сетке экрана: дробный сдвиг (после смены размера
        // окна, панорамирования) или размер, отличный на долю пикселя, размывали бы фильтрацией весь кадр
        if (Math.Abs(dest.Width - nw) < 2 && Math.Abs(dest.Height - nh) < 2)
            dest = new Rect(Math.Round(dest.X * sc) / sc, Math.Round(dest.Y * sc) / sc, nw, nh);
        var vis = dest.Intersect(new Rect(size));
        if (vis.Width <= 0 || vis.Height <= 0) return;
        double kx = bmp.PixelSize.Width / dest.Width, ky = bmp.PixelSize.Height / dest.Height;
        var src = new Rect((vis.X - dest.X) * kx, (vis.Y - dest.Y) * ky, vis.Width * kx, vis.Height * ky);
        ctx.DrawImage(bmp, src, vis);
    }

    public static double AreasMs; public static int AreasCount;

    /// <summary>Рисует копию подложки со статичным слоем зон поверх — с теми же центром, масштабом и запасом по краям.</summary>
    private void RenderAreas(DateTimeOffset now, bool crossfade = false)
    {
        long _at0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderAreasCore(now, crossfade); }
        finally { AreasMs += System.Diagnostics.Stopwatch.GetElapsedTime(_at0).TotalMilliseconds; AreasCount++; }
    }

    private void RenderAreasCore(DateTimeOffset now, bool crossfade)
    {
        _areasJobCts?.Cancel();
        if (_baseImage == null) return;
        _areasDirty = false;
        var job = RecordAreas(now) with { Seq = ++_areasSeq };
        using var bmp = RasterizeAreas(job, _baseImage);
        ApplyAreas(job, bmp, crossfade);
    }

    /// <summary>Записанный слой зон: команды Skia (SKPicture) и геометрия кадра, к которому он относится.</summary>
    private sealed record AreasJob(SkiaSharp.SKPicture Picture, PixelSize Px, (Point Center, double Zoom, Size Size) Geo, int BaseVersion, HashSet<int> Baked)
    {
        /// <summary>Порядковый номер записи — по нему видно, отражает ли слой состояние после анимации.</summary>
        public long Seq { get; init; }
    }

    private long _areasSeq, _areasFreshSeq;
    private bool _areasStale, _wasAreasAnimating;

    private sealed class AreasRecorder : Control
    {
        public Action<DrawingContext>? Draw;
        public override void Render(DrawingContext ctx) => Draw?.Invoke(ctx);
    }

    /// <summary>
    /// Запись слоя зон (UI-поток: объекты Avalonia и данные тревог). Сама запись дешёвая — дорогая растеризация
    /// (заливки, градиенты, штриховка) идёт потом в фоне.
    /// </summary>
    private AreasJob RecordAreas(DateTimeOffset now)
    {
        var px = _base!.PixelSize;
        var size = _baseSize;
        double mx = BasemapRenderer.MarginOf(size.Width, CacheMargin, _baseScaling), my = BasemapRenderer.MarginOf(size.Height, CacheMargin, _baseScaling);
        var full = new Size(size.Width + 2 * mx, size.Height + 2 * my);
        var (center, zoom) = (_center, _zoom);
        _center = _baseCenter;
        _zoom = _baseZoom;
        _cullMargin = Math.Max(mx, my);
        _hatchView = new Rect(ScreenToWorld(new Point(-mx, -my)), ScreenToWorld(new Point(size.Width + mx, size.Height + my)));
        _pass = Pass.Static;
        _shapes.Clear();
        var baked = new HashSet<int>();
        using var rec = new SkiaSharp.SKPictureRecorder();
        var canvas = rec.BeginRecording(new SkiaSharp.SKRect(0, 0, px.Width, px.Height), useRTree: true);
        try
        {
            foreach (var v in VisibleTracks())
                if (TrackBakeKey(v, now) is { } k) baked.Add(k);
            var prevBaked = new HashSet<int>(_bakedTracks);
            _bakedTracks.Clear();
            _bakedTracks.UnionWith(baked);
            var r = new AreasRecorder
            {
                Draw = actx =>
                {
                    using (actx.PushTransform(Matrix.CreateScale(px.Width / full.Width, px.Height / full.Height)))
                    using (actx.PushTransform(Matrix.CreateTranslation(mx, my)))
                    {
                        DrawActiveRegions(actx, now);
                        DrawExpectedRegions(actx, now);
                        DrawLocalZones(actx, now);
                        DrawTracks(actx, now);
                    }
                },
            };
            var bounds = new Rect(0, 0, px.Width, px.Height);
            r.Measure(bounds.Size);
            r.Arrange(bounds);
            Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(canvas, r, bounds, new Vector(96, 96));
            // до применения кадра на экране остаются прежние запечённые треки
            _bakedTracks.Clear();
            _bakedTracks.UnionWith(prevBaked);
        }
        finally
        {
            _center = center;
            _zoom = zoom;
            _cullMargin = 0;
            _pass = Pass.All;
            _shapes.Clear();
        }
        return new AreasJob(rec.EndRecording(), px, (_baseCenter, _baseZoom, _baseSize), _baseVersion, baked);
    }

    /// <summary>Растеризация записанного слоя поверх копии подложки (любой поток).</summary>
    private static SkiaSharp.SKBitmap RasterizeAreas(AreasJob job, SkiaSharp.SKImage baseImage)
    {
        int w = job.Px.Width, h = job.Px.Height;
        var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        // параллельно горизонтальными полосами: каждая воспроизводит запись только в своих строках
        int bands = h < 256 ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        int bandH = (h + bands - 1) / bands;
        nint ptr = bmp.GetPixels();
        int rowBytes = bmp.RowBytes;
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, band =>
        {
            int y0 = band * bandH, y1 = Math.Min(h, y0 + bandH);
            if (y1 <= y0) return;
            using var part = new SkiaSharp.SKBitmap();
            part.InstallPixels(new SkiaSharp.SKImageInfo(w, y1 - y0, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul), ptr + y0 * rowBytes, rowBytes);
            using var c = new SkiaSharp.SKCanvas(part);
            c.Translate(0, -y0);
            c.ClipRect(SkiaSharp.SKRect.Create(0, y0, w, y1 - y0));
            c.DrawImage(baseImage, SkiaSharp.SKRect.Create(w, h));
            c.DrawPicture(job.Picture);
            c.Flush();
        });
        return bmp;
    }

    private void ApplyAreas(AreasJob job, SkiaSharp.SKBitmap bmp, bool crossfade)
    {
        // прежний кадр остаётся для кроссфейда (если он уже был на экране)
        if (_areas != null && _areasShown && crossfade)
        {
            _areasPrev?.Dispose();
            _areasPrev = _areas;
            _areasPrevGeo = _areasGeo;
            _areasFadeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else _areas?.Dispose();
        // кадр непрозрачен (подложка залита морем): Opaque — вывод без смешивания, копированием строк
        _areas = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque, bmp.GetPixels(),
            new PixelSize(bmp.Width, bmp.Height), new Vector(96, 96), bmp.RowBytes);
        _areasGeo = job.Geo;
        _bakedTracks.Clear();
        _bakedTracks.UnionWith(job.Baked);
        _areasBaseVersion = job.BaseVersion;
        _areasStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (job.Seq >= _areasFreshSeq) _areasStale = false;
        job.Picture.Dispose();
    }

    // снимки подложки, которые ещё может читать фоновая растеризация зон
    private readonly List<SkiaSharp.SKImage> _retiredImages = [];

    private void RetireBaseImage()
    {
        if (_baseImage == null) return;
        _retiredImages.Add(_baseImage);
        _baseImage = null;
        DisposeRetired();
    }

    private void DisposeRetired()
    {
        if (_areasJob is { IsCompleted: false }) return;
        foreach (var i in _retiredImages) i.Dispose();
        _retiredImages.Clear();
    }

    private Task? _areasJob;
    private CancellationTokenSource? _areasJobCts;
    private bool _areasAgain;

    /// <summary>Перерисовка слоя зон в фоне: запись в UI-потоке, растеризация — в пуле потоков, подмена — в UI-потоке.</summary>
    private void RequestAreas(DateTimeOffset now, bool crossfade)
    {
        if (_areasJob is { IsCompleted: false })
        {
            // уже рисуется — после неё нарисуем ещё раз с актуальными данными
            if (crossfade) _areasAgain = true;
            return;
        }
        _areasDirty = false;
        _areasAgain = false;
        var job = RecordAreas(now) with { Seq = ++_areasSeq };
        var baseImage = _baseImage!;
        var cts = new CancellationTokenSource();
        _areasJobCts = cts;
        _areasStamp = System.Diagnostics.Stopwatch.GetTimestamp(); // плановое обновление не дублируется, пока идёт растеризация
        _areasJob = Task.Run(() =>
        {
            var bmp = RasterizeAreas(job, baseImage);
            Dispatcher.UIThread.Post(() =>
            {
                using (bmp)
                {
                    DisposeRetired();
                    if (cts.IsCancellationRequested) { job.Picture.Dispose(); if (_areasStale) _areasDirty = true; InvalidateVisual(); return; }
                    ApplyAreas(job, bmp, crossfade);
                    if (_areasAgain || _areasStale) _areasDirty = true;
                    InvalidateVisual();
                }
            }, DispatcherPriority.Render);
        });
    }

    /// <summary>Видимая область (с запасом при отрисовке в кэш) — для отсечения невидимых объектов.</summary>
    private Rect CullWorld() => ViewWorld().Inflate(_cullMargin / Scale);

    private BaseFrame CurrentFrame(Size size, double scaling)
    {
        var (center, zoom) = (_center, _zoom);
        // при отдалении подложка рисуется сразу для конечного масштаба и центра: её кадр крупнее окна на всех
        // промежуточных шагах — за жест одна перерисовка вместо синхронной на каждом выходе за край прежнего кадра
        if (ZoomOutGoal() is { } g) (center, zoom) = g;
        // подложка всегда в полном разрешении: половинная давала размытые подписи и границы на время жеста и после него
        const double res = 1;
        return new(center, zoom, size, scaling, CacheMargin, _showFront, _trackLabels, Borderline.Version)
            { KeepOut = _keepOut, KeepOutRects = _labelKeep, Resolution = res };
    }

    private bool Gesturing => _dragStart != null && _dragged || CameraAnimating;

    /// <summary>Конечные центр и масштаб идущего отдаления (колесо, кнопка, перелёт); null — камера не отдаляется.</summary>
    private (Point Center, double Zoom)? ZoomOutGoal()
    {
        if (!double.IsNaN(_zoomTarget) && _zoomTarget < _zoom - 0.05)
        {
            // точка под курсором остаётся на месте — центр после зума известен заранее
            var w = ScreenToWorld(_zoomAnchor);
            double s = Math.Pow(2, _zoomTarget);
            return (new Point(w.X - (_zoomAnchor.X - HalfW) / s, w.Y - (_zoomAnchor.Y - HalfH) / s), _zoomTarget);
        }
        if (_fly is { } f && f.ToZ < _zoom - 0.05) return (f.ToC, f.ToZ);
        return null;
    }

    private void RenderBaseSync(Size size, double scaling)
    {
        _baseJobCts?.Cancel();
        _baseDirty = false;
        using var res = BasemapRenderer.Render(CurrentFrame(size, scaling));
        ApplyBase(res);
    }

    /// <summary>Запускает перерисовку подложки в фоне (не более одной одновременно); готовый кадр подменяется в UI-потоке.</summary>
    private void RequestBase(Size size, double scaling)
    {
        if (_baseJob is { IsCompleted: false })
        {
            // текущая задача рисует уже неактуальный масштаб — отменяем, следующая начнётся после её выхода
            if (_baseJobFrame is { } jf && (Math.Abs(jf.Zoom - _zoom) > 0.75 || jf.Size != size)) _baseJobCts?.Cancel();
            return;
        }
        var frame = CurrentFrame(size, scaling);
        _baseJobFrame = frame;
        var cts = new CancellationTokenSource();
        _baseJobCts = cts;
        _wantStrict = false;
        _baseDirty = false; // запрос принят; новые изменения снова выставят флаг
        _baseJob = Task.Run(() =>
        {
            BasemapRenderer.Result res;
            try { res = BasemapRenderer.Render(frame, cts.Token, background: true); }
            catch (OperationCanceledException) { Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render); return; }
            catch (Exception ex)
            {
                // сбой отрисовки: флаг «устарела» уже снят при запуске — без него подложка больше не перезапросится
                System.Diagnostics.Debug.WriteLine($"basemap: {ex}");
                Dispatcher.UIThread.Post(() => { _baseDirty = true; InvalidateVisual(); }, DispatcherPriority.Background);
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                using (res)
                {
                    if (cts.IsCancellationRequested || res.Frame.Size != Bounds.Size) { InvalidateVisual(); return; }
                    ApplyBase(res);
                    InvalidateVisual();
                }
            }, DispatcherPriority.Render);
        });
    }

    private void ApplyBase(BasemapRenderer.Result res)
    {
        _baseLabels = res.Labels;
        var b = res.Bitmap;
        var bmp = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque, b.GetPixels(),
            new PixelSize(b.Width, b.Height), new Vector(96, 96), b.RowBytes);
        _base?.Dispose();
        _base = bmp;
        // неизменяемый снимок для фоновой растеризации слоя зон
        RetireBaseImage();
        // битмап больше не меняется: снимок делит с ним пиксели без копирования (Skia держит свою ссылку на буфер)
        b.SetImmutable();
        _baseImage = SkiaSharp.SKImage.FromBitmap(b);
        var f = res.Frame;
        _baseCenter = f.Center;
        _baseZoom = f.Zoom;
        _baseSize = f.Size;
        _baseScaling = f.Scaling;
        _baseRes = f.Resolution;
        _baseFrontVersion = f.FrontVersion;
        _baseVersion++;
    }

    private static bool Overlaps(List<Rect> placed, Rect r)
    {
        foreach (var p in placed) if (p.Intersects(r)) return true;
        return false;
    }

    // ---- обобщение подписей: общая для всех слоёв очередь с приоритетами ----
    // Подписи, плашки и выноски не рисуются сразу, а ставятся в очередь; в конце кадра раскладываются
    // по убыванию важности без наложений друг на друга и на значки, с бюджетом, зависящим от масштаба.

    private sealed class LabelReq
    {
        public required double Priority;
        public required Rect[] Candidates;
        public required Action<DrawingContext, int> Draw;
        public bool Force;
        public double Alpha = 1;
        /// <summary>Объект исчезает: подпись угасает на месте, но не занимает места в раскладке.</summary>
        public bool Leaving;
    }

    private readonly List<LabelReq> _labelQueue = [];
    private readonly List<Rect> _obstacles = [];
    /// <summary>Плашки регионов (из слоя зон): мировая точка левого верхнего угла и размер на экране.</summary>
    private readonly List<(Point World, Size Size)> _regionTagRects = [];
    private readonly List<(Rect Rect, AlertEvent Ev)> _regionTagHits = [];

    /// <summary>Сколько необязательных подписей помещается на масштабе (выбранное и наведённое — всегда).</summary>
    private int LabelBudget => _zoom < 5.6 ? 4 : _zoom < 6.3 ? 8 : _zoom < 7 ? 16 : _zoom < 8 ? 32 : 90;

    private void QueueLabel(double priority, Rect[] candidates, Action<DrawingContext, int> draw, bool force = false, double alpha = 1, bool leaving = false)
    {
        if (alpha < 0.02 && !force) return;
        _labelQueue.Add(new LabelReq { Priority = priority, Candidates = candidates, Draw = draw, Force = force, Alpha = alpha, Leaving = leaving });
    }

    private void QueueText(double priority, FormattedText t, Rect[] candidates, bool force = false, double alpha = 1, bool leaving = false) =>
        QueueLabel(priority, candidates, (c, i) => DrawHaloText(c, t, candidates[i].TopLeft), force, alpha, leaving);

    /// <summary>Место значка для раскладки подписей; значки исчезающих объектов места не занимают.</summary>
    private void AddObstacle(Point c, double r, bool leaving = false)
    {
        if (!leaving) _obstacles.Add(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
    }

    /// <summary>Событие угасает (снято с карты): раскладка строится так, будто его уже нет.</summary>
    private bool Leaving(AlertEvent ev) => _anim.Get(ev)?.Dying != null;

    /// <summary>
    /// Вытесняет ли событие текущего «главного» (в группе, районе, регионе): живое всегда важнее исчезающего —
    /// иначе по окончании угасания картинка перестраивается повторно.
    /// </summary>
    private bool Prefer(AlertEvent ev, bool sel, AlertEvent cur, bool curSel = false)
    {
        bool l = Leaving(ev), lc = Leaving(cur);
        if (l != lc) return lc;
        return sel || (!curSel && Severity(ev) > Severity(cur));
    }

    private void FlushLabels(DrawingContext ctx)
    {
        var placed = new List<Rect>(_obstacles.Count + _regionTagRects.Count + _labelQueue.Count);
        placed.AddRange(_obstacles);
        foreach (var (w, s) in _regionTagRects) placed.Add(new Rect(WorldToScreen(w), s));
        int n = 0, budget = LabelBudget;
        var view = new Rect(Bounds.Size).Inflate(40);
        var keep = new List<Rect>(_labelQueue.Count + _obstacles.Count);
        // раскладка — по конечному состоянию: появляющиеся подписи сразу занимают своё место, исчезающие
        // раскладываются последними и никого не сдвигают (иначе перестановка случилась бы после угасания)
        // сортировка устойчивая: при равной важности порядок не меняется от кадра к кадру (без мерцания)
        foreach (var r in _labelQueue.OrderBy(r => r.Leaving).ThenByDescending(r => r.Force).ThenByDescending(r => r.Priority))
        {
            if (r.Leaving)
            {
                int lp = Array.FindIndex(r.Candidates, c => !Overlaps(placed, c));
                if (lp < 0) lp = 0;
                using (LayerAlpha(ctx, r.Alpha)) r.Draw(ctx, lp);
                continue;
            }
            if (!r.Force && n >= budget) continue;
            int pick = -1;
            for (int i = 0; i < r.Candidates.Length; i++)
                if (!Overlaps(placed, r.Candidates[i])) { pick = i; break; }
            if (pick < 0) { if (!r.Force) continue; pick = 0; }
            var rect = r.Candidates[pick];
            if (rect.Intersects(view))
            {
                using (LayerAlpha(ctx, r.Alpha)) r.Draw(ctx, pick);
                n++;
            }
            placed.Add(rect.Inflate(2));
            keep.Add(rect);
        }
        keep.AddRange(_obstacles);
        foreach (var (w, s) in _regionTagRects) keep.Add(new Rect(WorldToScreen(w), s));
        UpdateLabelKeepOut(keep);
        LastLabelStats = (_labelQueue.Count, n, _plan.Hidden.Count, _plan.Forecast.Count, _regionTagRects.Count);
        _labelQueue.Clear();
        _obstacles.Clear();
    }

    // ---- верхний слой не перекрывается нижним: места подписей/значков уходят в подложку как запретные зоны ----

    private List<(Point World, Size Size)> _labelKeep = [];
    private long _labelKeepKey;
    private long _labelKeepStamp;
    /// <summary>
    /// После смены данных (на время проявления) запретные зоны подложки обновляются без задержки — подписи пунктов
    /// уступают место до анимации появления/исчезновения, а не после неё.
    /// </summary>
    private long _labelKeepUrgentUntil;

    /// <summary>
    /// Передаёт подложке прямоугольники подписей и значков текущего кадра: под ними она не рисует ни подписей
    /// пунктов, ни их точек. Подложка перерисовывается в фоне, поэтому — только при заметном изменении и не чаще раза в секунду.
    /// </summary>
    private void UpdateLabelKeepOut(List<Rect> rects)
    {
        if (_dragStart != null || CameraAnimating) return;
        var view = new Rect(Bounds.Size).Inflate(Math.Max(Bounds.Width, Bounds.Height) * CacheMargin);
        var list = new List<(Point, Size)>(rects.Count);
        long key = 17;
        foreach (var r in rects.Where(r => r.Intersects(view)).OrderBy(r => r.X).ThenBy(r => r.Y))
        {
            var w = ScreenToWorld(r.TopLeft);
            list.Add((w, r.Size));
            // квантование 8 px: дрожание подписей движущихся целей не перерисовывает подложку
            key = key * 31 + (long)Math.Round(w.X * Scale / 8);
            key = key * 31 + (long)Math.Round(w.Y * Scale / 8);
            key = key * 31 + (long)Math.Round(r.Width / 8) * 7 + (long)Math.Round(r.Height / 8);
        }
        key = key * 31 + (long)Math.Round(_zoom * 4);
        if (key == _labelKeepKey) return;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        bool urgent = t < _labelKeepUrgentUntil;
        bool first = _labelKeep.Count == 0 && list.Count > 0;
        if (!first && !urgent && System.Diagnostics.Stopwatch.GetElapsedTime(_labelKeepStamp, t).TotalSeconds < 1)
        {
            // не чаще раза в секунду: одна отложенная перерисовка к концу окна, а не повтор в каждом кадре
            // (повтор держал карту в непрерывной перерисовке, пока подписи движущихся целей сдвигались)
            if (!_labelKeepRetry)
            {
                _labelKeepRetry = true;
                var left = TimeSpan.FromSeconds(1) - System.Diagnostics.Stopwatch.GetElapsedTime(_labelKeepStamp, t);
                DispatcherTimer.RunOnce(() => { _labelKeepRetry = false; InvalidateVisual(); }, left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
            }
            return;
        }
        _labelKeepKey = key;
        _labelKeepStamp = t;
        _labelKeep = list;
        _baseDirty = true;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    private bool _labelKeepRetry;

    /// <summary>Отладка (--render-demo): подписей в очереди, нарисовано, скрытых «голов», треков с прогнозом, плашек регионов.</summary>
    public (int Queued, int Drawn, int MergedHeads, int Forecasts, int RegionTags) LastLabelStats;

    private static readonly Color SelTextColor = Color.Parse("#E8EDF3");
    private static readonly IBrush[] HoverTextSteps = BuildHoverSteps();

    private static IBrush[] BuildHoverSteps()
    {
        // 9 ступеней яркости: плавно за ~150 мс и без разрастания кэша текстов
        var a = ((ISolidColorBrush)MapTheme.CityText).Color;
        var r = new IBrush[9];
        for (int i = 0; i < r.Length; i++)
        {
            double k = i / 8.0;
            r[i] = new ImmutableSolidColorBrush(Color.FromRgb((byte)(a.R + (SelTextColor.R - a.R) * k), (byte)(a.G + (SelTextColor.G - a.G) * k), (byte)(a.B + (SelTextColor.B - a.B) * k)));
        }
        return r;
    }

    /// <summary>Цвет подписи по степени наведения/выделения 0..1 (плавный переход яркости).</summary>
    private static IBrush HoverText(double hl) => HoverTextSteps[(int)Math.Round(Math.Clamp(hl, 0, 1) * 8)];
    private static readonly Pen HaloPen = new(MapTheme.TextHalo, 3) { LineJoin = PenLineJoin.Round };
    // контур глифов строится один раз на текст (это дорого) и потом только сдвигается
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FormattedText, Geometry> HaloCache = new();

    private static double _textScaling = 1;

    private static void DrawHaloText(DrawingContext ctx, FormattedText t, Point at)
    {
        // по пиксельной сетке: при нечётной ширине окна центр экрана на полпикселя — текст иначе мылится
        at = new Point(Math.Round(at.X * _textScaling) / _textScaling, Math.Round(at.Y * _textScaling) / _textScaling);
        if (!HaloCache.TryGetValue(t, out var geo))
        {
            // обводка заранее превращена в заливаемый контур: заливка дешевле, чем обводка глифов каждый кадр
            var outline = t.BuildGeometry(default);
            geo = outline?.GetWidenedGeometry(HaloPen) ?? new StreamGeometry();
            HaloCache.AddOrUpdate(t, geo);
        }
        using (ctx.PushTransform(Matrix.CreateTranslation(at.X, at.Y))) ctx.DrawGeometry(MapTheme.TextHalo, null, geo);
        ctx.DrawText(t, at);
    }

    // ---------------- тревоги ----------------

    private static int Severity(AlertEvent e) => e.Status == AlertStatus.AllClear ? -1 : (int)e.EffectiveLevel * 10 + Severity(e.Threat);
    private static double Intensity(AlertEvent e) => e.Status == AlertStatus.AllClear ? 0.6 : ThreatCatalog.LevelIntensity(e.EffectiveLevel);

    private static int Severity(ThreatType t) => t switch
    {
        ThreatType.Missile => 5,
        ThreatType.Aviation => 4,
        ThreatType.Uav => 3,
        ThreatType.Naval => 2,
        ThreatType.Artillery => 1,
        _ => 0,
    };

    private Dictionary<string, AlertEvent> ActiveByRegion()
    {
        var active = new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
        foreach (var ev in Events.Where(e => e.Status != AlertStatus.AllClear).OrderBy(e => Severity(e)).ThenBy(e => e.Time))
            foreach (var r in ev.WholeRegionIds) active[r] = ev;
        return active;
    }

    // цвет и уровень заливки региона запоминаются: при угасании (отбой) регион докрашивается прежним цветом
    private readonly Dictionary<string, string> _regionColor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlertLevel> _regionLevel = new(StringComparer.Ordinal);

    /// <summary>
    /// Заливка области с учётом уровня: «внимание» — только штриховка и пунктирный контур,
    /// «опасность» — бледная заливка, «тревога» — полная, «удар» — усиленная с пульсирующим контуром.
    /// </summary>
    private void DrawLevelArea(DrawingContext ctx, BasemapFeature f, Paint p, AlertLevel lv, bool sel, double opacity, double s, bool dashedOutline)
    {
        double k = ThreatCatalog.LevelIntensity(lv);
        var geo = f.GeometryAt(s);
        var c = p.Color;
        // прозрачность «запечена» в кисти — без промежуточных слоёв
        if (DrawStatic)
        {
            double fade = sel ? 1 : AreaFade;
            if (lv != AlertLevel.Attention)
            {
                byte fa = sel || lv == AlertLevel.Strike ? (byte)60 : (byte)34;
                ctx.DrawGeometry(A(c, fa / 255.0 * Math.Min(1, k) * opacity * fade), null, geo);
            }
            // на обзорном масштабе штриховка сливается в серую рябь: остаётся только у «внимания» (у него нет заливки)
            if (!Overview || sel || lv == AlertLevel.Attention)
                DrawHatchArea(ctx, f, p.HatchColor, s, opacity * fade * (lv == AlertLevel.Attention ? 0.8 : 1));
            bool dash = dashedOutline || lv == AlertLevel.Attention;
            double w = (sel ? 1.8 : lv == AlertLevel.Strike ? 1.6 : 1.1) * (sel ? 1 : Math.Sqrt(fade)) / s;
            var stroke = A(c, opacity * (sel ? 1 : 0.5 + 0.5 * fade));
            var pen = dash ? new Pen(stroke, w, Dash(4, 2)) { LineJoin = PenLineJoin.Round } : new Pen(stroke, w) { LineJoin = PenLineJoin.Round };
            ctx.DrawGeometry(null, pen, geo);
        }
        if (lv == AlertLevel.Strike && DrawDynamic)
        {
            double ph = (_phase * 3) % 1.0;
            ctx.DrawGeometry(null, new Pen(A(c, (1 - ph) * 0.7 * opacity), (1.5 + 5 * ph) / s) { LineJoin = PenLineJoin.Round }, geo);
        }
    }

    /// <summary>Заливка регионов с тревогой — в мировых координатах, точно по границам подложки; плавно проявляется и гаснет.</summary>
    private void DrawActiveRegions(DrawingContext ctx, DateTimeOffset now)
    {
        foreach (var (r, ev) in ActiveByRegion()) { _regionColor[r] = ev.ColorHex; _regionLevel[r] = ev.EffectiveLevel; }
        var regions = _anim.Regions.ToHashSet(StringComparer.Ordinal);
        if (regions.Count == 0) return;

        var view = CullWorld();
        int lod = CurrentLod;
        double s = Scale;

        // регионы, где уровень только что повысился
        var regionUp = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var v in _anim.Events)
        {
            if (v.LevelUpAt == default || v.Event.Status == AlertStatus.AllClear) continue;
            foreach (var r in v.Event.WholeRegionIds)
                if (!regionUp.TryGetValue(r, out var cur) || v.LevelUpAt > cur) regionUp[r] = v.LevelUpAt;
        }

        using (ctx.PushTransform(WorldTransform()))
        {
            foreach (var id in regions)
            {
                if (Basemap.RegionById(id, lod) is not { } f || !f.Bounds.Intersects(view)) continue;
                double level = _anim.RegionLevel(f.Id);
                if (level < 0.01 || !_regionColor.TryGetValue(f.Id, out var hex)) continue;
                var p = P(hex);
                bool sel = Selected != null && Selected.WholeRegionIds.Contains(f.Id);
                var lv = _regionLevel.GetValueOrDefault(f.Id, AlertLevel.Alarm);
                DrawLevelArea(ctx, f, p, lv, sel, level, s, dashedOutline: false);
                if (DrawDynamic && regionUp.TryGetValue(f.Id, out var upAt)) DrawLevelUpOutline(ctx, f, p, s, upAt, now, level);
            }
        }
    }

    private bool _showExpected = true;

    private bool _showFront = true;

    /// <summary>Нет связи с источниками: поверх карты — эффект «потери сигнала» (обстановка устарела).</summary>
    public bool SignalLost
    {
        get => _signalLost;
        set
        {
            if (_signalLost == value) return;
            _signalLost = value;
            if (value)
            {
                // гасим наведение и жест, подсказка плавно исчезнет сама
                _hover = null;
                if (_hoverEvent != null) _areasDirty = true;
                _hoverEvent = null;
                _dragStart = null;
                _velocity = default;
                _radiusArea = null; _radiusHover = null; _pickHover = null; _pickPlace = null;
                _pickCursor = StandardCursorType.Arrow;
                Cursor = CursorOf(StandardCursorType.Arrow);
            }
            InvalidateVisual();
        }
    }
    private bool _signalLost;

    /// <summary>С какого момента обстановка не обновляется (последние данные); null — неизвестно, считаем «только что».</summary>
    public DateTimeOffset? SignalLostSince { get; set; }

    /// <summary>
    /// Сила помех по давности данных: сразу после обрыва — карта чёрно-белая и еле видна (80 %), дальше помехи нарастают
    /// (быстро в первые минуты, затем медленнее) и к получасу без данных картинка разрушена почти полностью.
    /// </summary>
    public static double SignalSeverity(TimeSpan stale)
    {
        double min = Math.Max(0, stale.TotalMinutes);
        return Math.Clamp(0.8 + 0.2 * Math.Log(1 + min / 1.5) / Math.Log(1 + 30 / 1.5), 0.8, 1);
    }

    /// <summary>Фон аналоговых помех (настройка «ретро-эффект»); переключается плавно.</summary>
    public bool RetroNoise { get => _retroTarget > 0.5; set { _retroTarget = value ? 1 : 0; InvalidateVisual(); } }
    /// <summary>Эффект потери сигнала при обрыве связи (без него карта при обрыве не меняется).</summary>
    public bool SignalLossEffect { get; set { field = value; InvalidateVisual(); } } = true;
    /// <summary>Затенение по краям вьюпорта.</summary>
    public bool Vignette { get; set { field = value; InvalidateVisual(); } } = true;
    private double _retroTarget = 1, _retro = 1;

    private double CurrentSeverity => SignalSeverity(SignalLostSince is { } t ? DateTimeOffset.UtcNow - t : TimeSpan.Zero);
    private double _signalLoss; // 0…1 — плавное нарастание/спад эффекта
    private long _noiseFrame;
    private double _heldSeverity = 0.8;

    /// <summary>Показывать линию соприкосновения (переключатель легенды; рисуется в подложке).</summary>
    public bool ShowFrontLine
    {
        get => _showFront;
        set
        {
            if (_showFront == value) return;
            _showFront = value;
            _baseDirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>Показывать регионы «ожидается» (переключатель легенды).</summary>
    public bool ShowExpected
    {
        get => _showExpected;
        set
        {
            if (_showExpected == value) return;
            _showExpected = value;
            _areasDirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Регионы-цели («пролёт в сторону Саратовской области»): слабая заливка с градиентом от стороны, откуда идёт
    /// угроза, пунктирный контур и подпись «ожидается». Регион с собственной тревогой не перекрывается.
    /// </summary>
    private void DrawExpectedRegions(DrawingContext ctx, DateTimeOffset now)
    {
        if (!DrawStatic || !_showExpected) return;
        var whole = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in _anim.Events)
            if (v.Event.Status != AlertStatus.AllClear) foreach (var r in v.Event.WholeRegionIds) whole.Add(r);
        var expected = new Dictionary<string, (AlertEvent Ev, double A, bool Sel)>(StringComparer.Ordinal);
        foreach (var v in _anim.Events)
        {
            var ev = v.Event;
            if (ev.Status == AlertStatus.AllClear || ev.ExpectedRegionIds.Count == 0) continue;
            double a = Alpha(ev, now);
            if (a < 0.02) continue;
            bool sel = ev == Selected || ev == _hoverEvent;
            foreach (var r in ev.ExpectedRegionIds)
            {
                if (whole.Contains(r)) continue;
                if (expected.TryGetValue(r, out var cur) && !Prefer(ev, sel, cur.Ev)) continue;
                expected[r] = (ev, Math.Max(a, cur.A), sel || cur.Sel);
            }
        }
        if (expected.Count == 0) return;
        var view = CullWorld();
        int lod = CurrentLod;
        double sc = Scale;
        using (ctx.PushTransform(WorldTransform()))
            foreach (var (id, d) in expected)
            {
                if (Basemap.RegionById(id, lod) is not { } f || !f.Bounds.Intersects(view)) continue;
                var paint = P(d.Ev.ColorHex);
                var src = d.Ev.Origin ?? d.Ev.Points.FirstOrDefault(p => !p.IsRegionLevel)?.Pos ?? d.Ev.Points.FirstOrDefault()?.Pos;
                var from = src is { } g ? Basemap.Project(g) : f.Label;
                var geo = f.GeometryAt(sc);
                // ровная лёгкая заливка + градиент со штриховкой от стороны, откуда идёт угроза
                double fade = d.Sel ? 1 : AreaFade;
                ctx.DrawGeometry(A(paint.Color, (d.Sel ? 0.14 : 0.08) * d.A * fade), null, geo);
                DrawAdjacentArea(ctx, f, paint, from, d.Sel, d.A * (d.Sel ? 1.0 : 0.85) * fade, sc);
                ctx.DrawGeometry(null, new Pen(A(paint.Color, (d.Sel ? 0.95 : 0.75) * d.A), (d.Sel ? 1.6 : 1.2) / sc, Dash(3, 3)) { LineJoin = PenLineJoin.Round }, geo);
            }
        // подпись — через общую очередь (динамический проход): уступает плашкам, маркерам и подписям целей
        if (_zoom < 4.5 || !DrawDynamic) return;
        foreach (var (id, d) in expected)
        {
            if (Basemap.RegionById(id, lod) is not { } f || !f.Bounds.Intersects(view)) continue;
            var t = Text("ожидается", 10, P(d.Ev.ColorHex).Solid, Medium);
            var c = WorldToScreen(f.Label);
            double w = t.Width, h = t.Height;
            QueueText(Priority(d.Ev, d.Sel) - 30, t,
                [new Rect(c.X - w / 2, c.Y + 9, w, h), new Rect(c.X - w / 2, c.Y - 9 - h, w, h), new Rect(c.X + 12, c.Y - h / 2, w, h), new Rect(c.X - 12 - w, c.Y - h / 2, w, h)],
                alpha: Math.Min(1, d.A + 0.2), leaving: Leaving(d.Ev));
        }
    }

    /// <summary>Пикселей на километр в точке (масштаб Меркатора зависит от широты).</summary>
    private double PxPerKm(GeoPoint at) => Scale * Basemap.WorldSize / (40075.0 * Math.Cos(at.Lat * Math.PI / 180));

    /// <summary>
    /// Локальные зоны опасности вокруг названных пунктов (без трека). Регион целиком не закрашивается.
    /// Радиус ограничен на экране, чтобы при приближении зона не превращалась в огромное пятно.
    /// </summary>
    private void DrawLocalZones(DrawingContext ctx, DateTimeOffset now)
    {
        var tracked = _anim.Tracks.Where(v => v.Track.IsMoving).SelectMany(v => v.Track.Events).ToHashSet();
        var view = CullWorld();
        int lod = CurrentLod;
        double sc = Scale;

        // 1. Районы. Роли (по убыванию силы): явно названный район → район, где находится названный пункт →
        //    смежный район («и близлежащие») — с градиентом от общей границы к дальней стороне.
        var districts = new Dictionary<string, (AlertEvent Ev, double A, bool Sel, DistrictRole Role, Point From)>(StringComparer.Ordinal);
        var partZones = new List<(BasemapFeature F, AlertEvent Ev, double A, bool Sel)>();
        void Put(string id, AlertEvent ev, double a, bool sel, DistrictRole role, Point from = default)
        {
            if (districts.TryGetValue(id, out var cur))
            {
                if (role < cur.Role) return;
                if (role == cur.Role && !Prefer(ev, sel, cur.Ev)) { if (sel) districts[id] = cur with { Sel = true }; return; }
                a = Math.Max(a, cur.A);
                sel |= cur.Sel;
            }
            districts[id] = (ev, a, sel, role, from);
        }
        foreach (var v in _anim.Events)
        {
            var ev = v.Event;
            if (ev.Status is AlertStatus.AllClear or AlertStatus.Launch) continue;
            double a = Alpha(ev, now);
            if (a < 0.02) continue;
            bool sel = ev == Selected || ev == _hoverEvent;
            bool moving = tracked.Contains(ev);
            var hosts = new List<string>();
            foreach (var p in ev.Points)
            {
                if (p.IsRegionLevel) continue;
                if (p.DistrictRegion is { } dr && Basemap.District(dr, p.Label, lod) is { } df)
                {
                    Put(df.Id, ev, a, sel, DistrictRole.Named);
                    hosts.Add(df.Id);
                }
                else if (p.PartRegion is { } pr && Basemap.PartShape(pr, p.PartBearing, lod) is { } pshape)
                {
                    // «южная часть области» — одна зона по внешнему контуру части, без внутренних границ районов
                    // и без подписи каждого района: уточнения по отдельным районам ложатся поверх неё
                    int pi = partZones.FindIndex(z => z.F.Id == pshape.Id);
                    if (pi < 0) partZones.Add((pshape, ev, a, sel));
                    else if (Prefer(ev, sel, partZones[pi].Ev)) partZones[pi] = (pshape, ev, Math.Max(a, partZones[pi].A), sel || partZones[pi].Sel);
                }
                else if (!moving && p.Corridor == null && p.WaterArea == null && Basemap.DistrictAt(p.Pos) is { } hid)
                {
                    Put(hid, ev, a, sel, DistrictRole.Host);
                    hosts.Add(hid);
                }
            }
            if (!ev.Nearby || moving) continue;
            foreach (var h in hosts.Distinct())
            {
                if (Basemap.DistrictById(h, lod) is not { } hf) continue;
                foreach (var n in Basemap.DistrictNeighbours(h))
                    if (!hosts.Contains(n)) Put(n, ev, a, sel, DistrictRole.Adjacent, hf.Label);
            }
        }
        if (partZones.Count > 0)
        {
            using (ctx.PushTransform(WorldTransform()))
                foreach (var (f, ev, a, sel) in partZones)
                {
                    if (!f.Bounds.Intersects(view)) continue;
                    var paint = P(ev.ColorHex);
                    // часть области — фон: названные районы внутри неё должны читаться поверх
                    bool inner = districts.Keys.Any(id => Basemap.DistrictById(id, lod) is { } df && f.Bounds.Contains(df.Label));
                    DrawLevelArea(ctx, f, paint, ev.EffectiveLevel, sel, a * (inner && !sel ? 0.55 : 1), sc, dashedOutline: true);
                    if (DrawDynamic) DrawLevelUpOutline(ctx, f, paint, sc, EventLevelUpAt(ev), now, a);
                }
            if (_zoom >= 6 && DrawDynamic)
                foreach (var (f, ev, a, sel) in partZones)
                {
                    if (!f.Bounds.Intersects(view)) continue;
                    var t = Text(f.Name, 11, P(ev.ColorHex).Solid, Medium);
                    var c = WorldToScreen(f.Label);
                    double w = t.Width, h = t.Height;
                    QueueText(Priority(ev, sel) - 35, t, [new Rect(c.X - w / 2, c.Y - h / 2, w, h), new Rect(c.X - w / 2, c.Y + 8, w, h), new Rect(c.X - w / 2, c.Y - 8 - h, w, h)], alpha: a, leaving: Leaving(ev));
                }
        }
        if (districts.Count > 0)
        {
            using (ctx.PushTransform(WorldTransform()))
                foreach (var (id, d) in districts.OrderBy(x => x.Value.Role))
                {
                    if (Basemap.DistrictById(id, lod) is not { } f || !f.Bounds.Intersects(view)) continue;
                    var paint = P(d.Ev.ColorHex);
                    switch (d.Role)
                    {
                        case DistrictRole.Named:
                            DrawLevelArea(ctx, f, paint, d.Ev.EffectiveLevel, d.Sel, d.A, sc, dashedOutline: true);
                            if (DrawDynamic) DrawLevelUpOutline(ctx, f, paint, sc, EventLevelUpAt(d.Ev), now, d.A);
                            break;
                        case DistrictRole.Host:
                            // район вокруг названного пункта — та же схема, но приглушённо (на обзоре — ещё слабее)
                            DrawLevelArea(ctx, f, paint, d.Ev.EffectiveLevel, d.Sel, d.A * (Overview && !d.Sel ? 0.4 : 0.62), sc, dashedOutline: true);
                            if (DrawDynamic) DrawLevelUpOutline(ctx, f, paint, sc, EventLevelUpAt(d.Ev), now, d.A * 0.8);
                            break;
                        default:
                            // «близлежащие» районы на обзоре — лишний слой пятен вокруг каждого пункта
                            if (DrawStatic && (!Overview || d.Sel)) DrawAdjacentArea(ctx, f, paint, d.From, d.Sel, d.A * Math.Min(1, Intensity(d.Ev)), sc);
                            break;
                    }
                }

            // подписи районов при достаточном приближении — через общую очередь (уступают плашкам и маркерам)
            if (_zoom >= 7 && DrawDynamic)
            {
                foreach (var (f, ev, a, sel) in districts.Where(d => d.Value.Role != DistrictRole.Adjacent)
                             .Select(d => (F: Basemap.DistrictById(d.Key, lod), d.Value.Ev, d.Value.A, d.Value.Sel)).Where(x => x.F != null)
                             .Select(x => (x.F!, x.Ev, x.A, x.Sel)))
                {
                    if (f.Bounds.Width * sc < 60 || !f.Bounds.Intersects(view)) continue;
                    var t = Text(f.Name, 10, P(ev.ColorHex).Solid, Medium);
                    var c = WorldToScreen(f.Label);
                    double w = t.Width, h = t.Height;
                    // крупный район важнее мелкого — подписи не прыгают от кадра к кадру
                    double prio = Priority(ev, sel) - 40 + Math.Min(5, f.Bounds.Width * sc / 200);
                    QueueText(prio, t, [new Rect(c.X - w / 2, c.Y - h / 2, w, h), new Rect(c.X - w / 2, c.Y + 6, w, h), new Rect(c.X - w / 2, c.Y - 6 - h, w, h)], alpha: a, leaving: Leaving(ev));
                }
            }
        }

        // 2. Пункты (и районы без контура) — круги. Для движущихся объектов зону не рисуем: её показывает трек.
        foreach (var v in _anim.Events)
        {
            var ev = v.Event;
            if (ev.Status is AlertStatus.AllClear or AlertStatus.Launch || tracked.Contains(ev)) continue;
            double a = Alpha(ev, now);
            if (a < 0.02) continue;
            var paint = P(ev.ColorHex);
            bool sel = ev == Selected || ev == _hoverEvent;
            foreach (var p in ev.Points)
            {
                if (p.IsRegionLevel) continue;
                if (HasArea(p, lod)) continue;
                if (p.Corridor is { Count: >= 2 } corr)
                {
                    // зона вдоль трассы: широкая мягкая полоса + осевая пунктирная линия
                    if (!DrawStatic) continue;
                    var pts = ToScreen(corr);
                    if (pts.Count < 2) continue;
                    double wpx = Math.Clamp(12 * PxPerKm(p.Pos), 8, 40);
                    var geo = Polyline(pts);
                    var lvz = ev.EffectiveLevel;
                    ctx.DrawGeometry(null, new Pen(A(paint.Color, (sel || lvz == AlertLevel.Strike ? 0.28 : 0.18) * a * Math.Min(1, Intensity(ev))), wpx * 2)
                        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
                    ctx.DrawGeometry(null, new Pen(A(paint.Color, 0.75 * a), sel ? 1.6 : 1.1, Dash(4, 3))
                        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
                    continue;
                }
                if (p.WaterArea is { Count: >= 3 } water)
                {
                    // зона над водой: часть акватории (без суши) — мягкая заливка и пунктирный контур
                    var wpts = ToScreen(water);
                    if (wpts.Count < 3) continue;
                    var wb = new Rect(wpts[0], wpts[0]);
                    foreach (var q in wpts) wb = wb.Union(new Rect(q, q));
                    if (!wb.Inflate(_cullMargin).Intersects(new Rect(Bounds.Size))) continue;
                    if (DrawDynamic) DrawLevelUpRings(ctx, ToScreen(p.Pos), Math.Clamp(p.RadiusKm * PxPerKm(p.Pos), 9, 90), paint, EventLevelUpAt(ev), now);
                    if (!DrawStatic) continue;
                    var wg = new StreamGeometry();
                    using (var gc = wg.Open())
                    {
                        gc.BeginFigure(wpts[0], true);
                        for (int i = 1; i < wpts.Count; i++) gc.LineTo(wpts[i]);
                        gc.EndFigure(true);
                    }
                    var lvw = ev.EffectiveLevel;
                    double fa = (sel || lvw == AlertLevel.Strike ? 0.30 : 0.18) * a * Math.Min(1, Intensity(ev));
                    // берег слоя ориентиров упрощён сильнее берега подложки: зона обрезается по нарисованной суше,
                    // чтобы не заходить на берег, косы и острова (Керчь, Тузла, отмели Сиваша)
                    Geometry shape = wg;
                    if (p.SeaId != null && WaterOnly(wg, wb, WaterKey(p.SeaId, wpts)) is { } clipped) shape = clipped;
                    ctx.DrawGeometry(A(paint.Color, fa), new Pen(A(paint.Color, 0.7 * a), sel ? 1.4 : 1.0, Dash(4, 3))
                        { LineJoin = PenLineJoin.Round }, shape);
                    continue;
                }
                var s = ToScreen(p.Pos);
                double r = Math.Clamp(p.RadiusKm * PxPerKm(p.Pos), 9, 90);
                double cm = r + _cullMargin;
                if (s.X < -cm || s.Y < -cm || s.X > Bounds.Width + cm || s.Y > Bounds.Height + cm) continue;
                if (DrawDynamic) DrawLevelUpRings(ctx, s, r, paint, EventLevelUpAt(ev), now);
                if (!DrawStatic) continue;
                var lv = ev.EffectiveLevel;
                var zone = (RadialGradientBrush)(sel || lv == AlertLevel.Strike ? paint.ZoneStrong : paint.Zone);
                var zb = new RadialGradientBrush { GradientStops = zone.GradientStops, Opacity = a * Math.Min(1, Intensity(ev)) };
                ctx.DrawEllipse(zb, null, s, r, r);
                var line = A(paint.Color, 170 / 255.0 * a);
                var zp = lv == AlertLevel.Attention ? new Pen(line, sel ? 1.1 : 0.8, Dash(3, 3)) : new Pen(line, sel ? 1.1 : lv == AlertLevel.Strike ? 1.3 : 0.7);
                ctx.DrawEllipse(null, zp, s, r, r);
            }
        }
    }

    /// <summary>
    /// Экранный контур зоны минус суша подложки (регионы РФ и соседних стран в рамке зоны).
    /// null — нечего вычитать или операция не удалась (рисуется исходный контур).
    /// </summary>
    private readonly Dictionary<(string, double, Point, int), Geometry?> _waterClipCache = [];

    private static int WaterKey(string seaId, IReadOnlyList<Point> pts)
    {
        var h = new HashCode();
        h.Add(seaId);
        foreach (var q in pts) { h.Add(Math.Round(q.X, 1)); h.Add(Math.Round(q.Y, 1)); }
        return h.ToHashCode();
    }

    private Geometry? WaterOnly(Geometry zone, Rect screenBounds, int shapeKey)
    {
        // одна и та же морская зона в том же кадре кэша — берём готовый результат (вычитание суши дорогое)
        var full = (FormattableString.Invariant($"{screenBounds.X:0.0},{screenBounds.Y:0.0},{screenBounds.Width:0.0},{screenBounds.Height:0.0}"), _zoom, _center, shapeKey);
        if (_waterClipCache.TryGetValue(full, out var cached)) return cached;
        if (_waterClipCache.Count > 64) _waterClipCache.Clear();
        var res = WaterOnlyCore(zone, screenBounds);
        _waterClipCache[full] = res;
        return res;
    }

    private Geometry? WaterOnlyCore(Geometry zone, Rect screenBounds)
    {
        var tl = ScreenToWorld(screenBounds.TopLeft);
        var br = ScreenToWorld(screenBounds.BottomRight);
        var world = new Rect(tl, br).Normalize();
        int lod = CurrentLod;
        Geometry? result = zone;
        bool any = false;
        foreach (var kind in new[] { FeatureKind.Region, FeatureKind.ForeignRegion })
            foreach (var f in Basemap.Of(kind, lod))
            {
                if (!f.Bounds.Intersects(world)) continue;
                var land = f.GeometryAt(Scale).Clone();
                land.Transform = new MatrixTransform(WorldTransform());
                result = new CombinedGeometry(GeometryCombineMode.Exclude, result, land);
                any = true;
            }
        return any ? result : null;
    }

    private enum DistrictRole { Adjacent, Host, Named }

    /// <summary>Точка рисуется контурами районов (район или часть региона), а не кругом/маркером.</summary>
    private static bool HasArea(MapPoint p, int lod) =>
        p.DistrictRegion is { } dr && Basemap.District(dr, p.Label, lod) != null ||
        p.PartRegion is { } pr && Basemap.PartShape(pr, p.PartBearing, lod) != null;

    /// <summary>
    /// Смежный район («и близлежащие»): заливка, штриховка и контур угасают от стороны, обращённой
    /// к району-источнику, к противоположной границе.
    /// </summary>
    private void DrawAdjacentArea(DrawingContext ctx, BasemapFeature f, Paint p, Point from, bool sel, double opacity, double s)
    {
        var b = f.Bounds;
        Vector dir = new(f.Label.X - from.X, f.Label.Y - from.Y);
        double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        dir = len < 1e-9 ? new Vector(1, 0) : dir / len;
        // протяжённость района вдоль направления «от источника»
        double min = double.MaxValue, max = double.MinValue;
        foreach (var part in f.Parts)
            foreach (var v in part)
            {
                double t = (v.X - f.Label.X) * dir.X + (v.Y - f.Label.Y) * dir.Y;
                if (t < min) min = t;
                if (t > max) max = t;
            }
        var a = f.Label + dir * min;
        var z = f.Label + dir * max;
        RelativePoint Rel(Point w) => new((w.X - b.X) / b.Width, (w.Y - b.Y) / b.Height, RelativeUnit.Relative);
        LinearGradientBrush Grad(double alpha) => new()
        {
            StartPoint = Rel(a),
            EndPoint = Rel(z),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)Math.Clamp(alpha * opacity, 0, 255), p.Color.R, p.Color.G, p.Color.B), 0),
                new GradientStop(Color.FromArgb((byte)Math.Clamp(alpha * 0.55 * opacity, 0, 255), p.Color.R, p.Color.G, p.Color.B), 0.35),
                new GradientStop(Color.FromArgb(0, p.Color.R, p.Color.G, p.Color.B), 1),
            },
        };
        if (opacity < 0.01) return;
        var geo = f.GeometryAt(s);
        ctx.DrawGeometry(Grad(sel ? 70 : 44), null, geo);
        // штриховка угасает вместе с заливкой: плиточная кисть под градиентной маской (маска — только по видимой части)
        var vis = b.Intersect(_hatchView);
        if (vis.Width > 0 && vis.Height > 0)
        {
            // слой маски — размером с видимую часть, а не со весь район (на крупном масштабе район в разы больше экрана)
            RelativePoint RelV(Point w) => new((w.X - vis.X) / vis.Width, (w.Y - vis.Y) / vis.Height, RelativeUnit.Relative);
            var mask = new LinearGradientBrush
            {
                StartPoint = RelV(a),
                EndPoint = RelV(z),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(140, 0, 0, 0), 0.35),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
                },
            };
            using (ctx.PushOpacityMask(mask, vis))
                DrawHatchArea(ctx, f, Color.FromArgb(70, p.Color.R, p.Color.G, p.Color.B), s, opacity);
        }
        ctx.DrawGeometry(null, new Pen(Grad(200), (sel ? 1.4 : 0.9) / s, Dash(3, 3)) { LineJoin = PenLineJoin.Round }, geo);
    }

    /// <summary>Видимая область в мировых координатах на время отрисовки кадра (ограничивает штриховку).</summary>
    private Rect _hatchView;

    /// <summary>
    /// Тайл штриховки (диагональ 45°) — один на цвет. Штриховка рисуется заливкой контура плиточной кистью:
    /// одна операция вместо сотен линий под маской-контуром.
    /// </summary>
    private readonly Dictionary<Color, RenderTargetBitmap> _hatchTiles = [];
    private const int HatchTilePx = 28;   // тайл = 7 экранных пикселей при масштабе экрана до 4×
    private const double HatchStep = 7;   // шаг штриховки на экране, px

    private IBrush HatchBrush(Color c, double scale)
    {
        if (!_hatchTiles.TryGetValue(c, out var tile))
        {
            tile = new RenderTargetBitmap(new PixelSize(HatchTilePx, HatchTilePx), new Vector(96, 96));
            using (var g = tile.CreateDrawingContext())
            {
                var pen = new Pen(new ImmutableSolidColorBrush(c), HatchTilePx / HatchStep) { LineCap = PenLineCap.Square };
                // линия по диагонали и её продолжения в соседних тайлах — без швов на стыках
                for (int k = -1; k <= 1; k++)
                    g.DrawLine(pen, new Point(k * HatchTilePx, HatchTilePx), new Point((k + 1) * HatchTilePx, 0));
            }
            _hatchTiles[c] = tile;
        }
        double step = HatchStep / scale;
        return new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            // привязка к мировым координатам: при сдвиге карты штриховка не «плывёт»
            DestinationRect = new RelativeRect(0, 0, step, step, RelativeUnit.Absolute),
        };
    }

    private void DrawHatchArea(DrawingContext ctx, BasemapFeature f, Color c, double scale, double opacity = 1)
    {
        if (!f.Bounds.Intersects(_hatchView) || opacity < 0.01) return;
        var brush = (ImageBrush)HatchBrush(c, scale);
        brush.Opacity = opacity;
        ctx.DrawGeometry(brush, null, f.GeometryAt(scale));
    }

    private double Alpha(AlertEvent ev, DateTimeOffset now)
    {
        double age = Math.Clamp((_dnow - ev.Time).TotalMinutes / 45.0, 0, 1);
        double a = ev.Status == AlertStatus.AllClear ? 0.6 : 1.0 - age * 0.4;
        // в кэше зон — конечное состояние (смену сглаживает кроссфейд кэша), напрямую — плавное
        if (_pass == Pass.Static) { if (Selected != null && Selected != ev) a *= 0.5; }
        else a *= 1 - 0.5 * _selAny * (1 - Hl(ev));
        var v = _anim.Get(ev);
        if (v != null) a *= MapAnimator.Presence(v.Born, v.Dying, now);
        return a;
    }

    private DateTimeOffset EventLevelUpAt(AlertEvent ev) =>
        _anim.Get(ev)?.LevelUpAt ?? default;

    /// <summary>«Ударная волна» при повышении уровня вокруг точки: два расходящихся кольца.</summary>
    private static void DrawLevelUpRings(DrawingContext ctx, Point c, double baseR, Paint p, DateTimeOffset at, DateTimeOffset now)
    {
        var (wa, wb) = MapAnimator.LevelUpWaves(at, now);
        foreach (var ph in new[] { wa, wb })
        {
            if (ph < 0) continue;
            double r = baseR + 46 * EaseOut(ph);
            ctx.DrawEllipse(null, new Pen(A(p.Color, (1 - ph) * 0.95), 2.6 * (1 - ph) + 0.6), c, r, r);
        }
    }

    /// <summary>«Ударная волна» по контуру области (в мировых координатах): утолщающаяся и гаснущая обводка + вспышка заливки.</summary>
    private void DrawLevelUpOutline(DrawingContext ctx, BasemapFeature f, Paint p, double s, DateTimeOffset at, DateTimeOffset now, double opacity)
    {
        var (wa, wb) = MapAnimator.LevelUpWaves(at, now);
        if (wa < 0 && wb < 0) return;
        var geo = f.GeometryAt(s);
        if (wa >= 0) ctx.DrawGeometry(A(p.Color, 60 / 255.0 * (1 - wa) * 0.35 * opacity), null, geo);
        foreach (var ph in new[] { wa, wb })
        {
            if (ph < 0) continue;
            ctx.DrawGeometry(null, new Pen(A(p.Color, (1 - ph) * 0.9 * opacity), (2 + 16 * EaseOut(ph)) / s) { LineJoin = PenLineJoin.Round }, geo);
        }
    }

    private double EventFlash(AlertEvent ev, DateTimeOffset now)
    {
        var v = _anim.Get(ev);
        return v == null ? 0 : MapAnimator.FlashLevel(v.LastUpdate, now);
    }

    // ---------------- траектории ----------------

    private static readonly Color Dark = Color.FromRgb(0x0B, 0x0E, 0x13);
    private static readonly IBrush DarkBrush = new SolidColorBrush(Dark).ToImmutable();

    private bool IsHighlighted(Track t) => (Selected != null && t.Events.Contains(Selected)) || (_hoverEvent != null && t.Events.Contains(_hoverEvent));

    private double TrackAlpha(TrackVisual v, DateTimeOffset now, bool final = false)
    {
        var t = v.Track;
        double a = t.StateAt(_dnow) switch
        {
            TrackState.Cleared => 0.35,
            TrackState.Stale => 0.6,
            _ => 1.0,
        };
        a *= 0.6 + 0.4 * t.Confidence;
        if (final || _pass == Pass.Static) { if (Selected != null && !IsHighlighted(t)) a *= 0.35; }
        else a *= 1 - 0.65 * _selAny * (1 - HlTrack(t));
        return a * MapAnimator.Presence(v.Born, v.Dying, now);
    }

    /// <summary>Конечная точка прогноза без маршрута планировщика: только заявленная цель (экстраполяции по курсу нет).</summary>
    private static GeoPoint? Projection(Track t, GeoPoint from, double bearing)
    {
        if (t.Cleared) return null;
        if (t.Target is { } target && Geo.DistanceKm(from, target) > 3) return target;
        // без цели прогноз — только маршрут планировщика: если он его не построил (шумный курс к линии пуска,
        // курс в море), своя экстраполяция по курсу вернула бы именно ту ошибку, от которой планировщик отказался
        return null;
    }

    /// <summary>Сглаженная кривая через точки (Catmull-Rom → кубические Безье).</summary>
    private static void AddSpline(StreamGeometryContext g, IReadOnlyList<Point> pts)
    {
        g.BeginFigure(pts[0], false);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(i - 1, 0)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(i + 2, pts.Count - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            g.CubicBezierTo(c1, c2, p2);
        }
        g.EndFigure(false);
    }

    private IEnumerable<TrackVisual> VisibleTracks() =>
        _anim.Tracks.Where(v => v.Track.IsMoving || v.Track.Points.Count > 1)
            .OrderBy(v => IsHighlighted(v.Track)).ThenBy(v => Severity(v.Track.Threat));

    /// <summary>Геометрия трека в экранных координатах.</summary>
    private sealed class TrackShape
    {
        public required List<Point> Confirmed;   // пройденный путь по подтверждённым точкам (вдоль ориентиров)
        public required Point LastFix;           // последняя подтверждённая точка
        public required Point Icon;              // расчётное текущее положение
        public Point? End;                       // конец прогноза (конец маршрута или заявленная цель)
        public bool HasTail;                     // нарисован синтетический «хвост» (одна точка)
        public required Vector Dir;              // единичный вектор курса на экране (у иконки)
        public List<Point> Pre = [];             // предыстория: от места пуска до первой точки
        public List<Point> Dead = [];            // счисление: от последней точки до иконки по маршруту
        public List<Point> Future = [];          // прогноз: от иконки до конца маршрута
        public Point? Launch;                    // место пуска
    }

    private List<Point> ToScreen(IEnumerable<GeoPoint> pts)
    {
        var res = new List<Point>();
        foreach (var g in pts)
        {
            var p = ToScreen(g);
            if (res.Count == 0 || Distance(res[^1], p) > 0.6) res.Add(p);
        }
        return res;
    }

    // геометрия треков считается один раз за кадр (её используют линии, «головы» и план обобщения)
    private readonly Dictionary<TrackVisual, TrackShape> _shapes = [];

    private TrackShape Shape(TrackVisual v)
    {
        if (_shapes.TryGetValue(v, out var s)) return s;
        return _shapes[v] = BuildShape(v);
    }

    /// <summary>
    /// План обобщения треков на кадр: близкие «головы» объединяются в одну иконку с числом объектов,
    /// прогноз (пунктир и конус) рисуется только у самых важных — остальные видны по пройденному пути.
    /// </summary>
    private sealed class TrackPlan
    {
        public readonly HashSet<TrackVisual> Forecast = [];
        public readonly HashSet<TrackVisual> Hidden = [];
        public readonly Dictionary<TrackVisual, (int Tracks, int Objects)> Groups = [];
        /// <summary>Лидер группы → все её треки (для веера).</summary>
        public readonly Dictionary<TrackVisual, List<TrackVisual>> Members = [];
    }

    private TrackPlan _plan = new();

    // на обзорном масштабе прогноз только у выделенного трека: видно «где летят», а не веер пунктиров
    private int ForecastBudget => _zoom < 5.6 ? 0 : _zoom < 6.3 ? 3 : _zoom < 7 ? 8 : _zoom < 8 ? 20 : int.MaxValue;
    private double HeadMergePx => _zoom < 5.6 ? 44 : _zoom < 6.3 ? 34 : _zoom < 7 ? 24 : 14;
    /// <summary>Обзорный масштаб: у треков без выделения — только пройденный путь и иконка.</summary>
    private bool Overview => _zoom < 6.3;
    /// <summary>Ослабление заливок областей и районов при отдалении: на обзоре главное — треки и метки, а не пятна.</summary>
    private double AreaFade => _zoom < 5.6 ? 0.5 : _zoom < 6.3 ? 0.65 : _zoom < 7 ? 0.85 : 1;

    private TrackPlan BuildTrackPlan(DateTimeOffset now)
    {
        var plan = new TrackPlan();
        var list = VisibleTracks().Where(v => TrackAlpha(v, now) >= 0.01)
            .Select(v => (V: v, P: Priority(v.Track))).OrderByDescending(x => x.P).ToList();
        var leaders = new List<(TrackVisual V, Point At)>();
        int budget = ForecastBudget;
        foreach (var (v, _) in list)
        {
            var at = Shape(v).Icon;
            bool hl = IsHighlighted(v.Track);
            int li = hl ? -1 : leaders.FindIndex(l => Distance(l.At, at) < HeadMergePx && !IsHighlighted(l.V.Track));
            if (li >= 0)
            {
                var lv = leaders[li].V;
                var g = plan.Groups[lv];
                plan.Groups[lv] = (g.Tracks + 1, g.Objects + v.Track.Count);
                plan.Members[lv].Add(v);
                plan.Hidden.Add(v);
                continue;
            }
            leaders.Add((v, at));
            plan.Groups[v] = (1, v.Track.Count);
            plan.Members[v] = [v];
            if (hl || plan.Forecast.Count < budget) plan.Forecast.Add(v);
        }
        return plan;
    }

    private TrackShape BuildShape(TrackVisual v)
    {
        var t = v.Track;
        // пройденный путь: участки между подтверждёнными точками (по ориентирам), во время прорисовки — до «головы»
        int full = Math.Min(t.Points.Count - 1, (int)Math.Floor(v.Reveal));
        bool revealing = v.Reveal < t.Points.Count - 1 - 1e-6;
        var path = new List<GeoPoint> { t.Points[0].Pos };
        for (int i = 0; i < full; i++)
        {
            var leg = i < t.Legs.Count && t.Legs[i].Count >= 2 ? t.Legs[i] : [t.Points[i].Pos, t.Points[i + 1].Pos];
            path.AddRange(leg.Skip(1));
        }
        if (revealing)
        {
            int i = full;
            var leg = i < t.Legs.Count && t.Legs[i].Count >= 2 ? t.Legs[i] : [t.Points[i].Pos, t.Points[Math.Min(i + 1, t.Points.Count - 1)].Pos];
            var (done, _) = Geo.Split(leg, (v.Reveal - i) * Geo.PathLength(leg));
            path.AddRange(done.Skip(1));
        }
        var confirmed = ToScreen(path);
        var lastFix = confirmed[^1];

        // счисление пути и прогноз — по маршруту трека (вдоль ориентиров, через промежуточные пункты)
        var dead = new List<Point>();
        var future = new List<Point>();
        Point icon;
        if (!revealing && t.FutureRoute.Count >= 2)
        {
            var (travelled, remaining) = Geo.Split(t.FutureRoute, Geo.DistanceKm(t.FutureRoute[0], v.Head) < 0.05 ? 0 : TravelledKm(t, v.Head));
            dead = ToScreen(travelled);
            future = ToScreen(remaining);
            icon = future.Count > 0 ? future[0] : ToScreen(v.Head);
        }
        else icon = revealing ? lastFix : ToScreen(v.Head);

        var br = v.Bearing * Math.PI / 180;
        var dir = new Vector(Math.Sin(br), -Math.Cos(br));
        // курс у иконки — по ближайшему участку прогноза
        for (int i = 1; i < future.Count; i++)
        {
            var d = future[i] - future[0];
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len > 6) { dir = d / len; break; }
        }
        Point? end = future.Count >= 2 ? future[^1] : null;
        if (end == null && !revealing && Projection(t, v.Head, v.Bearing) is { } eg) end = ToScreen(eg);

        bool tail = false;
        var pre = t.PreRoute.Count >= 2 ? ToScreen(t.PreRoute) : [];
        if (confirmed.Count == 1 && t.IsMoving && pre.Count < 2)
        {
            // одна точка и пуск неизвестен: короткий «хвост» с противоположной от курса стороны
            double len = 22 + 6 * Math.Clamp(_zoom - 6, 0, 3);
            confirmed.Insert(0, new Point(lastFix.X - dir.X * len, lastFix.Y - dir.Y * len));
            tail = true;
        }
        return new TrackShape
        {
            Confirmed = confirmed, LastFix = lastFix, Icon = icon, End = end, HasTail = tail, Dir = dir,
            Pre = pre, Dead = dead, Future = future, Launch = t.Launch is { } l && pre.Count >= 2 ? ToScreen(l) : null,
        };
    }

    /// <summary>Сколько км по маршруту прогноза от последней точки до анимированной «головы».</summary>
    private static double TravelledKm(Track t, GeoPoint head)
    {
        // ближайшая к голове точка маршрута (голова плавно догоняет расчётную позицию на маршруте)
        var r = t.FutureRoute;
        double best = double.MaxValue, at = 0, acc = 0;
        for (int i = 1; i < r.Count; i++)
        {
            double seg = Geo.DistanceKm(r[i - 1], r[i]);
            for (int k = 0; k <= 8; k++)
            {
                var p = Geo.Lerp(r[i - 1], r[i], k / 8.0);
                double d = Geo.DistanceKm(p, head);
                if (d < best) { best = d; at = acc + seg * k / 8.0; }
            }
            acc += seg;
        }
        return at;
    }

    private static StreamGeometry Polyline(IReadOnlyList<Point> pts)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(pts[0], false);
        for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
        g.EndFigure(false);
        return geo;
    }

    private readonly HashSet<int> _bakedTracks = [];
    private bool _useBakedTracks;

    /// <summary>
    /// Ключ неподвижной части трека (пройденный путь, предыстория, место пуска, прежние точки) для запекания в слой зон.
    /// null — трек сейчас анимируется (проявление, прорисовка, исчезновение) или у него «хвост», зависящий от курса.
    /// </summary>
    private int? TrackBakeKey(TrackVisual v, DateTimeOffset now)
    {
        var t = v.Track;
        if (v.Dying != null || now - v.Born < MapAnimator.FadeIn || v.Reveal < t.Points.Count - 1 - 1e-6) return null;
        if (t.Points.Count < 2 && t.PreRoute.Count < 2) return null;
        var h = new HashCode();
        h.Add(t.Id);
        h.Add(t.Points.Count);
        h.Add(t.Points[^1].Pos);
        h.Add(t.PreRoute.Count);
        if (t.PreRoute.Count > 0) h.Add(t.PreRoute[0]);
        h.Add(t.Legs.Sum(l => l.Count));
        h.Add(t.Launch);
        h.Add(t.LaunchExplicit);
        h.Add(t.Count);
        h.Add(t.Info.Color);
        // цель или курс из нового сообщения снимают «без курса» — запечённый вид (без линии) устаревает
        h.Add(t.UncertainHeading);
        h.Add(IsHighlighted(t));
        h.Add((int)Math.Round(TrackAlpha(v, now, final: true) * 200));
        return h.ToHashCode();
    }

    /// <summary>Совпадает ли набор запечённых треков с тем, что сейчас можно запечь (иначе слой зон устарел).</summary>
    private bool BakedTracksCurrent(DateTimeOffset now)
    {
        int n = 0;
        foreach (var v in VisibleTracks())
        {
            if (TrackBakeKey(v, now) is not { } k) continue;
            if (!_bakedTracks.Contains(k)) return false;
            n++;
        }
        return n == _bakedTracks.Count;
    }

    private void DrawTracks(DrawingContext ctx, DateTimeOffset now)
    {
        foreach (var v in VisibleTracks())
        {
            var t = v.Track;
            var paint = P(t.Info.Color);
            var c = paint.Color;
            bool hl = IsHighlighted(t);
            double width = 1.7 + 0.7 * (_pass == Pass.Static ? (hl ? 1 : 0) : HlTrack(t)) + Math.Min(1.0, Math.Log(t.Count) * 0.35);
            double alpha = TrackAlpha(v, now);
            if (alpha < 0.01) continue;
            // неподвижные части устоявшегося трека запечены в слой зон; каждый кадр рисуется только движущееся
            bool baked = TrackBakeKey(v, now) is { } bk && _bakedTracks.Contains(bk);
            bool staticOnly = _pass == Pass.Static;
            if (staticOnly && !baked) continue;
            bool st = staticOnly || !(baked && _useBakedTracks);
            bool dyn = !staticOnly;
            var state = t.StateAt(_dnow);
            var sh = Shape(v);
            bool forecast = _plan.Forecast.Contains(v);
            using var _ = LayerAlpha(ctx, alpha);

            // ---- 1. Коридор вероятного пути — мягкий конус от текущего положения к концу прогноза
            if (!dyn || !forecast) { }
            else if (sh.End is { } end && state != TrackState.Cleared && sh.Future.Count <= 2)
            {
                double len = Distance(sh.Icon, end);
                if (len > 20)
                {
                    var n = new Vector(-sh.Dir.Y, sh.Dir.X);
                    double w0 = 3, w1 = Math.Clamp(len * 0.16, 10, Math.Max(10, 26 * PxPerKm(v.Head) + 8));
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(sh.Icon + n * w0, true);
                        g.QuadraticBezierTo(sh.Icon + sh.Dir * len * 0.55 + n * w1 * 0.9, end + n * w1);
                        g.ArcTo(end - n * w1, new Size(w1, w1), 0, false, SweepDirection.CounterClockwise);
                        g.QuadraticBezierTo(sh.Icon + sh.Dir * len * 0.55 - n * w1 * 0.9, sh.Icon - n * w0);
                        g.EndFigure(true);
                    }
                    var cone = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(sh.Icon, RelativeUnit.Absolute),
                        EndPoint = new RelativePoint(end + sh.Dir * w1, RelativeUnit.Absolute),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(38, c.R, c.G, c.B), 0),
                            new GradientStop(Color.FromArgb(16, c.R, c.G, c.B), 0.75),
                            new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                        },
                    };
                    ctx.DrawGeometry(cone, null, geo);
                }
            }
            else if (sh.Future.Count > 2 && state != TrackState.Cleared)
            {
                // маршрут вдоль ориентира: мягкая широкая полоса под линией прогноза
                ctx.DrawGeometry(null, new Pen(Argb(28, c.R, c.G, c.B), Math.Clamp(10 * PxPerKm(v.Head), 6, 16))
                    { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Polyline(sh.Future));
            }

            // ---- 2. Предыстория от места пуска — тонкий редкий пунктир, тускло (на обзорном масштабе — только у выделенного)
            if (st && sh.Pre.Count >= 2 && (!Overview || hl))
            {
                var prePen = new Pen(Argb(t.LaunchExplicit ? (byte)120 : (byte)80, c.R, c.G, c.B), width * 0.7,
                    Dash(1.5, 4)) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                ctx.DrawGeometry(null, prePen, Polyline(sh.Pre));
            }

            // курс по двум отметкам не подтверждён (Track.UncertainHeading): линия между ними выглядела бы маршрутом — не рисуем
            bool noPath = t.UncertainHeading;

            // ---- 3. Пройденный путь — сплошная линия по подтверждённым точкам (вдоль ориентиров), без тени
            if (st && sh.Confirmed.Count >= 2 && !noPath)
            {
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(sh.Confirmed[0], RelativeUnit.Absolute),
                    EndPoint = new RelativePoint(sh.Confirmed[^1], RelativeUnit.Absolute),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(sh.HasTail ? (byte)0 : (byte)110, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(255, c.R, c.G, c.B), 1),
                    },
                };
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    if (t.Legs.Any(l => l.Count > 2)) { g.BeginFigure(sh.Confirmed[0], false); for (int i = 1; i < sh.Confirmed.Count; i++) g.LineTo(sh.Confirmed[i]); g.EndFigure(false); }
                    else AddSpline(g, sh.Confirmed);
                }
                ctx.DrawGeometry(null, new Pen(brush, width) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
            }

            // ---- 4. Счисление пути и прогноз — тот же цвет, вдвое тусклее, пунктир по маршруту
            if (dyn) DrawTrackForecast(ctx, v, sh, state, c, width, forecast);

            // ---- 5. Место пуска: кольцо с точкой (явное — ярче), подпись при приближении/выделении
            if (st && sh.Launch is { } lp && (!Overview || hl))
            {
                byte la = t.LaunchExplicit ? (byte)200 : (byte)120;
                var lb = Argb(la, c.R, c.G, c.B);
                ctx.DrawEllipse(null, new Pen(lb, 1.2, t.LaunchExplicit ? null : Dash(2, 2)), lp, 5, 5);
                ctx.DrawEllipse(lb, null, lp, 1.8, 1.8);
            }

            // ---- 6. Подтверждённые точки: ромбы (прежние — одной геометрией); свежая — вспышка
            int shown = Math.Min(t.Points.Count, (int)Math.Floor(v.Reveal) + 1);
            if (st && shown > 0)
            {
                int old = Math.Min(shown, t.Points.Count - 1);
                // прежние отметки на обзорном масштабе сливаются в рябь — путь виден по линии
                if (old > 0 && (!Overview || hl) && !noPath)
                {
                    var g = new StreamGeometry();
                    using (var x = g.Open())
                        for (int i = 0; i < old; i++) AddDiamond(x, ToScreen(t.Points[i].Pos), 2.8);
                    ctx.DrawGeometry(DarkBrush, new Pen(paint.Solid, 1.3), g);
                }
            }
            // без подтверждённого курса — пунктирная зона неточности на месте последней отметки
            // (центр района, «Старый Оскол, Губкин» — середина между пунктами): объект где-то здесь
            if (st && noPath && shown == t.Points.Count)
            {
                double r = Math.Clamp(Math.Max(t.Points[^1].SpreadKm, 8) * PxPerKm(v.Head), 10, 60);
                ctx.DrawEllipse(Argb(18, c.R, c.G, c.B),
                    new Pen(Argb(130, c.R, c.G, c.B), 1.1, Dash(2, 3)), ToScreen(t.Points[^1].Pos), r, r);
            }
            if (dyn && shown == t.Points.Count)
            {
                double fl = MapAnimator.FlashLevel(v.LastPulse, now);
                DrawDiamond(ctx, ToScreen(t.Points[^1].Pos), 3.6 + fl * 2.5, paint.Solid, new Pen(paint.Solid, 1.3));
            }
        }
    }

    /// <summary>
    /// Насколько уверенно рисовать прогноз: без измеренной скорости и при низкой достоверности — бледнее и реже,
    /// при надёжной — насыщеннее и плотнее. 0.55 / 0.75 / 1.
    /// </summary>
    private static double ForecastConfidence(Track t) => !t.HasMeasuredSpeed ? 0.55 : t.SpeedConfidence switch { 2 => 1.0, 1 => 0.75, _ => 0.55 };

    private void DrawTrackForecast(DrawingContext ctx, TrackVisual v, TrackShape sh, TrackState state, Color c, double width, bool forecast)
    {
        var t = v.Track;
        double conf = ForecastConfidence(t);
        // пройденный по счислению участок (от последней отметки до иконки) — обычной насыщенности
        var deadPen = new Pen(Argb(state == TrackState.Stale ? (byte)80 : (byte)140, c.R, c.G, c.B), width * 0.85, new DashStyle([2, 3], -(_phase * 20 % 5)))
            { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        // прогноз: насыщенность и плотность штриха по достоверности скорости (уверенный — плотный, грубый — редкий и бледный)
        double gap = conf >= 1 ? 2.5 : conf >= 0.75 ? 3.5 : 5;
        Pen? dimPen0 = null;
        Pen DimPen() => dimPen0 ??= new Pen(Argb((byte)((state == TrackState.Stale ? 80 : 150) * conf), c.R, c.G, c.B), width * (0.7 + 0.2 * conf),
                new DashStyle([2, gap], -(_phase * 20 % (2 + gap))))
            { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (sh.Dead.Count >= 2) ctx.DrawGeometry(null, deadPen, Polyline(sh.Dead));
        else if (Distance(sh.LastFix, sh.Icon) > 3) ctx.DrawLine(deadPen, sh.LastFix, sh.Icon);

        if (!forecast && state != TrackState.Cleared)
        {
            // на обзорном масштабе направление показывает сама иконка — «усы» только добавляют штрихов
            if (Overview) return;
            // обобщённый вид: только короткий «ус» по курсу вместо полного прогноза
            var tip = sh.Future.Count >= 2 ? SplitScreen(sh.Future, 26).Before : [sh.Icon, sh.Icon + sh.Dir * 26];
            var (_, whisker) = SplitScreen(tip, 10);
            if (whisker.Count >= 2) ctx.DrawGeometry(null, DimPen(), Polyline(whisker));
            return;
        }

        if (state != TrackState.Cleared)
        {
            bool pointTarget = t.HasPointTarget;
            if (t.TargetIsArea)
            {
                // цель — регион/район: известно лишь направление — реже и бледнее, к концу затухает, на конце шеврон
                var path = sh.Future.Count >= 2 ? SplitScreen(sh.Future, 10).After : sh.End is { } ea ? [sh.Icon + sh.Dir * 10, ea] : [];
                if (path.Count >= 2) DrawAreaDirection(ctx, path, c, width, state, conf);
            }
            else if (sh.Future.Count >= 2)
            {
                // начало прогноза — не под иконкой
                var (_, rest) = SplitScreen(sh.Future, 10);
                if (rest.Count >= 2) ctx.DrawGeometry(null, DimPen(), Polyline(rest));
                if (!pointTarget && sh.End is { } e3)
                    ctx.DrawEllipse(Argb(90, c.R, c.G, c.B), null, e3, 1.6, 1.6);
            }
            else if (sh.End is { } e2)
            {
                double len = Distance(sh.Icon, e2);
                if (len > 14)
                {
                    var from = sh.Icon + sh.Dir * 10;
                    ctx.DrawLine(DimPen(), from, e2 - sh.Dir * (pointTarget ? 6 : 0));
                    if (!pointTarget)
                        ctx.DrawEllipse(Argb(90, c.R, c.G, c.B), null, e2, 1.6, 1.6);
                }
            }
            // промежуточные пункты впереди
            foreach (var (pos, _) in t.Via)
            {
                var sp = ToScreen(pos);
                ctx.DrawEllipse(null, new Pen(Argb(150, c.R, c.G, c.B), 1.1), sp, 3, 3);
            }
        }
    }

    /// <summary>
    /// Направление к региону/району: редкий пунктир, затухающий к концу (градиент), и открытый шеврон на конце —
    /// «туда», а не «сюда»: точки назначения нет.
    /// </summary>
    private void DrawAreaDirection(DrawingContext ctx, List<Point> path, Color c, double width, TrackState state, double conf = 1)
    {
        byte a0 = (byte)((state == TrackState.Stale ? 60 : 105) * (0.6 + 0.4 * conf));
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(path[0], RelativeUnit.Absolute),
            EndPoint = new RelativePoint(path[^1], RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a0, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(a0 / 4), c.R, c.G, c.B), 1),
            },
        };
        ctx.DrawGeometry(null, new Pen(brush, width * 0.75, new DashStyle([1.5, 4.5], -(_phase * 24 % 6))) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
            Polyline(path));
        // шеврон по направлению последнего участка
        var end = path[^1];
        Vector d = default;
        for (int i = path.Count - 2; i >= 0; i--)
        {
            var dv = end - path[i];
            double l = Math.Sqrt(dv.X * dv.X + dv.Y * dv.Y);
            if (l > 4) { d = dv / l; break; }
        }
        if (d == default) return;
        var n = new Vector(-d.Y, d.X);
        var g = new StreamGeometry();
        using (var x = g.Open())
        {
            x.BeginFigure(end - d * 6 + n * 4.5, false);
            x.LineTo(end);
            x.LineTo(end - d * 6 - n * 4.5);
            x.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Argb((byte)(a0 * 0.8), c.R, c.G, c.B), 1.3) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
    }

    /// <summary>Делит экранную полилинию на расстоянии <paramref name="px"/> от начала.</summary>
    private static (List<Point> Before, List<Point> After) SplitScreen(IReadOnlyList<Point> pts, double px)
    {
        var before = new List<Point> { pts[0] };
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = Distance(pts[i - 1], pts[i]);
            if (acc + d >= px)
            {
                var cut = pts[i - 1] + (pts[i] - pts[i - 1]) * ((px - acc) / Math.Max(d, 1e-6));
                before.Add(cut);
                var after = new List<Point> { cut };
                for (int k = i; k < pts.Count; k++) after.Add(pts[k]);
                return (before, after);
            }
            acc += d;
            before.Add(pts[i]);
        }
        return (before, []);
    }

    private static void AddDiamond(StreamGeometryContext x, Point c, double r)
    {
        x.BeginFigure(new Point(c.X, c.Y - r), true);
        x.LineTo(new Point(c.X + r, c.Y));
        x.LineTo(new Point(c.X, c.Y + r));
        x.LineTo(new Point(c.X - r, c.Y));
        x.EndFigure(true);
    }

    private static void DrawDiamond(DrawingContext ctx, Point c, double r, IBrush fill, IPen pen)
    {
        var g = new StreamGeometry();
        using (var x = g.Open())
        {
            x.BeginFigure(new Point(c.X, c.Y - r), true);
            x.LineTo(new Point(c.X + r, c.Y));
            x.LineTo(new Point(c.X, c.Y + r));
            x.LineTo(new Point(c.X - r, c.Y));
            x.EndFigure(true);
        }
        ctx.DrawGeometry(fill, pen, g);
    }

    private void DrawTrackHeads(DrawingContext ctx, DateTimeOffset now)
    {
        var labelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in VisibleTracks().Reverse())
        {
            var t = v.Track;
            var paint = P(t.Info.Color);
            var c = paint.Color;
            bool hl = IsHighlighted(t);
            double alpha = TrackAlpha(v, now);
            if (alpha < 0.01) continue;
            bool leaving = v.Dying != null;
            var state = t.StateAt(_dnow);
            var sh = Shape(v);
            double prio = Priority(t);
            using var _ = LayerAlpha(ctx, alpha);

            // объединён с более важным соседом: только точка на месте объекта (иконку и подписи несёт лидер группы)
            if (_plan.Hidden.Contains(v))
            {
                ctx.DrawEllipse(paint.Solid, new Pen(DarkBrush, 1), sh.Icon, 2.6, 2.6);
                continue;
            }
            bool forecast = _plan.Forecast.Contains(v);
            var group = _plan.Groups.GetValueOrDefault(v, (1, t.Count));

            // ---- цель: кольцо + время до прибытия
            if (forecast && sh.End is { } end && t.HasPointTarget && state != TrackState.Cleared)
            {
                double conf = ForecastConfidence(t);
                var faded = Argb((byte)(150 * (0.5 + 0.5 * conf)), c.R, c.G, c.B);
                double pr = 5.5 + 1.2 * Math.Sin(_phase * Math.PI * 4);
                // низкая достоверность — кольцо цели пунктиром: время прибытия лишь ориентировочное
                ctx.DrawEllipse(null, new Pen(faded, 1.3, conf < 0.75 ? Dash(1.5, 2.5) : null), end, pr, pr);
                ctx.DrawEllipse(faded, null, end, 1.6, 1.6);
                AddObstacle(end, 6, leaving);
                if ((_zoom >= 6.8 || hl) && t.TargetLabel != null)
                {
                    var eta = t.EtaAt(_dnow);
                    string? txt = eta is { } et && et > TimeSpan.FromMinutes(1.5) ? t.EtaText(_dnow) : null;
                    if (txt != null)
                    {
                        var lbl = Text(txt, 10.5, Argb((byte)(220 * (0.6 + 0.4 * conf)), c.R, c.G, c.B), conf >= 0.75 ? Bold : Medium);
                        Rect At(double x, double y) => new(x, y, lbl.Width, lbl.Height);
                        QueueText(prio - 4, lbl, [At(end.X - lbl.Width / 2, end.Y + 9), At(end.X - lbl.Width / 2, end.Y - 9 - lbl.Height), At(end.X + 10, end.Y - lbl.Height / 2)], hl, alpha, leaving);
                    }
                }
            }

            // ---- место пуска и ориентир маршрута — подписи при приближении/выделении
            if ((hl || _zoom >= 7.4) && sh.Launch is { } lp && t.LaunchLabel != null)
            {
                var lt = Text((t.LaunchExplicit ? "пуск: " : "пуск? ") + t.LaunchLabel, 10, Argb(200, c.R, c.G, c.B), Medium);
                QueueText(prio - 12, lt, [new Rect(lp.X - lt.Width / 2, lp.Y + 8, lt.Width, lt.Height), new Rect(lp.X - lt.Width / 2, lp.Y - 8 - lt.Height, lt.Width, lt.Height)], hl, alpha, leaving);
            }
            if ((hl || _zoom >= 7.8) && t.CorridorLabel is { Length: > 0 } cl && sh.Confirmed.Count >= 2)
            {
                var mid = sh.Confirmed[sh.Confirmed.Count / 2];
                var lt = Text(cl, 9.5, Argb(190, c.R, c.G, c.B), Medium);
                QueueText(prio - 14, lt, [new Rect(mid.X - lt.Width / 2, mid.Y - lt.Height - 6, lt.Width, lt.Height), new Rect(mid.X - lt.Width / 2, mid.Y + 6, lt.Width, lt.Height)], hl, alpha, leaving);
            }

            // ---- подписи подтверждённых точек — на самих точках, без дублей с подложкой
            if (_zoom >= 7 || hl)
            {
                int shown = Math.Min(t.Points.Count, (int)Math.Floor(v.Reveal) + 1);
                for (int i = shown - 1; i >= 0; i--)
                {
                    var p = t.Points[i];
                    bool last = i == t.Points.Count - 1;
                    // сначала фильтр, потом «занято»: иначе непоказанная прежняя точка забирала имя у последней точки другого трека
                    if (string.IsNullOrEmpty(p.Label) || (!last && !hl) || !labelled.Add(p.Label)) continue;
                    var sp = ToScreen(p.Pos);
                    var lt = Text(p.Label, last ? 11 : 10, last ? MapTheme.CityText : MapTheme.MinorCityText, last ? Medium : Regular);
                    // подпись с противоположной от курса стороны, чтобы не пересекать линию
                    var left = new Rect(sp.X - lt.Width - 8, sp.Y - lt.Height / 2, lt.Width, lt.Height);
                    var right = new Rect(sp.X + 8, sp.Y - lt.Height / 2, lt.Width, lt.Height);
                    QueueText(prio - (last ? 6 : 16), lt, sh.Dir.X >= 0 ? [left, right] : [right, left], hl && last, alpha, leaving);
                }
            }

            // ---- иконка объекта
            var head = sh.Icon;
            double ang = Math.Atan2(sh.Dir.Y, sh.Dir.X) + Math.PI / 2;
            double size = 17 + 4 * HlTrack(t) + Math.Min(5, Math.Log(group.Item2) * 2);
            size *= 0.6 + 0.4 * MapAnimator.Presence(v.Born, null, now);
            size = Math.Round(size * 2) / 2; // шаг 0,5 px — ограничивает число спрайтов во время перехода
            AddObstacle(head, size * 0.5, leaving);

            if (t.Events.Any(IsMine)) DrawMineHalo(ctx, head, size / 2);
            DrawLevelUpRings(ctx, head, size / 2 + 2, paint, v.LevelUpAt, now);

            double fl = MapAnimator.FlashLevel(v.LastPulse, now);
            if (fl > 0)
            {
                double r = 10 + 30 * (1 - fl);
                ctx.DrawEllipse(null, new Pen(A(c, fl * 0.8), 1.6), head, r, r);
            }
            if (state == TrackState.Active)
            {
                double ph = (_phase * 1.2 + t.Id * 0.137) % 1.0;
                ctx.DrawEllipse(null, new Pen(A(c, (1 - ph) * 0.4), 1), head, 7 + 13 * EaseOut(ph), 7 + 13 * EaseOut(ph));
            }

            if (!MapIcons.IsDirectional(t.Info.Icon) || t.UncertainHeading) ang = 0;
            if (now - v.Born >= MapAnimator.FadeIn) DrawTrackIconCached(ctx, t.Info, paint, head, size, ang, state == TrackState.Stale);
            else using (ctx.PushTransform(MapIcons.Transform(head, size, ang)))
            {
                var icon = MapIcons.Get(t.Info);
                ctx.DrawGeometry(null, new Pen(DarkBrush, 3.4) { LineJoin = PenLineJoin.Round }, icon);
                if (state == TrackState.Stale)
                    ctx.DrawGeometry(DarkBrush, new Pen(paint.Solid, 1.5) { LineJoin = PenLineJoin.Round }, icon);
                else
                    ctx.DrawGeometry(paint.Solid, null, icon);
            }

            // ---- группа: число объектов в кружке у иконки (всегда — это главное на мелком масштабе)
            int objects = group.Item2;
            if (objects > 1)
            {
                var nt = Text(objects >= 10 ? "10+" : objects.ToString(CultureInfo.InvariantCulture), 9.5, MapTheme.CityText, Bold);
                double r = Math.Max(7.5, nt.Width / 2 + 4);
                var at = head + new Vector(size * 0.42, -size * 0.42);
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(paint.Solid, 1.4), at, r, r);
                ctx.DrawText(nt, new Point(at.X - nt.Width / 2, at.Y - nt.Height / 2));
                AddObstacle(at, r, leaving);
            }

            // ---- плашка: скорость и давность — по мере приближения
            if (_zoom >= 7 || hl)
            {
                var parts = new List<string>();
                // скорость по тем же двум отметкам, что и ненадёжный курс, — тоже шум привязки
                if (state != TrackState.Cleared && t.HasMeasuredSpeed && !t.UncertainHeading) parts.Add($"{Math.Round(t.SpeedKmh / 10) * 10:0} км/ч");
                if (state == TrackState.Stale && (_zoom >= 7.6 || hl)) parts.Add($"{(int)(_dnow - t.Updated).TotalMinutes} мин назад");
                if (parts.Count > 0) QueueBadge(head, sh.Dir, size, string.Join("  ·  ", parts), paint, hl, prio - 8, alpha, leaving);
            }
        }
    }

    /// <summary>Компактная плашка сбоку от иконки, перпендикулярно курсу (в общую раскладку).</summary>
    private void QueueBadge(Point head, Vector dir, double iconSize, string text, Paint paint, bool hl, double priority, double alpha, bool leaving = false)
    {
        var t = Text(text, 10.5, MapTheme.CityText, Medium);
        double w = t.Width + 14, h = t.Height + 4;
        // сначала — с той стороны, где плашка не перекрывает прогноз
        var n = new Vector(-dir.Y, dir.X);
        if (n.X < 0) n = -n;
        var anchor = head + n * (iconSize * 0.55 + 6);
        Rect[] rects =
        [
            new Rect(anchor.X, anchor.Y - h / 2, w, h),
            new Rect(head.X - w - iconSize * 0.55 - 6, head.Y - h / 2, w, h),
            new Rect(head.X - w / 2, head.Y + iconSize * 0.55 + 4, w, h),
        ];
        QueueLabel(priority, rects, (ctx, i) =>
        {
            var rect = rects[i];
            ctx.DrawRectangle(MapTheme.PanelBg, new Pen(hl ? paint.Solid : MapTheme.PanelBorder, 1), rect, 3, 3);
            ctx.DrawRectangle(paint.Solid, null, new Rect(rect.X, rect.Y + 3, 2, rect.Height - 6));
            ctx.DrawText(t, new Point(rect.X + 8, rect.Y + 2));
        }, hl, alpha, leaving);
    }

    private sealed class Cluster
    {
        public required Point Pos;
        public required AlertEvent Top;
        public readonly List<(AlertEvent Ev, string? Label)> Items = [];
    }

    /// <summary>Группирует близкие маркеры, чтобы точки не наслаивались друг на друга.</summary>
    private List<Cluster> BuildClusters(IEnumerable<AlertEvent> events)
    {
        var clusters = new List<Cluster>();
        // на мелком масштабе группы крупнее: один значок на район/город вместо россыпи
        double radius = _zoom < 5.6 ? 46 : _zoom < 6.3 ? 36 : _zoom < 7 ? 28 : 22;
        foreach (var ev in events)
        {
            foreach (var (pos, label) in MarkerPoints(ev, CurrentLod))
            {
                var s = ToScreen(pos);
                var c = clusters.FirstOrDefault(c => Distance(c.Pos, s) < radius);
                if (c == null)
                {
                    c = new Cluster { Pos = s, Top = ev };
                    clusters.Add(c);
                }
                else if (Prefer(ev, ev == Selected || ev == _hoverEvent, c.Top))
                {
                    c.Top = ev;
                }
                c.Items.Add((ev, label));
            }
        }
        return clusters;
    }

    /// <summary>
    /// Пуски: отметка в месте пуска (на стороне противника) — ромб с иконкой средства, расходящиеся «вспышки»,
    /// стрелка в сторону цели/курса (полупрозрачная, как прогноз) и подпись «Пуск · место». Регионы РФ не заливаются.
    /// </summary>
    private readonly HashSet<string> _fanLabelled = new(StringComparer.Ordinal);
    private readonly List<Rect> _fanBoxes = [];

    private void DrawLaunches(DrawingContext ctx, DateTimeOffset now)
    {
        _fanLabelled.Clear();
        _fanBoxes.Clear();
        var lp = P(ThreatCatalog.LaunchColor);
        foreach (var v in _anim.Events.Where(v => v.Event.Status == AlertStatus.Launch)
                     .OrderBy(v => v.Event == Selected || v.Event == _hoverEvent).ThenBy(v => v.Event.Time))
        {
            var ev = v.Event;
            if (ev.Origin is not { } o) continue;
            double a = Alpha(ev, now);
            if (a < 0.02) continue;
            bool sel = ev == Selected || ev == _hoverEvent;
            bool prep = ev.Level == AlertLevel.Attention;
            var at = ToScreen(o);
            var kind = P(ev.ColorHex);
            using var _ = LayerAlpha(ctx, a);

            DrawLaunchFan(ctx, o, LaunchRange.MaxKm(ev.Kind, ev.Kinds, ev.Threat), at, lp.Color, prep, sel, _anim.Get(ev)?.Born ?? ev.Time, now);

            // направление: к цели, иначе — по курсу из сообщения (стрелка ~70 px), полупрозрачно
            if (ev.Target is { } tg)
            {
                var to = ToScreen(tg);
                var d = to - at;
                double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (len > 18)
                {
                    var u = d / len;
                    double show = Math.Min(len, sel ? len : 160);
                    var end = at + u * show;
                    var pen = new Pen(A(lp.Color, prep ? 0.35 : 0.55), sel ? 2 : 1.6, new DashStyle([5, 4], -(_phase * 36 % 9))) { LineCap = PenLineCap.Round };
                    ctx.DrawLine(pen, at + u * 13, end);
                    var nrm = new Vector(-u.Y, u.X);
                    var head = new StreamGeometry();
                    using (var g = head.Open())
                    {
                        g.BeginFigure(end, true);
                        g.LineTo(end - u * 9 + nrm * 4.5);
                        g.LineTo(end - u * 9 - nrm * 4.5);
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(A(lp.Color, prep ? 0.45 : 0.75), null, head);
                }
            }

            // «вспышки» пуска: расходящиеся дуги (для подготовки — без анимации)
            if (!prep && DrawDynamic)
                for (int w = 0; w < 2; w++)
                {
                    double ph = Frac(_phase * 1.1 + w * 0.5 + at.X * 0.011);
                    ctx.DrawEllipse(null, new Pen(A(lp.Color, (1 - ph) * 0.6), 1.2), at, 9 + 16 * EaseOut(ph), 9 + 16 * EaseOut(ph));
                }

            // ромб с иконкой средства
            double r = 10 + 2 * Hl(ev);
            var dia = new StreamGeometry();
            using (var g = dia.Open())
            {
                g.BeginFigure(new Point(at.X, at.Y - r), true);
                g.LineTo(new Point(at.X + r, at.Y));
                g.LineTo(new Point(at.X, at.Y + r));
                g.LineTo(new Point(at.X - r, at.Y));
                g.EndFigure(true);
            }
            var border = prep ? new Pen(lp.Solid, 1.4, Dash(2.2, 1.6)) : new Pen(lp.Solid, 1.8);
            ctx.DrawGeometry(MapTheme.PanelBg, border, dia);
            using (ctx.PushTransform(MapIcons.Transform(at, r * 1.15, 0)))
                ctx.DrawGeometry(kind.Solid, null, MapIcons.Get(ev.Info));

            bool leaving = v.Dying != null;
            AddObstacle(at, r, leaving);
            var site = ev.Points.FirstOrDefault()?.Label ?? "";
            if (_zoom >= 5.5 || sel)
                QueueCallout(at, site, lp, sel, Priority(ev, sel) + (prep ? 0 : 15), prep ? "возможны пуски" : "пуск · " + ev.Info.Short, a, Hl(ev), leaving);
        }
    }

    private readonly record struct FanRay(double Bearing, double Km, string Name);
    private readonly Dictionary<(int, int, int), FanRay[]> _fanCache = [];

    /// <summary>
    /// Возможные направления пуска: по одному лучу на каждый из 8 секторов (С, СВ, В, …) — к ближайшему
    /// региону РФ в этом секторе (по ближайшей точке контура, не дальше дальности предполагаемого средства).
    /// </summary>
    private FanRay[] LaunchFan(GeoPoint o, double maxKm)
    {
        var key = ((int)Math.Round(o.Lat * 50), (int)Math.Round(o.Lon * 50), (int)Math.Round(maxKm));
        if (_fanCache.TryGetValue(key, out var cached)) return cached;
        var w = Basemap.Project(o);
        var best = new (double Km, double Bearing, string Name)?[8];
        foreach (var f in Basemap.Of(FeatureKind.Region, 0))
        {
            if (Gazetteer.IsForeign(f.Id) || Gazetteer.Get(f.Id) is not { } reg) continue;
            GeoPoint ng;
            if (Borderline.IsFrontRegion(f.Id))
            {
                // прифронтовой регион: контур включает и территорию противника (Краматорск лежит внутри контура ДНР),
                // поэтому цель — ближайшая точка на стороне РФ: контура региона или линии соприкосновения в нём
                var rings = f.Parts.Select(part => (IReadOnlyList<GeoPoint>)part.Select(q => Basemap.Unproject(q)).ToList());
                var onFront = Borderline.FrontLines.SelectMany(line => LaunchTargets.Sample(line))
                    .Where(q => Basemap.Project(q) is var pq && f.Bounds.Contains(pq));
                if (LaunchTargets.Nearest(o, rings, onFront, q => !Borderline.IsEnemySide(q, f.Id)) is not { } hit) continue;
                ng = hit.Pos;
            }
            else
            {
                // ближайшая точка контура (в мировых координатах — для поиска достаточно)
                Point near = default;
                double nd = double.MaxValue;
                foreach (var part in f.Parts)
                    for (int i = 0, j = part.Length - 1; i < part.Length; j = i++)
                    {
                        var p = ClosestOnSegment(w, part[j], part[i]);
                        double d = (p.X - w.X) * (p.X - w.X) + (p.Y - w.Y) * (p.Y - w.Y);
                        if (d < nd) { nd = d; near = p; }
                    }
                if (nd == double.MaxValue) continue;
                ng = Basemap.Unproject(near);
            }
            double km = Geo.DistanceKm(o, ng);
            if (km < 12 || km > maxKm) continue; // точка пуска в самом регионе / дальше, чем достаёт это средство
            // направление — между ближайшей точкой и центром региона: луч «входит» в регион, а не скользит по границе
            double bNear = Geo.Bearing(o, ng), bCenter = Geo.Bearing(o, reg.Center);
            double bearing = (bNear + Geo.AngleDiff(bNear, bCenter) * 0.35 + 360) % 360;
            int sector = (int)Math.Round(bearing / 45) % 8;
            if (best[sector] is not { } b || km < b.Km) best[sector] = (km, bearing, reg.Name);
        }
        var rays = best.Where(b => b != null).Select(b => new FanRay(b!.Value.Bearing, b.Value.Km, b.Value.Name)).ToArray();
        if (_fanCache.Count > 256) _fanCache.Clear();
        _fanCache[key] = rays;
        return rays;
    }

    private static Point ClosestOnSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double l2 = ab.X * ab.X + ab.Y * ab.Y;
        if (l2 < 1e-18) return a;
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / l2, 0, 1);
        return a + ab * t;
    }

    /// <summary>
    /// Веер едва заметных «карандашных» стрелок от места пуска к ближайшим регионам РФ: тонкая слегка
    /// дрожащая дуга в два штриха, плавное прорисовывание при появлении и бегущий по линии блик.
    /// </summary>
    private void DrawLaunchFan(DrawingContext ctx, GeoPoint o, double maxKm, Point at, Color c, bool prep, bool sel, DateTimeOffset born, DateTimeOffset now)
    {
        var rays = LaunchFan(o, maxKm);
        if (rays.Length == 0) return;
        double reveal = DrawDynamic ? EaseOut(Math.Clamp((now - born).TotalSeconds / 1.6, 0, 1)) : 1;
        double baseA = (prep ? 0.22 : 0.34) * (Overview ? 0.8 : 1) + (sel ? 0.18 : 0);
        int seed = (int)(Math.Abs(o.Lat * 1000 + o.Lon * 7919) % 997);
        for (int k = 0; k < rays.Length; k++)
        {
            var ray = rays[k];
            // луч заходит в регион на ~25 км, но не длиннее 440 км: показывает направление, а не маршрут
            double km = Math.Clamp(ray.Km + 25, 60, 440);
            var end = ToScreen(Geo.Destination(o, ray.Bearing, km));
            var d = end - at;
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len < 34) continue;
            var u = d / len;
            var n = new Vector(-u.Y, u.X);
            double bend = len * 0.07 * ((k + seed) % 2 == 0 ? 1 : -1);
            double start = 16, stop = len * (0.15 + 0.85 * reveal);
            if (stop <= start + 4) continue;

            // точки дуги с лёгким детерминированным дрожанием (рука, а не линейка)
            const int N = 22;
            Point Curve(double t, double jitter)
            {
                double s = start + (stop - start) * t;
                double f = s / len;
                double off = bend * 4 * f * (1 - f) + jitter * Math.Sin(f * 23 + seed + k * 1.7) * 0.6;
                return at + u * s + n * off;
            }
            void Stroke(double jitter, double shift, IBrush brush, double width, IDashStyle? dash)
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(Curve(0, jitter) + n * shift, false);
                    for (int i = 1; i <= N; i++) gc.LineTo(Curve(i / (double)N, jitter) + n * shift);
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(brush, width, dash) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
            }
            // основной штрих + второй, чуть смещённый и прерывистый — как повторный проход карандашом
            Stroke(1, 0, A(c, baseA), 0.9, null);
            Stroke(-1, 0.6, A(c, baseA * 0.55), 0.7, LaunchDash(seed % 7));

            // бегущий блик: короткий светлый отрезок, скользящий от пуска к региону
            if (DrawDynamic && reveal >= 1)
            {
                double ph = (_phase * 0.9 + k * 0.137 + seed * 0.01) % 1.0;
                double t0 = Math.Max(0, ph - 0.12), t1 = ph;
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(Curve(t0, 1), false);
                    for (int i = 1; i <= 5; i++) gc.LineTo(Curve(t0 + (t1 - t0) * i / 5, 1));
                    gc.EndFigure(false);
                }
                double fade = Math.Sin(ph * Math.PI);
                ctx.DrawGeometry(null, new Pen(A(c, baseA * 1.4 * fade), 1.3) { LineCap = PenLineCap.Round }, g);
            }

            // открытый «галочкой» наконечник по касательной к концу дуги
            var tip = Curve(1, 1);
            var dir = tip - Curve(0.93, 1);
            double dl = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (dl < 1e-6) continue;
            var tu = dir / dl;
            var tn = new Vector(-tu.Y, tu.X);
            var hp = new Pen(A(c, baseA * 1.15), 0.9) { LineCap = PenLineCap.Round };
            ctx.DrawLine(hp, tip, tip - tu * 7 + tn * 3.2);
            ctx.DrawLine(hp, tip, tip - tu * 6.4 - tn * 3.6);

            // подпись региона — мелко и бледно, только крупно или для выбранного пуска
            // и только один раз на регион за кадр: несколько пусков, нацеленных в одну область, давали стопку
            // одинаковых надписей, а стрелки и так показывают, куда идут
            if ((_zoom >= 7 || sel) && reveal >= 1 && _fanLabelled.Add(ray.Name))
            {
                var t = Text(ray.Name, 9.5, A(c, baseA * 1.3), Regular, italic: true);
                var lp = tip + tu * 6;
                var pos = new Point(lp.X - (tu.X < -0.3 ? t.Width : tu.X > 0.3 ? 0 : t.Width / 2), lp.Y - (tu.Y < -0.3 ? t.Height : tu.Y > 0.3 ? 0 : t.Height / 2));
                var box = new Rect(pos, new Size(t.Width, t.Height));
                // не поверх уже стоящей подписи другого региона
                if (_fanBoxes.Any(b => b.Intersects(box.Inflate(2)))) { _fanLabelled.Remove(ray.Name); continue; }
                _fanBoxes.Add(box);
                _obstacles.Add(box);
                DrawHaloText(ctx, t, pos);
            }
        }
    }

    private void DrawEventMarkers(DrawingContext ctx, IReadOnlyList<AlertEvent> events, DateTimeOffset now)
    {
        // плашки регионов — верхний слой: рисуются каждый кадр поверх зон, треков и стрелок (не в кэш зон)
        DrawRegionTags(ctx, events, now);

        _clusterHits.Clear();
        foreach (var c in BuildClusters(events).OrderBy(c => c.Items.Any(i => i.Ev == Selected || i.Ev == _hoverEvent)).ThenBy(c => Severity(c.Top)))
        {
            if (c.Items.Select(i => i.Ev).Distinct().Count() > 1) _clusterHits.Add((c.Pos, c.Items));
            var paint = P(c.Top.ColorHex);
            bool sel = c.Items.Any(i => i.Ev == Selected || i.Ev == _hoverEvent);
            double ca = c.Items.Max(i => Alpha(i.Ev, now));
            bool cLeaving = c.Items.All(i => Leaving(i.Ev));
            AddObstacle(c.Pos, 9, cLeaving);
            using var _ = LayerAlpha(ctx, ca);
            int count = c.Items.Select(i => i.Ev).Distinct().Count();
            bool live = c.Items.Any(i => i.Ev.Status != AlertStatus.AllClear);

            if (live && c.Items.Any(i => IsMine(i.Ev))) DrawMineHalo(ctx, c.Pos, 9);
            var topLevel = c.Items.Where(i => i.Ev.Status != AlertStatus.AllClear).Select(i => i.Ev.EffectiveLevel).DefaultIfEmpty(AlertLevel.Attention).Max();
            if (live && topLevel != AlertLevel.Attention)
            {
                // частота и размах пульса растут с уровнем
                double speed = topLevel switch { AlertLevel.Danger => 0.8, AlertLevel.Alarm => 1.4, _ => 2.6 };
                double amp = topLevel switch { AlertLevel.Danger => 12, AlertLevel.Alarm => 18, _ => 26 };
                int waves = topLevel == AlertLevel.Strike ? 2 : 1;
                for (int wv = 0; wv < waves; wv++)
                {
                    double ph = Frac(_phase * speed + c.Pos.X * 0.013 + wv * 0.5);
                    ctx.DrawEllipse(null, new Pen(A(paint.Color, (1 - ph) * (topLevel == AlertLevel.Danger ? 0.4 : 0.65)), topLevel == AlertLevel.Strike ? 1.6 : 1),
                        c.Pos, 6 + amp * EaseOut(ph), 6 + amp * EaseOut(ph));
                }
            }

            var clusterUp = c.Items.Select(i => EventLevelUpAt(i.Ev)).Max();
            DrawLevelUpRings(ctx, c.Pos, 8, paint, clusterUp, now);

            double flash = c.Items.Max(i => EventFlash(i.Ev, now));
            if (flash > 0)
            {
                double fr = 7 + 30 * (1 - flash);
                ctx.DrawEllipse(null, new Pen(A(paint.Color, flash * 0.9), 2), c.Pos, fr, fr);
            }

            if (count > 1)
            {
                var t = Text(count.ToString(CultureInfo.InvariantCulture), 10, MapTheme.CityText, Bold);
                double r = Math.Max(10, t.Width / 2 + 6);
                ctx.DrawEllipse(MapTheme.PanelBg, null, c.Pos, r, r);
                // кольцо-диаграмма: доля каждого типа угрозы в кластере
                var parts = c.Items.Select(i => i.Ev).Distinct().GroupBy(e => e.ColorHex).Select(g => (Color: g.Key, N: g.Count())).ToList();
                double a0 = -Math.PI / 2, total = parts.Sum(x => x.N);
                foreach (var (col, n) in parts)
                {
                    double a1 = a0 + 2 * Math.PI * n / total;
                    var arc = new StreamGeometry();
                    using (var g = arc.Open())
                    {
                        g.BeginFigure(new Point(c.Pos.X + r * Math.Cos(a0), c.Pos.Y + r * Math.Sin(a0)), false);
                        g.ArcTo(new Point(c.Pos.X + r * Math.Cos(a1 - 0.0001), c.Pos.Y + r * Math.Sin(a1 - 0.0001)), new Size(r, r), 0, a1 - a0 > Math.PI, SweepDirection.Clockwise);
                        g.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, new Pen(P(col).Solid, 2.6), arc);
                    a0 = a1;
                }
                ctx.DrawText(t, new Point(c.Pos.X - t.Width / 2, c.Pos.Y - t.Height / 2));
            }
            else if (c.Top.Status == AlertStatus.AllClear)
            {
                ctx.DrawEllipse(MapTheme.Sea, new Pen(paint.Solid, 1.6), c.Pos, 5, 5);
            }
            else
            {
                double hlc = c.Items.Max(i => Hl(i.Ev));
                DrawKindBadgeCached(ctx, c.Pos, c.Top.Info, paint, Math.Round((9.5 + 1.5 * hlc) * 4) / 4, c.Top.EffectiveLevel);
            }

            // подпись — самого важного пункта группы; «+N» вместо «и др.»; место — по общей раскладке
            var label = c.Items.Where(i => i.Ev == c.Top).Select(i => i.Label).FirstOrDefault(l => l != null)
                        ?? c.Items.Where(i => cLeaving || !Leaving(i.Ev)).Select(i => i.Label).FirstOrDefault(l => l != null);
            if (label != null && (_zoom >= 5.6 || sel))
            {
                int more = c.Items.Where(i => cLeaving || !Leaving(i.Ev)).Select(i => i.Label).Where(l => l != null).Distinct().Count() - 1;
                if (more > 0) label += $" +{more}";
                QueueCallout(c.Pos, label, paint, sel, Priority(c.Top, sel), alpha: ca, hl: c.Items.Max(i => Hl(i.Ev)), leaving: cLeaving);
            }
        }
    }

    /// <summary>Важность подписи: уровень и тип угрозы, затем свежесть (последние 15 мин — выше).</summary>
    private double Priority(AlertEvent ev, bool sel = false)
    {
        double fresh = Math.Clamp(1 - (_dnow - ev.Time).TotalMinutes / 15, 0, 1);
        return Severity(ev) * 10 + fresh * 8 + (sel ? 1000 : 0);
    }

    private double Priority(Track t)
    {
        double fresh = Math.Clamp(1 - (_dnow - t.Updated).TotalMinutes / 15, 0, 1);
        var state = t.StateAt(_dnow);
        // движущийся объект важнее неподвижной точки того же уровня
        int sev = t.Events.Count > 0 ? t.Events.Max(Severity) : Severity(t.Threat);
        return sev * 10 + 40 + fresh * 8 + Math.Min(6, t.Count) + (state == TrackState.Active ? 6 : 0) + (IsHighlighted(t) ? 1000 : 0);
    }

    /// <summary>Выноска (короткая линия и подпись) — в общую раскладку: вверх-вправо, вниз-вправо, вверх-влево, вниз-влево.</summary>
    private void QueueCallout(Point at, string label, Paint paint, bool sel, double priority, string? info = null, double alpha = 1, double hl = -1, bool leaving = false)
    {
        if (hl < 0) hl = sel ? 1 : 0;
        // размер и начертание не зависят от наведения — иначе текст «прыгает»; выделение — только яркостью
        var t = Text(label, 11, HoverText(hl), Medium);
        // вторая строка — цветом значка, чуть тусклее
        var it = info == null ? null : Text(info, 10.5, paint.Solid, Medium);
        double w = Math.Max(label.Length > 0 ? t.Width : 0, it?.Width ?? 0) + 4;
        double h = (label.Length > 0 ? t.Height : 0) + (it?.Height ?? 0);
        var elbows = new[] { new Point(at.X + 12, at.Y - 14), new Point(at.X + 12, at.Y + 14), new Point(at.X - 12, at.Y - 14), new Point(at.X - 12, at.Y + 14) };
        var rects = new Rect[4];
        for (int i = 0; i < 4; i++)
        {
            var e = elbows[i];
            double x = e.X > at.X ? e.X + 2 : e.X - 2 - w;
            double y = e.Y < at.Y ? e.Y - h - 1 : e.Y + 1;
            rects[i] = new Rect(x, y, w, h);
        }
        QueueLabel(priority, rects, (ctx, i) =>
        {
            var rect = rects[i];
            var elbow = elbows[i];
            var pen = new Pen(paint.Line, 1);
            ctx.DrawLine(pen, at, elbow);
            ctx.DrawLine(pen, elbow, new Point(elbow.X > at.X ? rect.Right : rect.Left, elbow.Y));
            double y = rect.Y;
            if (label.Length > 0) { DrawHaloText(ctx, t, new Point(rect.X + 2, y)); y += t.Height; }
            if (it != null) DrawHaloText(ctx, it, new Point(rect.X + 2, y));
        }, force: sel, alpha: alpha, leaving: leaving);
    }

    /// <summary>Регионы без конкретных пунктов — плашка в центре региона (тип и уровень угрозы).</summary>
    private void DrawRegionTags(DrawingContext ctx, IEnumerable<AlertEvent> events, DateTimeOffset now)
    {
        // одна плашка на регион: самое опасное (или выбранное) событие, остальные — счётчиком «+N»
        var groups = new Dictionary<string, (Point At, AlertEvent Top, int N, double A, bool Sel)>(StringComparer.Ordinal);
        foreach (var ev in events)
        {
            double a = Alpha(ev, now);
            if (a < 0.02) continue;
            bool sel = ev == Selected || ev == _hoverEvent;
            foreach (var p in ev.Points.Where(p => p.IsRegionLevel))
            {
                var key = p.RegionId ?? p.Label;
                if (groups.TryGetValue(key, out var g))
                {
                    bool better = Prefer(ev, sel, g.Top, g.Sel);
                    // счётчик «+N» — только живые события (исчезающее не учитывается заранее)
                    int n = g.N + (Leaving(ev) && !Leaving(g.Top) ? 0 : 1);
                    if (better && Leaving(g.Top) && !Leaving(ev)) n = 1;
                    groups[key] = (g.At, better ? ev : g.Top, n, Math.Max(g.A, a), g.Sel || sel);
                }
                else groups[key] = (ToScreen(p.Pos), ev, 1, a, sel);
            }
        }
        _regionTagRects.Clear();
        _regionTagHits.Clear();
        var placed = new List<Rect>();
        foreach (var g in groups.Values.OrderByDescending(g => g.Sel).ThenByDescending(g => Severity(g.Top)))
        {
            var at = g.At;
            if (at.X < -_cullMargin - 150 || at.Y < -_cullMargin - 60 || at.X > Bounds.Width + _cullMargin + 150 || at.Y > Bounds.Height + _cullMargin + 60) continue;
            // плашка видна при наведении/выделении и коротко мигает при появлении/повышении уровня; в покое — только значок
            double hl = Hl(g.Top), vis = Math.Max(hl, RegionTagBlink(g.Top, now));
            var paint = P(g.Top.ColorHex);
            AddObstacle(at, 9, Leaving(g.Top));
            if (IsMine(g.Top)) using (LayerAlpha(ctx, g.A)) DrawMineHalo(ctx, at, 9);
            var rect = RegionTagRect(at, g.Top, g.N);
            // плашки соседних регионов не наслаиваются: менее опасная уступает, а её значок остаётся виден
            bool tagFits = vis >= 0.02 && (g.Sel || !Overlaps(placed, rect));
            double badgeA = tagFits ? 1 - vis : 1;
            if (badgeA > 0.001)
                using (LayerAlpha(ctx, g.A * badgeA))
                    DrawKindBadgeCached(ctx, at, g.Top.Info, paint, 8.5, g.Top.EffectiveLevel);
            if (!tagFits) continue;
            placed.Add(rect.Inflate(4));
            // запретной зоной для подписей становится только устойчиво показанная плашка (не мигание)
            if (hl > 0.5 && !Leaving(g.Top)) _regionTagRects.Add((ScreenToWorld(rect.TopLeft), rect.Size));
            if (vis > 0.3) _regionTagHits.Add((rect, g.Top));
            using (LayerAlpha(ctx, g.A * vis))
                DrawRegionTag(ctx, at, g.Top, g.Sel, g.N);
        }
    }

    private const double TagBlinkPeriod = 0.8;
    private const int TagBlinkCount = 3;

    /// <summary>Мигание плашки региона после появления события или повышения уровня: три плавных вспышки и угасание.</summary>
    private double RegionTagBlink(AlertEvent ev, DateTimeOffset now)
    {
        if (_anim.Get(ev) is not { } v) return 0;
        var since = v.LevelUpAt > v.Born ? v.LevelUpAt : v.Born;
        double t = (now - since).TotalSeconds;
        if (t < 0 || t >= TagBlinkPeriod * TagBlinkCount) return 0;
        double s = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / TagBlinkPeriod);
        return Math.Sqrt(s);
    }

    /// <summary>На мелком масштабе плашка региона компактная: значок и тип угрозы, без строки статуса.</summary>
    private bool CompactRegionTags => _zoom < 5.6;

    private (FormattedText Title, FormattedText? Sub) RegionTagTexts(AlertEvent ev, int n)
    {
        var paint = P(ev.ColorHex);
        var title = ev.Info.Short.ToUpperInvariant() + (n > 1 ? $"  +{n - 1}" : "");
        var t = Text(title, 9.5, paint.Solid, Bold);
        if (CompactRegionTags) return (t, null);
        var sub = Text(ev.HasKinds ? $"{ev.StatusText} · {ev.KindsText}" : ev.StatusText, 9.5, LevelBrush(ev), ev.EffectiveLevel >= AlertLevel.Alarm ? Medium : Regular);
        return (t, sub);
    }

    private Rect RegionTagRect(Point s, AlertEvent ev, int n)
    {
        var (t, sub) = RegionTagTexts(ev, n);
        double icon = t.Height + (sub?.Height ?? 0);
        double w = Math.Max(t.Width, sub?.Width ?? 0) + 18 + icon + 6;
        double h = icon + 8;
        return new Rect(s.X - w / 2, s.Y - h / 2, w, h);
    }

    private void DrawRegionTag(DrawingContext ctx, Point s, AlertEvent ev, bool sel, int n = 1)
    {
        var paint = P(ev.ColorHex);
        var (t, sub) = RegionTagTexts(ev, n);
        // иконка высотой во все строки подписи
        double icon = t.Height + (sub?.Height ?? 0);
        var rect = RegionTagRect(s, ev, n);
        ctx.DrawRectangle(MapTheme.PanelBgSolid, new Pen(sel ? paint.Solid : MapTheme.PanelBorder, 1), rect);
        ctx.DrawRectangle(paint.Solid, null, new Rect(rect.X, rect.Y, 2, rect.Height));
        using (ctx.PushTransform(MapIcons.Transform(new Point(rect.X + 8 + icon / 2, rect.Center.Y), icon, 0)))
            ctx.DrawGeometry(paint.Solid, null, MapIcons.Get(ev.Info));
        double tx = rect.X + 14 + icon;
        ctx.DrawText(t, new Point(tx, rect.Y + 4));
        if (sub != null) ctx.DrawText(sub, new Point(tx, rect.Y + 4 + t.Height));
    }

    private static readonly Dictionary<AlertLevel, IBrush> LevelBrushes = new();
    private static IBrush LevelBrush(AlertEvent ev)
    {
        if (ev.Status == AlertStatus.AllClear) return MapTheme.HudText;
        var l = ev.EffectiveLevel;
        if (!LevelBrushes.TryGetValue(l, out var b))
            LevelBrushes[l] = b = new SolidColorBrush(Color.Parse(l == AlertLevel.Strike ? "#FF6B75" : ThreatCatalog.LevelBadgeFg(l)));
        return b;
    }

    // значок маркера (круг + векторная иконка) — дорогая геометрия; растеризуется один раз на вид и масштаб экрана
    private readonly Dictionary<(string Icon, uint Color, double R, AlertLevel Level, double Scaling), RenderTargetBitmap> _badgeSprites = [];

    private void DrawKindBadgeCached(DrawingContext ctx, Point at, ThreatInfo info, Paint paint, double r, AlertLevel level)
    {
        double scaling = _baseScaling > 0 ? _baseScaling : 1;
        bool strike = level is not (AlertLevel.Attention or AlertLevel.Danger or AlertLevel.Alarm);
        if (strike)
        {
            // мерцающие лучи «удара» анимируются — рисуются поверх спрайта
            double flick = 0.55 + 0.45 * Math.Abs(Math.Sin(_phase * Math.PI * 12));
            var rayPen = new Pen(A(paint.Color, flick), 1.8) { LineCap = PenLineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double ang = i * Math.PI / 4 + Math.PI / 8;
                var dir = new Vector(Math.Cos(ang), Math.Sin(ang));
                ctx.DrawLine(rayPen, at + dir * (r + 3), at + dir * (r + 7.5));
            }
        }
        var key = (info.Icon, paint.Color.ToUInt32(), r, level, scaling);
        double half = r + 5;
        if (!_badgeSprites.TryGetValue(key, out var bmp))
        {
            if (_badgeSprites.Count > 400)
            {
                foreach (var b in _badgeSprites.Values) b.Dispose();
                _badgeSprites.Clear();
            }
            int px = (int)Math.Ceiling(2 * half * scaling);
            bmp = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
            using (var bc = bmp.CreateDrawingContext())
            using (bc.PushTransform(Matrix.CreateScale(px / (2 * half), px / (2 * half))))
                DrawKindBadge(bc, new Point(half, half), info, paint, r, level);
            _badgeSprites[key] = bmp;
        }
        // по пиксельной сетке экрана — спрайт остаётся чётким
        double x = Math.Round((at.X - half) * scaling) / scaling, y = Math.Round((at.Y - half) * scaling) / scaling;
        ctx.DrawImage(bmp, new Rect(x, y, 2 * half, 2 * half));
    }

    private readonly Dictionary<(string Icon, uint Color, double Size, bool Stale, double Scaling, int Step), RenderTargetBitmap> _iconSprites = [];

    /// <summary>
    /// Иконка трека из спрайта: курс квантуется по 3° (поворот растра при выводе дороже самой векторной иконки),
    /// спрайт ставится по пиксельной сетке экрана без масштабирования — остаётся чётким.
    /// </summary>
    private void DrawTrackIconCached(DrawingContext ctx, ThreatInfo info, Paint paint, Point head, double size, double ang, bool stale)
    {
        double scaling = _baseScaling > 0 ? _baseScaling : 1;
        int step = (int)Math.Round(ang * 180 / Math.PI / 3) % 120;
        if (step < 0) step += 120;
        var key = (info.Icon, paint.Color.ToUInt32(), size, stale, scaling, step);
        double half = size * 0.72 + 3;
        if (!_iconSprites.TryGetValue(key, out var bmp))
        {
            if (_iconSprites.Count > 1500)
            {
                foreach (var b in _iconSprites.Values) b.Dispose();
                _iconSprites.Clear();
            }
            int px = (int)Math.Ceiling(2 * half * scaling);
            bmp = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
            using (var bc = bmp.CreateDrawingContext())
            using (bc.PushTransform(Matrix.CreateScale(px / (2 * half), px / (2 * half))))
            using (bc.PushTransform(MapIcons.Transform(new Point(half, half), size, step * 3 * Math.PI / 180)))
            {
                var icon = MapIcons.Get(info);
                bc.DrawGeometry(null, new Pen(DarkBrush, 3.4) { LineJoin = PenLineJoin.Round }, icon);
                if (stale) bc.DrawGeometry(DarkBrush, new Pen(paint.Solid, 1.5) { LineJoin = PenLineJoin.Round }, icon);
                else bc.DrawGeometry(paint.Solid, null, icon);
            }
            _iconSprites[key] = bmp;
        }
        double x = Math.Round((head.X - half) * scaling) / scaling, y = Math.Round((head.Y - half) * scaling) / scaling;
        ctx.DrawImage(bmp, new Rect(x, y, 2 * half, 2 * half));
    }

    /// <summary>Маркер точечной тревоги: тёмный круг с цветной каймой и силуэтом типа угрозы.</summary>
    private static void DrawKindBadge(DrawingContext ctx, Point at, ThreatInfo info, Paint paint, double r, AlertLevel level)
    {
        switch (level)
        {
            case AlertLevel.Attention:
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(paint.Solid, 1.4, Dash(2.2, 1.6)), at, r, r);
                break;
            case AlertLevel.Danger:
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(paint.Solid, 1.6), at, r, r);
                break;
            case AlertLevel.Alarm:
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(paint.Solid, 1.8), at, r, r);
                ctx.DrawEllipse(null, new Pen(paint.Line, 1), at, r + 3, r + 3);
                break;
            default:
                // «удар»: толстая кайма; мерцающие лучи анимируются и рисуются поверх спрайта (DrawKindBadgeCached)
                ctx.DrawEllipse(MapTheme.PanelBg, new Pen(paint.Solid, 2.6), at, r, r);
                break;
        }
        using (ctx.PushTransform(MapIcons.Transform(at, r * 1.45, 0)))
            ctx.DrawGeometry(paint.Solid, null, MapIcons.Get(info));
    }

    private static readonly IImmutableSolidColorBrush AllClearText = new ImmutableSolidColorBrush(Color.Parse("#9FD8BC"));

    private void DrawTooltip(DrawingContext ctx, AlertEvent ev, Point at, Size size)
    {
        const double pad = 14, maxW = 320, inner = maxW - 2 * pad;
        var paint = P(ev.ColorHex);
        // строка прибора, как в карточке ленты: тип угрозы слева, плашка уровня справа
        var lvl = Text(ev.StatusText.ToUpperInvariant(), 9.5, LevelBrush(ev), Bold);
        double lvlW = lvl.Width + 12;
        var head = Wrap(ev.ThreatText.ToUpperInvariant(), 10, paint.Solid, Bold, inner - lvlW - 10, 1);
        var kinds = ev.HasKinds ? Wrap(ev.KindsText, 10.5, MapTheme.MinorCityText, Regular, inner, 2) : null;
        var region = Wrap(ev.RegionNames, 13.5, MapTheme.CityText, Head, inner, 3, 18);
        var summary = ev.Summary;
        var body = summary.Length > 0 ? Wrap(summary, 11.5, MapTheme.HudText, Regular, inner, 5, 16) : null;
        var meta = Wrap($"{ev.TimeText}  ·  {ev.SourcesShort}", 10, MapTheme.MinorCityText, Regular, inner, 2, 14);
        var motion = ev.Motion is { Length: > 0 } mo ? Wrap(mo, 11, MapTheme.CityText, Medium, inner - 16, 2, 15) : null;
        var partial = ev.HasPartialClear
            ? Wrap("Отбой: " + string.Join(", ", ev.PartialClears.Select(c => c.RegionName)), 11, AllClearText, Medium, inner - 16, 3, 15)
            : null;

        // история уточнений: последние записи «время @канал — что добавил»
        var hist = new List<FormattedText>();
        FormattedText? histHead = null;
        if (ev.HasHistory)
        {
            const int maxRows = 6;
            var entries = ev.History.ToList();
            var rows = entries.Count > maxRows ? entries.Skip(entries.Count - maxRows).ToList() : entries;
            histHead = Text(entries.Count > maxRows ? $"ИСТОРИЯ · последние {maxRows} из {entries.Count}" : $"ИСТОРИЯ · {entries.Count}", 9, MapTheme.MinorCityText, Bold);
            foreach (var r in rows)
            {
                var prefix = $"{r.TimeText}  {r.ChannelText}  ";
                var ft = new FormattedText(prefix + r.Text.Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, 10.5, MapTheme.HudText)
                {
                    MaxTextWidth = inner, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, LineHeight = 14,
                };
                ft.SetForegroundBrush(MapTheme.MinorCityText, 0, prefix.Length);
                hist.Add(ft);
            }
        }

        // ширина — по самому широкому блоку (в пределах maxW), высота — точная сумма блоков с отступами
        const double gap = 6, tagPad = 8;
        double contentW = Math.Max(head.WidthIncludingTrailingWhitespace + lvlW + 10, region.WidthIncludingTrailingWhitespace);
        foreach (var f in (FormattedText?[])[kinds, body, meta, histHead])
            if (f != null) contentW = Math.Max(contentW, f.WidthIncludingTrailingWhitespace);
        foreach (var f in (FormattedText?[])[motion, partial])
            if (f != null) contentW = Math.Max(contentW, f.WidthIncludingTrailingWhitespace + 16 + tagPad);
        foreach (var f in hist) contentW = Math.Max(contentW, f.WidthIncludingTrailingWhitespace);
        double w = Math.Ceiling(Math.Clamp(contentW, 180, inner) + 2 * pad);

        double lvlH = lvl.Height + 4, headH = Math.Max(head.Height, lvlH);
        double h = 12 + headH + 6;
        if (kinds != null) h += kinds.Height + 2;
        h += region.Height;
        if (partial != null) h += gap + partial.Height + 6;
        if (motion != null) h += gap + motion.Height + 6;
        if (body != null) h += gap + body.Height;
        if (histHead != null) h += 10 + histHead.Height + 4 + hist.Sum(f => f.Height + 2);
        h += 10 + meta.Height + 11;

        double x = at.X + 16, y = at.Y + 16;
        if (x + w > size.Width - 8) x = at.X - w - 16;
        if (y + h > size.Height - 8) y = at.Y - h - 16;
        // высокая карточка у верхнего края / широкая у левого — не уходит за окно
        x = Math.Clamp(x, 8, Math.Max(8, size.Width - w - 8));
        y = Math.Clamp(y, 8, Math.Max(8, size.Height - h - 8));
        var rect = new Rect(Math.Round(x), Math.Round(y), w, Math.Ceiling(h));

        // стеклянная карточка: мягкая тень и размытие карты под ней, слева — полоса цвета угрозы, как в ленте
        ctx.DrawRectangle(null, null, rect, 0, 0, new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 24, Color = Color.FromArgb(120, 0, 0, 0) }));
        Glass.Draw(ctx, rect, 8, Color.FromArgb(205, 9, 12, 17), brackets: true, accent: paint.Color);
        ctx.DrawRectangle(paint.Solid, null, new Rect(rect.X, rect.Y + 8, 2, rect.Height - 16));
        double lx = rect.X + pad, rx = rect.Right - pad, cy = rect.Y + 12;

        ctx.DrawText(head, new Point(lx, cy + (headH - head.Height) / 2));
        var badge = new Rect(rx - lvlW, cy + (headH - lvlH) / 2, lvlW, lvlH);
        ctx.DrawRectangle(LevelBadgeBrush(ev), null, badge);
        ctx.DrawText(lvl, new Point(badge.X + 6, badge.Y + 2));
        cy += headH + 6;
        if (kinds != null) { ctx.DrawText(kinds, new Point(lx, cy)); cy += kinds.Height + 2; }
        ctx.DrawText(region, new Point(lx, cy)); cy += region.Height;

        if (partial != null)
        {
            cy += gap;
            DrawTag(partial, AllClearTagBg, AllClearTagLine, null);
        }
        if (motion != null)
        {
            cy += gap;
            DrawTag(motion, MotionTagBg, MotionTagLine, paint.Solid);
        }
        if (body != null) { cy += gap; ctx.DrawText(body, new Point(lx, cy)); cy += body.Height; }
        if (histHead != null)
        {
            cy += 5;
            ctx.DrawLine(new Pen(MapTheme.PanelBorder, 1), new Point(lx, cy + 0.5), new Point(rx, cy + 0.5));
            cy += 5;
            ctx.DrawText(histHead, new Point(lx, cy)); cy += histHead.Height + 4;
            foreach (var f in hist) { ctx.DrawText(f, new Point(lx, cy)); cy += f.Height + 2; }
        }
        cy += 5;
        ctx.DrawLine(new Pen(MapTheme.PanelBorder, 1), new Point(lx, cy + 0.5), new Point(rx, cy + 0.5));
        cy += 5;
        ctx.DrawText(meta, new Point(lx, cy));

        // плашка с полосой слева (отбой части, движение): как в карточке ленты
        void DrawTag(FormattedText t, IBrush bg, IBrush line, IBrush? arrow)
        {
            double tw = t.WidthIncludingTrailingWhitespace + 16 + tagPad, th = t.Height + 6;
            var r = new Rect(lx, cy, Math.Min(tw, rx - lx), th);
            ctx.DrawRectangle(bg, null, r);
            ctx.DrawRectangle(line, null, new Rect(r.X, r.Y, 1, r.Height));
            var mid = new Point(r.X + 9, r.Y + 3 + Math.Min(t.Height, 15) / 2);
            if (arrow != null)
                using (ctx.PushTransform(Matrix.CreateTranslation(mid.X - 5, mid.Y - 5)))
                    ctx.DrawGeometry(arrow, null, MotionArrow);
            else ctx.DrawEllipse(null, new Pen(AllClearDot, 1.4), mid, 3.5, 3.5);
            ctx.DrawText(t, new Point(r.X + 16 + tagPad / 2 - 2, r.Y + 3));
            cy += th;
        }
    }

    private static readonly IBrush AllClearTagBg = new ImmutableSolidColorBrush(Color.Parse("#13221C"));
    private static readonly IBrush AllClearTagLine = new ImmutableSolidColorBrush(Color.Parse("#3A7A5E"));
    private static readonly IBrush AllClearDot = new ImmutableSolidColorBrush(Color.Parse("#4FB286"));
    private static readonly IBrush MotionTagBg = new ImmutableSolidColorBrush(Color.Parse("#0F7CD4FF"));
    private static readonly IBrush MotionTagLine = new ImmutableSolidColorBrush(Color.Parse("#337CD4FF"));
    private static readonly Geometry MotionArrow = Geometry.Parse("M0 4 H7 V1 L11 5 L7 9 V6 H0 Z");
    private static readonly Dictionary<string, IBrush> BadgeBrushes = [];
    private static IBrush LevelBadgeBrush(AlertEvent ev)
    {
        var hex = ev.LevelBg;
        if (!BadgeBrushes.TryGetValue(hex, out var b)) BadgeBrushes[hex] = b = new ImmutableSolidColorBrush(Color.Parse(hex));
        return b;
    }

    /// <summary>Текст с переносом по ширине и многоточием после <paramref name="maxLines"/> строк.</summary>
    private readonly Dictionary<(string, double, uint, double, int, double, int, double), FormattedText> _wrapCache = [];

    /// <summary>Переносимый текст карточки; наведение держит одну карточку несколько секунд — разметка не строится заново каждый кадр.</summary>
    private FormattedText Wrap(string s, double size, IBrush brush, Typeface face, double width, int maxLines, double lineHeight = double.NaN)
    {
        var key = (s, size, brush is ISolidColorBrush sb ? sb.Color.ToUInt32() : (uint)brush.GetHashCode(), brush.Opacity, (int)face.Weight, width, maxLines, lineHeight);
        if (_wrapCache.TryGetValue(key, out var t)) return t;
        if (_wrapCache.Count > 256) _wrapCache.Clear();
        return _wrapCache[key] = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = width, MaxLineCount = maxLines, Trimming = TextTrimming.CharacterEllipsis, LineHeight = lineHeight,
        };
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>Дробная часть в [0, 1) и для отрицательных значений (у `%` знак делимого: точка левее окна давала фазу &lt; 0).</summary>
    private static double Frac(double x) => x - Math.Floor(x);

    // ---------------- HUD ----------------

    private void DrawHud(DrawingContext ctx, Size size)
    {
        var c = Basemap.Unproject(ScreenToWorld(new Point(size.Width / 2, size.Height / 2)));
        double metersPerPx = 156543.03 * Math.Cos(c.Lat * Math.PI / 180) / Scale;
        double[] steps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000];

        // справа внизу: линейка масштаба, за ней с небольшим отступом — координаты у правого края
        double right = size.Width - Obscured.Right, y = size.Height - 13 - Obscured.Bottom - HudLift; // низ — на уровне нижнего края легенды (отступ 12), над жестовой панелью
        var geo = _hover is { } h ? Basemap.Unproject(ScreenToWorld(h)) : c;
        var ct = Text(FormatGeo(geo), 10, MapTheme.HudText, Regular);
        double cx = right - 16 - ct.Width;
        ctx.DrawText(ct, new Point(cx, y - ct.Height + HudDescent(ct)));

        // длина линейки 40–110 и не левее кнопки легенды: самый крупный «круглый» шаг, который помещается
        var t1 = Text("2000 км", 10, MapTheme.HudText, Regular);
        double room = cx - 18 - t1.Width - 6 - Math.Max(0, HudLeftInset + 12);
        if (room < 36) return;
        double maxPx = Math.Min(room, 110);
        double km = steps.Where(k => k * 1000 / metersPerPx <= maxPx).DefaultIfEmpty(steps[0]).Max();
        double px = km * 1000 / metersPerPx;
        if (px < 18) return;
        t1 = Text($"{km:0} км", 10, MapTheme.HudText, Regular);
        double tx = cx - 18 - t1.Width, x = tx - 6 - px;
        var pen = new Pen(MapTheme.HudText, 1);
        ctx.DrawLine(pen, new Point(x, y), new Point(x + px, y));
        ctx.DrawLine(pen, new Point(x, y - 4), new Point(x, y));
        ctx.DrawLine(pen, new Point(x + px, y - 4), new Point(x + px, y));
        ctx.DrawText(t1, new Point(tx, y - t1.Height + HudDescent(t1)));
    }

    /// <summary>Расстояние от базовой линии до низа строки: текст ставим базовой линией на линию линейки.</summary>
    private static double HudDescent(FormattedText t) => t.Height - t.Baseline;

    private static string FormatGeo(GeoPoint p) => string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(p.Lat):F3}°{(p.Lat >= 0 ? "N" : "S")}   {Math.Abs(p.Lon):F3}°{(p.Lon >= 0 ? "E" : "W")}");

    private FormattedText Text(string s, double size, IBrush brush, Typeface face, bool italic = false)
    {
        // прозрачность кисти — отдельно от цвета (A() задаёт её через Opacity): без неё в ключе текст брал чужую прозрачность
        var key = (s, size, brush is ISolidColorBrush sb ? sb.Color.ToUInt32() : (uint)brush.GetHashCode(), brush.Opacity, (int)face.Weight, italic);
        if (_textCache.TryGetValue(key, out var t)) return t;
        if (_textCache.Count > 20000) _textCache.Clear();
        var tf = italic ? new Typeface(face.FontFamily, FontStyle.Italic, face.Weight) : face;
        t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush);
        _textCache[key] = t;
        return t;
    }

    /// <summary>Кисть цвета с прозрачностью — вместо PushOpacity (каждый слой прозрачности — отдельный offscreen-буфер).</summary>
    // кисти рисуются десятки раз за кадр: неизменяемые и общие, ключ — цвет и непрозрачность с шагом 1/255
    private static readonly Dictionary<(uint, int), IBrush> _brushes = [];

    private static IBrush A(Color c, double opacity)
    {
        int o = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var key = (c.ToUInt32(), o);
        if (_brushes.TryGetValue(key, out var b)) return b;
        if (_brushes.Count > 4096) _brushes.Clear();
        return _brushes[key] = new ImmutableSolidColorBrush(c, o / 255.0);
    }

    private static IBrush Argb(byte a, byte r, byte g, byte b) => A(Color.FromRgb(r, g, b), a / 255.0);

    private static readonly Dictionary<StandardCursorType, Cursor> _cursors = [];
    private static Cursor CursorOf(StandardCursorType t) => _cursors.TryGetValue(t, out var c) ? c : _cursors[t] = new Cursor(t);

    /// <summary>
    /// Прозрачность слоя без offscreen-буфера, когда она не нужна: при непрозрачности ≥ 0,98 слой не создаётся
    /// (PushOpacity даже с 1,0 в Skia выделяет буфер — на общем виде страны это десятки слоёв за кадр).
    /// </summary>
    private static DrawingContext.PushedState? LayerAlpha(DrawingContext ctx, double a) => a >= 0.98 ? null : ctx.PushOpacity(a);

    private Paint P(string hex)
    {
        if (_paints.TryGetValue(hex, out var p)) return p;
        var c = Color.Parse(hex);
        SolidColorBrush A(byte a) => new(Color.FromArgb(a, c.R, c.G, c.B));
        RadialGradientBrush Z(byte a) => new()
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(a * 0.6), c.R, c.G, c.B), 0.7),
                new GradientStop(Color.FromArgb((byte)(a * 0.25), c.R, c.G, c.B), 1),
            },
        };
        p = new Paint(c, new SolidColorBrush(c), A(170), Z(60), Z(100));
        _paints[hex] = p;
        return p;
    }
}
