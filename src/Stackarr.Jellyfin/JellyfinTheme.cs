namespace Stackarr.Jellyfin;

/// <summary>
/// The custom CSS Stackarr pushes to Jellyfin's branding so the media server visually matches
/// the Stackarr web app: dark violet glass surfaces, a subtle purple→pink→indigo accent, and
/// Product Sans.
///
/// The tokens below mirror the web app's site.css, so re-theming Stackarr and Jellyfin stays in
/// step. Jellyfin 12's web client mixes a MUI app bar with legacy Emby markup, so selectors
/// target the real client classes (`.emby-*`, `.card*`, `.listItem`, `.navMenuOption`,
/// `.skinHeader`, …) with MUI fallbacks where they exist.
/// </summary>
public static class JellyfinTheme
{
    public const string Css = """
        /* STACKARR THEME FOR JELLYFIN */
        @import url('https://fonts.googleapis.com/css2?family=Product+Sans:wght@400;700&display=swap');

        :root {
            /* Surfaces, mirrored from the Stackarr web app (site.css) */
            --st-bg-primary: #0f0a18;
            --st-bg-surface: #181225;
            --st-bg-elevated: #221a30;
            --st-border: #2d2040;
            --st-border-strong: #3a2a52;
            --st-text-primary: #e8e0f0;
            --st-text-secondary: #a899be;
            --st-text-muted: #6b5c80;

            /* Accents */
            --st-pink: #e879a0;
            --st-purple: #c084fc;
            --st-indigo: #818cf8;

            /* Brand gradient (same stops as the web app; dial with --st-grad-strength) */
            --st-grad-strength: 1;
            --st-grad-1: 232, 121, 160;
            --st-grad-2: 192, 132, 252;
            --st-grad-3: 129, 140, 248;
            --st-gradient: linear-gradient(135deg,
                rgba(var(--st-grad-1), var(--st-grad-strength)) 0%,
                rgba(var(--st-grad-2), var(--st-grad-strength)) 55%,
                rgba(var(--st-grad-3), var(--st-grad-strength)) 100%);
            --st-gradient-soft: linear-gradient(135deg,
                rgba(var(--st-grad-1), 0.16),
                rgba(var(--st-grad-2), 0.16));
            --st-gradient-line: linear-gradient(90deg,
                rgba(var(--st-grad-1), var(--st-grad-strength)),
                rgba(var(--st-grad-2), var(--st-grad-strength)),
                rgba(var(--st-grad-3), var(--st-grad-strength)));

            /* Glass */
            --st-glass-surface: rgba(24, 18, 36, 0.5);
            --st-glass-surface-strong: rgba(30, 22, 46, 0.72);
            --st-glass-image: linear-gradient(180deg, rgba(255, 255, 255, 0.025), rgba(255, 255, 255, 0.006));
            --st-glass-border: rgba(255, 255, 255, 0.08);
            --st-glass-highlight: inset 0 1px 0 rgba(255, 255, 255, 0.035);
            --st-glass-blur: blur(14px);

            /* Glow */
            --st-glow-1: 0 0 14px rgba(192, 132, 252, 0.18);
            --st-glow-2: 0 6px 22px rgba(0, 0, 0, 0.45), 0 0 16px rgba(192, 132, 252, 0.15);

            /* Radii */
            --st-radius-sm: 6px;
            --st-radius-md: 10px;
            --st-radius-lg: 14px;
            --st-radius-btn: 8px;
        }

        /* GLOBAL FONT & STRUCTURAL BACKGROUNDS */
        body, h1, h2, h3, h4, h5, h6, p, a, button, input, select, textarea,
        .MuiTypography-root, .MuiButton-root, .itemDetailTextContainer {
            font-family: 'Google Sans', 'Product Sans', 'Inter', 'Segoe UI', sans-serif !important;
        }

        html, body, #root, .page, .mainLayout, .mainAnimatedPages, .scroller, #itemDetailPage,
        .itemDetailPage, .detailPageWrapperContainer, .detailContainer, .detailRibbon {
            color: var(--st-text-primary) !important;
        }

        body, .backgroundContainer, .itemBackdrop {
            background-color: var(--st-bg-primary) !important;
        }

        .backdropContainer {
            background:
                radial-gradient(1100px 700px at 8% -10%, rgba(192, 132, 252, 0.07), transparent 60%),
                radial-gradient(1000px 700px at 100% 108%, rgba(232, 121, 160, 0.06), transparent 60%),
                var(--st-bg-primary) !important;
        }

        /* HEADER (MUI app bar / legacy skinHeader) */
        .skinHeader, .MuiAppBar-root {
            background-color: var(--st-glass-surface-strong) !important;
            background-image: var(--st-glass-image) !important;
            backdrop-filter: blur(20px) !important;
            -webkit-backdrop-filter: blur(20px) !important;
            border-bottom: 1px solid var(--st-glass-border) !important;
            box-shadow: 0 1px 0 rgba(255, 255, 255, 0.03), 0 4px 24px rgba(0, 0, 0, 0.35) !important;
        }

        /* Brand hairline under the header. */
        .skinHeader::after {
            content: '' !important;
            position: absolute !important;
            left: 0 !important;
            right: 0 !important;
            bottom: 0 !important;
            height: 2px !important;
            background: var(--st-gradient-line) !important;
            opacity: 0.5 !important;
            pointer-events: none !important;
        }

        .headerButton, .paper-icon-button-light, .btnHeaderNav, .headerLink {
            color: var(--st-text-primary) !important;
            border-radius: var(--st-radius-btn) !important;
            transition: color 0.2s, background-color 0.2s !important;
        }

        .headerButton:hover, .paper-icon-button-light:hover, .btnHeaderNav:hover, .headerLink:hover {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
        }

        .headerUsername {
            color: var(--st-text-secondary) !important;
        }

        /* LIBRARY TABS (.headerTabs > .emby-tabs) */
        .emby-tabs, .headerTabs {
            border-bottom: 1px solid var(--st-border) !important;
        }

        .emby-tab-button {
            font-size: 1rem !important;
            font-weight: 600 !important;
            text-transform: none !important;
            letter-spacing: 0 !important;
            color: var(--st-text-secondary) !important;
            background: transparent !important;
            border: none !important;
            border-radius: var(--st-radius-sm) var(--st-radius-sm) 0 0 !important;
            min-height: 48px !important;
            padding: 10px 16px !important;
            transition: color 0.2s, background-color 0.2s !important;
        }

        .emby-tab-button:hover {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
        }

        .emby-tab-button-active {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
        }

        /* Active-tab indicator (Jellyfin animates this element under the active tab). */
        .emby-tabs-slider {
            background: linear-gradient(90deg, var(--st-pink), var(--st-purple)) !important;
            height: 3px !important;
            border-radius: 3px 3px 0 0 !important;
        }

        .emby-button-foreground { color: inherit !important; }

        /* LEFT DRAWER (.mainDrawer / .navMenuOption) */
        .mainDrawer, .MuiDrawer-paper, .sidebarContainer {
            background-color: var(--st-glass-surface-strong) !important;
            background-image: var(--st-glass-image) !important;
            backdrop-filter: var(--st-glass-blur) !important;
            -webkit-backdrop-filter: var(--st-glass-blur) !important;
            border-right: 1px solid var(--st-glass-border) !important;
        }

        .sidebarHeader, .libraryMenuOptions h3 {
            color: var(--st-text-muted) !important;
            font-weight: 700 !important;
            letter-spacing: 0.08em !important;
            text-transform: uppercase !important;
            font-size: 0.75rem !important;
        }

        .navMenuOption, .MuiListItemButton-root, div.mainDrawerMenuItem, a.sidebarLink {
            color: var(--st-text-primary) !important;
            border-radius: var(--st-radius-md) !important;
            margin: 2px 8px !important;
            padding: 10px 12px !important;
            transition: color 0.2s, background-color 0.2s !important;
        }

        .navMenuOption:hover, .MuiListItemButton-root:hover,
        div.mainDrawerMenuItem:hover, a.sidebarLink:hover {
            background: var(--st-gradient-soft) !important;
            color: var(--st-text-primary) !important;
        }

        .navMenuOption-selected, .navMenuOption-active, .Mui-selected.MuiListItemButton-root,
        div.mainDrawerMenuItem-selected, a.sidebarLink-active, .mainDrawerMenuItem-active {
            background-color: var(--st-glass-surface-strong) !important;
            background-image: var(--st-gradient) !important;
            color: #ffffff !important;
            box-shadow: 0 2px 12px rgba(0, 0, 0, 0.3), 0 0 12px rgba(192, 132, 252, 0.18) !important;
        }

        .navMenuOptionIcon { color: inherit !important; }

        /* BUTTONS (.emby-button / .raised / MUI) */
        .emby-button, .MuiButton-root {
            border-radius: var(--st-radius-btn) !important;
            text-transform: none !important;
            font-weight: 600 !important;
            letter-spacing: 0.01em !important;
            transition: background-color 0.2s, color 0.2s, box-shadow 0.2s !important;
        }

        /* Flat / link buttons are secondary. */
        .button-flat, .button-link, .button-flat-mini {
            color: var(--st-text-secondary) !important;
        }

        .button-flat:hover, .button-link:hover, .button-flat-mini:hover {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
        }

        /* Normal primary action: matte pink glass (matches the web app). */
        .raised, .button-submit, .MuiButton-containedPrimary {
            background-color: rgba(232, 121, 160, 0.85) !important;
            background-image: none !important;
            color: #ffffff !important;
            border: 1px solid rgba(255, 255, 255, 0.28) !important;
            backdrop-filter: blur(10px) !important;
            -webkit-backdrop-filter: blur(10px) !important;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.3), 0 2px 8px rgba(0, 0, 0, 0.22) !important;
        }

        .raised:hover, .button-submit:hover, .MuiButton-containedPrimary:hover {
            background-color: rgba(238, 138, 174, 0.95) !important;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.36), var(--st-glow-2) !important;
        }

        /* Floating action buttons (e.g. card play) use the brand gradient. */
        .fab, .cardOverlayFab-primary {
            background: var(--st-gradient) !important;
            color: #ffffff !important;
            border-radius: 50% !important;
            box-shadow: 0 2px 10px rgba(0, 0, 0, 0.35), 0 0 12px rgba(192, 132, 252, 0.2) !important;
        }

        /* MEDIA CARDS (.card / .cardBox / .cardImageContainer) */
        .cardBox {
            background-color: var(--st-glass-surface) !important;
            background-image: var(--st-glass-image) !important;
            border: 1px solid var(--st-glass-border) !important;
            border-radius: var(--st-radius-md) !important;
            overflow: hidden !important;
            backdrop-filter: var(--st-glass-blur) !important;
            -webkit-backdrop-filter: var(--st-glass-blur) !important;
            transition: border-color 0.2s, box-shadow 0.2s, transform 0.2s !important;
        }

        .card:hover .cardBox {
            border-color: rgba(192, 132, 252, 0.55) !important;
            box-shadow: 0 0 0 2px rgba(192, 132, 252, 0.5), var(--st-glow-2) !important;
            transform: translateY(-3px) !important;
        }

        .cardScalable, .cardContent, .cardImageContainer {
            border-radius: var(--st-radius-md) var(--st-radius-md) 0 0 !important;
            overflow: hidden !important;
        }

        .cardPadder {
            background-color: transparent !important;
        }

        /* Card text: left-aligned with breathing room below the poster. */
        .cardText {
            text-align: left !important;
            padding: 8px 12px 4px !important;
        }

        .cardBox-bottompadded { padding-bottom: 8px !important; }

        .cardText-first {
            color: var(--st-text-primary) !important;
            font-weight: 600 !important;
        }

        .cardText-secondary, .cardText-secondary a {
            color: var(--st-text-secondary) !important;
        }

        /* Libraries without artwork use the brand gradient instead of Jellyfin's flat colours.
           (Only the placeholder class — real posters set their own background-image.) */
        [class*="defaultCardBackground"], .defaultCardBackground {
            background-color: var(--st-purple) !important;
            background-image: var(--st-gradient) !important;
        }

        .cardImageIcon {
            color: rgba(255, 255, 255, 0.85) !important;
        }

        /* Hover play overlay. */
        .cardOverlayButton-br {
            background-color: var(--st-glass-surface-strong) !important;
            border: 1px solid var(--st-glass-border) !important;
            border-radius: var(--st-radius-btn) !important;
            backdrop-filter: blur(8px) !important;
        }

        .cardOverlayButton-hover {
            background-color: var(--st-glass-surface-strong) !important;
        }

        /* DETAIL PAGE */
        /* Compact hero so the title is reachable without a long scroll. */
        .itemBackdrop {
            height: 26vh !important;
            min-height: 190px !important;
        }

        .detailLogo { max-height: 100px !important; }

        .detailImageContainer {
            border-radius: var(--st-radius-lg) !important;
            overflow: hidden !important;
            border: 1px solid var(--st-glass-border) !important;
            box-shadow: var(--st-shadow-lg, 0 8px 24px rgba(0, 0, 0, 0.5)) !important;
        }

        .detailSection {
            background-color: var(--st-glass-surface) !important;
            background-image: var(--st-glass-image) !important;
            border: 1px solid var(--st-glass-border) !important;
            border-radius: var(--st-radius-md) !important;
            padding: 14px 16px !important;
            margin-bottom: 12px !important;
        }

        .detailSectionContent { background: transparent !important; }

        .mainDetailButtons, .detailButtons {
            display: flex !important;
            flex-direction: row !important;
            flex-wrap: wrap !important;
            gap: 12px !important;
            align-items: center !important;
        }

        /* Play is the primary CTA → brand gradient. */
        .btnPlay, .btnResume {
            background: var(--st-gradient) !important;
            color: #ffffff !important;
            font-weight: 700 !important;
            border: 1px solid rgba(255, 255, 255, 0.15) !important;
            border-radius: var(--st-radius-btn) !important;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3), 0 0 10px rgba(192, 132, 252, 0.12) !important;
        }

        .btnPlay:hover, .btnResume:hover {
            box-shadow: var(--st-glow-2) !important;
            filter: brightness(1.04) !important;
        }

        .detailButton:not(.btnPlay):not(.btnResume) {
            border-radius: var(--st-radius-btn) !important;
            background-color: transparent !important;
            color: var(--st-text-secondary) !important;
            border: 1px solid var(--st-glass-border) !important;
        }

        .detailButton:not(.btnPlay):not(.btnResume):hover {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
            box-shadow: var(--st-glow-1) !important;
        }

        .detailButton-icon { color: inherit !important; }

        .detailsGroupItem, .detail-clamp-text {
            color: var(--st-text-secondary) !important;
        }

        /* LISTS (.listItem / settings menu / action sheet items) */
        .listItem {
            border-radius: var(--st-radius-md) !important;
            border-bottom: none !important;
            transition: background-color 0.2s, color 0.2s !important;
        }

        .listItem:hover {
            background: var(--st-gradient-soft) !important;
        }

        .listItemBodyText, .listItemBody {
            color: var(--st-text-primary) !important;
        }

        .listItemIcon {
            color: var(--st-text-secondary) !important;
        }

        /* PROGRESS, BADGES & INPUTS */
        .itemProgressBarForeground, .MuiLinearProgress-bar {
            background: var(--st-gradient-line) !important;
            border-radius: 9999px !important;
        }

        .itemProgressBar, .MuiLinearProgress-root {
            background-color: rgba(255, 255, 255, 0.06) !important;
            border-radius: 9999px !important;
        }

        .progressring-bg { stroke: rgba(255, 255, 255, 0.12) !important; }

        .countIndicator, .playedIndicator, .MuiBadge-badge {
            background: var(--st-pink) !important;
            color: #ffffff !important;
            font-weight: 700 !important;
        }

        .emby-input, .emby-textarea, .emby-select, select, .MuiOutlinedInput-root {
            background-color: rgba(255, 255, 255, 0.06) !important;
            background-image: var(--st-glass-image) !important;
            backdrop-filter: blur(8px) !important;
            -webkit-backdrop-filter: blur(8px) !important;
            color: var(--st-text-primary) !important;
            border: 1px solid rgba(255, 255, 255, 0.14) !important;
            border-radius: var(--st-radius-btn) !important;
        }

        .emby-input:focus, .emby-textarea:focus, .emby-select:focus,
        .MuiOutlinedInput-root.Mui-focused {
            border-color: var(--st-purple) !important;
            box-shadow: 0 0 0 3px rgba(192, 132, 252, 0.16) !important;
        }

        .inputLabel, .selectLabel {
            color: var(--st-text-secondary) !important;
        }

        .selectArrow { color: var(--st-text-secondary) !important; }

        /* Checkboxes / switches. */
        .checkboxOutline {
            border: 2px solid var(--st-border-strong) !important;
            border-radius: var(--st-radius-sm) !important;
        }

        .emby-checkbox:checked + .checkboxOutline,
        .checkboxIcon-checked {
            background-color: var(--st-pink) !important;
            border-color: var(--st-pink) !important;
        }

        /* Rounded-square glass tag (matches the web app's chips). */
        .mediaInfoItem {
            border: 1px solid var(--st-glass-border) !important;
            color: var(--st-text-secondary) !important;
            background-color: rgba(255, 255, 255, 0.04) !important;
            border-radius: var(--st-radius-sm) !important;
            padding: 6px 14px !important;
            font-weight: 600 !important;
        }

        /* DIALOGS, ACTION SHEETS & POPUPS (glass) */
        .dialog, .actionSheet, .popup, .dropdownMenu, .paperList, .menu,
        .MuiMenu-paper, .MuiDialog-paper, .MuiPopover-paper {
            background-color: var(--st-glass-surface-strong) !important;
            background-image: var(--st-glass-image) !important;
            backdrop-filter: blur(26px) saturate(1.3) !important;
            -webkit-backdrop-filter: blur(26px) saturate(1.3) !important;
            border: 1px solid var(--st-glass-border) !important;
            border-radius: var(--st-radius-lg) !important;
            box-shadow: 0 16px 40px rgba(0, 0, 0, 0.5), 0 0 20px rgba(192, 132, 252, 0.1) !important;
            overflow: hidden !important;
        }

        .actionSheetMenuItem, .listItem-button, .actionSheetItemText {
            color: var(--st-text-primary) !important;
        }

        .actionSheetMenuItem:hover, .dropdownMenuItem:hover,
        .MuiMenuItem-root:hover, .MuiListItem-root:hover {
            background: var(--st-gradient-soft) !important;
            color: var(--st-text-primary) !important;
        }

        /* SECTION HEADINGS */
        .sectionTitle {
            font-weight: 800 !important;
            letter-spacing: -0.01em !important;
            background: var(--st-gradient-line) !important;
            -webkit-background-clip: text !important;
            background-clip: text !important;
            -webkit-text-fill-color: transparent !important;
            color: transparent !important;
        }

        .itemDetailPage .itemName, #itemDetailPage .itemName {
            font-size: 2.8em !important;
            font-weight: 700 !important;
            line-height: 1.2 !important;
        }

        /* SCROLL BUTTONS & SCROLLBARS */
        .emby-scrollbuttons-button {
            color: var(--st-text-secondary) !important;
            border-radius: var(--st-radius-full, 9999px) !important;
        }

        .emby-scrollbuttons-button:hover {
            color: var(--st-text-primary) !important;
            background: var(--st-gradient-soft) !important;
        }

        *:hover {
            scrollbar-color: var(--st-border-strong) transparent;
        }
        """;
}
