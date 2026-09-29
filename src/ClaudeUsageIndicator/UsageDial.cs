using System;
using System.Windows;
using System.Windows.Media;

namespace ClaudeUsageIndicator;

/// <summary>
/// The compact form of the overlay: the same two rings as the tray icon, drawn larger. Outer
/// thick ring is session usage, inner thin ring is weekly usage, both colour-coded like the bars.
/// With <see cref="ShowTimeMarker"/> a short radial tick on each ring marks how far through the
/// window we are, the circular equivalent of the tick on the bars.
/// </summary>
internal sealed class UsageDial : FrameworkElement
{
    /// <summary>Drawable area in DIPs. With the overlay's border and 5 px padding the window is a 46 px square, about two thirds of the three-row layout.</summary>
    public const double Size = 34;

    private const double OuterRadius = 14.5, OuterWidth = 5;
    private const double InnerRadius = 8.5, InnerWidth = 3;

    private static readonly Pen OuterTrack = TrackPen(OuterWidth);
    private static readonly Pen InnerTrack = TrackPen(InnerWidth);
    private static readonly Brush IdleDot = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));

    private LimitWindow? _outer, _inner;
    private TimeSpan? _outerLength, _innerLength;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private Brush _markerBrush = Brushes.White;

    public bool ShowTimeMarker { get; set; } = true;

    public void SetMarkerBrush(Brush brush)
    {
        _markerBrush = brush;
        InvalidateVisual();
    }

    public void Update(LimitWindow? session, TimeSpan? sessionLength, LimitWindow? weekly, TimeSpan? weeklyLength, DateTimeOffset now)
    {
        _outer = session;
        _outerLength = sessionLength;
        _inner = weekly;
        _innerLength = weeklyLength;
        _now = now;
        InvalidateVisual();
    }

    /// <summary>Once a second while visible: moves the time markers.</summary>
    public void UpdateClock(DateTimeOffset now)
    {
        _now = now;
        if (ShowTimeMarker && IsVisible) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(Size / 2, Size / 2);
        DrawRing(dc, c, OuterRadius, OuterWidth, OuterTrack, _outer, _outerLength);
        DrawRing(dc, c, InnerRadius, InnerWidth, InnerTrack, _inner, _innerLength);
        if (_outer is null && _inner is null)
        {
            dc.DrawEllipse(IdleDot, null, c, 3, 3);
        }
    }

    private void DrawRing(DrawingContext dc, Point c, double r, double width, Pen track, LimitWindow? window, TimeSpan? length)
    {
        dc.DrawEllipse(null, track, c, r, r);
        if (window is null) return;

        var pct = Math.Clamp(window.Utilization, 0, 100);
        var pen = new Pen(LimitRow.BrushFor(pct), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var sweep = 360.0 * pct / 100.0;
        if (sweep >= 359.5)
        {
            dc.DrawEllipse(null, pen, c, r, r);
        }
        else if (sweep >= 0.5)
        {
            dc.DrawGeometry(null, pen, Arc(c, r, sweep));
        }

        if (!ShowTimeMarker || length is not { } len || len <= TimeSpan.Zero || window.ResetsAt is not { } resetsAt) return;
        var elapsed = Math.Clamp(1.0 - (resetsAt - _now) / len, 0.0, 1.0);
        var a = elapsed * 2 * Math.PI;
        var (sin, cos) = (Math.Sin(a), Math.Cos(a));
        var inner = r - width / 2 - 1;
        var outer = r + width / 2 + 1;
        var markerPen = new Pen(_markerBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        markerPen.Freeze();
        dc.PushOpacity(0.9);
        dc.DrawLine(markerPen, new Point(c.X + inner * sin, c.Y - inner * cos), new Point(c.X + outer * sin, c.Y - outer * cos));
        dc.Pop();
    }

    /// <summary>Clockwise arc from twelve o'clock through <paramref name="sweepDegrees"/>.</summary>
    private static StreamGeometry Arc(Point c, double r, double sweepDegrees)
    {
        var end = sweepDegrees * Math.PI / 180;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(end), c.Y - r * Math.Cos(end)), new Size(r, r), 0,
                sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Pen TrackPen(double width)
    {
        var pen = new Pen(Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), width);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
