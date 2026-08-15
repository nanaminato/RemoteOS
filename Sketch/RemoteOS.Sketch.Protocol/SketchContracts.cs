namespace RemoteOS.Sketch.Protocol;

public sealed record MockLoginRequest(string Username, string Password);
public sealed record MockLoginResponse(string Token, string DisplayName);
public sealed record ManagerStatus(string Name, bool IsInstalled, string Version, string Message, IReadOnlyList<string> InstallSteps);
public sealed record ManagerInstallationRequest(bool IsInstalled);

// Shared shell contracts used by all product-style manager surfaces.
public sealed record ManagerOverview(string Manager, string Health, string Headline, string Detail, IReadOnlyList<MetricCard> Metrics, IReadOnlyList<ActivityItem> RecentActivity);
public sealed record MetricCard(string Label, string Value, string Detail, string Tone = "neutral");
public sealed record ActivityItem(DateTimeOffset OccurredAt, string Action, string Target, string Result, string? Actor = "design-user");
public sealed record MockOperationResult(bool Succeeded, string Message, DateTimeOffset OccurredAt, string? OperationId = null);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

// Docker
public sealed record DockerContainerSummary(string Id, string Name, string Image, string State, string Status, string Ports, double CpuPercent, string Memory);
public sealed record DockerContainerDetail(string Id, string Name, string Image, string State, string Status, string Ports, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Mounts, IReadOnlyList<string> Networks, IReadOnlyList<string> Logs);
public sealed record DockerContainerActionRequest(string Action, bool Confirmed = false);
public sealed record DockerStackSummary(string Name, string Source, string Status, int Services, DateTimeOffset UpdatedAt, string Compose);
public sealed record DockerStackUpsertRequest(string Name, string Source, string Compose, bool Confirmed = false);
public sealed record DockerImageSummary(string Id, string Repository, string Tag, string Size, string Created, bool InUse);
public sealed record DockerNetworkSummary(string Id, string Name, string Driver, int Containers);
public sealed record DockerVolumeSummary(string Name, string Driver, string MountPoint, int Consumers);
public sealed record DockerPrunePreview(int Containers, int Images, int Volumes, string ReclaimableSize);

// Nginx
public sealed record NginxSiteSummary(string Id, string Name, string Domains, string Upstream, bool Enabled, string Certificate, DateTimeOffset UpdatedAt);
public sealed record NginxSiteUpsertRequest(string Name, string Domains, string Upstream, bool Enabled, string? CertificateId = null);
public sealed record NginxConfigSnapshot(string Version, DateTimeOffset CreatedAt, string Author, string Summary, string Content);
public sealed record NginxLogEntry(DateTimeOffset OccurredAt, string Level, string Site, string Message, int? StatusCode = null);
public sealed record NginxTestResult(bool Succeeded, IReadOnlyList<string> Messages, DateTimeOffset TestedAt);

// Certificates
public sealed record CertificateSummary(string Id, string Domains, string Issuer, DateOnly ExpiresOn, string Status, bool AutoRenew, string Validation);
public sealed record CertificateIssueRequest(string PrimaryDomain, IReadOnlyList<string> AlternativeNames, string Validation, string? SiteId, bool AutoRenew);
public sealed record CertificateRenewRequest(bool Force = false);
public sealed record AcmeAccountSummary(string Id, string Email, string Directory, string Status, DateTimeOffset CreatedAt);
public sealed record DnsProviderSummary(string Id, string Name, string CredentialReference, bool IsConfigured);
public sealed record CertificateRenewalPolicy(int DaysBeforeExpiry, bool Enabled, string PreferredWindow);
