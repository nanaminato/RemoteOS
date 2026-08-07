using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.StaticFiles;
using RemoteOS.Protocol.Capabilities;
using RemoteOS.Protocol.Files;
using Server.Files;
using Server.Identity;

namespace Server.Endpoints;

/// <summary>Host-authenticated issuance of file API capabilities and single-file media leases.</summary>
public static class AppCapabilityEndpoints
{
    private static readonly HashSet<string> FileScopes =
    [
        FileCapabilityScopes.List,
        FileCapabilityScopes.Read,
        FileCapabilityScopes.Write,
        FileCapabilityScopes.Manage,
    ];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapAppCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(AppCapabilityRoutes.FileToken, (
            IssueFileCapabilityRequest request,
            ClaimsPrincipal principal,
            JwtTokenService tokens) =>
        {
            if (!TryGetOwner(principal, out var owner)
                || string.IsNullOrWhiteSpace(request.AppId)
                || request.Scopes is null
                || request.Scopes.Count == 0
                || request.Scopes.Any(scope => !FileScopes.Contains(scope)))
                return Results.BadRequest();

            return Results.Ok(tokens.IssueFileCapability(
                owner.UserId, owner.WorkspaceId, owner.DeviceId, request.AppId, request.Scopes));
        })
        .RequireAuthorization()
        .WithTags("Application capabilities");

        app.MapPost(AppCapabilityRoutes.MediaLeases, (
            CreateMediaLeaseRequest request,
            ClaimsPrincipal principal,
            IFileService files,
            MediaLeaseStore leases) =>
        {
            if (!TryGetOwner(principal, out var owner)
                || string.IsNullOrWhiteSpace(request.AppId)
                || string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest();

            try
            {
                var entry = files.GetInfo(request.Path);
                if (entry is null || entry.Type != FileSystemEntryType.File)
                    return Results.NotFound();
                var lease = leases.Create(owner.UserId, owner.WorkspaceId, owner.DeviceId, request.AppId, request.Path);
                return Results.Ok(new MediaLeaseDto(lease.Id, lease.ExpiresAt));
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        })
        .RequireAuthorization()
        .WithTags("Application capabilities");

        app.MapPost(AppCapabilityRoutes.MediaLeases + "/{leaseId}/renew", (
            string leaseId,
            ClaimsPrincipal principal,
            MediaLeaseStore leases) =>
        {
            if (!TryGetOwner(principal, out var owner))
                return Results.Unauthorized();
            return leases.TryRenew(leaseId, owner.UserId, owner.WorkspaceId, owner.DeviceId, out var lease)
                ? Results.Ok(new MediaLeaseDto(lease.Id, lease.ExpiresAt))
                : Results.NotFound();
        })
        .RequireAuthorization()
        .WithTags("Application capabilities");

        app.MapDelete(AppCapabilityRoutes.MediaLeases + "/{leaseId}", (
            string leaseId,
            ClaimsPrincipal principal,
            MediaLeaseStore leases) =>
        {
            if (TryGetOwner(principal, out var owner))
                leases.Revoke(leaseId, owner.UserId, owner.WorkspaceId, owner.DeviceId);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithTags("Application capabilities");

        app.MapMethods($"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/media/{{leaseId}}", ["GET", "HEAD"], (
            string leaseId,
            HttpContext context,
            IFileService files,
            MediaLeaseStore leases) =>
        {
            if (!leases.TryGetActive(leaseId, out var lease))
                return Results.NotFound();

            try
            {
                var read = files.OpenRead(lease.Path);
                if (read is not (var stream, _, _))
                    return Results.NotFound();

                ContentTypes.TryGetContentType(lease.Path, out var contentType);
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(
                    stream,
                    contentType ?? "application/octet-stream",
                    fileDownloadName: null,
                    lastModified: File.GetLastWriteTimeUtc(lease.Path),
                    entityTag: null,
                    enableRangeProcessing: true);
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
            catch (ArgumentException) { return Results.NotFound(); }
        })
        .WithTags("Media");

        return app;
    }

    private static bool TryGetOwner(ClaimsPrincipal principal, out CapabilityOwner owner)
    {
        if (Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            && Guid.TryParse(principal.FindFirstValue("workspace_id"), out var workspaceId)
            && Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId))
        {
            owner = new CapabilityOwner(userId, workspaceId, deviceId);
            return true;
        }

        owner = default!;
        return false;
    }

    private sealed record CapabilityOwner(Guid UserId, Guid WorkspaceId, Guid DeviceId);
}
