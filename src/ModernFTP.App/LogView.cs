using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>The log pane: appends lines in batches and keeps at most <see cref="MaxLines"/>, trimming from the top.</summary>
public sealed class LogView(RichTextBox box, Func<bool> isDark)
{
    public const int MaxLines = 5000;

    public int LineCount => box.Document.Blocks.Count;

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

    /// <summary>Adds the lines as one change: one layout pass and one scroll however many lines there are.</summary>
    public void Append(IReadOnlyList<IReadOnlyList<LogSegment>> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var dark = isDark();
        var document = box.Document;
        box.BeginChange();
        try
        {
            var start = Math.Max(0, lines.Count - MaxLines);
            var keep = MaxLines - (lines.Count - start);
            if (keep <= 0)
            {
                document.Blocks.Clear();
            }
            else
            {
                while (document.Blocks.Count > keep)
                {
                    document.Blocks.Remove(document.Blocks.FirstBlock);
                }
            }

            for (var i = start; i < lines.Count; i++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                foreach (var segment in lines[i])
                {
                    paragraph.Inlines.Add(new Run(segment.Text) { Foreground = LogPalette.Get(segment.Tag, dark) });
                }

                document.Blocks.Add(paragraph);
            }
        }
        finally
        {
            box.EndChange();
        }

        box.ScrollToEnd();
    }
}
