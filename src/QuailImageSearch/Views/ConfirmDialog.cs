using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace QuailImageSearch.Views;

/// <summary>Minimal OK/Cancel confirmation dialog (Avalonia has no built-in message box).</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText) =>
        (await ShowAsync(owner, title, message, confirmText, null, false)).Confirmed;

    /// <summary>With an optional checkbox (<paramref name="optionText"/>); returns whether it was ticked.</summary>
    public static async Task<(bool Confirmed, bool Option)> ShowAsync(
        Window owner, string title, string message, string confirmText, string? optionText, bool optionDefault)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var confirm = new Button { Content = confirmText, Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var body = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16, MaxWidth = 460 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        CheckBox? option = null;
        if (optionText is not null)
        {
            option = new CheckBox { Content = optionText, IsChecked = optionDefault };
            body.Children.Add(option);
        }
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { confirm, cancel },
        });
        dialog.Content = body;

        var confirmed = await dialog.ShowDialog<bool>(owner);
        return (confirmed, option?.IsChecked == true);
    }
}
