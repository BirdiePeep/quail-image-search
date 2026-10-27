using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuailImageSearch.Services;
using QuailImageSearch.Services.Models;
using QuailImageSearch.ViewModels;

namespace QuailImageSearch.Views;

public partial class MainWindow : Window
{
    private const double DefaultLogHeight = 240;
    private GridLength _logHeight = new(DefaultLogHeight);
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        // Keep the log scrolled to the newest entry.
        LogBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        };
        TabsControl.LayoutUpdated += (_, _) => PlaceNewTabButton();
        // Tunnel so the drag starts even though the tab handles the press itself (to select it).
        TabsControl.AddHandler(PointerPressedEvent, Tab_PointerPressed, RoutingStrategies.Tunnel);
        TabsControl.AddHandler(PointerMovedEvent, Tab_PointerMoved, RoutingStrategies.Tunnel);
        TabsControl.AddHandler(PointerReleasedEvent, Tab_PointerReleased, RoutingStrategies.Tunnel);
        TabsControl.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Tunnel);
    }

    // ---- Tab strip: "+" after the last tab, drag to reorder ----

    private const double DragThreshold = 6;
    private SearchTabViewModel? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    private TabItem? TabContainer(int index) => TabsControl.ContainerFromIndex(index) as TabItem;

    /// <summary>Keeps the "+" button just to the right of the last tab (on its row when tabs wrap).</summary>
    private void PlaceNewTabButton()
    {
        if (_vm is null || _vm.Tabs.Count == 0 || TabContainer(_vm.Tabs.Count - 1) is not { IsVisible: true } last) return;
        if (last.TranslatePoint(new Point(last.Bounds.Width, 0), (Visual)NewTabButton.Parent!) is not { } topRight) return;
        var top = topRight.Y + Math.Max(0, (last.Bounds.Height - NewTabButton.Bounds.Height) / 2);
        var margin = new Thickness(Math.Round(topRight.X + 4), Math.Round(top), 0, 0);
        if (NewTabButton.Margin != margin) NewTabButton.Margin = margin; // only on change, to avoid a layout loop
    }

    /// <summary>The tab header under <paramref name="source"/>, or null (e.g. for the close button or the tab's content).</summary>
    private TabItem? HeaderAt(object? source)
    {
        for (var v = source as Visual; v is not null && v != TabsControl; v = v.GetVisualParent())
        {
            if (v is Button) return null; // the ✕ button closes the tab; don't start a drag
            if (v is TabItem tab) return tab;
        }
        return null;
    }

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndTabDrag();
        if (!e.GetCurrentPoint(TabsControl).Properties.IsLeftButtonPressed) return;
        if (HeaderAt(e.Source)?.DataContext is not SearchTabViewModel tab) return;
        _dragTab = tab;
        _dragStart = e.GetPosition(TabsControl);
    }

    private void Tab_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || _vm is null) return;
        var pos = e.GetPosition(TabsControl);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < DragThreshold && Math.Abs(pos.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(TabsControl); // keep receiving moves while the pointer is over other tabs or outside
        }
        // Move the dragged tab into the slot of the tab under the pointer.
        for (var i = 0; i < _vm.Tabs.Count; i++)
        {
            if (TabContainer(i) is not { } container || container.TranslatePoint(default, TabsControl) is not { } origin) continue;
            if (!new Rect(origin, container.Bounds.Size).Contains(pos)) continue;
            var from = _vm.Tabs.IndexOf(_dragTab);
            if (from >= 0 && from != i) _vm.MoveTab(from, i);
            break;
        }
        e.Handled = true;
    }

    private void Tab_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _dragging;
        EndTabDrag();
        if (wasDragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void EndTabDrag()
    {
        if (_dragging) _vm?.SaveSession(); // the new order is part of the session
        _dragTab = null;
        _dragging = false;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.ShowPreferences = ShowPreferencesAsync;
        ApplyLogVisibility(_vm.IsLogVisible);
        RestorePlacement(_vm.WindowPlacement);
    }

    // ---- Window placement ----

    private void RestorePlacement(WindowPlacement? p)
    {
        if (p is null) return;
        if (p.Width >= 400 && p.Height >= 300)
        {
            Width = p.Width;
            Height = p.Height;
        }
        // Only restore the position if it's still on a connected screen.
        var pos = new PixelPoint(p.X, p.Y);
        if (Screens.All.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 40))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = pos;
        }
        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_vm is null) return;
        var previous = _vm.WindowPlacement;
        _vm.WindowPlacement = WindowState == WindowState.Normal
            ? new WindowPlacement { X = Position.X, Y = Position.Y, Width = Width, Height = Height }
            // Maximized/minimized: keep the last normal bounds so un-maximizing next time looks right.
            : new WindowPlacement
            {
                X = previous?.X ?? Position.X, Y = previous?.Y ?? Position.Y,
                Width = previous?.Width ?? Width, Height = previous?.Height ?? Height,
                Maximized = WindowState == WindowState.Maximized,
            };
        _vm.SaveSession();
    }

    /// <summary>Opens a new tab searching for images like <paramref name="imagePath"/>.</summary>
    public void FindSimilar(string imagePath) => _vm?.FindSimilar(imagePath);

    // ---- Image preview ----

    private PreviewWindow? _preview;

    /// <summary>Opens the preview window (one, reused), which follows the active tab's focused image.</summary>
    public void OpenPreview()
    {
        if (_vm is not { } vm) return;
        if (_preview is not null)
        {
            _preview.Activate();
            return;
        }
        var preview = new PreviewWindow { DataContext = new PreviewViewModel(vm) };
        preview.RestorePlacement(vm.Preferences.PreviewWindow);
        preview.Closing += (_, _) => vm.SavePreviewPlacement(preview.CurrentPlacement(vm.Preferences.PreviewWindow));
        preview.Closed += (_, _) =>
        {
            (preview.DataContext as PreviewViewModel)?.Detach();
            _preview = null;
        };
        _preview = preview;
        preview.Show(this); // owned: stays above the main window, but doesn't block it
    }

    // ---- First run ----

    /// <summary>While no model is installed, explains the app and offers to install one (every start).</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm is not { } vm || vm.Models.Installed.Any() || vm.Models.Entries.Any(m => m.IsDownloading)) return;
        var recommended = vm.Models.Entries.FirstOrDefault(m => m.Info.Id == ModelCatalog.RecommendedId);
        switch (await WelcomeDialog.ShowAsync(this, recommended?.Info))
        {
            case WelcomeChoice.InstallRecommended when recommended is not null:
                await vm.InstallModelAsync(recommended);
                break;
            case WelcomeChoice.ChooseModel:
                await ShowPreferencesAsync(PreferencesWindow.ModelsPage);
                break;
        }
    }

    // ---- Menu ----

    private System.Threading.Tasks.Task ShowPreferencesAsync() => ShowPreferencesAsync(PreferencesWindow.GeneralPage);

    /// <summary>Opens Preferences on the Models page (e.g. from a tab that has no model to search with).</summary>
    public System.Threading.Tasks.Task ShowModelPreferencesAsync() => ShowPreferencesAsync(PreferencesWindow.ModelsPage);

    private async System.Threading.Tasks.Task ShowPreferencesAsync(int page)
    {
        if (_vm is null) return;
        var dialog = new PreferencesWindow
        {
            DataContext = new PreferencesViewModel(_vm.Preferences, _vm.Models, _vm.ToolRegistry),
        };
        dialog.ShowPage(page);
        dialog.Closed += (_, _) => (dialog.DataContext as PreferencesViewModel)?.Detach();
        if (await dialog.ShowDialog<Preferences?>(this) is { } prefs)
            _vm.ApplyPreferences(prefs);
        // Light/dark and density were previewed live; this keeps them if saved and restores them if cancelled.
        if (Application.Current is { } app)
            Appearance.ApplyLive(app, _vm.Preferences.ThemeMode, _vm.Preferences.ThemeDensity);
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    // ---- Debug log panel ----

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLogVisible) && sender is MainViewModel vm)
            ApplyLogVisibility(vm.IsLogVisible);
    }

    private void ApplyLogVisibility(bool visible)
    {
        var row = MainGrid.RowDefinitions[2];
        if (visible)
        {
            row.Height = _logHeight;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }
        else
        {
            if (row.Height.Value > 0) _logHeight = row.Height; // remember the splitter size
            row.Height = new GridLength(0);
        }
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard && _vm is not null)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _vm.Log.Text);
    }
}
