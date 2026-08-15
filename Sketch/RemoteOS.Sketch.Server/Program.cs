using RemoteOS.Sketch.Protocol;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

app.MapPost("/api/mock/auth/login", (MockLoginRequest request) =>
    Results.Ok(new MockLoginResponse("sketch-token", string.IsNullOrWhiteSpace(request.Username) ? "Design User" : request.Username.Trim())));

app.MapGet("/api/sketch/managers", () => Results.Ok(new[]
{
    new ManagerStatus("Docker", true, "27.1.1", "3 containers are running.", []),
    new ManagerStatus("Nginx", false, "—", "Nginx is not installed on this mock host.", ["Review the platform and the official Nginx installation instructions.", "An administrator installs and starts Nginx.", "Return here and refresh the status check."]),
    new ManagerStatus("Certificates", false, "—", "No supported ACME client was detected.", ["Install an approved ACME client.", "Prepare DNS or HTTP-01 validation.", "Verify the service before issuing a certificate."]),
}));
app.MapGet("/api/sketch/docker/containers", () => Results.Ok(new[]
{
    new DockerSummary("remoteos-web", "nginx:1.27", "Running", "80:80, 443:443"),
    new DockerSummary("remoteos-api", "remoteos/server:sketch", "Running", "5000:8080"),
    new DockerSummary("postgres", "postgres:16", "Stopped", "5432:5432"),
}));
app.MapGet("/api/sketch/nginx/sites", () => Results.Ok(new[] { new SiteSummary("Example site", "example.com", "127.0.0.1:5000", "Design preview") }));
app.MapGet("/api/sketch/certificates", () => Results.Ok(new[] { new CertificateSummary("example.com, www.example.com", "Let's Encrypt", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(78)), "Valid") }));
app.Run();
