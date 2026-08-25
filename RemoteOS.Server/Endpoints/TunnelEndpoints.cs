using System.Security.Claims;
using RemoteOS.Protocol.Tunnels;
using Server.Runtimes;
using Server.Secrets;
using Server.Tunnels;

namespace Server.Endpoints;

/// <summary>Tunnel API. All mutations are server-authorized and all error payloads carry only stable problem codes.</summary>
public static class TunnelEndpoints
{
    public static IEndpointRouteBuilder MapTunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(TunnelApiRoutes.Tunnels).RequireAuthorization().WithTags("Tunnels");
        group.MapGet(TunnelApiRoutes.ProfilesPattern, (ClaimsPrincipal user, ITunnelService service, CancellationToken ct) => service.ListProfilesAsync(UserId(user), ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.ProfilePattern, async (Guid profileId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await service.GetProfileAsync(profileId, UserId(user), ct) is { } value ? Results.Ok(value) : Results.NotFound()).RequireAuthorization("TunnelsRead");
        group.MapPost(TunnelApiRoutes.ProfilesPattern, async (UpsertTunnelServerProfileRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertProfileAsync(null, request, UserId(user), ct), created: true)).RequireAuthorization("TunnelsManage");
        group.MapPut(TunnelApiRoutes.ProfilePattern, async (Guid profileId, UpsertTunnelServerProfileRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertProfileAsync(profileId, request, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapDelete(TunnelApiRoutes.ProfilePattern, async (Guid profileId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleDeleteAsync(() => service.DeleteProfileAsync(profileId, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        // This dedicated write-only endpoint is the sole credential ingress. Its response never reflects the request body.
        group.MapPut(TunnelApiRoutes.ProfileSecretPattern, async (Guid profileId, SetTunnelProfileTokenRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
        {
            try { await service.SetProfileTokenAsync(profileId, request.Token, UserId(user), ct); return Results.NoContent(); }
            catch (TunnelNotFoundException) { return Results.NotFound(); }
            catch (SecretValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
            catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization("TunnelsManage");
        group.MapGet(TunnelApiRoutes.ProfileLogsPattern, async (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) =>
            await provider.GetLogsAsync(profileId, UserId(user), ct) is { } logs ? Results.Ok(logs) : Results.NotFound()).RequireAuthorization("TunnelsRead");

        group.MapGet(TunnelApiRoutes.CollectionPattern, (ClaimsPrincipal user, ITunnelService service, CancellationToken ct) => service.ListTunnelsAsync(UserId(user), ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await service.GetTunnelAsync(tunnelId, UserId(user), ct) is { } value ? Results.Ok(value) : Results.NotFound()).RequireAuthorization("TunnelsRead");
        group.MapPost(TunnelApiRoutes.CollectionPattern, async (UpsertTunnelDefinitionRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertTunnelAsync(null, request, UserId(user), ct), created: true)).RequireAuthorization("TunnelsManage");
        group.MapPut(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, UpsertTunnelDefinitionRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertTunnelAsync(tunnelId, request, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapDelete(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleDeleteAsync(() => service.DeleteTunnelAsync(tunnelId, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.ApplyProfilePattern, (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) => provider.ApplyAsync(profileId, UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.StopProfilePattern, (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) => provider.StopAsync(profileId, UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapGet(TunnelApiRoutes.Runtime, (IRuntimeManager runtime, CancellationToken ct) => runtime.GetManagedFrpcStatusAsync(ct)).RequireAuthorization("TunnelsRead");
        group.MapPost(TunnelApiRoutes.RuntimeDetectExternal, (DetectExternalTunnelRuntimeRequest request, IRuntimeManager runtime, CancellationToken ct) => runtime.DetectExternalFrpcAsync(request.ExecutablePath, ct)).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.RuntimeInstallPattern, async (InstallManagedTunnelRuntimeRequest request, ClaimsPrincipal user, IRuntimeManager runtime, ITunnelAudit audit, CancellationToken ct) =>
        {
            if (!request.Confirmed) return Problem("tunnel.runtime_confirmation_required", StatusCodes.Status400BadRequest);
            var result = await runtime.InstallManagedFrpcAsync(request.Version, ct);
            await audit.RecordAsync(UserId(user), "runtime.install", null, result.Succeeded ? "succeeded" : "failed", result.ProblemCode, ct);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.RuntimeRollbackPattern, async (ClaimsPrincipal user, IRuntimeManager runtime, ITunnelAudit audit, CancellationToken ct) =>
        {
            var result = await runtime.RollbackManagedFrpcAsync(ct);
            await audit.RecordAsync(UserId(user), "runtime.rollback", null, result.Succeeded ? "succeeded" : "failed", result.ProblemCode, ct);
            return result.Succeeded ? Results.Ok(result) : Results.Conflict(result);
        }).RequireAuthorization("TunnelsManage");
        return app;
    }

    private static async Task<IResult> HandleAsync<T>(Func<Task<T>> operation, bool created = false)
    {
        try { return created ? Results.Created("", await operation()) : Results.Ok(await operation()); }
        catch (TunnelNotFoundException) { return Results.NotFound(); }
        catch (TunnelRevisionConflictException) { return Problem("tunnel.revision_conflict", StatusCodes.Status409Conflict); }
        catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
    }
    private static async Task<IResult> HandleDeleteAsync(Func<Task<bool>> operation)
    {
        try { return await operation() ? Results.NoContent() : Results.NotFound(); }
        catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status409Conflict); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code, type: $"https://remoteos.app/problems/{code}", extensions: new Dictionary<string, object?> { ["problemCode"] = code });
    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();
}
