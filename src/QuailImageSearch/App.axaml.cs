using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QuailImageSearch.Services;
using QuailImageSearch.Services.Models;
using QuailImageSearch.ViewModels;
using QuailImageSearch.Views;

namespace QuailImageSearch;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = new DebugLog();
            // Renamed from "Image Search": carry its data over (copied, never moved) before anything is loaded.
            if (AppPaths.CopyLegacyData() is { } copied) log.Write(copied);
            var prefs = AppFiles.Preferences.Load();
            if (prefs is null)
                prefs = Preferences.CreateDefault(); // first run (saved when anything is changed)
            else if (prefs.Migrate())
                AppFiles.Preferences.Save(prefs);
            Appearance.ApplyAtStartup(this, prefs.Theme, prefs.ThemeMode, prefs.ThemeDensity);
            var models = new ModelManager(log);
            var tools = new ToolRegistry(log, prefs, models, new ScoreCalibrator(log));
            var vm = new MainViewModel(tools, models, prefs, log, AppFiles.Session.Load());
            if ((AppFiles.Preferences.LoadError ?? AppFiles.Session.LoadError) is { } error)
            {
                log.Write(error);
                vm.BackgroundStatus = error;
            }
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => tools.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
