using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>One log line: its colored segments and the theme it was written in.</summary>
public sealed record LogLine(IReadOnlyList<LogSegment> Segments, bool Dark)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
}

/// <summary>
/// The log pane: a virtualized list, so only the visible lines are laid out however many are kept. Lines are
/// added in batches with one change notification per batch, at most <see cref="MaxLines"/> are kept (trimmed
/// from the top), and Ctrl+C copies the selected lines.
/// </summary>
public sealed class LogView
{
    public const int MaxLines = 5000;

    /// <summary>Attached to the TextBlock of each row: builds its colored runs (rows are recycled, so this runs on reuse too).</summary>
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(LogLine), typeof(LogView), new PropertyMetadata(null, OnLineChanged));

    private readonly ListBox _list;
    private readonly Func<bool> _isDark;
    private readonly BatchCollection _lines = new();

    public LogView(ListBox list, Func<bool> isDark)
    {
        _list = list;
        _isDark = isDark;
        list.ItemsSource = _lines;
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(), (_, e) => e.CanExecute = list.SelectedItems.Count > 0));
    }

    public int LineCount => _lines.Count;

    public static LogLine? GetLine(DependencyObject element) => (LogLine?)element.GetValue(LineProperty);

    public static void SetLine(DependencyObject element, LogLine? value) => element.SetValue(LineProperty, value);

    /// <summary>The log lines for one batch: a drop notice first when events were dropped, then every event except progress.</summary>
    public static List<IReadOnlyList<LogSegment>> Lines(IReadOnlyList<ServerEvent> events, long dropped)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        if (dropped > 0)
        {
            lines.Add([new LogSegment(string.Format(CultureInfo.CurrentCulture, Strings.EventsDroppedFormat, dropped), LogTag.Text)]);
        }

        // Lines that would be trimmed straight away are not formatted at all.
        var skip = Math.Max(0, events.Count - MaxLines);
        for (var i = skip; i < events.Count; i++)
        {
            if (events[i] is not TransferProgressEvent)
            {
                lines.Add(LogFormatter.Format(events[i]));
            }
        }

        return lines;
    }

    public void AppendLine(IReadOnlyList<LogSegment> line) => Append([line]);

    /// <summary>Adds the lines with one change notification and scrolls to the newest line once.</summary>
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

        _lines.AppendAndTrim(added, MaxLines);
        _list.ScrollIntoView(_lines[^1]);
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

    /// <summary>An observable list that adds and trims a whole batch with a single Reset notification.</summary>
    private sealed class BatchCollection : ObservableCollection<LogLine>
    {
        public void AppendAndTrim(IReadOnlyList<LogLine> added, int max)
        {
            CheckReentrancy();
            var items = (List<LogLine>)Items;
            items.AddRange(added);
            if (items.Count > max)
            {
                items.RemoveRange(0, items.Count - max);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
