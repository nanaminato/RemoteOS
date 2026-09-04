# Proxy Manager implementation discovery

## Phase 0 result

This document maps docs/applications/RemoteOS.ProxyManager.Design.md (read in full: 4,155 lines) to the repository as inspected on 2026-08-31. Phase 0 makes no product-code changes. There is no Proxy Manager API, service, Mihomo controller client, runtime installer, profile store, or Avalonia UI yet.

The implementation must be a host-global built-in app. It can reuse existing RemoteOS Protocol, typed HTTP client, Avalonia workspace/modal, secret, operation-ledger, audit, and runtime safety patterns. It must not reuse FRP's child-process supervision for Mihomo: TUN requires a native OS service, management-route protection, recovery markers, and network rollback.

## Relevant projects

| Area | Actual repository role | Proxy mapping |
| --- | --- | --- |
| Shared/RemoteOS.Protocol | Client/server DTOs, enums, route and hub constants, JSON conventions. | Add a Proxy contract family here. |
| RemoteOS.Server | ASP.NET Core .NET 10 host, minimal APIs, host integrations and SQLite storage. | Add proxy domain, platform adapters, services, providers and endpoints here. |
| Client/RemoteOS.Client | Avalonia shell, built-in apps, typed remote clients, DI, localization and theme. | Add the remoteos.proxy app, typed proxy repository, workspace/pages and view models here. |
| Framework/RemoteOS.Core | App manifests and requested app-capability declarations. | Add only stable Proxy app capability declarations needed by its manifest. |
| Framework/RemoteOS.App.SDK and RemoteOS.WindowManager | Managed windows and owner-scoped modal dialogs. | Use for the Proxy window and profile/config/recovery dialogs. |
| RemoteOS.Server.Tests | Console-based Server verification suite, rather than xUnit. | Add focused contract/safety tests here, then platform integration tests. |

The Server is one Windows/Linux assembly. Program.cs composes platform implementations and maps endpoint families. There is no current RemoteOS.Network, PlatformPaths abstraction, generic service manager, generic operation framework, or Proxy feature.

## Existing abstractions to reuse

### Protocol, API, and error conventions

- RemoteOsEndpoints fixes the REST root at /api/v1. Proxy routes must use /api/v1/proxy, not the specification's unversioned /api/proxy examples.
- Existing contract families such as WebServers and Tunnels contain shared DTOs plus absolute and group-relative route constants. Add Shared/RemoteOS.Protocol/Proxy; do not duplicate route strings in endpoints or client code.
- Endpoint groups use RequireAuthorization and WithTags. WebServerEndpoints is the closest long-running mutation example: Idempotency-Key is required, a durable operation is returned with 202 Accepted, and stable elevation-required problem codes map to 403.
- API responses use stable problem codes and safe diagnostics. Client localization, not raw host/controller messages, supplies presentation text.
- The proxy contracts must be engine-neutral Proxy* types. Mihomo controller JSON, YAML parsing types and secrets cannot leave the Server.

### Authorization and built-in app permissions

- The base Server protection is JWT authentication. Program.cs has explicit role-policy precedent: TunnelsRead allows controller/observer and TunnelsManage allows controller; TunnelEndpoints attaches those policies per endpoint.
- AppPermissions in Framework/RemoteOS.Core is a desktop app manifest/capability catalogue. Built-in app view models use IAppPermissionManager to enable UI actions. It is not Server authorization.
- The current built-in app convention assumes a single administrator-managed host and says first-party apps do not currently use AppPermissions as their final Server authorization mechanism. It nevertheless requires authenticated APIs, host OS checks, confirmations, and audit.

Resolution: the Proxy specification governs this high-impact network feature. Later phases must add actual Server policies for proxy read/manage and separate TUN, runtime, and recovery actions. The desktop app manifest remains an affordance only. The precise proxy permission-to-claim/role mapping must be designed before endpoints are added.

### Secret storage and sanitization

- ISecretStore and DataProtectionSecretStore provide the Server-only encrypted-secret pattern. The present interface/entity is tunnel-profile-specific, stores Data Protection ciphertext in SQLite, and deliberately offers no list/export API.
- Tunnel safe DTOs return TokenConfigured instead of secret values. FrpTunnelProvider bounds and redacts its logs.

Proxy must add proxy-scoped encrypted secret storage/purposes for subscription URLs and controller secret; it must not repurpose tunnel secret entities. No route, DTO, audit entry, log, exception response, or UI state may return controller secrets, subscription tokens, authentication headers, proxy credentials, UUIDs, or private/WireGuard keys. The proxy sanitizer must be explicitly tested for every required source.

### Audit and operation infrastructure

- Tunnels/ITunnelAudit and TunnelAudit are the closest simple audit precedent: actor, action, target, result, problem code and timestamp in SQLite.
- HostOperationJournal, WebServerOperationStore, and CertificateOperationStore are the closest host-global durable operation precedents. WebServerOperationStore gives idempotency, stages, cancellation, per-instance gates, restart recovery, atomic persistence, and a secret-free operation DTO.
- There is no generic job/operation framework. The two operation stores are internal and feature-specific.

Proxy long operations must reuse or carefully extract the WebServer operation semantics: idempotency, cancellation, stages/progress, durable correlation ID, locking, and interrupted-operation recovery. Do not just clone a third store without documenting why extraction is not yet viable. TUN needs an additional durable recovery marker and rollback state: an interrupted generic operation alone is not sufficient.

### Runtime and configuration safety

- Runtimes/FrpRuntimeManager already distinguishes Managed and External runtime. Its managed flow uses a pinned host-supplied release manifest, HTTPS download, archive size/content validation, SHA-256 verification, staging, executable health check, versioned releases, active/previous state and rollback.
- FrpTunnelProvider shows temporary write, validation, backup, atomic commit, restart and attempted rollback for a configuration transaction.

These are reusable safety patterns, not types to extend. They use FRP-specific data paths and FrpTunnelProvider owns Server child PIDs. Proxy must not add Mihomo to either class because it must use a native Windows/service-manager or systemd lifecycle and protected platform paths.

### Native services, platform layer and elevation

- ProcessGuardian/INativeServiceAdapter is the only native-service facade. It lists and starts/stops/restarts only configuration-allowlisted service names through sc.exe or systemctl. It cannot install/remove services and returns Guardian-specific DTOs.
- WebServer/IHostPrivilegeService only reports whether the Server process is already root/administrator. Nginx returns elevation-required codes when this is not so; it does not provide elevation delegation.
- Linux Firewall has a root-owned, narrowly scoped UFW helper. IHostFirewallService accepts only validated structured firewall actions, never shell text. This is the appropriate least-privilege shape, but it is firewall-specific and must not be extended into a generic proxy command runner.
- Guardian can run an administrator-configured installer; Docker returns a non-executing installation plan. Neither is a general elevation workflow.
- Platform interfaces exist by domain (identity, metrics, terminal, firewall and web servers) and are selected in Program.cs. There is no IProxyPlatformService, PlatformPaths, route/DNS snapshot abstraction or network management layer. Most current Server storage uses content-root data paths.

Required new components, because no equivalent exists:

1. A focused Proxy platform-path abstraction for ProgramData on Windows and /etc, /var/lib, /var/log, /opt on Linux.
2. Windows and Linux IProxyPlatformService implementations for capabilities, interface/route/DNS inspection, route protection validation, snapshots, recovery and TUN diagnostics.
3. A strongly typed proxy privileged-operation boundary containing only the named proxy actions: runtime/service install/remove/update, protected configuration write, service lifecycle/startup, and network recovery. It must use structured data and results, never a generic command/executable/argument/password API.
4. A service lifecycle integration that may extract shared allowlisted controls from INativeServiceAdapter, but does not create per-platform ProxyServiceManager classes or scatter systemctl, sc.exe, netsh, PowerShell or ip calls through business services.

The Firewall helper cannot be used for proxy actions. A generic privileged command executor is prohibited.

### Streaming and logs

- SignalR is already registered; terminal, performance, and Guardian logs have hubs. Guardian broadcast/subscription types provide a live-log precedent.
- FRP and Docker presently expose bounded REST logs.

Start with bounded sanitized REST logs in the adapter. If Proxy later needs live logs/connections, use the existing SignalR conventions and add shared hub contracts/authorization. Do not introduce a ProxyWebSocketManager or a raw socket framework.

## Avalonia MVVM, workspace and modal conventions

- Built-in apps derive from RemoteApplicationBase, declare a remoteos.* AppId/manifest, resolve typed services from AppContext.Services, and open a managed in-desktop window. DockerManagerApp, TunnelManagerApp and WebServerManagerApp are direct references.
- Every Server client is a typed HttpClient interface/implementation registered in Bootstrapper with NetworkDiagnosticsHandler, AcceptLanguageHandler and authentication. Proxy must introduce IProxyRepository/RemoteProxyRepository in this pattern. Views and view models must never construct HttpClient or connect to Mihomo.
- View models use CommunityToolkit.Mvvm ObservableObject, ObservableProperty and RelayCommand. TunnelManagerViewModel is a useful cancellation/reentrancy/periodic refresh reference.
- DockerManagerWorkspace is the current multi-page workspace example: left navigation, ContentControl host and independent page AXAML files. Follow the built-in application convention: no giant page and no hidden tab pages in one AXAML.
- AppContext.ShowDialogAsync<TResult> is the required modal API; it uses a managed ModalDialog and owner-scoped blocker. DockerManagerDialogs and TunnelManagerApp demonstrate profile/configuration-like dialogs. Do not use native OS dialogs for Proxy management actions.

## Theme and localization conventions

- ThemeService provides dynamically swapped, validated semantic palette resources. Use DynamicResource values such as AppBackgroundBrush, SurfaceBrush, TextPrimaryBrush, AccentBrush, DangerBrush and Border*Brush. Do not add a Mihomo/Clash palette or hard-coded colors.
- LocalizationService loads JSON packs in Client/RemoteOS.Client/Localization, resolves stable keys with English fallback, and raises LanguageChanged. LocalizedText and Loc are current call sites.
- Add every Proxy key in en-US, zh-CN and ja-JP. Dynamic view-model text must react to language changes. Typed HTTP clients already send Accept-Language; backend problem codes are not localized UI strings.

## Persistence and ownership

Proxy state is host-global. Runtime inventory, active profile, recovery marker, network snapshots, controller configuration, operation/audit history and safety state cannot be workspace preferences or user-owned rows. User/session identity belongs in audit attribution.

RemoteOsDbContext currently contains user/workspace/tunnel data, while the certificate/web-server path uses host-global migration/journal patterns. Before Phase 1 code, select a host-global schema/migration approach for proxy metadata and document it. Store raw YAML, backups and runtime artifacts in protected platform paths; preserve the full engine configuration as raw YAML plus a RemoteOS-managed overlay rather than attempting a complete DTO rewrite.

## Required component map

### Phase 1: domain and contracts

Add Shared/RemoteOS.Protocol/Proxy models for engine/platform capabilities, operating/runtime/TUN/health/operation states, stable problem codes, profiles, runtime, groups, connections, logs, DNS, recovery and route constants. Add only engine-neutral Server interfaces: IProxyEngine, IProxyRuntimeManager, profile/configuration/recovery services, engine registry and platform boundary. Add serialization/problem-code tests. No UI, download, service or TUN activation.

### Phase 2: Mihomo adapter

Implement a Server-only MihomoEngine and local-only controller client. It maps controller output into neutral contracts, protects a generated controller secret, validates configuration and sanitizes bounded logs. No client controller access.

### Phase 3: runtime and native service

Implement Managed/External Mihomo runtime with the FRP verification/staging/version/rollback lessons, but use Proxy platform paths. Add strongly typed privileged service/runtime/configuration operations and Windows/Linux service integrations. Install and first health-check with TUN off.

### Phase 4: profiles and configuration transaction

Implement host-global profile metadata, active-profile state, raw YAML edit/read, validation, temporary write, backup, commit, reload, health check and rollback. Avoid a complete YAML visual model.

### Phase 5: TUN safety

Implement capability detection, active session route capture, protected system bypasses, outbound-interface selection, route/DNS snapshots, recovery marker, transactional enable/disable, startup recovery evaluation, rollback and emergency disable. This is Server-tested before UI/API exposure. It must test Server crash/reboot during activation and prove that current RemoteOS management traffic remains reachable.

### Phase 6: API and authorization

Register services in Program.cs and add MapProxyEndpoints. Apply authenticated plus dangerous-operation policies. Runtime/TUN/recovery mutations require idempotency and durable operation IDs. Audit every required action with actor/session/host/engine/profile/result/correlation ID and no sensitive content.

### Phase 7: Avalonia

Register typed Proxy repository, manifest and built-in app. Add Overview, Profiles, Proxies, Connections, DNS, Logs and Settings as separate pages, capability-driven rather than engine-name-driven. Use existing theme, localization and managed modals.

### Phases 8 through 10

Complete audit/secret/controller security and authorization tests, run real Windows and Ubuntu TUN integration tests that prove the management path survives activation, then add the required docs/proxy architecture, Mihomo, TUN, recovery, security, installation and troubleshooting documentation.

## Risks and decisions before their phases

1. No cross-platform elevation workflow currently exists. Phase 3 must design a constrained deployment/typed-helper boundary before any service install or route recovery code. It must not collect OS passwords or become a generic executor.
2. Platform paths and network-route/DNS abstractions do not exist. These are new focused infrastructure, not duplications.
3. INativeServiceAdapter is too narrow to call a service manager. Extract only safe common parts if useful.
4. Existing operation stores need either an intentional limited reuse/extraction decision. A host-wide TUN lock and recovery marker are mandatory additions.
5. AppPermissions are UI capability metadata, not endpoint authorization. Add genuine Proxy policies while retaining single-admin compatibility.
6. FRP is a safety-pattern reference only; its long-lived child process design violates the Mihomo service requirement.
7. Proxy Manager must diagnose firewall state only in Phase 1 scope. It must not write UFW, nftables, iptables or Windows firewall policy directly.
8. The specification's uppercase error examples conflict with the repository's lower-case dotted problem-code convention. Phase 1 must choose one stable public convention and use it consistently; do not mix styles ad hoc.

## Phase-0 completion

- [x] Read the complete specification.
- [x] Inspected the solution, Server composition and persistence.
- [x] Inspected permissions, elevation, services, platform seams, API, operations, audit, secret storage and streaming.
- [x] Inspected Avalonia MVVM, typed clients, workspace, modal, theme and localization patterns.
- [x] Identified reusable infrastructure, missing components and conflicts.
- [x] Made no product-code changes beyond this Phase 0 discovery document.

Phase 1 should begin only after this document is accepted.
