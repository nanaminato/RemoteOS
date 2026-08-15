namespace RemoteOS.Sketch.Protocol;

public sealed record MockLoginRequest(string Username, string Password);
public sealed record MockLoginResponse(string Token, string DisplayName);
public sealed record ManagerStatus(string Name, bool IsInstalled, string Version, string Message, IReadOnlyList<string> InstallSteps);
public sealed record CertificateSummary(string Domains, string Issuer, DateOnly ExpiresOn, string Status);
public sealed record SiteSummary(string Name, string Domains, string Upstream, string Status);
public sealed record DockerSummary(string Name, string Image, string Status, string Ports);
