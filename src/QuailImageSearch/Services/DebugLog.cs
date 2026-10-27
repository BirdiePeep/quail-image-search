using System;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuailImageSearch.Services;

/// <summary>
/// App-wide debug log shown in the log panel: search requests, the commands tools ran, their raw
/// output and parsed results. Safe to call from any thread.
/// </summary>
public sealed partial class DebugLog : ObservableObject
{
    private const int MaxChars = 1_000_000; // keep the tail; the panel is a TextBox
    private readonly StringBuilder _text = new();

    [ObservableProperty]
    public partial string Text { get; private set; } = "";

    /// <summary>Raised synchronously on the writing thread (the panel updates later, on the UI thread).</summary>
    public event Action<string>? Written;

    public void Write(string message)
    {
        Written?.Invoke(message);
        var stamped = new StringBuilder();
        var prefix = $"[{DateTime.Now:HH:mm:ss.fff}] ";
        foreach (var line in message.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            stamped.Append(prefix).Append(line).Append('\n');
            prefix = new string(' ', prefix.Length); // indent continuation lines
        }

        Dispatcher.UIThread.Post(() =>
        {
            _text.Append(stamped);
            if (_text.Length > MaxChars)
            {
                var cut = _text.ToString().IndexOf('\n', _text.Length - MaxChars);
                _text.Remove(0, cut < 0 ? _text.Length - MaxChars : cut + 1);
            }
            Text = _text.ToString();
        });
    }

    public void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _text.Clear();
            Text = "";
        });
    }
}
