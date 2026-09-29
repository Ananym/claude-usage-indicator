using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUsageIndicator;

/// <summary>One Claude Code session on a single line: state dot, name, state text.</summary>
internal sealed class SessionRow : Grid
{
    private static readonly SolidColorBrush WaitingBrush = Frozen(0x4C, 0xAF, 0x50);
    private static readonly SolidColorBrush BusyBrush = Frozen(0x64, 0xB5, 0xF6);

    private readonly TextBlock _dot = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _name = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _state = new() { FontSize = 10, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0), TextAlignment = TextAlignment.Right };

    private SessionInfo? _session;

    /// <summary>See <see cref="SessionInfo.Label"/> for the accepted styles.</summary>
    public string LabelStyle { get; set; } = "folder-title";

    public SessionRow()
    {
        Margin = new Thickness(0, 2, 0, 2);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SessionState" });
        SetColumn(_dot, 0);
        SetColumn(_name, 1);
        SetColumn(_state, 2);
        Children.Add(_dot);
        Children.Add(_name);
        Children.Add(_state);
    }

    public void SetTextBrush(Brush brush)
    {
        _name.Foreground = brush;
        _state.Foreground = brush;
    }

    public void Update(SessionInfo session, DateTimeOffset now)
    {
        _session = session;
        _name.Text = session.Label(LabelStyle);
        _name.ToolTip = BuildToolTip(session);
        _dot.Text = session.Waiting ? "○" : "●";
        _dot.Foreground = session.Waiting ? WaitingBrush : BusyBrush;
        UpdateClock(now);
    }

    public void UpdateClock(DateTimeOffset now)
    {
        if (_session is null) return;
        _state.Text = _session.Waiting
            ? "waiting " + TimeText.Duration(now - _session.StatusSince)
            : _session.Busy ? "working" : _session.Status;
    }

    private static string BuildToolTip(SessionInfo s)
    {
        var lines = new System.Collections.Generic.List<string> { s.Cwd };
        if (s.ChosenName is { } chosen) lines.Add("Name: " + chosen);
        if (s.Title is { } title) lines.Add("Title: " + title);
        if (s.LastPrompt is { } prompt) lines.Add("Last prompt: " + prompt);
        lines.Add($"Session {s.SessionId} · pid {s.Pid} · {s.Status}");
        return string.Join(Environment.NewLine, lines);
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
