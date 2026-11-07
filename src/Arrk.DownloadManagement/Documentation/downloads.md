# Downloads, indexers and VPN

Set up download sources, understand transfer states and recover safely from slow downloads or a full disk.

## Indexers and sources

An indexer is a searchable catalogue of download releases. Arrk uses Prowlarr to connect torrent indexers to your library services. Transmission downloads torrents, while slskd downloads music from the Soulseek file-sharing network.

1. As an administrator, add the indexer's settings in Configuration under `indexers.entries`, or use Prowlarr's own interface when its proxy is enabled.
2. Use the correct indexer definition and credentials for your account. Requirements differ by provider; placeholder settings will not work.
3. Test a known search before adding more sources.

Use only sources and content you are authorised to access. Keep indexer credentials private. The challenge solver under `indexers.solver` can help with supported website challenges, but cannot fix incorrect credentials or a provider outage. Disable administrative proxies you no longer need.

## Read the Downloads page

Open Downloads at `/downloads` to see torrents and Soulseek transfers together. Cards show progress, speed, size and estimated time remaining. Torrents also show connected peers and a ratio, the amount uploaded compared with the amount downloaded.

- Downloading means data is transferring; Queued means it is waiting.
- Paused needs attention before it can continue.
- Seeding means a completed torrent is sharing data with other people.
- Error needs investigation; download completion alone does not mean a file has reached the library.

Administrators can pause, resume and cancel transfers, including bulk pause/resume. Cancelling manually does not automatically start a replacement search. Soulseek does not use torrent seeding or torrent cleanup rules. Transmission remains part of the stack; `services.torrentDownloader.enabled` is not a working disable switch.

## Slow, stalled and errored torrents

A stalled torrent has stopped transferring with no peers or seeds available. A slow torrent is below the configured speed threshold. Arrk can remove persistently slow or stalled downloads and try another release. It holds those penalties while the measured internet connection is unhealthy, rather than treating an outage as every torrent's fault.

1. Check the transfer's error, progress and peer count on Downloads.
2. Check whether other transfers or the internet connection are also affected.
3. Review Events for cleanup or retry activity before starting another search yourself.
4. If an administrator revokes a penalty, check that the cause has improved first.

Error cleanup uses its own waiting period. Even a completed torrent can be removed after a persistent tracker error. Cleanup can delete downloaded files, so review settings under `services.torrentDownloader.management` before changing them.

A failed download can cause a different release to be tried. It does not permanently ban the movie, show or album from household requests.

## Seeding and disk pressure

Seeding shares a completed torrent back to others. Choose goals that follow your tracker's rules. Under `services.torrentDownloader.seeding`, minimum seed time delays cleanup; ratio or maximum time can then cause a completed torrent to be removed.

By default, storage management starts removing seeding torrents when the downloads filesystem reaches 85% used. At 90%, it pauses torrents instead and skips that cleanup pass. Paused torrents do not automatically resume when space becomes available.

If storage fills up, free space safely, check Downloads and resume intentionally. Deleting an old download job and deleting a library title are different operations. Storage cleanup does not start replacement searches. Check which filesystem is full before removing library media.

## Route a service through VPN

A VPN sends a service's traffic through a provider's encrypted connection. Arrk uses Gluetun to manage that connection. You need a supported VPN provider and the provider's connection details before enabling routing.

1. In Configuration, enter your provider and connection type under VPN, with the required credentials or WireGuard details.
2. Enable VPN routing for the service you want to protect. For Transmission, the setting is `services.torrentDownloader.vpn`.
3. Save and check the VPN and downloader's health, then test a transfer you are authorised to download.
4. Enable `vpn.portForwarding` only if your provider supports it and your setup needs it.

Routing is per service. Routing the downloader does not route your players or the whole stack. A service's VPN setting can start the tunnel even with `vpn.enabled: false`; switching off that global setting alone is not enough to remove every service's VPN routing.

If downloads stop after a change, check provider credentials, tunnel health and connectivity before changing indexers. Keep WireGuard private keys and other secrets out of support messages. A VPN does not replace access control or permission to download content.
