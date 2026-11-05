# Authentication

Stackarr is the account authority for the stack and an OpenID Connect provider. Players either
trust Stackarr's OIDC (Jellyfin's OIDC RBAC plugin) or are provisioned with a username and
optional password for clients without OIDC, such as Infuse and Subsonic apps.

Discord and MyAnimeList federate into Stackarr, so users keep one account everywhere. Discord
uses OIDC; MyAnimeList uses OAuth2 through its own module.

```
Discord / MyAnimeList
          |  OIDC or OAuth2
          v
    Stackarr  - ASP.NET Core Identity (SQLite)
          |     OpenIddict: discovery, authorize, token, userinfo, roles claim
          +--> Jellyfin: OIDC RBAC plugin, Quick Connect, account/password provisioning
          +--> Navidrome: user and password provisioning
```

The feature lives in `Stackarr.Auth`. Cross-module contracts (`StackarrUser`, roles, claims,
`IExternalLoginProvider`, `IAccountProvisioner`, `IUserDirectory`, `IExternalIdentityStore`,
`IExternalIdentityResolver`) live in `Stackarr.Auth.Abstractions`; other modules never reference
`Stackarr.Auth`.

## Accounts and roles

- `StackarrUser : IdentityUser<int>` plus avatar, active flag, created and last-login. Roles are
  `Admin` and `User`.
- The first administrator comes from `auth.adminUsername` / `auth.adminPassword` (default
  `admin` / `admin`). There is no setup wizard.
- Usernames are canonical; there is no separate display name. External logins never derive a
  username from an email address.
- Users manage their own profile at `/account` (username, avatar, password, connections). Admins
  manage accounts at `/users` and review pending sign-ups there and on the dashboard.
- Stackarr's database is the system of record. Jellyfin and Navidrome keep their own credential
  stores and are updated through their APIs. Passwords are forwarded only when set, because
  Stackarr stores hashes.

## Invite-only sign-up

`auth.registrationMode` decides how accounts are created. Every page except sign-in requires an
authenticated, approved account.

| Mode | Behaviour |
|---|---|
| `Approval` (default) | `/register` and an unknown Discord sign-in create a `Pending` account. It reserves the username/identity but cannot sign in. Admins approve (role becomes `User`) or deny (delete). |
| `Open` | Sign-up creates a normal `User` account and signs in immediately. |
| `AdminOnly` | No self-service sign-up. `/register` redirects and unknown external identities are refused. |

A pending account is a placeholder, not a request row. Approval is only a role change, and
promotion triggers normal player sync. The default policy and `StackarrPolicies.Authenticated`
require `User` or `Admin`, so even a hypothetical cookie grants a pending account nothing.
`/register` exists only when `auth.allowLocalSignUp` is true, password login is enabled and the
mode is not `AdminOnly`.

## Stackarr as an OIDC provider

Implemented with OpenIddict.

- Endpoints: discovery, JWKS, `/connect/authorize`, `/connect/token`, `/connect/userinfo`.
- Flows: authorization code with PKCE required, plus refresh tokens.
- Claims: `sub`, `name`, `preferred_username`, `email`, `roles`.
- Signing/encryption certificates persist under `/config/openiddict`, so tokens survive restarts.
- The issuer is `auth.provider.issuer`, otherwise `routes.publicUrl`, otherwise
  `routes.baseUrl`. It must be reachable by browsers and service containers and stable.
- `OidcClientSeeder` registers a `jellyfin` client with a secret generated once and stored in
  `Settings`.

## Discord

- Configured under `auth.external.discord`; `authority` is configurable for testing.
- Sign-in first tries `prompt=none`, then retries interactively once if Discord needs input.
- The post-login cookie persists for `auth.sessionDays`.
- The username preference is `preferred_username`, then `username`, then `name`/`global_name`/
  `nickname`, skipping email-looking values, with `user-{sub}` as the last resort.
- The OIDC `picture` claim becomes the avatar when the account has none. A failed download never
  blocks sign-in.
- Unknown identities follow `registrationMode`; a disabled account cannot sign in.
- Linking from `/account` attaches the external login to the current account. An identity
  already linked elsewhere is rejected, and unlinking is refused when it would remove the only
  sign-in method. The linked Discord identity is also what notification direct messages resolve;
  unlinking stops them.
- Register `{base}{callbackPath}` (default `/signin-discord`) in the Discord developer portal.
- `auth.allowPasswordLogin` controls whether the username/password form is offered.

## MyAnimeList

- MAL is OAuth2 with `plain` PKCE, so it has its own flow rather than the OpenIdConnect handler.
- The module contributes an `IExternalLoginProvider`; login and linking dispatch through the
  shared provider registry.
- Configuration lives under `services.mal`, and the MAL app must register `{origin}/mal-signin`
  exactly.
- Linking and sign-in share one flow. Signing out does not revoke MAL tokens; unlinking does.

## Player credentials

- `auth.players.provisionAccounts` mirrors Stackarr accounts into players.
- `auth.players.provisionPasswords` forwards passwords for clients without OIDC. This is
  independent of `allowPasswordLogin`.
- `IAccountProvisioner` is the extension point. Failures are logged and retried on the next sync
  and never block local account management.

## Jellyfin SSO

`JellyfinSsoInstaller` drives Jellyfin's admin API idempotently from startup and the config-sync
job: add the plugin repository, install the OIDC RBAC plugin, restart, write issuer/client/role
mapping/auto-provision config, then restart again. It rewrites the configuration when the client
secret rotates.

Jellyfin notes: plugins need a dotted folder name under `/config/data/plugins`; the plugin applies
written configuration only after a restart; `ServerBaseUrl` must match the registered redirect
URI. Stackarr injects plain sign-in and Quick Connect links into Jellyfin's login disclaimer
because Jellyfin 12 strips scripts. A Stackarr admin's Jellyfin admin cannot be disabled, because
Jellyfin refuses to disable administrators.

## Sessions, proxy and security

- The UI uses an HTTP-only cookie carrying the Identity security stamp, re-checked against the
  live account on every request. Disabling, deleting, demoting or changing a password ends
  existing sessions immediately.
- Data Protection keys persist to `/config/dataprotection`, so sessions and OIDC state survive
  restarts and container recreation.
- Traefik plus optional Cloudflare tunnel; forwarded headers and an HTTPS override make
  OpenIddict advertise `https://` endpoints.
- Passwords are hashed; plaintext exists only transiently for player provisioning. New passwords
  need at least 8 characters and reject a small common-password list.
- Sign-in, sign-up and OAuth-start endpoints are rate-limited per client IP
  (`auth.authRateLimitPerMinute`) and protected by antiforgery tokens. Login CSRF and open
  redirects through `returnUrl` are rejected.
- Only loopback and private Docker/LAN ranges may supply `X-Forwarded-*` headers.
- Anonymous surfaces are `/login`, `/register` (when enabled), `/denied`, `/connect/*` and
  `/health`. The Hangfire dashboard and the config editor/schema APIs require Admin.

## Configuration

```yaml
auth:
  adminUsername: admin
  adminPassword: admin            # bootstrap only; change after first login
  sessionDays: 14
  requireHttps: false
  authRateLimitPerMinute: 10      # 0 disables
  allowPasswordLogin: true
  allowLocalSignUp: true
  registrationMode: Approval      # Open | Approval | AdminOnly
  provider:
    enabled: true
    issuer: ""                    # default: routes.publicUrl, then routes.baseUrl
    jellyfinClientId: jellyfin
    jellyfinProviderId: stackarr
    jellyfinRedirectUris: []
  external:
    discord:
      enabled: true
      clientId: ""
      clientSecret: ""
      callbackPath: /signin-discord
      authority: https://discord.com
  players:
    provisionAccounts: true
    provisionPasswords: true
```

Jellyfin-specific SSO settings live under `services.jellyfin`: `manageUsers`, `oidcSso`,
`oidcPluginManifestUrl`, `oidcPluginVersion` and `oidcAdminRole`.

## Limitations

- Discord requires its redirect URI to be registered in the developer portal.
- Jellyfin administrators cannot be disabled through Stackarr.
- Navidrome authentication is provisioned, not federated.
- MAL has no manga support and its refresh token expires if unused for about a month.
