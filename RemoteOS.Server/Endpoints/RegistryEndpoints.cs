using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Browser;
using RemoteOS.Protocol.Workspace;
using RemoteOS.Protocol.Registry;
using Server.ConfigurationRegistry;
using Server.Storage;

namespace Server.Endpoints;

/// <summary>Read-only first-stage API for the schema-approved configuration registry.</summary>
public static partial class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(RegistryApiRoutes.Entries, (string? scope, ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            RegistryScope? parsed = null;
            if (!string.IsNullOrWhiteSpace(scope) && (!Enum.TryParse<RegistryScope>(scope, true, out var requestedScope) || !Enum.IsDefined(requestedScope)))
                return Results.BadRequest(new { message = "Scope must be user, workspace, or device." });
            if (!string.IsNullOrWhiteSpace(scope)) parsed = Enum.Parse<RegistryScope>(scope, true);
            return Results.Ok(registry.List(userId, parsed).Select(ToDto));
        }).RequireAuthorization().WithTags("Registry");

        app.MapGet(RegistryApiRoutes.Summary, (ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            var entries = registry.List(userId);
            return Results.Ok(new RegistrySummaryDto(
                entries.Count(x => x.State is RegistryEntryState.PendingSync or RegistryEntryState.Applying),
                entries.Count(x => x.State == RegistryEntryState.Failed),
                entries.Count(x => x.State == RegistryEntryState.RestartRequired)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapPut(RegistryApiRoutes.Entries, (PutRegistryEntryRequest request, ClaimsPrincipal principal, IRegistryRepository registry, IWorkspaceRepository workspaces) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (!TryScopeId(principal, userId, request.Scope, out var scopeId)
                || !PathPattern().IsMatch(request.Path) || !(request.Name == "(Default)" || NamePattern().IsMatch(request.Name))
                || !IsCompatible(request.ValueType, request.Value))
                return Results.BadRequest(new { message = "Invalid registry value." });
            var now = DateTimeOffset.UtcNow;
            if (!TryProjectKnownWorkspaceValue(request, userId, scopeId, workspaces, out var projectionError))
                return Results.BadRequest(new { message = projectionError });
            var saved = registry.Upsert(new Server.Domain.RegistryEntry
            {
                UserId = userId, Scope = request.Scope, ScopeId = scopeId, Path = request.Path.Trim(), Name = request.Name.Trim(),
                ValueType = request.ValueType, ValueJson = request.Value.GetRawText(), State = RegistryEntryState.Synced,
                DesiredUpdatedAt = now, DesiredUpdatedBy = userId.ToString("D"), AppliedRevision = 1, AppliedAt = now,
            });
            return Results.Ok(ToDto(saved));
        }).RequireAuthorization().WithTags("Registry");

        app.MapDelete(RegistryApiRoutes.Entries, (RegistryScope scope, string path, string name, ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (!TryScopeId(principal, userId, scope, out var scopeId) || !PathPattern().IsMatch(path) || !(name == "(Default)" || NamePattern().IsMatch(name)))
                return Results.BadRequest(new { message = "Invalid registry value." });
            return registry.Delete(userId, scope, scopeId, path, name) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithTags("Registry");
        return app;
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId) => Guid.TryParse(
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryScopeId(ClaimsPrincipal principal, Guid userId, RegistryScope scope, out Guid scopeId)
    {
        scopeId = scope switch
        {
            RegistryScope.User => userId,
            RegistryScope.Workspace when Guid.TryParse(principal.FindFirstValue("workspace_id"), out var workspaceId) => workspaceId,
            RegistryScope.Device when Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId) => deviceId,
            _ => Guid.Empty,
        };
        return scopeId != Guid.Empty;
    }

    private static bool IsCompatible(RegistryValueType type, JsonElement value) => type switch
    {
        RegistryValueType.String => value.ValueKind == JsonValueKind.String,
        RegistryValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        RegistryValueType.Number => value.ValueKind == JsonValueKind.Number,
        RegistryValueType.Json => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null,
        _ => false,
    };

    private static bool TryProjectKnownWorkspaceValue(PutRegistryEntryRequest request, Guid userId, Guid scopeId, IWorkspaceRepository workspaces, out string? error)
    {
        error = null;
        if (request.Scope != RegistryScope.Workspace || request.Name != "(Default)") return true;
        var workspace = workspaces.FindById(scopeId);
        if (workspace is null || workspace.UserId != userId) { error = "Workspace was not found."; return false; }
        try
        {
            switch (request.Path)
            {
                // Terminal appearance is read directly from the registry cache. Do not touch
                // Workspace.TerminalSettings: it is a legacy migration source only.
                case "Workspace\\Terminal\\Appearance":
                    _ = request.Value.Deserialize<TerminalSettingsDto>(RemoteOsJsonOptions.Default) ?? TerminalSettingsDto.Default;
                    return true;
                case "Workspace\\Desktop\\Preferences": workspace.Preferences = request.Value.Deserialize<WorkspacePreferencesDto>(RemoteOsJsonOptions.Default) ?? WorkspacePreferencesDto.Default; break;
                case "Workspace\\Browser\\Settings": workspace.BrowserSettings = (request.Value.Deserialize<BrowserSettingsDto>(RemoteOsJsonOptions.Default) ?? BrowserSettingsDto.Default).Normalize(); break;
                default: return true;
            }
            workspaces.Update(workspace);
            return true;
        }
        catch (JsonException) { error = "The value does not match this registry key's configuration format."; return false; }
    }

    private static RegistryEntryDto ToDto(Server.Domain.RegistryEntry entry)
    {
        using var document = JsonDocument.Parse(entry.ValueJson);
        var schema = RegistrySchema.Find(entry.Scope, entry.Path, entry.Name);
        return new RegistryEntryDto(entry.Scope, entry.Path, entry.Name, entry.ValueType, document.RootElement.Clone(), entry.Revision,
            entry.State, entry.DesiredUpdatedAt, entry.AppliedAt, schema?.ApplyMode ?? RegistryApplyMode.Immediate, schema?.RestartTarget, entry.LastErrorCode, entry.LastErrorMessage);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._\\\\-]{0,63}(\\\\[A-Za-z0-9][A-Za-z0-9 ._\\\\-]{0,63}){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
