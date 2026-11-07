# Accounts, requests and remote access

Invite your household, request titles and reach Arrk safely away from home.

## Accounts and roles

Administrators manage accounts on Users at `/users`. A regular user can browse and request media; administrator-only actions include managing accounts and settings. Give that access only to people who need it.

1. Replace the initial administrator password before inviting anyone.
2. In Configuration, choose how new accounts are admitted with `auth.registrationMode`.
3. Use Approval to review new accounts before they can sign in, or AdminOnly to keep account creation with administrators. Open allows open registration.
4. Check the account's role before sharing access.

In My account at `/account`, users can manage their own account and link supported Discord or MyAnimeList identities when enabled. Password sign-in, external sign-in and creating accounts are separate settings.

Arrk can also provision accounts for players. Single sign-on, using Arrk to sign into another service, depends on the player and client supporting it. It is separate from allowing password sign-in to Arrk itself.

## Request a title

Requests let a household user ask for media without adding it directly. Requests must be enabled, and the chosen kind of request must be allowed by the administrator.

1. Find the movie, show, album or artist in discovery and open its details.
2. Check the title, year or artist, then choose the available request action.
3. Choose the offered scope and quality. A scope is how much you want, such as a series, season or episode.
4. Open Requests at `/requests` to follow the decision. Owners can revoke their own pending requests; administrators can approve, deny or blacklist them.

Pending means waiting for moderation. Approved means the media service accepted the request, not that the download is complete. Check library availability before expecting playback. Failed and Denied are different: one reports an unsuccessful approval attempt, the other an administrator's decision.

Administrators can enable automatic approval for selected users or roles. `requests.maxOpenPerUser` limits pending requests; zero means unlimited. Existing series can currently prevent requests for another season or episode, even when those files are missing. Ask an administrator to manage the missing content instead.

## Request blacklist versus release failure

A request blacklist prevents future requests for a title in that media category. It is different from avoiding one broken download release. A successful download elsewhere does not remove the request blacklist.

Administrators should use Blacklist deliberately. With `requests.blacklistOnDeny` enabled, denying a request also blacklists it. There is currently no general un-blacklist control, so denial without blacklisting is the less permanent choice when appropriate.

A failed torrent or music source may be avoided while another download is tried. That does not mean the title has been banned from requests.

## Remote access and HTTPS

Remote access needs a reachable address and a secure connection. HTTPS encrypts traffic between the browser and your server. A reverse proxy accepts that traffic and sends it to the right service; Arrk uses Traefik for its routes.

1. Arrange HTTPS through your proxy or a configured tunnel before exposing account sign-in publicly.
2. Set `routes.publicUrl` to the exact public address. Arrk uses it for shared links and player sign-in redirects.
3. Enable only the service routes you intend to expose. When proxied, Jellyfin and Navidrome use `/jellyfin` and `/navidrome`.
4. Enable `auth.requireHttps` when Arrk is reachable only through HTTPS.
5. Test sign-in, a shared link and player access from outside your home network.

A Cloudflare tunnel can provide access without opening host ports; it needs its secret token under `routes.cloudflared`. Tunnel-only installations can set `routes.httpPort` and `routes.httpsPort` to zero. Keep a safe administrative access route while changing connectivity.

Setting an HTTPS public address does not automatically obtain a certificate. Certificate automation is not complete; your proxy or tunnel must provide working HTTPS. A hard-to-guess address is not a substitute for account security.

## Share a diagnostic safely

When asking for help, include the failed action, approximate time, affected title, your account role and the visible error. Check Events at `/events` for related activity.

Remove passwords, API keys, cookies, tunnel tokens and webhook URLs before sharing logs or screenshots. Signed playback URLs can grant access to a video too. Share a short relevant error, not an entire configuration file.
