using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Radar.Map;
using Radar.Models;

namespace Radar.Views;

/// <summary>
/// Зеркало на внешнем экране: телефон — ведущий, внешнее представление повторяет его в своём разрешении и раскладке —
/// камеру карты (тот же район с тем же охватом), вкладку и прокрутку боковой панели, её показ и скрытие.
/// Общие данные (режим, выбранная тревога, фильтры, легенда) и так одни — модель представления общая.
/// </summary>
public partial class MainView
{
    private static MainView? _leader;
    private static readonly List<WeakReference<MainView>> Followers = [];

    private IEnumerable<MainView> LiveFollowers()
    {
        lock (Followers)
        {
            Followers.RemoveAll(w => !w.TryGetTarget(out _));
            return Followers.Select(w => w.TryGetTarget(out var v) ? v : null).OfType<MainView>().ToList();
        }
    }

    private void InitMirror()
    {
        if (IsExternal)
        {
            lock (Followers) Followers.Add(new WeakReference<MainView>(this));
            // внешний экран не принимает ввод (у телевизора нет касаний) — всё управление с телефона
            IsHitTestVisible = false;
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => _leader?.PushMirror(this), DispatcherPriority.Background);
            return;
        }
        _leader = this;
        Map.CameraMoved += (c, z) => { foreach (var f in LiveFollowers()) f.FollowCamera(c, z, this); };
        SideTabs.SelectionChanged += (_, _) => { foreach (var f in LiveFollowers()) f.SideTabs.SelectedIndex = SideTabs.SelectedIndex; };
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => MirrorScroll(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // всплывающие элементы на Android рисуются в слое своего окна, а не отдельными окнами — на внешний экран
        // они не попадают сами: открываем такие же у ведомого
        Map.TouchTipChanged += g => { foreach (var f in LiveFollowers()) f.Map.ShowTouchTip(g); };
        TouchTips.Toggled += (c, open) => MirrorTo(c, (fc, _) => ToolTip.SetIsOpen(fc, open), byTip: true);
        ChartPalette.HoverChanged += c => MirrorTo(c, (fc, lc) =>
        {
            if (fc is DaysChart fd && lc is DaysChart ld) fd.Hover = ld.Hover;
            else if (fc is WeekHourChart fw && lc is WeekHourChart lw) fw.Hover = lw.Hover;
        });
        DropDownHook ??= Hook();
    }

    private static object? DropDownHook;

    private static object Hook()
    {
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((c, _) =>
            _leader?.MirrorTo(c, (fc, lc) => { if (fc is ComboBox f && lc is ComboBox l) f.IsDropDownOpen = l.IsDropDownOpen; }));
        CalendarDatePicker.IsDropDownOpenProperty.Changed.AddClassHandler<CalendarDatePicker>((c, _) =>
            _leader?.MirrorTo(c, (fc, lc) => { if (fc is CalendarDatePicker f && lc is CalendarDatePicker l) f.IsDropDownOpen = l.IsDropDownOpen; }));
        return new object();
    }

    /// <summary>
    /// Тот же элемент у каждого ведомого: по типу и порядковому номеру среди таких же в дереве (оба представления
    /// построены из одной разметки); для подсказок — среди элементов с тем же текстом подсказки (строки лент могут
    /// быть виртуализированы по-разному).
    /// </summary>
    private void MirrorTo(Control c, Action<Control, Control> apply, bool byTip = false)
    {
        if (IsExternal || !c.GetSelfAndVisualAncestors().Contains(this)) return;
        var followers = LiveFollowers().ToList();
        if (followers.Count == 0) return;
        var tip = byTip ? ToolTip.GetTip(c) as string : null;
        bool Same(Control x) => x.GetType() == c.GetType() && (!byTip || tip == null || ToolTip.GetTip(x) as string == tip);
        int idx = this.GetVisualDescendants().OfType<Control>().Where(Same).ToList().IndexOf(c);
        if (idx < 0) return;
        foreach (var f in followers)
            if (f.GetVisualDescendants().OfType<Control>().Where(Same).ElementAtOrDefault(idx) is { } fc)
                apply(fc, c);
    }

    private void FollowCamera(GeoPoint c, double z, MainView leader)
    {
        double side = Math.Min(leader.Map.Bounds.Width, leader.Map.Bounds.Height);
        if (side < 1) return;
        Map.FollowCamera(c, z, side);
    }

    /// <summary>Подключённый экран получает текущее состояние телефона.</summary>
    private void PushMirror(MainView f)
    {
        var (c, z) = Map.Camera;
        f.FollowCamera(c, z, this);
        f.SideTabs.SelectedIndex = SideTabs.SelectedIndex;
        f.MirrorSidebar(_sidebarHidden);
        MirrorScroll();
    }

    /// <summary>Панель на телефоне открыта или закрыта — на большом экране так же (там она колонкой справа).</summary>
    private void MirrorSidebarChanged()
    {
        if (IsExternal) return;
        foreach (var f in LiveFollowers()) f.MirrorSidebar(_sidebarHidden);
    }

    private void MirrorSidebar(bool hidden)
    {
        if (_sidebarHidden != hidden) SetSidebarHidden(hidden);
    }

    /// <summary>Прокрутка списков панели — по доле, а не по пикселям: высота строк на экранах разная.</summary>
    private void MirrorScroll()
    {
        var followers = LiveFollowers().ToList();
        if (followers.Count == 0) return;
        var mine = ScrollersOf(this);
        foreach (var f in followers)
        {
            var theirs = ScrollersOf(f);
            foreach (var (name, sv) in mine)
            {
                if (!theirs.TryGetValue(name, out var t)) continue;
                double range = sv.Extent.Height - sv.Viewport.Height, trange = t.Extent.Height - t.Viewport.Height;
                if (range <= 0 || trange <= 0) continue;
                t.Offset = new Vector(t.Offset.X, Math.Round(sv.Offset.Y / range * trange));
            }
        }
    }

    /// <summary>Прокручиваемые списки открытой вкладки боковой панели, по пути в дереве (одинаковому у обоих представлений).</summary>
    private static Dictionary<string, ScrollViewer> ScrollersOf(MainView v)
    {
        var res = new Dictionary<string, ScrollViewer>();
        int i = 0;
        foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v.Sidebar).OfType<ScrollViewer>())
            if (sv.IsEffectivelyVisible) res[$"{v.SideTabs.SelectedIndex}:{i++}"] = sv;
        return res;
    }
}
