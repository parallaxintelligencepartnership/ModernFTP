using System.Globalization;
using System.IO;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>Logic behind the IP Restriction window: the server wide banned address list.</summary>
public sealed class IpRestrictionViewModel
{
    private readonly ModernFtpConfig _config;

    public IpRestrictionViewModel(ModernFtpConfig config)
    {
        _config = config;
        Entries = [.. config.BannedAddresses];
    }

    public List<string> Entries { get; }

    /// <summary>Adds one IP address or CIDR range. Returns an error message or null.</summary>
    public string? Add(string text)
    {
        var entry = text.Trim();
        if (!BanList.IsValidEntry(entry))
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.IpInvalidFormat, entry.Length == 0 ? "(empty)" : entry);
        }

        if (Entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.IpDuplicateFormat, entry);
        }

        Entries.Add(entry);
        return null;
    }

    public void Remove(string entry) => Entries.Remove(entry);

    /// <summary>Writes the list onto the loaded config and saves it. Returns an error message or null.</summary>
    public string? Save(string configPath)
    {
        _config.BannedAddresses = [.. Entries];
        var errors = ConfigLoader.Validate(_config, Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        if (errors.Count > 0)
        {
            return errors[0];
        }

        ConfigLoader.Save(_config, configPath);
        return null;
    }
}
