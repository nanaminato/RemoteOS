using RemoteOS.Protocol.Files;
using Server.Files;

namespace Server.Endpoints;

/// <summary>文件管理 REST 端点。路由常量见 <see cref="FileApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 错误统一返回 RFC 7807 ProblemDetails，错误码通过 type URI 传递（仿 <see cref="AuthEndpoints"/>）。
/// 服务端以宿主 OS 进程身份执行 IO，复用宿主用户/权限。</summary>
public static class FileEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/";

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        // GET drives
        app.MapGet(FileApiRoutes.Drives, (IFileService fs) =>
            Results.Ok(fs.GetDrives()))
           .RequireAuthorization()
           .WithTags("Files");

        // GET special — 跨平台枚举家目录/桌面/文档/下载/图片/音乐/视频（已 Directory.Exists 过滤）
        app.MapGet(FileApiRoutes.Special, (IFileService fs) =>
            Results.Ok(fs.GetSpecialLocations()))
           .RequireAuthorization()
           .WithTags("Files");

        // GET list?path=
        app.MapGet(FileApiRoutes.List, (string? path, IFileService fs) =>
        {
            try { return Results.Ok(fs.GetDirectory(path)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // GET info?path=
        app.MapGet(FileApiRoutes.Info, (string path, IFileService fs) =>
        {
            try
            {
                var dto = fs.GetInfo(path);
                return dto is null ? Problem(404, "not-found", "路径不存在", $"找不到: {path}") : Results.Ok(dto);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // GET download?path=
        app.MapGet(FileApiRoutes.Download, (string path, IFileService fs) =>
        {
            try
            {
                var r = fs.OpenRead(path);
                if (r is not (var stream, var contentType, var fileName))
                    return Problem(404, "not-found", "文件不存在", $"找不到: {path}");
                return Results.File(stream, contentType, fileName);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        app.MapGet(FileApiRoutes.Content, (string path, IFileService fs) =>
        {
            try
            {
                var result = fs.OpenRead(path);
                if (result is not (var stream, var contentType, _))
                    return Problem(404, "not-found", "Not found", $"Cannot find {path}");
                return Results.File(stream, contentType);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "Access denied", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        app.MapPut(FileApiRoutes.Content, async (string path, HttpRequest request, IFileService fs) =>
        {
            try { return Results.Ok(fs.WriteFile(path, request.Body)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "Target directory not found", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "Access denied", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "I/O error", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        app.MapGet(FileApiRoutes.Properties, (string path, IFileService fs) =>
        {
            try
            {
                var properties = fs.GetProperties(path);
                return properties is null ? Problem(404, "not-found", "Not found", $"Cannot find {path}") : Results.Ok(properties);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "Access denied", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST directory?path=
        app.MapPost(FileApiRoutes.Directory, (string path, IFileService fs) =>
        {
            try
            {
                fs.CreateDirectory(path);
                return Results.Created(path, fs.GetInfo(path));
            }
            catch (IOException ex) { return Problem(409, "already-exists", "已存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // DELETE files?path=
        app.MapDelete(FileApiRoutes.Delete, (string path, IFileService fs) =>
        {
            try
            {
                fs.Delete(path);
                return Results.NoContent();
            }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "IO 错误", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST rename
        app.MapPost(FileApiRoutes.Rename, (RenameRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.NewName))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 newName 不能为空");
            try { return Results.Ok(fs.Rename(req.SourcePath, req.NewName)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST move
        app.MapPost(FileApiRoutes.Move, (MoveRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Move(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST copy
        app.MapPost(FileApiRoutes.Copy, (CopyRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Copy(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST upload?path=
        app.MapPost(FileApiRoutes.Upload, async (HttpContext ctx, IFileService fs) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Problem(400, "invalid-input", "输入无效", "query path 不能为空");
            if (!ctx.Request.HasFormContentType)
                return Problem(415, "unsupported-media-type", "不支持的媒体类型", "需 multipart/form-data");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Problem(400, "invalid-input", "输入无效", "未提供文件");

            try
            {
                await using var stream = file.OpenReadStream();
                var dto = fs.Upload(path, file.FileName, stream);
                return Results.Created(dto.Path, dto);
            }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "目标目录不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "IO 错误", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        return app;
    }

    private static IResult Problem(int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: title, type: ProblemBase + typeSuffix);
}
