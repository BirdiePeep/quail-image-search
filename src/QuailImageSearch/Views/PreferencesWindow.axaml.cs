using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuailImageSearch.Services;
using QuailImageSearch.ViewModels;

namespace QuailImageSearch.Views;

/// <summary>Shown with ShowDialog; closes with the new <see cref="Preferences"/>, or null if cancelled.</summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
    }

    private PreferencesViewModel? Vm => DataContext as PreferencesViewModel;

    /// <summary>Page indexes for <see cref="ShowPage"/>.</summary>
    public const int GeneralPage = 0, ModelsPage = 1;

    public void ShowPage(int index) => PageTabs.SelectedIndex = index;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.Confirm = message => ConfirmDialog.ShowAsync(this, "Delete search index", message, "Delete");
            vm.ConfirmWithOption = (title, message, confirmText, optionText, optionDefault) =>
                ConfirmDialog.ShowAsync(this, title, message, confirmText, optionText, optionDefault);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.TryBuild() is { } prefs)
            Close(prefs);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape && !e.Handled)
            Close(null);
    }

    private async void BrowseToolExe_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedCustomTool is { } tool && await PickExecutableAsync("Choose the tool's executable") is { } path)
            tool.Executable = path;
    }

    private async Task<string?> PickExecutableAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }, FilePickerFileTypes.All]
                : [FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
