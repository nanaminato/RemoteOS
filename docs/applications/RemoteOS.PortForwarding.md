# RemoteOS Port Forwarding

## Scope

`remoteos.port-forwarding` is a first-party Client application that creates local SSH forwards for
services listening on the RemoteOS Server loopback interface. A request such as `localhost:7000`
produces a Client-host URL such as `http://localhost:7000`; if that port is occupied, it searches
for the next bindable loopback port and returns that effective URL.

## Boundary and security model

- The Client starts `ssh -N -L 127.0.0.1:<local>:<server-loopback>:<remote>`; the listening socket
  is always restricted to `127.0.0.1`, so it does not expose a service on the LAN.
- Requests only accept `localhost` and `127.0.0.1` as the server-side target, and HTTP/HTTPS links.
  They cannot become arbitrary network proxies.
- SSH authentication is delegated to the host `ssh` program and its existing config, key files, and
  agent. RemoteOS neither collects nor stores passwords, private keys, or tokens.
- The service is registered as `IPortForwardingService` for the Port Forwarding application's local
  lifecycle; RemoteBrowser does not call it or create tunnels automatically.

## Local-only state

SSH host/user/port are stored at `LocalApplicationData/RemoteOS/port-forwarding.json`. The file
contains non-secret connection preferences only. Running processes are held in memory and disappear
when the Client exits. Neither setting is sent to the Server or added to Workspace preferences, so
they are never synchronized between devices.

## Lifecycle

1. Validate the loopback target and choose the requested local port when it can be bound; otherwise
   search upward and then wrap for the next available port.
2. Start OpenSSH with `BatchMode=yes` and `ExitOnForwardFailure=yes`; wait briefly for immediate
   authentication/bind failure before returning success.
3. Keep the owned process in the runtime registry and surface it in the application UI.
4. Update replaces the selected process; stop terminates only the process owned by this Client.

The UI allows starting, listing, changing, and stopping forwards. A failed SSH start returns a
generic actionable status and never logs SSH output or credentials.
