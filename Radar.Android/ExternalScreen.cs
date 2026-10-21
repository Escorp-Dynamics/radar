using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace Radar.Android;

/// <summary>
/// Вывод карты на внешний экран (Miracast, Chromecast в режиме экрана, HDMI/USB-C): Android сообщает о таком экране как
/// о «презентационном» — на нём показывается отдельное представление в его собственном разрешении и раскладке
/// (как на десктопе), а телефон остаётся пультом. Новый экран — предложение вывести карту (уведомление-подсказка).
/// </summary>
internal sealed class ExternalScreen : Java.Lang.Object, DisplayManager.IDisplayListener
{
    private readonly Activity _activity;
    private readonly DisplayManager? _dm;
    private Presentation? _presentation;

    public ExternalScreen(Activity activity)
    {
        _activity = activity;
        _dm = (DisplayManager?)activity.GetSystemService(Context.DisplayService);
        _dm?.RegisterDisplayListener(this, new Handler(Looper.MainLooper!));
        Update();
    }

    /// <summary>«Имя · 1920×1080» показанного экрана или null.</summary>
    public string? Current { get; private set; }
    public event Action? Changed;

    public void OnDisplayAdded(int displayId) => Update();
    public void OnDisplayChanged(int displayId) => Update();
    public void OnDisplayRemoved(int displayId) => Update();

    private void Update()
    {
        var target = _dm?.GetDisplays(DisplayManager.DisplayCategoryPresentation)?.FirstOrDefault(d => d.IsValid);
        if (_presentation is { } p && (target == null || p.Display.DisplayId != target.DisplayId))
        {
            try { p.Dismiss(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"внешний экран: {ex.Message}"); }
            _presentation = null;
            SetCurrent(null);
        }
        if (target == null || _presentation != null || _activity.IsFinishing || _activity.IsDestroyed) return;
        try
        {
            var pres = new MapPresentation(_activity, target);
            pres.Show();
            _presentation = pres;
            var size = new global::Android.Graphics.Point();
#pragma warning disable CA1422 // GetRealSize: размер внешнего экрана целиком (WindowMetrics — только для окон)
            target.GetRealSize(size);
#pragma warning restore CA1422
            SetCurrent($"«{target.Name}» · {size.X}×{size.Y}");
        }
        catch (Exception ex)
        {
            // экран пропал между уведомлением и показом или нет разрешения на окно — без трансляции
            System.Diagnostics.Trace.WriteLine($"внешний экран: {ex.Message}");
        }
    }

    private void SetCurrent(string? v)
    {
        if (Current == v) return;
        Current = v;
        Changed?.Invoke();
    }

    public void Release()
    {
        _dm?.UnregisterDisplayListener(this);
        try { _presentation?.Dismiss(); } catch (Exception) { }
        _presentation = null;
        SetCurrent(null);
    }

    /// <summary>Окно на внешнем экране с собственным представлением карты.</summary>
    private sealed class MapPresentation(Context ctx, Display display) : Presentation(ctx, display)
    {
        private AvaloniaView? _view;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // во весь экран, без системных панелей (у телевизора их нет, у монитора — не нужны)
            Window?.SetFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn, WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
            _view = new AvaloniaView(NativeContext()) { Content = App.CreateExternalView() };
            SetContentView(_view);
        }

        /// <summary>
        /// Контекст с плотностью под нативные пиксели экрана: Android отдаёт внешнему экрану 1920×1080 плотность xhdpi (2),
        /// и интерфейс раскладывался в 960×540 с растяжением кадра вдвое. Здесь масштаб — по короткой стороне к ~720
        /// логическим точкам (1080p → 1,5; 4K → 3): кадр рисуется в полном разрешении экрана, а надписи читаются с дивана.
        /// </summary>
        private Context NativeContext()
        {
            var size = new global::Android.Graphics.Point();
#pragma warning disable CA1422
            Display.GetRealSize(size);
#pragma warning restore CA1422
            double scale = Math.Max(1, Math.Round(Math.Min(size.X, size.Y) / 720.0 * 4) / 4);
            var cfg = new global::Android.Content.Res.Configuration(Context.Resources!.Configuration!)
            {
                DensityDpi = (int)Math.Round((int)global::Android.Util.DisplayMetricsDensity.Default * scale),
            };
            return Context.CreateConfigurationContext(cfg) ?? Context;
        }

        public override void OnDetachedFromWindow()
        {
            _view?.Dispose();
            _view = null;
            base.OnDetachedFromWindow();
        }
    }
}
