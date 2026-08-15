using RemoteOS.Sketch.Protocol;
using RemoteOS.Sketch.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("REMOTEOS_SKETCH_URL") ?? "http://127.0.0.1:5088");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<SketchMockStore>();
var app = builder.Build();
app.UseCors();

app.MapGet("/api/sketch/health", () => Results.Ok(new { status = "ready", mode = "stateful-design-mock", timestamp = DateTimeOffset.UtcNow }));
app.MapPost("/api/mock/auth/login", (MockLoginRequest request) => Results.Ok(new MockLoginResponse("sketch-token", string.IsNullOrWhiteSpace(request.Username) ? "Design User" : request.Username.Trim())));
app.MapGet("/api/sketch/managers", (SketchMockStore store) => Results.Ok(store.Managers()));
app.MapPost("/api/sketch/managers/{manager}/installation", (string manager, ManagerInstallationRequest request, SketchMockStore store) => Results.Ok(store.SetInstalled(manager, request.IsInstalled)));

var docker = app.MapGroup("/api/sketch/docker");
docker.MapGet("/overview", (SketchMockStore store) => store.DockerOverview());
docker.MapGet("/containers", (SketchMockStore store) => store.Containers());
docker.MapGet("/containers/{id}", (string id, SketchMockStore store) => store.Container(id) is { } item ? Results.Ok(item) : Results.NotFound());
docker.MapPost("/containers/{id}/actions", (string id, DockerContainerActionRequest request, SketchMockStore store) => Results.Ok(store.ContainerAction(id, request.Action, request.Confirmed)));
docker.MapGet("/stacks", (SketchMockStore store) => store.Stacks());
docker.MapPost("/stacks", (DockerStackUpsertRequest request, SketchMockStore store) => Results.Ok(store.SaveStack(request)));
docker.MapPost("/stacks/{name}/actions/{action}", (string name, string action, bool? confirmed, SketchMockStore store) => Results.Ok(store.StackAction(name, action, confirmed ?? false)));
docker.MapGet("/images", (SketchMockStore store) => store.Images());
docker.MapGet("/images/prune-preview", (SketchMockStore store) => store.PrunePreview());
docker.MapPost("/images/prune", (bool? confirmed, SketchMockStore store) => Results.Ok(store.Prune(confirmed ?? false)));
docker.MapGet("/networks", (SketchMockStore store) => store.Networks());
docker.MapGet("/volumes", (SketchMockStore store) => store.Volumes());

var nginx = app.MapGroup("/api/sketch/nginx");
nginx.MapGet("/overview", (SketchMockStore store) => store.NginxOverview());
nginx.MapGet("/sites", (SketchMockStore store) => store.Sites());
nginx.MapPost("/sites", (NginxSiteUpsertRequest request, SketchMockStore store) => Results.Ok(store.SaveSite(null, request)));
nginx.MapPut("/sites/{id}", (string id, NginxSiteUpsertRequest request, SketchMockStore store) => Results.Ok(store.SaveSite(id, request)));
nginx.MapDelete("/sites/{id}", (string id, bool? confirmed, SketchMockStore store) => Results.Ok(store.DeleteSite(id, confirmed ?? false)));
nginx.MapPost("/configuration/test", (SketchMockStore store) => Results.Ok(store.TestNginx()));
nginx.MapPost("/configuration/reload", (bool? confirmed, SketchMockStore store) => Results.Ok(store.ReloadNginx(confirmed ?? false)));
nginx.MapGet("/configuration/versions", (SketchMockStore store) => store.Configs());
nginx.MapGet("/logs", (SketchMockStore store) => store.NginxLogs());

var certificates = app.MapGroup("/api/sketch/certificates");
certificates.MapGet("/overview", (SketchMockStore store) => store.CertificateOverview());
certificates.MapGet("/items", (SketchMockStore store) => store.Certificates());
certificates.MapPost("/items", (CertificateIssueRequest request, SketchMockStore store) => Results.Ok(store.IssueCertificate(request)));
certificates.MapPost("/items/{id}/actions/{action}", (string id, string action, bool? force, SketchMockStore store) => Results.Ok(store.CertificateAction(id, action, force ?? false)));
certificates.MapGet("/acme-accounts", (SketchMockStore store) => store.AcmeAccounts());
certificates.MapGet("/dns-providers", (SketchMockStore store) => store.DnsProviders());
certificates.MapGet("/renewal-policy", (SketchMockStore store) => store.RenewalPolicy());

app.Run();
