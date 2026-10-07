using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>One log line: its colored segments and the theme it was written in.</summary>
public sealed record LogLine(IReadOnlyList<LogSegment> Segments, bool Dark)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
}

/// <summary>
/// The log pane: a virtualized list over a <see cref="LogRing"/> of <see cref="MaxLines"/> lines, so only the
/// visible lines are laid out and trimming the oldest costs nothing. Each batch is one change notification and
/// at most one scroll, and only when the user was already at the bottom. Ctrl+C copies the selected lines.
/// </summary>
public sealed class LogView
{
    public const int MaxLines = 5000;

    /// <summary>Lines added per drain tick at most; further events of that tick count as dropped.</summary>
    public const int MaxLinesPerBatch = 500;

    /// <summary>Attached to the TextBlock of each row: builds its colored runs (rows are recycled, so this runs on reuse too).</summary>
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(LogLine), typeof(LogView), new PropertyMetadata(null, OnLineChanged));

    private readonly ListBox _list;
    private readonly Func<bool> _isDark;
    private readonly LogRing _lines = new(MaxLines);
    private ScrollViewer? _scroller;

    public LogView(ListBox list, Func<bool> isDark)
    {
        _list = list;
        _isDark = isDark;
        list.ItemsSource = _lines;
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(), (_, e) => e.CanExecute = list.SelectedItems.Count > 0));
    }

    public int LineCount => _lines.Count;

    /// <summary>How long the last <see cref="Append(IReadOnlyList{IReadOnlyList{LogSegment}})"/> spent updating the collection (measurements).</summary>
    public TimeSpan LastUpdate { get; private set; }

    /// <summary>How long the last append spent on the scroll request (zero when the user was not at the bottom).</summary>
    public TimeSpan LastScroll { get; private set; }

    public static LogLine? GetLine(DependencyObject element) => (LogLine?)element.GetValue(LineProperty);

    public static void SetLine(DependencyObject element, LogLine? value) => element.SetValue(LineProperty, value);

    /// <summary>
    /// The log lines for one batch: the newest <see cref="MaxLinesPerBatch"/> events except progress, preceded by
    /// one "N events dropped" line when the queue dropped events or the batch had more lines than that.
    /// </summary>
    public static List<IReadOnlyList<LogSegment>> Lines(IReadOnlyList<ServerEvent> events, long dropped)
    {
        var shown = new List<ServerEvent>(Math.Min(events.Count, MaxLinesPerBatch));
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is TransferProgressEvent)
            {
                continue;
            }

            if (shown.Count < MaxLinesPerBatch)
            {
                shown.Add(events[i]);
            }
            else
            {
                dropped++;
            }
        }

        var lines = new List<IReadOnlyList<LogSegment>>(shown.Count + 1);
        if (dropped > 0)
        {
            lines.Add([new LogSegment(string.Format(CultureInfo.CurrentCulture, Strings.EventsDroppedFormat, dropped), LogTag.Text)]);
        }

        for (var i = shown.Count - 1; i >= 0; i--)
        {
            lines.Add(LogFormatter.Format(shown[i]));
        }

        return lines;
    }

    public void AppendLine(IReadOnlyList<LogSegment> line) => Append([line]);

    /// <summary>Adds the lines with one change notification; scrolls to the end once, only if the view was at the end.</summary>
    public void Append(IReadOnlyList<IReadOnlyList<LogSegment>> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var dark = _isDark();
        var start = Math.Max(0, lines.Count - MaxLines);
        var added = new LogLine[lines.Count - start];
        for (var i = start; i < lines.Count; i++)
        {
            added[i - start] = new LogLine(lines[i], dark);
        }

        _scroller ??= FindScroller(_list);
        var atBottom = _scroller is null || _scroller.VerticalOffset >= _scroller.ScrollableHeight - 0.5;
        var watch = Stopwatch.StartNew();
        _lines.AppendBatch(added);
        LastUpdate = watch.Elapsed;
        watch.Restart();
        if (atBottom)
        {
            _scroller?.ScrollToBottom();
        }

        LastScroll = atBottom ? watch.Elapsed : TimeSpan.Zero;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScroller(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void CopySelection()
    {
        var selected = _list.SelectedItems.Cast<LogLine>().OrderBy(_lines.IndexOf).Select(l => l.Text);
        Clipboard.SetText(string.Join(Environment.NewLine, selected));
    }

    private static void OnLineChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is LogLine line)
        {
            foreach (var segment in line.Segments)
            {
                block.Inlines.Add(new Run(segment.Text) { Foreground = LogPalette.Get(segment.Tag, line.Dark) });
            }
        }
    }
}
