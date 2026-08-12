namespace RemoteOS.Protocol.Firewall;

/// <summary>Read-only UFW status. The API intentionally exposes rules structurally, never command text.</summary>
public sealed record FirewallStatusDto(
    bool IsAvailable,
    bool IsEnabled,
    string Backend,
    string? Version,
    string? DefaultIncomingPolicy,
    string? DefaultOutgoingPolicy,
    string ProblemCode = "");

public sealed record FirewallRuleDto(
    int Number,
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port);

/// <summary>One-shot credential confirmation. It is ignored for root and must never be persisted.</summary>
public sealed record FirewallCredentialConfirmation(string Password);

public sealed record CreateFirewallRuleRequest(
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port,
    FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record UpdateFirewallEnabledRequest(bool Enabled, FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record UpdateFirewallDefaultsRequest(
    string IncomingPolicy,
    string OutgoingPolicy,
    FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record DeleteFirewallRuleRequest(FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record FirewallOperationResult(bool Success, string ProblemCode = "");
