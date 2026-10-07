using ModernFTP.Config;
using ModernFTP.Config.Import;

namespace ModernFTP.Host.Console;

internal static class ImportCommand
{
    /// <summary>Returns 0 on success, 1 when the target exists without force, 2 when the source cannot be read or parsed.</summary>
    public static int Run(string from, string to, bool force, TextWriter output, TextWriter error)
    {
        var target = Path.GetFullPath(to);
        if (File.Exists(target) && !force)
        {
            error.WriteLine($"Refusing to overwrite {target}. Use --force to replace it.");
            return 1;
        }

        ImportResult result;
        try
        {
            result = TypsoftImporter.Import(from);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        try
        {
            ConfigLoader.Save(result.Config, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        foreach (var warning in result.Warnings)
        {
            output.WriteLine($"Warning: {warning}");
        }

        output.WriteLine($"Imported {result.Config.Users.Count} users, {result.DirectoryCount} directories, {result.Warnings.Count} warnings. Wrote {target}");
        return 0;
    }
}
