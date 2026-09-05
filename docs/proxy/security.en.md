# Proxy security

The public API exposes engine-neutral, safe DTOs only. It never returns raw YAML, controller addresses or secrets, subscription credentials, private keys or arbitrary command arguments. Controller logs are bounded and sanitized.

No Proxy workflow disables Defender or a firewall, opens a public controller port, asks for an OS password, or provides a generic privileged-command endpoint.
