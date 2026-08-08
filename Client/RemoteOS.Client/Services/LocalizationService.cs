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
using Client.Services.Auth;
using ManagedWindow = RemoteOS.WindowManager.ManagedWindow;
using RemoteWindowManager = RemoteOS.WindowManager.WindowManager;

namespace Client.Services;

/// <summary>
/// Loads the language files shipped beside the client and keeps the built-in Avalonia UI in sync
/// with the workspace language. A new <c>Localization/*.json</c> file is enough to add a language.
/// </summary>
public sealed class LocalizationService : ObservableObject, ISystemLanguage
{
    private const string DefaultLanguage = "en-US";
    private readonly ShellSettings _settings;
    private readonly LocalLanguageStore _localLanguageStore;
    private readonly IAuthSession _session;
    private readonly Dictionary<string, LanguageFile> _languages;
    private readonly RemoteWindowManager _windowManager;
    private readonly ConditionalWeakTable<AvaloniaObject, Dictionary<string, string>> _sourceValues = new();
    private readonly Dictionary<ManagedWindow, string> _windowTitles = new();
    private string _currentLanguage;

    public LocalizationService(
        ShellSettings settings,
        LocalLanguageStore localLanguageStore,
        IAuthSession session,
        RemoteWindowManager windowManager)
    {
        _settings = settings;
        _localLanguageStore = localLanguageStore;
        _session = session;
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
            {
                if (_session.State != AuthSessionState.Authenticated)
                    _localLanguageStore.Save(_settings.Language);
                SetLanguage(_settings.Language);
            }
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
        void ApplyOnUiThread()
        {
            LanguageChanged?.Invoke(this, new SystemLanguageChangedEventArgs(previous, next));
            LocalizeOpenWindows();
        }

        if (Dispatcher.UIThread.CheckAccess())
            ApplyOnUiThread();
        else
            Dispatcher.UIThread.Post(ApplyOnUiThread);
    }

    private void LocalizeOpenWindows()
    {
        foreach (var window in _windowManager.Windows)
            LocalizeWindow(window);
    }

    /// <summary>Localizes a top-level or managed built-in control tree without altering bound values.</summary>
    public void Localize(Control root)
    {
        LocalizeControl(root);
        foreach (var descendant in root.GetVisualDescendants().OfType<Control>())
            LocalizeControl(descendant);
    }

    private void LocalizeWindow(ManagedWindow window)
    {
        var title = _windowTitles.GetValueOrDefault(window) ?? window.Info.Title;
        _windowTitles[window] = title;
        window.Title = Get(title);

        Localize(window.View);
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

        if (ToolTip.GetTip(control) is string toolTip)
            ToolTip.SetTip(control, LocalizeValue(control, "ToolTip", toolTip));
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
            loaded[DefaultLanguage] = new LanguageFile(DefaultLanguage, "English", 0, new Dictionary<string, string>());
        return loaded;
    }

    private sealed record LanguageFile(string Culture, string DisplayName, int SortOrder, Dictionary<string, string>? Strings);
}

/// <summary>A selectable UI language discovered from a language file.</summary>
public sealed record SystemLanguageOption(string Culture, string DisplayName);
