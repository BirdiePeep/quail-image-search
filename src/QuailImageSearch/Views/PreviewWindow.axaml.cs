using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuailImageSearch.Services;
using QuailImageSearch.ViewModels;

namespace QuailImageSearch.Views;

/// <summary>
/// The image preview (double-click a result). One window, reused; not modal, so the main window
/// stays usable, and it can sit on another monitor. Its size and position are kept in preferences.
/// Keys: Left/Right previous/next, R / Shift+R rotate right/left, Esc closes.
/// </summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();
        // Tunnel, so arrow keys reach us before any focused button uses them to move focus.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
    }

    private PreviewViewModel? Vm => DataContext as PreviewViewModel;

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                vm.PreviousCommand.Execute(null);
                break;
            case Key.Right:
                vm.NextCommand.Execute(null);
                break;
            case Key.R when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                (shift ? vm.RotateLeftCommand : vm.RotateRightCommand).Execute(null);
                break;
            case Key.Enter:
                Open_Click(null, e);
                break;
            case Key.C when e.KeyModifiers.HasFlag(CommandModifier):
                Copy_Click(null, e);
                break;
            case Key.Delete:
                Delete_Click(null, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Ctrl on Windows/Linux, Cmd on macOS.</summary>
    private static KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    // ---- Right-click menu (same actions as on the result tiles) ----

    private async void Open_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(vm => FileActions.OpenAsync(this, [vm.Item!.Path]));

    private async void ShowInFolder_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(vm => FileActions.ShowInFolderAsync(this, [vm.Item!.Path]));

    private void FindSimilar_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { Item: { } item } && Owner is MainWindow main) main.FindSimilar(item.Path);
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(async vm => vm.Message = await FileCommands.CopyAsync(this, [vm.Item!.Path]));

    /// <summary>
    /// Moves the shown image to the Recycle Bin (after confirming), then shows the next one (the
    /// previous one if it was the last); closes the preview when no images are left.
    /// </summary>
    private async void Delete_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(async vm =>
        {
            var item = vm.Item!;
            var index = item.Index;
            var tab = vm.Tab;
            if (await FileCommands.DeleteAsync(this, [item.Path]) is not { } done) return;
            if (done.Result.Moved.Count > 0)
            {
                tab?.OnFilesDeleted(done.Result.Moved); // drops it from every tab and index
                if (tab is null || tab.ShownCount == 0)
                {
                    Close();
                    return;
                }
                tab.FocusAt(index); // the image that took its place, or the new last one
            }
            vm.Message = done.Message;
        });

    private async System.Threading.Tasks.Task RunAsync(Func<PreviewViewModel, System.Threading.Tasks.Task> action)
    {
        if (Vm is not { Item: not null } vm) return;
        try
        {
            await action(vm);
        }
        catch (Exception ex)
        {
            vm.Message = "Error: " + ex.Message;
        }
    }

    /// <summary>Restores a saved size/position, if it's still on a connected screen.</summary>
    public void RestorePlacement(WindowPlacement? p)
    {
        if (p is null) return;
        if (p.Width >= MinWidth && p.Height >= MinHeight)
        {
            Width = p.Width;
            Height = p.Height;
        }
        var pos = new PixelPoint(p.X, p.Y);
        if (Screens.All.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 40))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = pos;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>The current size/position (the last normal bounds while maximized).</summary>
    public WindowPlacement CurrentPlacement(WindowPlacement? previous) => WindowState == WindowState.Normal
        ? new WindowPlacement { X = Position.X, Y = Position.Y, Width = Width, Height = Height }
        : new WindowPlacement
        {
            X = previous?.X ?? Position.X, Y = previous?.Y ?? Position.Y,
            Width = previous?.Width ?? Width, Height = previous?.Height ?? Height,
            Maximized = WindowState == WindowState.Maximized,
        };
}
