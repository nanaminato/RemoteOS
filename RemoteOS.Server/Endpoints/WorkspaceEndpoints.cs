using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.Workspace;
using Server.Storage;

namespace Server.Endpoints;

/// <summary>Workspace-scoped presentation settings endpoints.</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(WorkspaceApiRoutes.TerminalSettings, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            return workspace is null ? Results.NotFound() : Results.Ok(workspace.TerminalSettings);
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.TerminalSettings, (Guid id, TerminalSettingsDto request, ClaimsPrincipal principal, IWorkspaceRepository workspaces) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();

            if (!TryNormalize(request, out var normalized))
                return Results.BadRequest(new { message = "Invalid terminal appearance settings." });

            workspace.TerminalSettings = normalized;
            workspaces.Update(workspace);
            return Results.Ok(normalized);
        }).RequireAuthorization().WithTags("Workspace");

        return app;
    }

    private static Server.Domain.Workspace? FindAuthorizedWorkspace(
        Guid workspaceId, ClaimsPrincipal principal, IWorkspaceRepository workspaces)
    {
        var userText = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                       ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userText, out var userId)
               && workspaces.FindById(workspaceId) is { } workspace
               && workspace.UserId == userId
            ? workspace
            : null;
    }

    private static bool TryNormalize(TerminalSettingsDto request, out TerminalSettingsDto settings)
    {
        settings = TerminalSettingsDto.Default;
        var fontFamily = request.FontFamily?.Trim();
        var scheme = request.ColorScheme?.Trim();
        if (string.IsNullOrWhiteSpace(fontFamily) || fontFamily.Length > 128
            || string.IsNullOrWhiteSpace(scheme) || scheme.Length > 64
            || request.FontSize is < 8 or > 40
            || !IsHexColor(request.BackgroundColor) || !IsHexColor(request.ForegroundColor) || !IsHexColor(request.CursorColor))
            return false;

        settings = new TerminalSettingsDto(
            fontFamily, request.FontSize, scheme,
            request.BackgroundColor.ToUpperInvariant(),
            request.ForegroundColor.ToUpperInvariant(),
            request.CursorColor.ToUpperInvariant());
        return true;
    }

    private static bool IsHexColor(string? color) => color is { Length: 7 }
        && color[0] == '#'
        && color[1..].All(Uri.IsHexDigit);
}
