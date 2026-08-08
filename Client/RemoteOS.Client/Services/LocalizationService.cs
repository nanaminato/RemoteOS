using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOS.AppSDK;
using ManagedWindow = RemoteOS.WindowManager.ManagedWindow;
using RemoteWindowManager = RemoteOS.WindowManager.WindowManager;

namespace Client.Services;

/// <summary>
/// Loads the language files shipped beside the client and keeps the built-in Avalonia UI in sync
/// with the workspace language. A new <c>Localization/*.json</c> file is enough to add a language.
/// </summary>
public sealed class LocalizationService : ObservableObject, ISystemLanguage
{
    private const string DefaultLanguage = "zh-CN";
    private readonly ShellSettings _settings;
    private readonly Dictionary<string, LanguageFile> _languages;
    private readonly RemoteWindowManager _windowManager;
    private readonly ConditionalWeakTable<AvaloniaObject, Dictionary<string, string>> _sourceValues = new();
    private readonly Dictionary<ManagedWindow, string> _windowTitles = new();
    private string _currentLanguage;

    public LocalizationService(ShellSettings settings, RemoteWindowManager windowManager)
    {
        _settings = settings;
        _windowManager = windowManager;
        _languages = LoadLanguageFiles();
        _currentLanguage = ResolveLanguage(settings.Language);
        AvailableLanguages = _languages.Values
            .OrderBy(language => language.SortOrder)
            .Select(language => new SystemLanguageOption(language.Culture, language.DisplayName))
            .ToArray();

        _settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellSettings.Language))
                SetLanguage(_settings.Language);
        };
        _windowManager.WindowOpened += (_, window) => LocalizeWindow(window);
    }

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<SystemLanguageOption> AvailableLanguages { get; }
    public event EventHandler<SystemLanguageChangedEventArgs>? LanguageChanged;

    /// <summary>Returns a translated built-in UI string, falling back to the supplied source text.</summary>
    public string Get(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        var language = _languages.GetValueOrDefault(_currentLanguage);
        return language?.Strings is { } strings && strings.TryGetValue(source, out var localized)
            ? localized
            : source;
    }

    private void SetLanguage(string requestedLanguage)
    {
        var next = ResolveLanguage(requestedLanguage);
        if (string.Equals(next, _currentLanguage, StringComparison.OrdinalIgnoreCase)) return;

        var previous = _currentLanguage;
        _currentLanguage = next;
        OnPropertyChanged(nameof(CurrentLanguage));
        LanguageChanged?.Invoke(this, new SystemLanguageChangedEventArgs(previous, next));
        Dispatcher.UIThread.Post(LocalizeOpenWindows);
    }

    private void LocalizeOpenWindows()
    {
        foreach (var window in _windowManager.Windows)
            LocalizeWindow(window);
    }

    private void LocalizeWindow(ManagedWindow window)
    {
        var title = _windowTitles.GetValueOrDefault(window) ?? window.Info.Title;
        _windowTitles[window] = title;
        window.Title = Get(title);

        LocalizeControl(window.View);
        foreach (var descendant in window.View.GetVisualDescendants().OfType<Control>())
            LocalizeControl(descendant);
    }

    private void LocalizeControl(Control control)
    {
        if (control is TextBlock textBlock && !HasBinding(textBlock, TextBlock.TextProperty))
            textBlock.Text = LocalizeValue(textBlock, nameof(TextBlock.Text), textBlock.Text);

        if (control is TextBox textBox && !HasBinding(textBox, TextBox.PlaceholderTextProperty))
            textBox.PlaceholderText = LocalizeValue(textBox, nameof(TextBox.PlaceholderText), textBox.PlaceholderText);

        if (control is ContentControl contentControl && !HasBinding(contentControl, ContentControl.ContentProperty)
            && contentControl.Content is string content)
            contentControl.Content = LocalizeValue(contentControl, nameof(ContentControl.Content), content);

        if (control is HeaderedSelectingItemsControl headered && !HasBinding(headered, HeaderedSelectingItemsControl.HeaderProperty)
            && headered.Header is string header)
            headered.Header = LocalizeValue(headered, nameof(HeaderedSelectingItemsControl.Header), header);
    }

    private static bool HasBinding(AvaloniaObject target, AvaloniaProperty property)
        => BindingOperations.GetBindingExpressionBase(target, property) is not null;

    private string? LocalizeValue(AvaloniaObject target, string propertyName, string? currentValue)
    {
        if (string.IsNullOrEmpty(currentValue)) return currentValue;
        var source = _sourceValues.GetOrCreateValue(target);
        if (!source.TryGetValue(propertyName, out var original))
        {
            original = currentValue;
            source[propertyName] = original;
        }
        return Get(original);
    }

    private string ResolveLanguage(string requestedLanguage)
    {
        if (_languages.ContainsKey(requestedLanguage)) return requestedLanguage;
        var neutral = requestedLanguage.Split('-', 2)[0];
        return _languages.Keys.FirstOrDefault(language => language.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase))
            ?? (_languages.ContainsKey(DefaultLanguage) ? DefaultLanguage : _languages.Keys.First());
    }

    private static Dictionary<string, LanguageFile> LoadLanguageFiles()
    {
        var directory = Path.Combine(System.AppContext.BaseDirectory, "Localization");
        var loaded = new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var language = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(path));
                    if (language is { Culture.Length: > 0, DisplayName.Length: > 0 })
                        loaded[language.Culture] = language with { Strings = language.Strings ?? new Dictionary<string, string>() };
                }
                catch (JsonException)
                {
                    // One malformed optional language pack must not prevent the desktop from starting.
                }
            }
        }

        if (loaded.Count == 0)
            loaded[DefaultLanguage] = new LanguageFile(DefaultLanguage, "简体中文", 0, new Dictionary<string, string>());
        return loaded;
    }

    private sealed record LanguageFile(string Culture, string DisplayName, int SortOrder, Dictionary<string, string>? Strings);
}

/// <summary>A selectable UI language discovered from a language file.</summary>
public sealed record SystemLanguageOption(string Culture, string DisplayName);
