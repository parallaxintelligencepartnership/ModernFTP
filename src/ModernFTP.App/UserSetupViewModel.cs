using System.IO;
using System.Text.Json;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>
/// Logic behind the User Setup window. Edits the loaded config object in place, so settings the window
/// does not show survive a save. Passwords are only ever stored as PBKDF2 hashes.
/// </summary>
public sealed class UserSetupViewModel
{
    private readonly ModernFtpConfig _config;

    public UserSetupViewModel(ModernFtpConfig config)
    {
        _config = config;
    }

    public ModernFtpConfig Config => _config;

    public IReadOnlyList<UserConfig> Users => _config.Users;

    public IEnumerable<string> UserNames =>
        _config.Users.Select(u => u.Username).Order(StringComparer.OrdinalIgnoreCase);

    public UserConfig? Find(string? name) =>
        _config.Users.Find(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));

    private string? CheckName(string name, UserConfig? except = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Strings.UserNameEmpty;
        }

        var existing = Find(name.Trim());
        return existing is not null && !ReferenceEquals(existing, except) ? Strings.UserNameTaken : null;
    }

    /// <summary>Adds a disabled user without a password. Returns an error message or null.</summary>
    public string? NewUser(string name)
    {
        var error = CheckName(name);
        if (error is not null)
        {
            return error;
        }

        _config.Users.Add(new UserConfig { Username = name.Trim(), Enabled = false });
        return null;
    }

    public string? CopyUser(string source, string name)
    {
        var from = Find(source);
        var error = CheckName(name);
        if (from is null || error is not null)
        {
            return error ?? Strings.UserNameEmpty;
        }

        var copy = JsonSerializer.Deserialize<UserConfig>(JsonSerializer.Serialize(from, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)!;
        copy.Username = name.Trim();
        _config.Users.Add(copy);
        return null;
    }

    public string? RenameUser(string source, string name)
    {
        var user = Find(source);
        var error = CheckName(name, user);
        if (user is null || error is not null)
        {
            return error ?? Strings.UserNameEmpty;
        }

        user.Username = name.Trim();
        return null;
    }

    public void DeleteUser(string name)
    {
        var user = Find(name);
        if (user is not null)
        {
            _config.Users.Remove(user);
        }
    }

    /// <summary>Hashes the password with PBKDF2 and clears any plain text value.</summary>
    public static string? SetPassword(UserConfig user, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Strings.UserPasswordEmpty;
        }

        var credential = Pbkdf2Credential.Create(password);
        user.PasswordHash = Convert.ToBase64String(credential.Hash);
        user.PasswordSalt = Convert.ToBase64String(credential.Salt);
        user.PasswordIterations = credential.Iterations;
        user.Password = null;
        return null;
    }

    /// <summary>Empty text means "use the global value" (null). Otherwise a whole number of 0 or more.</summary>
    public static bool TryParseLimit(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            value = number;
            return true;
        }

        return false;
    }

    public static bool HasPassword(UserConfig user) =>
        !string.IsNullOrEmpty(user.PasswordHash) || !string.IsNullOrEmpty(user.Password);

    public static void AddDirectory(UserConfig user, string path, string? alias, bool includeSubdirectories) =>
        user.Directories.Add(new DirectoryConfig
        {
            Path = path.Trim(),
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            IncludeSubdirectories = includeSubdirectories,
        });

    public static void EditDirectory(DirectoryConfig directory, string path, string? alias, bool includeSubdirectories)
    {
        directory.Path = path.Trim();
        directory.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        directory.IncludeSubdirectories = includeSubdirectories;
    }

    /// <summary>Validates and writes the config. Returns an error message or null.</summary>
    public string? Save(string configPath)
    {
        var errors = ConfigLoader.Validate(_config, Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        if (errors.Count > 0)
        {
            return errors[0];
        }

        ConfigLoader.Save(_config, configPath);
        return null;
    }
}
