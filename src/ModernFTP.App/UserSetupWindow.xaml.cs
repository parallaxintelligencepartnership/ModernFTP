using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernFTP.Config;

namespace ModernFTP.App;

public partial class UserSetupWindow : Window
{
    private readonly UserSetupViewModel _model;
    private readonly string? _configPath;
    private UserConfig? _user;
    private bool _loading;
    private bool _speedInvalid;
    private readonly HashSet<TextBox> _invalidLimits = [];

    /// <param name="configPath">Where Save writes the config. Null disables writing (capture mode).</param>
    public UserSetupWindow(ModernFtpConfig config, string? configPath)
    {
        InitializeComponent();
        _model = new UserSetupViewModel(config);
        _configPath = configPath;
        RefreshUsers(null);
    }

    public void ShowDirectoryTab() => Tabs.SelectedItem = DirectoryTab;

    private void RefreshUsers(string? select)
    {
        _loading = true;
        UserBox.ItemsSource = _model.UserNames.ToList();
        _loading = false;
        UserBox.SelectedItem = select ?? UserBox.Items.Cast<string>().FirstOrDefault();
        if (UserBox.SelectedItem is null)
        {
            LoadUser(null);
        }
    }

    private void OnUserChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadUser(_model.Find(UserBox.SelectedItem as string));
        }
    }

    private void LoadUser(UserConfig? user)
    {
        _loading = true;
        _user = user;
        var has = user is not null;
        MainPanel.IsEnabled = has;
        DirectoryPanel.IsEnabled = has;
        CopyButton.IsEnabled = has;
        RenameButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
        EnabledBox.IsChecked = user?.Enabled ?? false;
        PasswordBox.Clear();
        PasswordStatus.Text = user is null ? string.Empty
            : UserSetupViewModel.HasPassword(user) ? Strings.UserPasswordSet : Strings.UserPasswordNone;
        SpeedBox.Text = (user?.DownloadRateKBps ?? 0).ToString(CultureInfo.InvariantCulture);
        UserMaxConnBox.Text = user?.MaxConnections?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        UserMaxIpBox.Text = user?.MaxConnectionsPerIp?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        UserIdleBox.Text = user?.IdleTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _invalidLimits.Clear();
        _speedInvalid = false;
        _loading = false;
        ErrorText.Text = string.Empty;
        RefreshDirectories(has && user!.Directories.Count > 0 ? 0 : -1);
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_user is not null)
        {
            _user.Enabled = EnabledBox.IsChecked == true;
        }
    }

    private void OnSetPassword(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            return;
        }

        var error = UserSetupViewModel.SetPassword(_user, PasswordBox.Password);
        ErrorText.Text = error ?? string.Empty;
        if (error is null)
        {
            PasswordBox.Clear();
            PasswordStatus.Text = Strings.UserPasswordUpdated;
        }
    }

    private void OnSpeedChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _user is null)
        {
            return;
        }

        if (int.TryParse(SpeedBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            _user.DownloadRateKBps = value;
            _speedInvalid = false;
            ErrorText.Text = string.Empty;
        }
        else
        {
            _speedInvalid = true;
            ErrorText.Text = string.Format(CultureInfo.CurrentCulture, Strings.UserNumberFormat, Strings.UserDownloadSpeed);
        }
    }

    private void OnLimitChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _user is null)
        {
            return;
        }

        var box = (TextBox)sender;
        var label = box == UserMaxConnBox ? Strings.UserMaxConnections : box == UserMaxIpBox ? Strings.UserMaxPerIp : Strings.UserIdleTimeout;
        if (!UserSetupViewModel.TryParseLimit(box.Text, out var value))
        {
            _invalidLimits.Add(box);
            ErrorText.Text = string.Format(CultureInfo.CurrentCulture, Strings.UserNumberFormat, label);
            return;
        }

        _invalidLimits.Remove(box);
        ErrorText.Text = string.Empty;
        if (box == UserMaxConnBox)
        {
            _user.MaxConnections = value;
        }
        else if (box == UserMaxIpBox)
        {
            _user.MaxConnectionsPerIp = value;
        }
        else
        {
            _user.IdleTimeoutSeconds = value;
        }
    }

    // User list actions

    private void OnNewUser(object sender, RoutedEventArgs e) => Prompt(Strings.UserPromptNewTitle, string.Empty, name => _model.NewUser(name));

    private void OnCopyUser(object sender, RoutedEventArgs e)
    {
        if (_user is { } source)
        {
            Prompt(Strings.UserPromptCopyTitle, source.Username, name => _model.CopyUser(source.Username, name));
        }
    }

    private void OnRenameUser(object sender, RoutedEventArgs e)
    {
        if (_user is { } source)
        {
            Prompt(Strings.UserPromptRenameTitle, source.Username, name => _model.RenameUser(source.Username, name));
        }
    }

    private void Prompt(string title, string initial, Func<string, string?> action)
    {
        var name = PromptWindow.Ask(this, title, Strings.UserPromptLabel, initial);
        if (name is null)
        {
            return;
        }

        var error = action(name);
        ErrorText.Text = error ?? string.Empty;
        if (error is null)
        {
            RefreshUsers(name.Trim());
        }
    }

    private void OnDeleteUser(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            return;
        }

        var index = UserBox.SelectedIndex;
        _model.DeleteUser(_user.Username);
        RefreshUsers(null);
        if (UserBox.Items.Count > 0)
        {
            UserBox.SelectedIndex = Math.Min(index, UserBox.Items.Count - 1);
        }
    }

    private void OnUserKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            OnDeleteUser(sender, e);
            e.Handled = true;
        }
    }

    // Directory access

    private void RefreshDirectories(int select)
    {
        _loading = true;
        DirBox.ItemsSource = _user?.Directories.Select(Describe).ToList();
        _loading = false;
        DirBox.SelectedIndex = select;
        if (select < 0)
        {
            LoadDirectory(null);
        }
    }

    private static string Describe(DirectoryConfig d) =>
        d.Alias is null ? $"{d.Path}  {Strings.UserDirHomeAlias}" : $"{d.Path}  ({d.Alias})";

    private DirectoryConfig? SelectedDirectory =>
        _user is not null && DirBox.SelectedIndex >= 0 && DirBox.SelectedIndex < _user.Directories.Count
            ? _user.Directories[DirBox.SelectedIndex]
            : null;

    private void OnDirChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            LoadDirectory(SelectedDirectory);
        }
    }

    private void LoadDirectory(DirectoryConfig? d)
    {
        var was = _loading;
        _loading = true;
        var has = d is not null;
        DirEditButton.IsEnabled = has;
        DirRemoveButton.IsEnabled = has;
        foreach (var box in new[] { RightDownload, RightUpload, RightDelete, RightMakeDir, RightRemoveDir, RightRename, RightList })
        {
            box.IsEnabled = has;
        }

        if (d is not null)
        {
            DirPathBox.Text = d.Path;
            DirAliasBox.Text = d.Alias ?? string.Empty;
            DirSubBox.IsChecked = d.IncludeSubdirectories;
        }

        var p = d?.Permissions ?? new PermissionsConfig { Download = false, List = false };
        RightDownload.IsChecked = p.Download;
        RightUpload.IsChecked = p.Upload;
        RightDelete.IsChecked = p.Delete;
        RightMakeDir.IsChecked = p.MakeDir;
        RightRemoveDir.IsChecked = p.RemoveDir;
        RightRename.IsChecked = p.Rename;
        RightList.IsChecked = p.List;
        _loading = was;
    }

    private void OnRightClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDirectory is not { } d)
        {
            return;
        }

        var p = d.Permissions;
        p.Download = RightDownload.IsChecked == true;
        p.Upload = RightUpload.IsChecked == true;
        p.Delete = RightDelete.IsChecked == true;
        p.MakeDir = RightMakeDir.IsChecked == true;
        p.RemoveDir = RightRemoveDir.IsChecked == true;
        p.Rename = RightRename.IsChecked == true;
        p.List = RightList.IsChecked == true;
    }

    private void OnDirAdd(object sender, RoutedEventArgs e)
    {
        if (_user is null || !CheckPath())
        {
            return;
        }

        UserSetupViewModel.AddDirectory(_user, DirPathBox.Text, DirAliasBox.Text, DirSubBox.IsChecked == true);
        RefreshDirectories(_user.Directories.Count - 1);
    }

    private void OnDirEdit(object sender, RoutedEventArgs e)
    {
        if (SelectedDirectory is not { } d || !CheckPath())
        {
            return;
        }

        var index = DirBox.SelectedIndex;
        UserSetupViewModel.EditDirectory(d, DirPathBox.Text, DirAliasBox.Text, DirSubBox.IsChecked == true);
        RefreshDirectories(index);
    }

    private void OnDirRemove(object sender, RoutedEventArgs e)
    {
        if (_user is null || SelectedDirectory is not { } d)
        {
            return;
        }

        var index = DirBox.SelectedIndex;
        _user.Directories.Remove(d);
        RefreshDirectories(_user.Directories.Count == 0 ? -1 : Math.Min(index, _user.Directories.Count - 1));
    }

    private bool CheckPath()
    {
        if (string.IsNullOrWhiteSpace(DirPathBox.Text))
        {
            ErrorText.Text = Strings.UserDirPathEmpty;
            DirPathBox.Focus();
            return false;
        }

        ErrorText.Text = string.Empty;
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_speedInvalid)
        {
            SpeedBox.Focus();
            return;
        }

        if (_invalidLimits.FirstOrDefault() is { } invalid)
        {
            invalid.Focus();
            return;
        }

        if (_configPath is null)
        {
            return;
        }

        try
        {
            var error = _model.Save(_configPath);
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, ex.Message, Strings.SetupSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
