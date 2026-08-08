using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOS.AppSDK;
using Client.Services.Auth;

namespace Client.Services;

/// <summary>
/// Loads the language files shipped beside the client. Built-in UI must resolve stable resource
/// keys through <see cref="Get(string,string)"/>; the service deliberately does not inspect or
/// rewrite an Avalonia visual tree.
/// </summary>
public sealed class LocalizationService : ObservableObject, ISystemLanguage
{
    private const string DefaultLanguage = "en-US";
    private readonly ShellSettings _settings;
    private readonly LocalLanguageStore _localLanguageStore;
    private readonly IAuthSession _session;
    private readonly Dictionary<string, LanguageFile> _languages;
    private string _currentLanguage;

    public LocalizationService(
        ShellSettings settings,
        LocalLanguageStore localLanguageStore,
        IAuthSession session)
    {
        _settings = settings;
        _localLanguageStore = localLanguageStore;
        _session = session;
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
    }

    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyList<SystemLanguageOption> AvailableLanguages { get; }
    public event EventHandler<SystemLanguageChangedEventArgs>? LanguageChanged;

    /// <summary>Resolves a stable resource key with an English source fallback.</summary>
    public string Get(string key, string englishFallback)
    {
        var language = _languages.GetValueOrDefault(_currentLanguage);
        if (language?.Strings is { } strings
            && strings.TryGetValue(key, out var localized)
            && !string.IsNullOrWhiteSpace(localized)
            && !string.Equals(localized, key, StringComparison.Ordinal))
            return localized;

        // English is the single source-of-truth key table. Optional locale packs may lag
        // behind it, but an untranslated string must never surface the resource key.
        if (_languages.GetValueOrDefault(DefaultLanguage)?.Strings is { } english
            && english.TryGetValue(key, out var englishValue)
            && !string.IsNullOrWhiteSpace(englishValue)
            && !string.Equals(englishValue, key, StringComparison.Ordinal))
            return englishValue;

        return englishFallback;
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
        }

        if (Dispatcher.UIThread.CheckAccess())
            ApplyOnUiThread();
        else
            Dispatcher.UIThread.Post(ApplyOnUiThread);
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
