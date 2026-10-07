using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ModernFTP.App;

/// <summary>
/// The log lines in a fixed ring buffer: appending past the capacity overwrites the oldest line, so trimming
/// costs nothing. A whole batch is published with one Reset notification; there is never a per item Add or
/// Remove. Read only for bindings (the list control reads it through IList, which keeps virtualization cheap).
/// </summary>
public sealed class LogRing(int capacity) : IList, IReadOnlyList<LogLine>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);
    private static readonly PropertyChangedEventArgs CountArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerArgs = new("Item[]");

    private readonly LogLine[] _buffer = new LogLine[capacity];
    private int _start;
    private int _count;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Capacity => _buffer.Length;

    public int Count => _count;

    public bool IsReadOnly => true;

    public bool IsFixedSize => false;

    public bool IsSynchronized => false;

    public object SyncRoot => _buffer;

    public LogLine this[int index] =>
        (uint)index < (uint)_count ? _buffer[(_start + index) % _buffer.Length] : throw new ArgumentOutOfRangeException(nameof(index));

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Appends the lines, overwriting the oldest when full, then raises exactly one Reset.</summary>
    public void AppendBatch(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            _buffer[(_start + _count) % _buffer.Length] = line;
            if (_count < _buffer.Length)
            {
                _count++;
            }
            else
            {
                _start = (_start + 1) % _buffer.Length;
            }
        }

        PropertyChanged?.Invoke(this, CountArgs);
        PropertyChanged?.Invoke(this, IndexerArgs);
        CollectionChanged?.Invoke(this, ResetArgs);
    }

    public int IndexOf(object? value)
    {
        for (var i = 0; i < _count; i++)
        {
            if (ReferenceEquals(this[i], value))
            {
                return i;
            }
        }

        return -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < _count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator<LogLine> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}
