using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QuailImageSearch.Services.Models;

namespace QuailImageSearch.Views;

public enum WelcomeChoice { NotNow, InstallRecommended, ChooseModel }

/// <summary>
/// Shown at startup while no model is installed: explains how the app works and offers to download
/// the recommended model, or to pick one in Preferences.
/// </summary>
public static class WelcomeDialog
{
    public static async Task<WelcomeChoice> ShowAsync(Window owner, ModelInfo? recommended)
    {
        var result = WelcomeChoice.NotNow;
        var dialog = Create(recommended, choice => result = choice);
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>"an" for sizes read with a leading vowel sound ("an 816 MB", "an 11 GB"), else "a".</summary>
    private static string Article(string size) =>
        size.StartsWith('8') || size.StartsWith("11") || size.StartsWith("18") ? "an" : "a";

    /// <summary>Builds the dialog; <paramref name="chosen"/> is called with the button pressed, then it closes.</summary>
    public static Window Create(ModelInfo? recommended, System.Action<WelcomeChoice> chosen)
    {
        var dialog = new Window
        {
            Title = "Welcome to Quail Image Search",
            SizeToContent = SizeToContent.Height,
            Width = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var body = new StackPanel { Margin = new Avalonia.Thickness(24, 20), Spacing = 12 };
        void Para(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        Para("Search your images with plain text, for example “cat on a chair” or “sunset”.");
        // Checked against the code: the only network use is ModelManager's model download (the online model
        // list isn't fetched), and ONNX Runtime's telemetry is turned off (see ClipModel's static constructor).
        Para("Your data never leaves your computer, and no analytics or other information is collected. " +
             "The network is only used to download image models when you ask. Models and searches run entirely locally.");
        // PRIVACY.md rule 2: disclose DirectML, the one component whose diagnostics the app can't turn off.
        if (System.OperatingSystem.IsWindows())
            body.Children.Add(new TextBlock { Text = QuailImageSearch.Services.PrivacyText.DirectMLNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });
        Para("To start, download an image model. It looks at each image once to learn what's in it." +
             (recommended is not null ? $" We recommend {recommended.Name}, {Article(recommended.SizeText)} {recommended.SizeText} download." : ""));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        void Add(Button button, WelcomeChoice choice)
        {
            button.Click += (_, _) => { chosen(choice); dialog.Close(); };
            buttons.Children.Add(button);
        }
        if (recommended is not null)
            Add(new Button { Content = $"Install {recommended.Name} (recommended)", Classes = { "accent" } }, WelcomeChoice.InstallRecommended);
        Add(new Button { Content = "Choose a model…" }, WelcomeChoice.ChooseModel);
        Add(new Button { Content = "Not now", IsCancel = true }, WelcomeChoice.NotNow);
        body.Children.Add(buttons);
        dialog.Content = body;
        return dialog;
    }
}
