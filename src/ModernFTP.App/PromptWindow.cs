using System.Windows;
using System.Windows.Controls;

namespace ModernFTP.App;

/// <summary>Small one line text prompt, built in code so it follows the app theme like every other window.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box = new() { MinWidth = 260, Margin = new Thickness(0, 4, 0, 12) };

    public PromptWindow(string title, string label, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box.Text = initial;

        var ok = new Button { Content = "_OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 80 };
        var prompt = new Label { Content = label, Target = _box, Padding = new Thickness(0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(prompt);
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var window = new PromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true ? window._box.Text : null;
    }
}
