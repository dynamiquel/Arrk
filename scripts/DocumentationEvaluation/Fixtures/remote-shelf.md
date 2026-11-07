# Remote Shelf: offline device copies

Remote Shelf is an evaluation-only plugin fixture, not a shipped Arrk feature. It copies selected library files to trusted devices for offline playback. It is separate from exposing Arrk over HTTPS.

## Pair a device

Open Remote Shelf and request a pairing code. Approve it while signed in to the account that owns the device. Pairing authorizes offline copies for that account; it does not make the device an administrator. Reject unexpected pairing requests and revoke lost devices promptly.

## Queue an offline copy

Choose an available library title, a paired device and the copy quality. Check destination storage before starting. Copying an existing file is not a new media request. The device can play it only after the copy completes and its player supports the format.

## Limit transfer bandwidth

Set a per-device transfer limit and optional quiet hours. Limits apply to Remote Shelf copies, not torrents, music streaming or remote web playback. A slow network may still reduce actual transfer speed below the configured ceiling. Check which transfer is slow before changing a downloader's VPN.

## Expire offline copies

Choose how long the device keeps downloaded copies. Expiry affects device copies only; it does not delete the server's library file. Pinning media on the server does not override an offline expiry date. Renew the device copy separately before travelling.

## Revoke a lost device

Remove the device grant to prevent future transfers. Revocation cannot guarantee erasure of files already copied to an offline device. Treat device storage and account security as separate responsibilities. Do not assume removing a web login deletes offline files.

## Recover an interrupted transfer

Reconnect the device and inspect the transfer's resume status. If the source file changed, discard the incomplete device copy and restart it. Do not delete the original media to clear a stuck transfer. A completed offline copy is not evidence that Arrk's reverse proxy is configured correctly.
