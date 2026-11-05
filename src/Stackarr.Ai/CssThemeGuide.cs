namespace Stackarr.Ai;

/// <summary>
/// Compact, curated context for the CSS generation prompt. It describes the real token layers and
/// the selectors that still own a background or gradient, without shipping the whole stylesheet to
/// the model on every request.
/// </summary>
internal static class CssThemeGuide
{
    public const string Text =
        """
        Stackarr's theme is layered. Most of the UI derives from a small set of tokens, so change
        the source tokens first and only target a component when the request truly needs it.

        Source tokens (override these):
        - Surfaces: --st-bg-primary, --st-bg-surface, --st-bg-elevated, --st-border,
          --st-border-strong, --st-border-hover.
        - Text: --st-text-primary, --st-text-secondary, --st-text-muted, --st-on-accent.
        - Accents: --st-pink (primary), --st-purple (secondary), --st-blue (tertiary),
          --st-green, --st-yellow, --st-red, --st-orange.
        - Optional status text on solid fills: --st-on-green, --st-on-yellow.

        Derived tokens (they follow the source tokens; override only when needed):
        - Glass: --st-glass-surface, --st-glass-surface-strong, --st-glass-popover,
          --st-glass-border, --st-glass-border-soft, --st-glass-border-strong,
          --st-glass-border-bright, --st-glass-fill, --st-glass-highlight, --st-glass-image.
        - Overlays: --st-overlay-fill, --st-overlay-fill-strong, --st-overlay-border.
        - Accent tints and glows: --st-pink-soft, --st-pink-glow, --st-purple-soft,
          --st-blue-soft, --st-green-soft, --st-yellow-soft, --st-red-soft, --st-orange-soft,
          --st-pink-fill, --st-pink-border, --st-glow-1, --st-glow-2, --st-glow-accent,
          --st-glow-primary, --st-red-glow.
        - Brand gradient: --st-gradient, --st-gradient-soft, --st-gradient-line.
        - Drawer and appbar: --st-bg-drawer and the appbar gradient derive from the surface tokens.

        Because the theme is derivative, a light theme usually only needs:
        - base surfaces, borders and text;
        - --st-pink, --st-purple, --st-blue for the accent family;
        - optionally the status colours and --st-on-accent.
        Do not hard-code component colours that already use a token.

        Components worth targeting only when the request demands more than a token change:
        - Shell: .st-appbar, .st-drawer, .st-main, .st-page-container, .st-appbar-actions.
        - Surfaces: .st-card, .st-stat-card, .st-tile, .st-section, .st-section-header,
          .st-page-header, .mud-paper, .mud-dialog, .mud-popover, .mud-overlay-scrim,
          .mud-expansion-panel, .mud-table, .mud-progress-linear.
        - Controls: .mud-button-filled-primary, .st-btn-gradient, .mud-button-outlined,
          .mud-button-text, .mud-icon-button, .mud-chip-color-primary,
          .mud-chip-color-secondary, .mud-input, .mud-input-outlined-border.
        - Navigation: .st-nav-item .mud-nav-link, .st-nav-grid-item .mud-nav-link.
        - Media: .st-media-card, .st-media-card-info, .st-media-dialog, .st-music-player.

        Rules:
        - Return plain CSS only. No markdown fences, no explanation, no HTML.
        - Prefer token overrides. Aim for roughly 40-120 lines; never exceed 180 lines.
        - If a surface already has a gradient, replace background or background-image, not just
          background-color.
        - Never hide navigation, app-bar actions, dialogs, form fields or media controls.
        - Keep text and controls readable in the resulting theme.
        - Do not use @import or external url().
        """;
}
