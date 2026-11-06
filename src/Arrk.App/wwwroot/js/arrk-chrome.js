// Keeps app chrome in sync with the persistent music mini-player.
//
// The mobile control cluster floats at the bottom-right, which is exactly where the mini
// player's controls live. Rather than guessing its height, we publish the player's measured
// height as `--st-player-height` on :root; CSS then lifts the cluster (and page padding)
// above it. A light poll (plus resize) is used because Blazor can replace the player node
// during hydration, which would invalidate an element-scoped observer.
window.arrkChrome = (() => {
    const apply = () => {
        const player = document.getElementById('st-music-player');
        const visible = !!player && getComputedStyle(player).display !== 'none';
        const height = visible ? Math.ceil(player.getBoundingClientRect().height) : 0;
        document.documentElement.style.setProperty('--st-player-height', `${height}px`);
    };

    const start = () => {
        apply();
        window.addEventListener('resize', apply);
        // Blazor toggles the player's `visible` class and re-renders it; poll lightly so the
        // offset always tracks the real height without a fragile element observer.
        setInterval(apply, 400);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }

    return { sync: apply };
})();
