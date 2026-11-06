# Tiles and per-account UI

How the shared tile collection, dashboard document, Tile Editor and custom CSS fit together. The
shell never hard-codes a dashboard widget; modules contribute tiles and the account owns the
arrangement.

## Model

A **tile** is one widget contributed by a module. Its frame (icon, title and optional
navigation) is shared, so the same tile reads correctly on its owning page and on a dashboard.

| Contract | Purpose |
|---|---|
| `ITileProvider` | Contributes `TileDefinition`s from a module or plugin |
| `TileDefinition` | Stable `Id`, `Category`, `Title`, `Icon`, body `Component`, optional `Href`, visibility and default size |
| `TileContext` | Definition, placement (`Dashboard` or `Page`), admin flag and account id |
| `TileRegistry` | Composes providers, filters disabled services live and rejects duplicate ids |
| `TileView` | Renders the frame and body; links only on the dashboard |
| `TileSectionView` | Renders one dashboard section and its responsive grid |

A page shows the short title (`Download`); a dashboard prefixes the category
(`TORRENTS - DOWNLOAD`). A tile that links to an admin-only page sets `LinkAdminOnly`, so the tile
stays visible but the link does not. `AdminOnly` hides the tile itself from non-admins.

## Dashboard document

The dashboard is a per-account YAML document and is the source of truth:

```yaml
version: 1
sections:
  - id: system
    title: System
    tiles:
      - id: services.status
        size: Large
      - id: system.host
        size: Medium
  - id: downloads
    title: Downloads
    tiles:
      - id: torrents.download
```

- `size` is `Small`, `Medium` (default), `Large` (double `Medium`) or `VeryLarge` (full width).
- A new account starts from `ui.defaultDashboardSections`: an array of sections, each with an
  array of tiles (`id` and `size`). The built-in default is a **Host** section with service
  status, host, storage and speedtest, plus a **Library** section with pending requests and the
  media/music shelves. Administrators can change this in Configuration → Interface; unknown,
  disabled and admin-only tiles are filtered out per account. Other tiles stay available in the
  editor.
- Validation rejects unknown ids, duplicate tiles, disabled services, admin-only tiles for
  non-admins and oversized documents. Unknown ids render as a placeholder.
- The document is stored per account in `UserUiPreferences` (`DashboardYaml`).

## Dashboard editor

The **Edit Dashboard** button opens the editor as a card-sheet dialog:

- **Visual** edits sections and tiles (add, remove, reorder, resize, rename).
- **YAML** uses the shared Monaco editor with a generated schema for the dashboard document.
  _Apply YAML_ validates into the visual view; _Save_ stores the canonical document.

The editor only offers tiles the account may use and whose service is enabled. Links are a
dashboard-only affordance.

## Custom CSS

Each account can store CSS in `UserUiPreferences` (`CustomCss`), rendered after the shared theme
by `UserCssStyle`. It can override tokens and component rules without forking the UI, and saving
updates the running shell immediately.

The theme is layered so small token changes travel: base surface/border/text tokens and three
accent colours drive the brand gradient, tints, glows, glass surfaces, overlays and component
defaults. Component selectors are only needed for exceptions.

When AI is enabled, **Generate with AI** turns a plain-language brief into CSS through
`IUserCssAssistant`. The prompt carries a curated token map rather than the whole stylesheet. The
result is reviewed before saving, and the UI layer never references the AI module. A `</style>`
sequence is stripped before storage.

## Module checklist

1. Register `ITileProvider` and give every tile a stable, namespaced id (`torrents.download`).
2. Set `EnabledConfigPath` when the tile belongs to a config-gated service.
3. Set `Href` (and `LinkAdminOnly` for admin-only pages) if the tile represents a page.
4. Share one module-owned summary service when several tiles read the same expensive library.
5. Put the body in the module and take `TileContext` only if it needs placement, role or account.
6. Reuse the same definition on the owning page instead of duplicating markup.
