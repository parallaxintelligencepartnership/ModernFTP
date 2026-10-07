using System.Runtime.InteropServices;

namespace ModernFTP.Host.Console;

/// <summary>
/// A click in a Windows console window starts a Quick Edit selection, which suspends every write to
/// the console until the selection ends. The server log must never stall, so Quick Edit is turned off
/// for this console at startup. Failures (no console, redirected input) are ignored.
/// </summary>
internal static class ConsoleQuickEdit
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode))
            {
                return;
            }

            _ = SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
