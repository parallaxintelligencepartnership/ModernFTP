using System.Text;

namespace ModernFTP.Config.Import;

/// <summary>
/// Reads a text file written by the original. A file that is valid UTF-8 (with or without a BOM) is read as
/// UTF-8; anything else is read as Windows code page 1252, the ANSI code page the original wrote on Western
/// European systems.
/// </summary>
internal static class LegacyText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static (string Text, bool Ansi) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        try
        {
            return (StrictUtf8.GetString(bytes, start, bytes.Length - start), false);
        }
        catch (DecoderFallbackException)
        {
            var ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252)
                ?? throw new InvalidOperationException("Code page 1252 is not available.");
            return (ansi.GetString(bytes), true);
        }
    }

    public static string AnsiWarning(string path) =>
        $"{Path.GetFileName(path)} is not UTF-8; it was read as Windows code page 1252 (Western European). Check accented names and paths.";
}
