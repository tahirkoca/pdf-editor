using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PdfEditor.Views;

public static class PasswordDialog
{
    /// <summary>Asks for the password of an encrypted PDF; returns null when cancelled.</summary>
    public static string? Ask(Window? owner, string fileName)
    {
        var box = new PasswordBox { MinWidth = 280, Margin = new Thickness(0, 10, 0, 14) };
        var ok = new Wpf.Ui.Controls.Button { Content = "Aç", IsDefault = true, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 90 };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Vazgeç", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"{fileName} şifreli. Şifreyi girin:", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Şifreli PDF",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
        };
        window.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }
}
