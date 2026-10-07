namespace ModernFTP.App;

/// <summary>Every user visible string of the app. US English; no dashes used as punctuation.</summary>
public static class Strings
{
    public const string AppName = "ModernFTP";

    // Menus
    public const string MenuFile = "_File";
    public const string MenuExit = "E_xit";
    public const string MenuServer = "_Server";
    public const string MenuStart = "_Start";
    public const string MenuStop = "S_top";
    public const string MenuSetup = "S_etup";
    public const string MenuFtpSetup = "_FTP Setup";
    public const string MenuTray = "_Tray";
    public const string MenuMinimizeToTray = "_Minimize to tray";
    public const string MenuHelp = "_Help";
    public const string MenuAbout = "_About";

    // Tabs and columns
    public const string TabLog = "Log";
    public const string TabUsers = "Users";
    public const string ColId = "ID";
    public const string ColUser = "User";
    public const string ColIp = "IP";
    public const string ColIdle = "Idle time";
    public const string ColTransfer = "Current transfer";
    public const string ColProgress = "Progress";
    public const string ColTimeLeft = "Time left";

    // Users page buttons
    public const string ButtonDisconnect = "_Disconnect";
    public const string ButtonAbort = "_Abort transfer";
    public const string ButtonBan = "_Ban IP";
    public const string NotSupportedYet = "The engine does not support this action yet.";
    public const string BannedFormat = "{0} is banned until the server restarts.";

    // Status bar
    public const string StatusStopped = "Server stopped";
    public const string StatusRunning = "Server running";
    public const string StatusConnectionsFormat = "Connections: {0}";
    public const string StatusSentFormat = "Sent: {0}";
    public const string StatusReceivedFormat = "Received: {0}";

    // Messages
    public const string StartFailedTitle = "The server could not start";
    public const string ServerStartedFormat = "Server started on {0}.";
    public const string ServerStopped = "Server stopped.";
    public const string NoUsersWarning = "No users are configured. Add users to the config file before clients can log in.";

    // Setup window
    public const string SetupTitle = "FTP Setup";
    public const string SetupGroupNetwork = "Network";
    public const string SetupGroupLimits = "Limits";
    public const string SetupGroupMessages = "Messages";
    public const string SetupGroupApp = "Application";
    public const string SetupBindAddress = "_Bind address";
    public const string SetupPort = "FTP _port";
    public const string SetupPasvRange = "_Passive port range";
    public const string SetupPasvTo = "to";
    public const string SetupPasvIp = "Public _IP for passive mode";
    public const string SetupMaxConnections = "_Max. connections";
    public const string SetupMaxPerIp = "Max. per _IP address";
    public const string SetupIdleTimeout = "Idle _timeout (seconds)";
    public const string SetupZeroUnlimited = "0 means no limit";
    public const string SetupWelcome = "_Welcome text";
    public const string SetupGoodbye = "_Goodbye text";
    public const string SetupHideServerName = "Hide the server name in the banner";
    public const string SetupStartMinimized = "Start minimized to tray";
    public const string SetupStartWithWindows = "Start with Windows";
    public const string SetupRestartNote = "Changes to network settings apply the next time the server starts.";
    public const string SetupSave = "_Save";
    public const string SetupClose = "_Close";
    public const string SetupSaveFailedTitle = "Settings were not saved";
    public const string SetupNumberFormat = "{0} must be a whole number.";
    public const string SetupSaved = "Settings saved.";

    // About window
    public const string AboutTitle = "About ModernFTP";
    public const string AboutVersionFormat = "Version {0}";
    public const string AboutCopyright = "Copyright Parallax Intelligence Partnership, LLC";
    public const string AboutInspired = "Inspired by TYPSoft FTP Server by Marc Bergeron (2003)";
    public const string AboutLicenseFormat = "License: {0}";
    public const string AboutLicenseName = "PolyForm Noncommercial License 1.0.0";
    public const string AboutRepoText = "github.com/parallaxintelligencepartnership/ModernFTP";
    public const string AboutRepoUrl = "https://github.com/parallaxintelligencepartnership/ModernFTP";
    public const string AboutClose = "_Close";

    // Tray
    public const string TrayShow = "Show";
    public const string TrayStart = "Start";
    public const string TrayStop = "Stop";
    public const string TrayExit = "Exit";
    public const string TrayHint = "ModernFTP";

    // Sample data used by capture mode
    public const string CaptureLogPageDescription = "Main window, Log page, ten sample log lines.";
    public const string CaptureUsersPageDescription = "Main window, Users page, two sample sessions.";
    public const string CaptureSetupDescription = "FTP Setup window with default values.";
    public const string CaptureAboutDescription = "About window.";
}
