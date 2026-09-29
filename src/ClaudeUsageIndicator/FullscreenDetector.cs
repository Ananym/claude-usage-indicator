using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeUsageIndicator;

/// <summary>
/// Decides whether a fullscreen application (game, fullscreen video, presentation) currently
/// owns the monitor the overlay sits on, in which case the overlay should get out of the way.
/// </summary>
internal static class FullscreenDetector
{
    // Windows shell surfaces that can legitimately cover a whole monitor without being "fullscreen apps".
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                       // desktop
        "WorkerW",                       // desktop (wallpaper host)
        "Shell_TrayWnd",                 // taskbar
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",    // start menu / search / action centre
        "XamlExplorerHostIslandWindow",  // Win11 start / task view
        "ForegroundStaging",
        "MultitaskingViewFrame",         // alt-tab / task view
        "TaskSwitcherWnd",
        "LockScreenInputOcclusionWindow",
        "LockScreenBackstopFrame",
    };

    public static bool IsFullscreenAppActive(IntPtr selfHandle)
    {
        var fg = Native.GetForegroundWindow();
        var fgUsable = fg != IntPtr.Zero && fg != selfHandle && Native.IsWindowVisible(fg) && !Native.IsIconic(fg);

        // Presentation mode is a user-wide setting rather than a window, so it applies everywhere.
        var shellState = Native.SHQueryUserNotificationState(out var state) == 0 ? state : Native.QueryUserNotificationState.AcceptsNotifications;
        if (shellState == Native.QueryUserNotificationState.PresentationMode) return true;

        // Everything else is about a window, and only one on *our* monitor matters. With no
        // usable foreground window (desktop, our own overlay) nothing is covering us.
        if (!fgUsable || selfHandle == IntPtr.Zero) return false;
        var selfMonitor = Native.MonitorFromWindow(selfHandle, Native.MONITOR_DEFAULTTONEAREST);
        var fgMonitor = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (selfMonitor != IntPtr.Zero && fgMonitor != IntPtr.Zero && selfMonitor != fgMonitor) return false;

        var className = new StringBuilder(256);
        Native.GetClassName(fg, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return false;

        // An ordinary framed window in front means the user is working in it; a fullscreen app
        // elsewhere (which the shell still reports, it is not per monitor) is not covering us.
        var style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;

        // 1. The shell's verdict, now that we know it is about a caption-less window on our
        //    monitor. This is what Windows itself uses to suppress toasts and covers exclusive
        //    fullscreen D3D apps, whose window rectangle is not always trustworthy.
        if (shellState is Native.QueryUserNotificationState.Busy
            or Native.QueryUserNotificationState.RunningD3dFullScreen)
        {
            return true;
        }

        // 2. Geometry: a caption-less window that covers its whole monitor (borderless-fullscreen
        //    games, fullscreen browser video, kiosk apps).
        if (Native.DwmGetWindowAttribute(fg, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<Native.RECT>()) != 0
            && !Native.GetWindowRect(fg, out rect))
        {
            return false;
        }

        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(fgMonitor, ref info)) return false;

        var m = info.rcMonitor;
        return rect.Left <= m.Left + 1 && rect.Top <= m.Top + 1
            && rect.Right >= m.Right - 1 && rect.Bottom >= m.Bottom - 1;
    }
}
