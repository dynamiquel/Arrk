# Installation, storage and configuration

Start Arrk safely, choose where files live and preserve your settings when upgrading.

## First start

Arrk runs and configures the services behind your media library. You need a Linux host with Docker, internet access and space for configuration, downloads and media. Docker runs each service in a container, a separate application environment on the same host.

Arrk needs access to the Docker socket to manage those containers. That access gives it extensive control over the host. Install it only on a machine you control.

1. Choose persistent directories for configuration, downloads and media before installation.
2. From the repository, use `./scripts/run-arrk.sh`, or use `docker compose up -d` for the included Compose deployment.
3. Open Arrk on your host's port 5186, unless you changed the port.
4. On a fresh installation, sign in with `admin` / `admin` and change the password immediately in My account.
5. Keep the installation private while you configure access and test the library.

The published image is `ghcr.io/dynamiquel/arrk:latest`. The launcher uses `./arrk-data` by default and reuses `./stackarr-data` when it exists. Compose retains its legacy storage default so upgrades do not silently start with empty directories.

## Storage and backups

Keep the directories mounted at `/config`, `/downloads` and `/media` persistent. Configuration includes accounts, settings and keys; media holds your library; downloads holds temporary and completed transfers awaiting import.

Back up configuration and any separate Docker configuration volumes used by managed services. A volume is storage Docker maintains outside a container's disposable files. Saving only the Arrk configuration file does not back up the whole stack. Include media in your backup plan if you need to recover the files themselves.

Leave enough free space for a download and its imported copy. Uploaded videos are copied into the library, so staging and the finished file can occupy space at the same time.

The default TV root is `$media/tv`, with movies and shows beneath it; music uses `$media/music`. Administrators can change these through `storage.tv` and `storage.music`. Moving a configured root does not move an existing library automatically. Back up first, update mounts and settings, then check the library folders in the affected service.

## Editing configuration

Open Configuration for validated editing, help text and version history. Most changes are picked up without restarting Arrk, though a managed service may take time to apply them. Change one area at a time and check the result before continuing.

Administrators who prefer manual editing can use `/config/arrk.yaml`. The configuration editor's schema lists supported options. Keep an external backup even when version history is available, and remove secrets before sharing an example.

For full movie and TV discovery, add a TMDb API key or read token under Metadata. TMDb supplies titles, artwork and descriptions. The manual settings are `metadata.tmdbApiKey` and `metadata.tmdbReadToken`. Without them, discovery is limited but library-manager search remains available. Music metadata does not need a Deezer API key.

## Upgrades and legacy installations

Keep your existing configuration and storage rather than creating new empty directories during an upgrade.

1. Back up configuration, service configuration volumes and any library files you cannot replace.
2. Stop the old Arrk application before starting its replacement.
3. Pull the new image and recreate the container with the same configuration directory mounted at `/config`, the same media/download mounts and the same `StackName`.
4. Sign in and check your accounts, settings, library and playback before deleting any backup.

Do not rename or delete legacy `stackarr.yaml`, `stackarr.json` or `stackarr.db` files. Keep any accompanying database files together too. Arrk reads existing installations in place; old configuration filenames take precedence when both old and new names exist. Fresh installations use `arrk.yaml` and `arrk.db`.

Never run old and new versions against the same configuration at once. Existing Docker resources may still have `stackarr` in their names; that is expected and is not a reason to rename or remove them.

## When setup fails

Check that Docker is available, storage mounts point to the intended directories, the disk has free space and no other application is using the chosen host ports. Multiple Arrk installations need different `StackName` values and non-overlapping host ports.

If Arrk opens, check Events at `/events` and application logs at `/logs` for the failing service. If it does not open, use `docker logs -f arrk` for an installation whose container is named `arrk`. Share only the relevant error with credentials removed, not your entire configuration.
