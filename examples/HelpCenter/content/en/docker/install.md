# Install Docker

This guide installs Docker Engine on a Linux server. Use the official Docker packages for your distribution whenever they are available.

## Before you begin

- Confirm that you have administrator access to the server.
- Check that the server can reach Docker's package repository.
- Remove or account for older Docker packages before installing a newer engine.

## Install

1. Follow the Docker Engine installation instructions for the server's Linux distribution.
2. Start and enable the Docker service.
3. Verify that the daemon is running.

```bash
docker --version
docker info
```

## Verify in RemoteOS

Open Docker Manager and wait for the status card to show that the engine is available. If it remains unavailable, inspect the service log and confirm that the current user is allowed to access the Docker socket.
