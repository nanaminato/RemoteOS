using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using RemoteOS.Protocol.Registry;
using Server.ConfigurationRegistry;
using Server.Storage;

namespace Server.Endpoints;

/// <summary>Read-only first-stage API for the schema-approved configuration registry.</summary>
public static class RegistryEndpoints
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
        return app;
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId) => Guid.TryParse(
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static RegistryEntryDto ToDto(Server.Domain.RegistryEntry entry)
    {
        using var document = JsonDocument.Parse(entry.ValueJson);
        var schema = RegistrySchema.Find(entry.Scope, entry.Path, entry.Name)
            ?? throw new InvalidOperationException("Persisted registry entry does not have a schema definition.");
        return new RegistryEntryDto(entry.Scope, entry.Path, entry.Name, entry.ValueType, document.RootElement.Clone(), entry.Revision,
            entry.State, entry.DesiredUpdatedAt, entry.AppliedAt, schema.ApplyMode, schema.RestartTarget, entry.LastErrorCode, entry.LastErrorMessage);
    }
}
