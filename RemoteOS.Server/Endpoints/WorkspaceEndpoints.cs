using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.Desktop;
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

        // ── 用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）── 与 TerminalSettings 同模式：GET 直读，PUT 校验后整列覆盖。
        app.MapGet(WorkspaceApiRoutes.Preferences, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            return workspace is null ? Results.NotFound() : Results.Ok(workspace.Preferences);
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.Preferences, (Guid id, WorkspacePreferencesDto request, ClaimsPrincipal principal, IWorkspaceRepository workspaces) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();

            if (!TryNormalize(request, out var normalized))
                return Results.BadRequest(new { message = "Invalid workspace preferences." });

            workspace.Preferences = normalized;
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

    /// <summary>校验并归一化用户偏好。字段长度封顶防止滥用；枚举/格式白名单校验。
    /// DefaultApps 去重（按 scheme，后者覆盖前者）并剔除空值。</summary>
    private static bool TryNormalize(WorkspacePreferencesDto request, out WorkspacePreferencesDto preferences)
    {
        preferences = WorkspacePreferencesDto.Default;

        var wallpaperKey = request.WallpaperKey?.Trim();
        if (string.IsNullOrWhiteSpace(wallpaperKey) || wallpaperKey.Length > 128)
            return false;
        if (!Enum.IsDefined(request.Theme))
            return false;
        var timeFormat = request.TimeFormat?.Trim();
        if (timeFormat != WorkspacePreferencesDto.TimeFormat24H
            && timeFormat != WorkspacePreferencesDto.TimeFormat12H)
            return false;
        var dateFormat = request.DateFormat?.Trim();
        if (string.IsNullOrWhiteSpace(dateFormat) || dateFormat.Length > 32)
            return false;
        var language = request.Language?.Trim();
        if (language.Length > 16)
            return false;
        var region = request.Region?.Trim();
        if (region.Length > 16)
            return false;

        var sourceApps = request.DefaultApps ?? Array.Empty<DefaultAppMappingDto>();
        if (sourceApps.Count > 64)
            return false;

        // 按 scheme 去重（大小写不敏感），保留最后一条；剔除空 scheme/appId。
        var deduped = new Dictionary<string, DefaultAppMappingDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in sourceApps)
        {
            var scheme = mapping.Scheme?.Trim();
            var appId = mapping.AppId?.Trim();
            if (string.IsNullOrWhiteSpace(scheme) || scheme.Length > 32
                || string.IsNullOrWhiteSpace(appId) || appId.Length > 128)
                return false;
            deduped[scheme] = new DefaultAppMappingDto(scheme, appId);
        }

        preferences = new WorkspacePreferencesDto(
            wallpaperKey, request.Theme, timeFormat!, dateFormat!,
            string.IsNullOrEmpty(language) ? WorkspacePreferencesDto.Default.Language : language,
            string.IsNullOrEmpty(region) ? WorkspacePreferencesDto.Default.Region : region,
            deduped.Values.ToArray());
        return true;
    }
}
