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
    public const string MenuUsers = "_Users";
    public const string MenuIpRestriction = "_IP restriction";
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
    public const string ActionFailed = "Nothing to do: the session or transfer has already ended.";
    public const string BanSaveFailedFormat = "{0} is banned, but saving it to the config failed: {1}";
    public const string BannedFormat = "{0} is banned and saved to the config.";

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
    public const string EventsDroppedFormat = "{0} events dropped";
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

    // User setup window
    public const string UserSetupTitle = "User Setup";
    public const string UserList = "_List of users";
    public const string UserNew = "_New";
    public const string UserCopy = "C_opy";
    public const string UserRename = "_Rename";
    public const string UserDelete = "_Delete";
    public const string UserPromptNewTitle = "New user";
    public const string UserPromptCopyTitle = "Copy user";
    public const string UserPromptRenameTitle = "Rename user";
    public const string UserPromptLabel = "_Login name";
    public const string UserNameEmpty = "The login name cannot be empty.";
    public const string UserNameTaken = "A user with that name already exists.";
    public const string UserTabMain = "Main";
    public const string UserTabDirectories = "Directory access";
    public const string UserTabIp = "IP";
    public const string UserEnabled = "Account _enabled";
    public const string UserPassword = "_Password";
    public const string UserSetPassword = "Set pass_word";
    public const string UserPasswordSet = "A password is set. It is stored only as a hash.";
    public const string UserPasswordNone = "No password is set.";
    public const string UserPasswordUpdated = "Password updated. Save to keep it.";
    public const string UserPasswordEmpty = "Enter a password first.";
    public const string UserMaxConnections = "Max. connections for this user";
    public const string UserMaxPerIp = "Max. per IP address";
    public const string UserIdleTimeout = "Idle timeout (seconds)";
    public const string UserLimitsHint = "Empty uses the global value";
    public const string UserDownloadSpeed = "Download speed cap (KB/s)";
    public const string UserDownloadSpeedHint = "0 means unlimited";
    public const string UserDirectories = "_Directories";
    public const string UserDirPath = "Pa_th";
    public const string UserDirAlias = "A_lias";
    public const string UserDirSubdirs = "Include s_ubdirectories";
    public const string UserDirAdd = "_Add";
    public const string UserDirEdit = "_Edit";
    public const string UserDirRemove = "Re_move";
    public const string UserDirPathEmpty = "Enter a directory path.";
    public const string UserDirHomeAlias = "(home)";
    public const string UserRights = "Rights for the selected directory";
    public const string RightDownload = "Download";
    public const string RightUpload = "Upload";
    public const string RightDelete = "Delete";
    public const string RightMakeDir = "Make directory";
    public const string RightRemoveDir = "Remove directory";
    public const string RightRename = "Rename";
    public const string RightList = "List";
    public const string UserIpComing = "Per-user IP rules are coming";
    public const string UserNumberFormat = "{0} must be a whole number of 0 or more.";
    public const string UserSavedRestartNote = "Changes apply the next time the server starts.";

    // IP restriction window
    public const string IpTitle = "IP Restriction";
    public const string IpListLabel = "_Banned addresses";
    public const string IpEntryLabel = "IP address or _range";
    public const string IpEntryHint = "One IP address or CIDR range per entry, for example 203.0.113.0/24.";
    public const string IpAdd = "_Add";
    public const string IpRemove = "_Remove";
    public const string IpInvalidFormat = "{0} is not an IP address or CIDR range.";
    public const string IpDuplicateFormat = "{0} is already in the list.";
    public const string IpAllowComing = "A global allow list is coming. Only banned addresses are supported now.";

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
    public const string CaptureSetupDescription = "FTP Setup window with default values and the restart note under the Application group.";
    public const string CaptureUserMainDescription = "User Setup window, Main tab, two sample users, per-user limit fields enabled with values.";
    public const string CaptureUserDirectoryDescription = "User Setup window, Directory access tab, two sample directory entries, rights checkboxes for the selected entry.";
    public const string CaptureIpDescription = "IP Restriction window with three sample banned entries and the allow list note.";
    public const string CaptureAboutDescription = "About window.";
}
