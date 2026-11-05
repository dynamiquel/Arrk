// Swipe-to-dismiss for mobile dialog "cards".
//
// On phones/tablets MudBlazor dialogs are presented as cards with a grabber pill. Dragging
// from the top band of a dialog follows the finger, and releasing past a threshold slides the
// card away and dismisses it (via the dialog overlay, which honours BackdropClick). Releasing
// below the threshold springs the card back into place.
//
// Delegated from `document`, so it works for dynamically created dialogs with no per-component
// JS interop. Taps and drags that start on an interactive control are ignored.
window.stackarrSheet = (() => {
    const isMobile = () => window.matchMedia('(max-width: 959.98px)').matches;
    const TOP_BAND = 64;   // px from the dialog top where a swipe may start

    let drag = null;

    const interactive = (el) =>
        !!el.closest('button, a, input, textarea, select, [role="button"], .mud-button-root, .mud-nav-link, .mud-input, .mud-chip');

    const overlayFor = (dialog) => dialog.parentElement?.querySelector('.mud-overlay') ?? null;

    const onDown = (e) => {
        if (drag || !isMobile() || (e.button !== undefined && e.button !== 0)) return;
        const dialog = e.target.closest('.mud-dialog');
        if (!dialog || interactive(e.target)) return;
        const rect = dialog.getBoundingClientRect();
        if (e.clientY - rect.top > TOP_BAND) return;

        drag = { dialog, overlay: overlayFor(dialog), startY: e.clientY, dy: 0, height: rect.height || 400 };
        dialog.style.animation = 'none';
        dialog.style.transition = 'none';
        dialog.style.willChange = 'transform';
        dialog.style.touchAction = 'none';
        dialog.style.cursor = 'grabbing';
    };

    const onMove = (e) => {
        if (!drag) return;
        const dy = Math.max(0, e.clientY - drag.startY);
        drag.dy = dy;
        drag.dialog.style.transform = `translateY(${dy}px)`;
        if (drag.overlay) drag.overlay.style.opacity = String(Math.max(0, 1 - dy / drag.height));
        if (e.cancelable) e.preventDefault();
    };

    const finish = () => {
        if (!drag) return;
        const { dialog, overlay, dy, height } = drag;
        drag = null;

        const threshold = Math.min(140, height * 0.3);
        const dismiss = dy > threshold;
        dialog.style.transition = 'transform .22s cubic-bezier(.2,.85,.3,1)';
        if (overlay) overlay.style.transition = 'opacity .22s ease';
        dialog.style.cursor = '';

        if (dismiss) {
            dialog.style.transform = `translateY(${height + 60}px)`;
            if (overlay) overlay.style.opacity = '0';
            setTimeout(() => {
                // Close the top-most dialog by clicking its backdrop (respects BackdropClick).
                const top = document.querySelector('.mud-dialog-container:last-of-type .mud-overlay');
                (top || overlay)?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            }, 190);
            return;
        }

        dialog.style.transform = 'translateY(0)';
        if (overlay) overlay.style.opacity = '';
        setTimeout(() => reset(dialog), 260);
    };

    const reset = (dialog) => {
        dialog.style.transform = '';
        dialog.style.transition = '';
        dialog.style.animation = '';
        dialog.style.willChange = '';
        dialog.style.touchAction = '';
    };

    document.addEventListener('pointerdown', onDown, { passive: true });
    document.addEventListener('pointermove', onMove, { passive: false });
    document.addEventListener('pointerup', finish);
    document.addEventListener('pointercancel', finish);

    return { isMobile };
})();
