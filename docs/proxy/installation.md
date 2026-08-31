# Proxy installation

1. Grant the Proxy application permissions appropriate to the operator.
2. Install a manifest-verified Managed runtime, or validate an existing External runtime.
3. Create and activate a profile, then apply a validated configuration.
4. Start the service with TUN disabled and confirm controller health.
5. Enable TUN only after the platform checklist in `RemoteOS.ProxyManager.SkippedTests.md` has been passed.

An unavailable privileged boundary or platform capability is a safe failure; RemoteOS will not request a password or run substituted shell text.
