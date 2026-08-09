using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Windows;

namespace RemoteOS.WindowManager;

/// <summary>
/// View-model / public handle for a single managed window. Bound to both the
/// <see cref="RemoteWindow"/> chrome and the taskbar entry. The <see cref="WindowManager"/>
/// owns the authoritative state in <see cref="Info"/>; this object mirrors it for binding.
/// </summary>
public partial class ManagedWindow : ObservableObject
{
    public const string MinimizeGlyph = "\uE921";
    public const string CloseGlyph = "\uE8BB";
    public const string MaximizeGlyphChar = "\uE922";
    public const string RestoreGlyphChar = "\uE923";

    public ManagedWindow(WindowInfo info, RemoteWindow view, bool isModalDialog = false)
    {
        Info = info;
        View = view;
        IsModalDialog = isModalDialog;
        Sync();
    }

    public WindowInfo Info { get; }
    public RemoteWindow View { get; }

    /// <summary>Whether this window is a transient modal dialog rather than an application task.</summary>
    public bool IsModalDialog { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _iconGlyph;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximizeGlyph))]
    [NotifyPropertyChangedFor(nameof(IsOnScreen))]
    [NotifyPropertyChangedFor(nameof(IsFullScreen))]
    private WindowState _state;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _canMinimize = true;
    [ObservableProperty] private bool _canMaximize = true;
    [ObservableProperty] private bool _canResize = true;

    /// <summary>True when the window is visible on the desktop (i.e. not minimized).</summary>
    public bool IsOnScreen => State != WindowState.Minimized;

    /// <summary>True when this window currently occupies the shell-wide full-screen overlay.</summary>
    public bool IsFullScreen => State == WindowState.FullScreen;

    public string MaximizeGlyph => State == WindowState.Maximized ? RestoreGlyphChar : MaximizeGlyphChar;

    /// <summary>Push authoritative state from <see cref="Info"/> into bindable properties.</summary>
    internal void Sync()
    {
        Title = Info.Title;
        IconGlyph = Info.IconGlyph;
        State = Info.State;
        CanMinimize = Info.CanMinimize;
        CanMaximize = Info.CanMaximize;
        CanResize = Info.CanResize;
    }

    public event EventHandler? FocusRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeToggleRequested;
    public event EventHandler? TaskbarToggleRequested;

    [RelayCommand] private void Activate() => FocusRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Minimize() => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleMaximize() => MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void TaskbarToggle() => TaskbarToggleRequested?.Invoke(this, EventArgs.Empty);
}
