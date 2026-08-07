using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Client.Services.AppPermissions;
using RemoteOS.Core.Applications;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Edits one application's declared permissions as a draft until the user saves.</summary>
public sealed partial class AppPermissionDialogViewModel : ObservableObject
{
    private readonly AppId _appId;
    private readonly IAppPermissionManager _permissionManager;
    private readonly Action<bool> _complete;

    public AppPermissionDialogViewModel(
        ApplicationInfo app,
        IAppPermissionManager permissionManager,
        Action<bool> complete)
    {
        _appId = app.Id;
        _permissionManager = permissionManager;
        _complete = complete;
        AppName = app.DisplayName;
        AppId = app.Id.Value;

        PermissionGroups = app.Permissions
            .Select(AppPermissions.Find)
            .OfType<AppPermissionDefinition>()
            .GroupBy(AppPermissions.GetCategory, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AppPermissionCategoryViewModel(
                group.Key,
                group.OrderBy(permission => permission.DisplayName, StringComparer.Ordinal)
                    .Select(permission => new AppPermissionChoiceViewModel(
                        permission,
                        permissionManager.IsGranted(app.Id, permission.Id)))
                    .ToArray()))
            .ToArray();
    }

    public string AppName { get; }
    public string AppId { get; }
    public IReadOnlyList<AppPermissionCategoryViewModel> PermissionGroups { get; }

    [RelayCommand]
    private void Save()
    {
        foreach (var permission in PermissionGroups.SelectMany(group => group.Permissions))
            _permissionManager.SetGranted(_appId, permission.PermissionId, permission.IsGranted);
        _complete(true);
    }

    [RelayCommand]
    private void Cancel() => _complete(false);
}

public sealed record AppPermissionCategoryViewModel(string Name, IReadOnlyList<AppPermissionChoiceViewModel> Permissions);

public sealed partial class AppPermissionChoiceViewModel : ObservableObject
{
    public AppPermissionChoiceViewModel(AppPermissionDefinition definition, bool isGranted)
    {
        PermissionId = definition.Id;
        DisplayName = definition.DisplayName;
        Description = definition.Description;
        _isGranted = isGranted;
    }

    public string PermissionId { get; }
    public string DisplayName { get; }
    public string Description { get; }

    [ObservableProperty] private bool _isGranted;
}
