using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ClaudeUsageIndicator;

/// <summary>Notification-area icon and its context menu. The icon's ring mirrors session usage.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _chimeItem;
    private readonly ToolStripMenuItem _opacityMenu;
    private static readonly int[] OpacityPercents = { 100, 90, 80, 70, 60, 50, 40, 30, 20 };
    private Icon? _currentIcon;
    private bool _suppressStartupEvent;
    private bool _suppressChimeEvent;

    public event Action? ToggleVisibility;
    public event Action? RefreshRequested;
    public event Action? OpenConfigRequested;
    public event Action? OpenLogRequested;
    public event Action? ExitRequested;
    public event Action<bool>? RunAtStartupChanged;
    public event Action<bool>? ChimeChanged;
    /// <summary>Raised with the chosen opacity (0.2 - 1.0).</summary>
    public event Action<double>? OpacitySelected;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("Hide indicator", null, (_, _) => ToggleVisibility?.Invoke());
        var refreshItem = new ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        var configItem = new ToolStripMenuItem("Open config file", null, (_, _) => OpenConfigRequested?.Invoke());
        var logItem = new ToolStripMenuItem("Open log", null, (_, _) => OpenLogRequested?.Invoke());
        _startupItem = new ToolStripMenuItem("Run at startup") { CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressStartupEvent) RunAtStartupChanged?.Invoke(_startupItem.Checked);
        };
        _chimeItem = new ToolStripMenuItem("Chime when a session finishes") { CheckOnClick = true };
        _chimeItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressChimeEvent) ChimeChanged?.Invoke(_chimeItem.Checked);
        };
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke());

        _opacityMenu = new ToolStripMenuItem("Opacity");
        foreach (var pct in OpacityPercents)
        {
            var value = pct / 100.0;
            var item = new ToolStripMenuItem($"{pct}%") { Tag = value };
            item.Click += (_, _) => OpacitySelected?.Invoke(value);
            _opacityMenu.DropDownItems.Add(item);
        }

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _toggleItem,
            refreshItem,
            _opacityMenu,
            _chimeItem,
            new ToolStripSeparator(),
            configItem,
            logItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        });

        _notifyIcon = new NotifyIcon
        {
            Text = "Claude Usage Indicator",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleVisibility?.Invoke();
        };

        SetIcon(null, null);
    }

    public void SetRunAtStartup(bool enabled)
    {
        _suppressStartupEvent = true;
        _startupItem.Checked = enabled;
        _suppressStartupEvent = false;
    }

    public void SetChime(bool enabled)
    {
        _suppressChimeEvent = true;
        _chimeItem.Checked = enabled;
        _suppressChimeEvent = false;
    }

    /// <summary>Tick the preset matching the current opacity (none if the config holds an in-between value).</summary>
    public void SetOpacity(double opacity)
    {
        foreach (ToolStripMenuItem item in _opacityMenu.DropDownItems)
        {
            item.Checked = item.Tag is double v && Math.Abs(v - opacity) < 0.005;
        }
    }

    public void SetVisibleState(bool visible) =>
        _toggleItem.Text = visible ? "Hide indicator" : "Show indicator";

    /// <summary>
    /// Open the tray menu at the cursor (used for right-click on the overlay). Goes through
    /// NotifyIcon's own private path when available, because that is what makes the menu close on
    /// an outside click for a process that never owns the foreground.
    /// </summary>
    public void ShowMenu()
    {
        try
        {
            var method = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method is not null)
            {
                method.Invoke(_notifyIcon, null);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"NotifyIcon.ShowContextMenu unavailable: {ex.Message}");
        }

        // Fallback: the documented TrackPopupMenu dance so the menu can dismiss itself.
        _menu.Show(Cursor.Position);
        Native.SetForegroundWindow(_menu.Handle);
        Native.PostMessage(_menu.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Apply(UsageService service, AppConfig config, System.Collections.Generic.IReadOnlyList<SessionInfo>? sessions)
    {
        var snap = service.Snapshot;
        var session = snap?.Get(UsageSnapshot.SessionKey);
        var weekly = snap?.Get(UsageSnapshot.WeeklyKey);
        var modelKey = snap?.ResolveModelKey(config.FableLimitKey);
        var model = modelKey is null ? null : snap?.Get(modelKey);

        SetIcon(session?.Utilization, weekly?.Utilization);

        string text;
        if (service.Error is not null && snap is null)
        {
            text = "Claude Usage: " + service.Error;
        }
        else
        {
            text = $"Session {Pct(session)} · Weekly {Pct(weekly)}";
            if (model is not null && snap is not null) text += $" · {snap.LabelFor(modelKey)} {Pct(model)}";
        }
        if (sessions is { Count: > 0 } && config.SessionsMode != "off")
        {
            int waiting = 0, working = 0;
            foreach (var s in sessions) { if (s.Waiting) waiting++; else working++; }
            text += $"\n{waiting} waiting · {working} working";
        }
        _notifyIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string Pct(LimitWindow? w) => w is null ? "–" : $"{Math.Round(w.Utilization)}%";

    private void SetIcon(double? sessionPct, double? weeklyPct)
    {
        var icon = DrawIcon(sessionPct, weeklyPct);
        _notifyIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    /// <summary>Outer thick ring = session usage, inner thin ring = weekly usage.</summary>
    private static Icon DrawIcon(double? sessionPct, double? weeklyPct)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var outer = new RectangleF(3.5f, 3.5f, 25, 25);
            var inner = new RectangleF(10.5f, 10.5f, 11, 11);

            using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), 5f))
            {
                g.DrawEllipse(track, outer);
            }
            using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), 3f))
            {
                g.DrawEllipse(track, inner);
            }

            if (sessionPct is { } s)
            {
                using var pen = new Pen(ColorFor(s), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, outer, -90f, (float)(360.0 * Math.Clamp(s, 0, 100) / 100.0));
            }
            if (weeklyPct is { } w)
            {
                using var pen = new Pen(ColorFor(w), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, inner, -90f, (float)(360.0 * Math.Clamp(w, 0, 100) / 100.0));
            }
            if (sessionPct is null && weeklyPct is null)
            {
                using var brush = new SolidBrush(Color.FromArgb(160, 255, 255, 255));
                g.FillEllipse(brush, 13f, 13f, 6f, 6f);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    private static Color ColorFor(double pct) =>
        pct < 50 ? Color.FromArgb(0x4C, 0xAF, 0x50)
        : pct < 80 ? Color.FromArgb(0xFF, 0xB3, 0x00)
        : Color.FromArgb(0xEF, 0x53, 0x50);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _currentIcon?.Dispose();
    }
}
