using System.Net;
using System.Net.Sockets;

namespace ModernFTP.Engine;

/// <summary>Thread safe set of banned addresses and CIDR ranges, checked at accept time.</summary>
public sealed class BanList
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>
    /// Raised after an entry is added or removed, outside the list's lock, on the thread that made the
    /// change. Hosts use it to persist the list (see ModernFTP.Config's ConfigStore).
    /// </summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(e => e.Text).ToArray();
            }
        }
    }

    /// <summary>Adds an address ("203.0.113.7") or a CIDR range ("10.0.0.0/8", "2001:db8::/32").</summary>
    public void Add(string entry)
    {
        if (!TryParse(entry, out var parsed))
        {
            throw new FormatException($"'{entry}' is not an IP address or CIDR range.");
        }

        bool added;
        lock (_gate)
        {
            added = !_entries.Any(e => e.Text == parsed.Text);
            if (added)
            {
                _entries.Add(parsed);
            }
        }

        if (added)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public static bool IsValidEntry(string entry) => TryParse(entry, out _);

    public bool Remove(string entry)
    {
        if (!TryParse(entry, out var parsed))
        {
            return false;
        }

        bool removed;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e => e.Text == parsed.Text) > 0;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public bool IsBanned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.Matches(address.AddressFamily, bytes))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParse(string? text, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix))
        {
            return false;
        }

        var network = address.GetAddressBytes();
        Mask(network, prefix);
        entry = new Entry($"{new IPAddress(network)}/{prefix}", address.AddressFamily, network, prefix);
        return true;
    }

    private static void Mask(byte[] bytes, int prefix)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
    }

    private readonly record struct Entry(string Text, AddressFamily Family, byte[] Network, int Prefix)
    {
        public bool Matches(AddressFamily family, byte[] address)
        {
            if (family != Family)
            {
                return false;
            }

            var full = Prefix / 8;
            for (var i = 0; i < full; i++)
            {
                if (address[i] != Network[i])
                {
                    return false;
                }
            }

            var remaining = Prefix % 8;
            if (remaining == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remaining));
            return (address[full] & mask) == Network[full];
        }
    }
}
