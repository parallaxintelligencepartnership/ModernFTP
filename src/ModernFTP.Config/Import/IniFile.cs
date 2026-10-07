namespace ModernFTP.Config.Import;

/// <summary>Minimal reader for the Windows INI files the original wrote. Keeps line numbers for error messages.</summary>
internal sealed class IniFile
{
    private readonly List<IniSection> _sections = [];

    public IReadOnlyList<IniSection> Sections => _sections;

    /// <summary>True when the file was not UTF-8 and was read as code page 1252.</summary>
    public bool DecodedAsAnsi { get; private set; }

    public static IniFile Parse(string path)
    {
        var file = new IniFile();
        IniSection? current = null;
        var number = 0;
        var (text, ansi) = LegacyText.Read(path);
        file.DecodedAsAnsi = ansi;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            number++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (!line.EndsWith(']'))
                {
                    throw new InvalidDataException($"{path} line {number}: section header is missing its closing bracket.");
                }

                var name = line[1..^1].Trim();
                current = file._sections.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    current = new IniSection(name);
                    file._sections.Add(current);
                }

                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new InvalidDataException($"{path} line {number}: expected key=value, a [section] or a comment.");
            }

            if (current is null)
            {
                throw new InvalidDataException($"{path} line {number}: key found before any [section].");
            }

            current.Entries.Add(new IniEntry(line[..equals].Trim(), line[(equals + 1)..].Trim(), number));
        }

        return file;
    }

    public IniSection? Find(string name) =>
        _sections.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class IniSection(string name)
{
    public string Name { get; } = name;

    public List<IniEntry> Entries { get; } = [];

    public IniEntry? Find(string key) =>
        Entries.FindLast(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? Get(string key) => Find(key)?.Value;
}

internal sealed record IniEntry(string Key, string Value, int Line);
