using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace ModernFTP.Engine;

/// <summary>
/// A normalized absolute path inside a user's virtual file system ("/" is the home directory).
/// Resolution is a single pass over the input, never touches the disk, and cannot climb above
/// the root: ".." at the root stays at the root. Inputs with control characters, backslashes,
/// drive letters, Windows reserved names or other aliasing tricks are rejected outright.
/// </summary>
public sealed class VirtualPath : IEquatable<VirtualPath>
{
    public const int MaxInputLength = 2048;
    public const int MaxSegmentLength = 255;
    public const int MaxDepth = 64;

    // Backslash and colon block Windows separators, drive letters and NTFS streams; the rest are
    // invalid in Windows file names, so rejecting them everywhere keeps behavior identical per OS.
    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("\\:*?\"<>|");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly StringComparison PhysicalComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly string[] _segments;

    private VirtualPath(string[] segments) => _segments = segments;

    public static VirtualPath Root { get; } = new([]);

    public IReadOnlyList<string> Segments => _segments;

    public bool IsRoot => _segments.Length == 0;

    public string Name => IsRoot ? "/" : _segments[^1];

    public VirtualPath Parent => IsRoot ? this : new VirtualPath(_segments[..^1]);

    /// <summary>Resolves <paramref name="input"/> relative to <paramref name="current"/>.</summary>
    public static bool TryResolve(VirtualPath current, string? input, [NotNullWhen(true)] out VirtualPath? result)
    {
        ArgumentNullException.ThrowIfNull(current);
        result = null;
        if (string.IsNullOrEmpty(input))
        {
            result = current;
            return true;
        }

        if (input.Length > MaxInputLength)
        {
            return false;
        }

        var span = input.AsSpan();
        if (span.IndexOfAny(ForbiddenChars) >= 0)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        var stack = new List<string>();
        if (span[0] != '/')
        {
            stack.AddRange(current._segments);
        }

        var position = 0;
        while (position <= span.Length)
        {
            var rest = span[position..];
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            position += (slash < 0 ? rest.Length : slash) + 1;

            if (segment.IsEmpty || segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            if (!IsValidSegment(segment))
            {
                return false;
            }

            stack.Add(segment.ToString());
            if (stack.Count > MaxDepth)
            {
                return false;
            }
        }

        result = stack.Count == 0 ? Root : new VirtualPath([.. stack]);
        return true;
    }

    /// <summary>Validates a single file or directory name (no separators allowed).</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrEmpty(name)
        && name is not "." and not ".."
        && name.AsSpan().IndexOfAny(ForbiddenChars) < 0
        && name.IndexOf('/') < 0
        && !name.Any(char.IsControl)
        && IsValidSegment(name);

    private static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length > MaxSegmentLength)
        {
            return false;
        }

        // "...", "name." and "name " alias other names on Windows; reject them everywhere.
        var last = segment[^1];
        if (last is '.' or ' ')
        {
            return false;
        }

        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        if (stem.Length is 3 or 4 && ReservedNames.Contains(stem.ToString()))
        {
            return false;
        }

        return true;
    }

    /// <summary>Maps this path into <paramref name="homeDirectory"/>, with a final containment check.</summary>
    public string ToPhysical(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeDirectory));
        if (IsRoot)
        {
            return root;
        }

        var full = Path.GetFullPath(Path.Join(root, string.Join(Path.DirectorySeparatorChar, _segments)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PhysicalComparison))
        {
            throw new UnauthorizedAccessException("Path escapes the home directory.");
        }

        return full;
    }

    public override string ToString() => IsRoot ? "/" : "/" + string.Join('/', _segments);

    public bool Equals(VirtualPath? other) =>
        other is not null && _segments.AsSpan().SequenceEqual(other._segments);

    public override bool Equals(object? obj) => obj is VirtualPath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
