using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Dialogs;

/// <summary>Presentation and Linux POSIX-permission editing for the file properties dialog.</summary>
public sealed partial class FilePropertiesDialogViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Func<int, Task<FilePropertiesDto>>? _saveUnixPermissions;
    private bool _initializingPermissions;
    private int _specialPermissionBits;

    public FilePropertiesDialogViewModel(
        FilePropertiesDto properties,
        Func<int, Task<FilePropertiesDto>>? saveUnixPermissions,
        Action close)
    {
        _close = close;
        _saveUnixPermissions = saveUnixPermissions;
        Properties = properties;
    }

    [ObservableProperty] private FilePropertiesDto _properties = null!;
    [ObservableProperty] private bool _ownerRead;
    [ObservableProperty] private bool _ownerWrite;
    [ObservableProperty] private bool _ownerExecute;
    [ObservableProperty] private bool _groupRead;
    [ObservableProperty] private bool _groupWrite;
    [ObservableProperty] private bool _groupExecute;
    [ObservableProperty] private bool _othersRead;
    [ObservableProperty] private bool _othersWrite;
    [ObservableProperty] private bool _othersExecute;
    [ObservableProperty] private bool _isSavingPermissions;
    [ObservableProperty] private string _permissionStatus = "Select permissions, then save the changes to the server.";

    public string SizeText => Properties.Size is { } size ? $"{size:N0} bytes" : "—";
    public bool CanEditPermissions => Properties.UnixMode is not null && _saveUnixPermissions is not null;
    public string PermissionOctal => Convert.ToString(CurrentUnixMode, 8).PadLeft(4, '0');

    partial void OnPropertiesChanged(FilePropertiesDto value)
    {
        OnPropertyChanged(nameof(SizeText));
        InitializePermissions();
    }

    partial void OnIsSavingPermissionsChanged(bool value) => SavePermissionsCommand.NotifyCanExecuteChanged();

    partial void OnOwnerReadChanged(bool value) => OnPermissionsChanged();
    partial void OnOwnerWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnOwnerExecuteChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupReadChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupExecuteChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersReadChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersExecuteChanged(bool value) => OnPermissionsChanged();

    [RelayCommand(CanExecute = nameof(CanSavePermissions))]
    private async Task SavePermissionsAsync()
    {
        if (_saveUnixPermissions is null)
            return;

        IsSavingPermissions = true;
        PermissionStatus = "Saving permissions…";
        try
        {
            Properties = await _saveUnixPermissions(CurrentUnixMode);
            PermissionStatus = $"Saved as {PermissionOctal}.";
        }
        catch (Exception ex)
        {
            PermissionStatus = $"Could not save permissions: {ex.Message}";
        }
        finally
        {
            IsSavingPermissions = false;
        }
    }

    [RelayCommand]
    private void Close() => _close();

    private bool CanSavePermissions() => CanEditPermissions && !IsSavingPermissions;

    private void InitializePermissions()
    {
        _initializingPermissions = true;
        var mode = Properties.UnixMode ?? 0;
        _specialPermissionBits = mode & ~0x1FF;
        OwnerRead = HasMode(mode, 0x100);
        OwnerWrite = HasMode(mode, 0x80);
        OwnerExecute = HasMode(mode, 0x40);
        GroupRead = HasMode(mode, 0x20);
        GroupWrite = HasMode(mode, 0x10);
        GroupExecute = HasMode(mode, 0x8);
        OthersRead = HasMode(mode, 0x4);
        OthersWrite = HasMode(mode, 0x2);
        OthersExecute = HasMode(mode, 0x1);
        _initializingPermissions = false;
        OnPropertyChanged(nameof(CanEditPermissions));
        OnPropertyChanged(nameof(PermissionOctal));
        SavePermissionsCommand.NotifyCanExecuteChanged();
    }

    private void OnPermissionsChanged()
    {
        if (_initializingPermissions)
            return;

        OnPropertyChanged(nameof(PermissionOctal));
        PermissionStatus = "Unsaved permission changes.";
    }

    private int CurrentUnixMode => _specialPermissionBits |
        (OwnerRead ? 0x100 : 0) | (OwnerWrite ? 0x80 : 0) | (OwnerExecute ? 0x40 : 0) |
        (GroupRead ? 0x20 : 0) | (GroupWrite ? 0x10 : 0) | (GroupExecute ? 0x8 : 0) |
        (OthersRead ? 0x4 : 0) | (OthersWrite ? 0x2 : 0) | (OthersExecute ? 0x1 : 0);

    private static bool HasMode(int mode, int flag) => (mode & flag) != 0;
}
