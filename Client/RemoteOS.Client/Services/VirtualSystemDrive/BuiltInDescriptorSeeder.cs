using RemoteOS.AppSDK;
using RemoteOS.Core.VirtualSystemDrive;

namespace Client.Services.VirtualSystemDrive;

/// <summary>Repairs observable built-in descriptors from Host-compiled definitions.</summary>
public sealed class BuiltInDescriptorSeeder
{
    private readonly VirtualSystemDrive _drive;
    private readonly IBuiltInApplicationFactoryRegistry _registry;

    public BuiltInDescriptorSeeder(VirtualSystemDrive drive, IBuiltInApplicationFactoryRegistry registry)
    {
        _drive = drive;
        _registry = registry;
    }

    /// <summary>
    /// A descriptor is never trusted as the source of built-in identity. Missing, stale, or
    /// malformed files are simply replaced from the registry; user-owned VSD content is untouched.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        _drive.EnsureCreated();
        foreach (var definition in _registry.Definitions)
        {
            var directory = _drive.ResolveUnder(_drive.BuiltInProgramsDirectory, definition.AppId.Value);
            Directory.CreateDirectory(directory);
            var path = _drive.ResolveUnder(directory, "app.remoteos.json");
            var expected = ToDescriptor(definition);
            var mustReplace = true;
            try
            {
                var existing = await _drive.ReadJsonAsync<ApplicationDescriptor>(path, cancellationToken);
                mustReplace = existing != expected || !ApplicationDescriptorValidator.Validate(existing).IsValid;
            }
            catch (VirtualSystemDriveException)
            {
                // The descriptor is a recoverable installation mirror, not user configuration.
            }

            if (mustReplace)
                await _drive.WriteJsonAtomicallyAsync(path, expected, cancellationToken);
        }
    }

    public static ApplicationDescriptor ToDescriptor(BuiltInApplicationDefinition definition)
    {
        var manifest = definition.Manifest;
        return new ApplicationDescriptor(
            ApplicationDescriptorValidator.CurrentSchemaVersion,
            definition.AppId.Value,
            ApplicationDescriptorKind.BuiltIn,
            manifest.DisplayName,
            manifest.Version,
            new ApplicationDescriptorActivation(BuiltInKey: definition.BuiltInKey),
            manifest.Description,
            new ApplicationDescriptorIcon(manifest.IconPath, manifest.IconGlyph),
            manifest.Permissions,
            manifest.FileExtensions,
            manifest.UriSchemes,
            manifest.InstancePolicy.ToString(),
            manifest.SupportedClientPlatforms,
            manifest.PermissionModelVersion);
    }
}
