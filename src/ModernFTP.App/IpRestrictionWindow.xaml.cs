using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernFTP.Config;

namespace ModernFTP.App;

public partial class IpRestrictionWindow : Window
{
    private readonly IpRestrictionViewModel _model;
    private readonly string? _configPath;

    /// <param name="configPath">Where Save writes the config. Null disables writing (capture mode).</param>
    public IpRestrictionWindow(ModernFtpConfig config, string? configPath)
    {
        InitializeComponent();
        _model = new IpRestrictionViewModel(config);
        _configPath = configPath;
        Refresh();
    }

    private void Refresh()
    {
        IpBox.ItemsSource = null;
        IpBox.ItemsSource = _model.Entries;
        RemoveButton.IsEnabled = IpBox.SelectedItem is not null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = IpBox.SelectedItem is not null;

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var error = _model.Add(EntryBox.Text);
        ErrorText.Text = error ?? string.Empty;
        if (error is null)
        {
            EntryBox.Clear();
            Refresh();
        }

        EntryBox.Focus();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (IpBox.SelectedItem is string entry)
        {
            _model.Remove(entry);
            ErrorText.Text = string.Empty;
            Refresh();
        }
    }

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAdd(sender, e);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            OnRemove(sender, e);
            e.Handled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
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
