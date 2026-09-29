using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ClaudeUsageIndicator;

/// <summary>
/// The overlay. All positioning is done in physical screen pixels through the HWND so a saved
/// position means the same thing on every monitor regardless of DPI scaling.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly ConfigStore _store;
    private AppConfig _config;

    private readonly LimitRow _session = new("Session");
    private readonly LimitRow _weekly = new("Weekly");
    private readonly LimitRow _model = new("Fable");

    private System.Drawing.Point _dragCursorStart;
    private Native.RECT _dragWindowStart;
    private bool _dragging;
    private bool _moved;

    private UsageSnapshot? _snapshot;
    private DateTimeOffset? _lastSuccess;
    private string? _error;
    private IReadOnlyList<SessionInfo> _sessions = Array.Empty<SessionInfo>();
    private Brush _textBrush = Brushes.White;

    // Compact mode. The saved position always describes the expanded layout's top-left; while
    // compact the dial hugs whichever screen edges the expanded panel was nearest, so a panel in
    // the top-right corner shrinks towards that corner and grows back out of it.
    private bool _compact;
    private bool _switchingMode;
    private bool _anchorRight, _anchorBottom;
    private (int X, int Y)? _expandedTopLeft;
    private (int Width, int Height) _expandedSizePx;
    private readonly TextBlock _dialTipText = new();

    public IntPtr Handle { get; private set; }

    public event Action? ContextMenuRequested;
    /// <summary>Double-click: the user wants the other layout (dial or rows).</summary>
    public event Action? CompactToggleRequested;

    public bool IsCompact => _compact;

    public MainWindow(AppConfig config, AppState state, ConfigStore store)
    {
        _config = config;
        _state = state;
        _store = store;
        InitializeComponent();

        Rows.Children.Insert(0, _session);
        Rows.Children.Insert(1, _weekly);
        Rows.Children.Insert(2, _model);

        // Create the HWND now so we can strip activation before it is ever shown.
        Handle = new WindowInteropHelper(this).EnsureHandle();
        var exStyle = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)(exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));

        Root.MouseLeftButtonDown += OnDragStart;
        Root.MouseMove += OnDragMove;
        Root.MouseLeftButtonUp += OnDragEnd;
        Root.LostMouseCapture += (_, _) => FinishDrag();
        Root.MouseRightButtonUp += (_, e) => { e.Handled = true; ContextMenuRequested?.Invoke(); };

        Dial.ToolTip = new ToolTip { Content = _dialTipText };

        Loaded += (_, _) => PlaceWindow();
        SizeChanged += (_, _) =>
        {
            if (!_compact) _expandedSizePx = LayoutSizePx();
            if (IsLoaded && !_dragging && !_switchingMode) ClampToScreen();
        };
        ApplyConfig(config);
        Render(DateTimeOffset.UtcNow);
    }

    public void ShowNoActivate()
    {
        Show();
        AssertTopmost();
    }

    /// <summary>
    /// Render the panel (without the window's own opacity) to a PNG at <paramref name="pixelScale"/>
    /// times its layout size, e.g. 2 for a crisp README image. Used by --demo --shot.
    /// </summary>
    public void SaveImage(string path, double pixelScale)
    {
        Root.UpdateLayout();
        var size = new Size(Root.ActualWidth * RootScale.ScaleX, Root.ActualHeight * RootScale.ScaleY);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * pixelScale), (int)Math.Ceiling(size.Height * pixelScale),
            96 * pixelScale, 96 * pixelScale, PixelFormats.Pbgra32);
        // Draw through a brush so the capture is framed on the panel, not on its offset in the window.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.Uniform },
                null, new Rect(size));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    public void AssertTopmost()
    {
        if (!_config.AlwaysOnTop || Handle == IntPtr.Zero) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Opacity = config.Opacity;
        RootScale.ScaleX = RootScale.ScaleY = config.Scale;
        Topmost = config.AlwaysOnTop;
        Root.Cursor = config.LockPosition ? Cursors.Arrow : Cursors.SizeAll;

        Root.Background = ParseBrush(config.BackgroundColor, Color.FromArgb(0xE6, 0x14, 0x18, 0x20));
        var text = ParseBrush(config.TextColor, Colors.White);
        _textBrush = text;
        StatusText.Foreground = text;
        SessionSummaryLabel.Foreground = text;
        SessionSummaryText.Foreground = text;
        foreach (var row in new[] { _session, _weekly, _model })
        {
            row.SetTextBrush(text);
            row.ShowReset = config.ShowResetTimes;
            row.ShowTimeMarker = config.ShowTimeMarker;
            row.Use24HourClock = config.Use24HourClock;
        }
        Dial.ShowTimeMarker = config.ShowTimeMarker;
        Dial.SetMarkerBrush(text);

        _session.Visibility = config.ShowSession ? Visibility.Visible : Visibility.Collapsed;
        _weekly.Visibility = config.ShowWeekly ? Visibility.Visible : Visibility.Collapsed;
        _model.Visibility = config.ShowFable ? Visibility.Visible : Visibility.Collapsed;

        Render(DateTimeOffset.UtcNow);
        RenderSessions(DateTimeOffset.UtcNow);
        if (IsVisible) AssertTopmost();
    }

    public void ApplySessions(IReadOnlyList<SessionInfo> sessions)
    {
        _sessions = sessions;
        RenderSessions(DateTimeOffset.UtcNow);
    }

    private void RenderSessions(DateTimeOffset now)
    {
        var mode = _config.SessionsMode;
        var any = mode != "off" && _sessions.Count > 0;

        SessionDivider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SessionSummary.Visibility = any && mode == "collapsed" ? Visibility.Visible : Visibility.Collapsed;
        SessionRows.Visibility = any && mode == "expanded" ? Visibility.Visible : Visibility.Collapsed;

        if (!any)
        {
            SessionRows.Children.Clear();
            return;
        }

        if (mode == "collapsed")
        {
            SessionRows.Children.Clear();
            SessionSummaryText.Text = SessionSummaryLine(_sessions);
            return;
        }

        // Expanded: reuse rows where possible so the layout does not flicker.
        while (SessionRows.Children.Count > _sessions.Count) SessionRows.Children.RemoveAt(SessionRows.Children.Count - 1);
        while (SessionRows.Children.Count < _sessions.Count) SessionRows.Children.Add(new SessionRow());
        for (var i = 0; i < _sessions.Count; i++)
        {
            var row = (SessionRow)SessionRows.Children[i];
            row.SetTextBrush(_textBrush);
            row.LabelStyle = _config.SessionsLabel;
            row.Update(_sessions[i], now);
        }
    }

    private static string SessionSummaryLine(IReadOnlyList<SessionInfo> sessions)
    {
        int waiting = 0, working = 0;
        foreach (var s in sessions)
        {
            if (s.Waiting) waiting++; else working++;
        }
        var parts = new List<string>(2);
        if (waiting > 0) parts.Add($"{waiting} waiting");
        if (working > 0) parts.Add($"{working} working");
        return string.Join(" · ", parts);
    }

    public void Apply(UsageService service)
    {
        _snapshot = service.Snapshot;
        _lastSuccess = service.LastSuccess;
        _error = service.Error;
        Render(DateTimeOffset.UtcNow);
    }

    /// <summary>Called every second: countdowns and the "updated … ago" line.</summary>
    public void UpdateClock(DateTimeOffset now)
    {
        _session.UpdateCountdown(now);
        _weekly.UpdateCountdown(now);
        _model.UpdateCountdown(now);
        RenderStatus(now);
        if (_compact)
        {
            Dial.UpdateClock(now);
            UpdateDialTip(now);
        }
        foreach (var child in SessionRows.Children)
        {
            if (child is SessionRow row) row.UpdateClock(now);
        }
    }

    private void Render(DateTimeOffset now)
    {
        _session.WindowLength = TimeSpan.FromHours(5);
        _session.Update(_snapshot?.Get(UsageSnapshot.SessionKey), now);
        _weekly.WindowLength = TimeSpan.FromDays(7);
        _weekly.Update(_snapshot?.Get(UsageSnapshot.WeeklyKey), now);

        var modelKey = _snapshot?.ResolveModelKey(_config.FableLimitKey);
        _model.SetName(_snapshot?.LabelFor(modelKey) ?? "Fable");
        _model.WindowLength = UsageSnapshot.WindowLengthFor(modelKey);
        _model.Update(modelKey is null ? null : _snapshot?.Get(modelKey), now);

        Dial.Update(_snapshot?.Get(UsageSnapshot.SessionKey), TimeSpan.FromHours(5),
                    _snapshot?.Get(UsageSnapshot.WeeklyKey), TimeSpan.FromDays(7), now);
        if (_compact) UpdateDialTip(now);

        RenderStatus(now);
    }

    // --- compact mode --------------------------------------------------------------------------

    /// <summary>
    /// Switch between the row layout and the dial. The window keeps the screen edges the
    /// expanded panel was nearest to, and expanding returns to the exact expanded position unless
    /// the dial was dragged in between.
    /// </summary>
    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        if (!IsLoaded || Handle == IntPtr.Zero)
        {
            _compact = compact;
            ApplyMode();
            return;
        }

        var before = WindowRect();
        _switchingMode = true;
        try
        {
            if (compact)
            {
                _expandedSizePx = LayoutSizePx();
                _expandedTopLeft = (before.Left, before.Top);
                var area = Forms.Screen.FromHandle(Handle).WorkingArea;
                _anchorRight = (before.Left + before.Right) / 2 > area.Left + area.Width / 2;
                _anchorBottom = (before.Top + before.Bottom) / 2 > area.Top + area.Height / 2;
            }
            _compact = compact;
            ApplyMode();
            UpdateLayout();
            var (w, h) = LayoutSizePx();
            int x, y;
            if (compact)
            {
                x = _anchorRight ? before.Right - w : before.Left;
                y = _anchorBottom ? before.Bottom - h : before.Top;
            }
            else
            {
                (x, y) = _expandedTopLeft ?? (before.Left, before.Top);
            }
            MoveTo(x, y);
            Log.Info($"{(compact ? "Compact" : "Expanded")} layout: {before.Right - before.Left}x{before.Bottom - before.Top} -> {w}x{h} at ({x},{y})");
        }
        finally
        {
            _switchingMode = false;
        }
        ClampToScreen();
    }

    private void ApplyMode()
    {
        Rows.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        Dial.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        Root.Padding = _compact ? new Thickness(5) : new Thickness(9, 4, 9, 4);
        if (_compact) UpdateDialTip(DateTimeOffset.UtcNow);
    }

    /// <summary>Hover text for the dial: the same numbers the rows would show.</summary>
    private void UpdateDialTip(DateTimeOffset now)
    {
        var lines = new List<string>(5);
        void Add(string label, LimitWindow? w)
        {
            if (w is null) return;
            var pct = Math.Round(Math.Clamp(w.Utilization, 0, 100));
            var reset = w.ResetsAt is { } r ? " · " + TimeText.FormatReset(r, now, _config.Use24HourClock) : "";
            lines.Add($"{label} {pct}%{reset}");
        }
        Add("Session", _snapshot?.Get(UsageSnapshot.SessionKey));
        Add("Weekly", _snapshot?.Get(UsageSnapshot.WeeklyKey));
        if (_config.ShowFable)
        {
            var key = _snapshot?.ResolveModelKey(_config.FableLimitKey);
            Add(_snapshot?.LabelFor(key) ?? "Fable", key is null ? null : _snapshot?.Get(key));
        }
        if (_lastSuccess is { } ok) lines.Add("updated " + TimeText.Ago(now - ok));
        lines.Add("double-click to expand");
        _dialTipText.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// After the dial moved (drag or clamp): re-derive which edges it hugs and where the expanded
    /// panel would therefore sit, so expanding and the saved position both follow the dial.
    /// </summary>
    private void SyncExpandedFromCompact()
    {
        if (!_compact) return;
        var r = WindowRect();
        var area = Forms.Screen.FromHandle(Handle).WorkingArea;
        _anchorRight = (r.Left + r.Right) / 2 > area.Left + area.Width / 2;
        _anchorBottom = (r.Top + r.Bottom) / 2 > area.Top + area.Height / 2;
        var (ew, eh) = _expandedSizePx;
        _expandedTopLeft = (_anchorRight ? r.Right - ew : r.Left, _anchorBottom ? r.Bottom - eh : r.Top);
    }

    /// <summary>Freshness rides on the first visible row; the status line only appears for errors or before first data.</summary>
    private void RenderStatus(DateTimeOffset now)
    {
        var freshness = _lastSuccess is { } ok ? TimeText.Freshness(now - ok) : null;
        var placed = false;
        foreach (var row in new[] { _session, _weekly, _model })
        {
            if (!placed && row.Visibility == Visibility.Visible)
            {
                row.SetFreshness(freshness);
                placed = true;
            }
            else
            {
                row.SetFreshness(null);
            }
        }

        if (_error is not null)
        {
            StatusText.Text = _error;
            StatusText.Visibility = Visibility.Visible;
        }
        else if (_snapshot is null)
        {
            StatusText.Text = "loading…";
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    // --- placement (physical pixels) -----------------------------------------------------------

    private Native.RECT WindowRect()
    {
        Native.GetWindowRect(Handle, out var r);
        return r;
    }

    /// <summary>
    /// Current size in physical pixels, from WPF's layout rather than the HWND: the HWND lags
    /// behind layout (at Loaded it still has WPF's default size), the layout result does not.
    /// </summary>
    private (int Width, int Height) LayoutSizePx()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
    }

    private void MoveTo(int x, int y) =>
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    /// <summary>Persist the expanded layout's top-left (derived from the dial while compact).</summary>
    private void SavePosition()
    {
        if (_compact && _expandedTopLeft is { } e)
        {
            _state.X = e.X;
            _state.Y = e.Y;
        }
        else
        {
            var r = WindowRect();
            _state.X = r.Left;
            _state.Y = r.Top;
        }
        _store.SaveState(_state);
    }

    private void PlaceWindow()
    {
        var (width, height) = LayoutSizePx();

        var primary = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int defaultX = primary.Right - width - 16, defaultY = primary.Top + 16;

        int x = (int)(_state.X ?? defaultX), y = (int)(_state.Y ?? defaultY);
        if (!IsUsefullyOnSomeScreen(new System.Drawing.Rectangle(x, y, width, height)))
        {
            Log.Info($"Saved position ({x},{y}) is off-screen; using default.");
            x = defaultX;
            y = defaultY;
        }

        MoveTo(x, y);
        ClampToScreen();
    }

    /// <summary>True when enough of the rectangle lies inside some monitor's working area to grab it.</summary>
    private static bool IsUsefullyOnSomeScreen(System.Drawing.Rectangle rect)
    {
        var minArea = Math.Min(40 * 20, rect.Width * rect.Height / 4);
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var overlap = System.Drawing.Rectangle.Intersect(screen.WorkingArea, rect);
            if (!overlap.IsEmpty && overlap.Width * overlap.Height >= minArea) return true;
        }
        return false;
    }

    /// <summary>Keep the whole overlay inside the working area of the monitor it is on (e.g. after a scale change).</summary>
    private void ClampToScreen()
    {
        if (Handle == IntPtr.Zero) return;
        var r = WindowRect();
        var (width, height) = LayoutSizePx();
        if (width <= 0 || height <= 0) return;

        var area = Forms.Screen.FromHandle(Handle).WorkingArea;
        var x = Math.Max(area.Left, Math.Min(r.Left, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(r.Top, area.Bottom - height));
        if (x != r.Left || y != r.Top)
        {
            MoveTo(x, y);
            SyncExpandedFromCompact();
            SavePosition();
        }
    }

    // --- dragging ------------------------------------------------------------------------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // The first click of the pair started a drag; drop it so the toggle does not also move us.
            _dragging = false;
            Root.ReleaseMouseCapture();
            e.Handled = true;
            CompactToggleRequested?.Invoke();
            return;
        }
        if (_config.LockPosition) return;
        _dragCursorStart = Forms.Cursor.Position;
        _dragWindowStart = WindowRect();
        _dragging = true;
        _moved = false;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = Forms.Cursor.Position;
        MoveTo(_dragWindowStart.Left + (cursor.X - _dragCursorStart.X),
               _dragWindowStart.Top + (cursor.Y - _dragCursorStart.Y));
        _moved = true;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        Root.ReleaseMouseCapture(); // triggers FinishDrag via LostMouseCapture
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_moved)
        {
            SyncExpandedFromCompact();
            SavePosition();
        }
    }

    private static SolidColorBrush ParseBrush(string text, Color fallback)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(fallback);
            brush.Freeze();
            return brush;
        }
    }
}
