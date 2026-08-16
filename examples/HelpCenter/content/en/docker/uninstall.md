# Uninstall Docker

Removing Docker also removes the local engine and its service. Containers, images, volumes, and configuration may remain until you remove them explicitly.

## Before you begin

- Export images or volumes that you need to keep.
- Stop workloads that depend on Docker.
- Record any compose files and registry credentials required for a later reinstall.

## Remove the engine

1. Use your Linux distribution's package manager to remove Docker Engine and its related packages.
2. Disable the Docker service if the package manager did not do so.
3. Verify that the command is no longer available.

```bash
docker --version
```

## Optional data cleanup

Docker data is commonly stored below `/var/lib/docker`. Delete it only after confirming that no required images, volumes, or container data remain. This action is irreversible.
