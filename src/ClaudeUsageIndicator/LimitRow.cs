using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUsageIndicator;

/// <summary>
/// One usage row on a single line: label, bar, percent, reset time, optional freshness marker.
/// Columns line up across rows through shared size groups (the parent must set
/// <c>Grid.IsSharedSizeScope</c>).
/// </summary>
internal sealed class LimitRow : Grid
{
    private static readonly SolidColorBrush Green = Frozen(0x4C, 0xAF, 0x50);
    private static readonly SolidColorBrush Amber = Frozen(0xFF, 0xB3, 0x00);
    private static readonly SolidColorBrush Red = Frozen(0xEF, 0x53, 0x50);
    private static readonly SolidColorBrush Grey = Frozen(0x9E, 0x9E, 0x9E);

    /// <summary>Fill colour for a utilisation percentage; shared with the compact dial.</summary>
    public static SolidColorBrush BrushFor(double pct) => pct < 50 ? Green : pct < 80 ? Amber : Red;

    private readonly TextBlock _name = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
    private readonly TextBlock _percent = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(7, 0, 0, 0) };
    private readonly TextBlock _reset = new() { FontSize = 10, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
    private readonly TextBlock _fresh = new() { FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill;
    private readonly ColumnDefinition _fillColumn;
    private readonly ColumnDefinition _restColumn;
    private readonly Grid _markerLayer;
    private readonly Border _marker;
    private readonly ColumnDefinition _markerColumn;
    private readonly ColumnDefinition _markerRestColumn;

    private LimitWindow? _window;

    public bool ShowReset { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;

    /// <summary>Show a tick at "how far through the window we are". Needs <see cref="WindowLength"/>.</summary>
    public bool ShowTimeMarker { get; set; } = true;

    /// <summary>Length of this limit's rolling window (5h for session, 7d for weekly); null hides the marker.</summary>
    public TimeSpan? WindowLength { get; set; }

    public LimitRow(string name)
    {
        Margin = new Thickness(0, 2, 0, 2);

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 72 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Pct" });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Reset" });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Fresh" });

        _name.Text = name;
        SetColumn(_name, 0);

        var track = new Grid { Height = 7, VerticalAlignment = VerticalAlignment.Center };
        _fillColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _restColumn = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
        track.ColumnDefinitions.Add(_fillColumn);
        track.ColumnDefinitions.Add(_restColumn);
        var background = new Border
        {
            CornerRadius = new CornerRadius(3.5),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        };
        SetColumnSpan(background, 2);
        _fill = new Border { CornerRadius = new CornerRadius(3.5), Background = Grey };
        track.Children.Add(background);
        track.Children.Add(_fill);

        // Time marker: a thin tick whose horizontal position is elapsed / window length.
        _markerLayer = new Grid { Visibility = Visibility.Collapsed };
        _markerColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _markerRestColumn = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
        _markerLayer.ColumnDefinitions.Add(_markerColumn);
        _markerLayer.ColumnDefinitions.Add(_markerRestColumn);
        _marker = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -2, -1, -2),
            Background = Brushes.White,
            Opacity = 0.9,
            CornerRadius = new CornerRadius(1),
        };
        _markerLayer.Children.Add(_marker);
        SetColumnSpan(_markerLayer, 2);
        track.Children.Add(_markerLayer);
        SetColumn(track, 1);

        SetColumn(_percent, 2);
        SetColumn(_reset, 3);
        SetColumn(_fresh, 4);

        Children.Add(_name);
        Children.Add(track);
        Children.Add(_percent);
        Children.Add(_reset);
        Children.Add(_fresh);
    }

    public void SetName(string name) => _name.Text = name;

    public void SetTextBrush(Brush brush)
    {
        _name.Foreground = brush;
        _percent.Foreground = brush;
        _reset.Foreground = brush;
        _fresh.Foreground = brush;
        _marker.Background = brush;
    }

    /// <summary>Small trailing marker such as "↻ 4m"; null hides it.</summary>
    public void SetFreshness(string? text)
    {
        _fresh.Text = text ?? "";
        _fresh.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Update(LimitWindow? window, DateTimeOffset now)
    {
        _window = window;
        if (window is null)
        {
            _percent.Text = "–";
            _fillColumn.Width = new GridLength(0, GridUnitType.Star);
            _restColumn.Width = new GridLength(100, GridUnitType.Star);
            _fill.Background = Grey;
        }
        else
        {
            var pct = Math.Clamp(window.Utilization, 0, 100);
            _percent.Text = $"{Math.Round(pct)}%";
            _fillColumn.Width = new GridLength(pct, GridUnitType.Star);
            _restColumn.Width = new GridLength(100 - pct, GridUnitType.Star);
            _fill.Background = BrushFor(pct);
        }
        UpdateCountdown(now);
    }

    public void UpdateCountdown(DateTimeOffset now)
    {
        UpdateMarker(now);

        if (!ShowReset)
        {
            _reset.Visibility = Visibility.Collapsed;
            return;
        }

        _reset.Visibility = Visibility.Visible;
        _reset.Text = _window?.ResetsAt is { } resetsAt
            ? TimeText.FormatReset(resetsAt, now, Use24HourClock)
            : "";
    }

    private void UpdateMarker(DateTimeOffset now)
    {
        if (!ShowTimeMarker || WindowLength is not { } length || length <= TimeSpan.Zero || _window?.ResetsAt is not { } resetsAt)
        {
            _markerLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var remaining = resetsAt - now;
        var elapsed = Math.Clamp(1.0 - remaining / length, 0.0, 1.0) * 100.0;
        _markerColumn.Width = new GridLength(elapsed, GridUnitType.Star);
        _markerRestColumn.Width = new GridLength(100.0 - elapsed, GridUnitType.Star);
        _markerLayer.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
