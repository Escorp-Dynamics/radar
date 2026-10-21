using System;
using System.Runtime.CompilerServices;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Radar.Views;

/// <summary>
/// Помечает любой ScrollViewer классом «scrolling» на время прокрутки и ещё ~0.9 с после неё,
/// чтобы скрытая полоса прокрутки проявлялась, даже когда курсор не над блоком.
/// Заодно растворяет край содержимого в прозрачность с той стороны, куда ещё можно листать:
/// у упора край чёткий, а с отходом от него прозрачность нарастает плавно (на первых <see cref="Fade"/> пикселях сдвига).
/// </summary>
public static class ScrollReveal
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(900);
    private static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> Timers = new();

    private const double Fade = 22;
    private static readonly ConditionalWeakTable<ScrollViewer, LinearGradientBrush> Masks = new();

    public static void Register() =>
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>(OnScrollChanged);

    private static void OnScrollChanged(ScrollViewer sv, ScrollChangedEventArgs e)
    {
        UpdateFade(sv);
        if (e.OffsetDelta.X == 0 && e.OffsetDelta.Y == 0) return;
        sv.Classes.Set("scrolling", true);
        var timer = Timers.GetValue(sv, v =>
        {
            var t = new DispatcherTimer { Interval = Hold };
            t.Tick += (_, _) => { t.Stop(); v.Classes.Set("scrolling", false); };
            return t;
        });
        timer.Stop();
        timer.Start();
    }

    /// <summary>Маска ставится на презентер содержимого, а не на весь блок: полоса прокрутки остаётся непрозрачной.</summary>
    private static void UpdateFade(ScrollViewer sv)
    {
        // поле ввода листает текст под кареткой — растворять его края нельзя
        if (sv.TemplatedParent is TextBox) return;
        var presenter = sv.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(p => p.TemplatedParent == sv);
        if (presenter == null) return;
        Vector off = sv.Offset;
        Size ext = sv.Extent, vp = sv.Viewport;
        bool vertical = ext.Height - vp.Height > 0.5;
        double len = vertical ? vp.Height : vp.Width;
        double before = vertical ? off.Y : off.X;
        double after = vertical ? ext.Height - vp.Height - off.Y : ext.Width - vp.Width - off.X;
        if (len < 3 * Fade || (!vertical && ext.Width - vp.Width <= 0.5))
        {
            if (presenter.OpacityMask != null) presenter.OpacityMask = null;
            return;
        }
        double a = Math.Clamp(before / Fade, 0, 1), b = Math.Clamp(after / Fade, 0, 1);
        if (a <= 0 && b <= 0)
        {
            if (presenter.OpacityMask != null) presenter.OpacityMask = null;
            return;
        }
        double edge = Fade / len;
        var brush = Masks.GetValue(sv, _ => new LinearGradientBrush
        {
            GradientStops = { new GradientStop(), new GradientStop(), new GradientStop(), new GradientStop() },
        });
        brush.StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative);
        brush.EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative);
        var st = brush.GradientStops;
        st[0].Offset = 0; st[0].Color = Color.FromArgb((byte)Math.Round(255 * (1 - a)), 0, 0, 0);
        st[1].Offset = edge; st[1].Color = Colors.Black;
        st[2].Offset = 1 - edge; st[2].Color = Colors.Black;
        st[3].Offset = 1; st[3].Color = Color.FromArgb((byte)Math.Round(255 * (1 - b)), 0, 0, 0);
        if (presenter.OpacityMask != brush) presenter.OpacityMask = brush;
        else presenter.InvalidateVisual();
    }
}
