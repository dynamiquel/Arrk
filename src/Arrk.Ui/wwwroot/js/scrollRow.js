// Horizontal scroll helper used by the reusable <ScrollRow> component.
// The row's arrows are only shown while the track overflows, and each side disappears at its end.
window.arrkScrollRow = (() => {
    const update = (row, track) => {
        if (!row || !track) return;
        const maxScroll = track.scrollWidth - track.clientWidth;
        const overflow = maxScroll > 3;
        row.classList.toggle('st-scroll-row--overflow', overflow);
        row.classList.toggle('st-scroll-row--can-left', overflow && track.scrollLeft > 3);
        row.classList.toggle('st-scroll-row--can-right', overflow && maxScroll - track.scrollLeft > 3);
    };

    return {
        init: (row, track) => {
            if (!row || !track) return;
            if (!track._stScrollBound) {
                track._stScrollBound = true;
                track.addEventListener('scroll', () => update(row, track), { passive: true });
                if (typeof ResizeObserver !== 'undefined') {
                    const ro = new ResizeObserver(() => update(row, track));
                    ro.observe(track);
                    ro.observe(row);
                }
            }
            update(row, track);
            requestAnimationFrame(() => update(row, track));
        },
        scroll: (row, track, direction) => {
            if (!track) return;
            const amount = Math.max(Math.round(track.clientWidth * 0.85), 240);
            track.scrollBy({ left: direction * amount, behavior: 'smooth' });
            setTimeout(() => update(row, track), 320);
        }
    };
})();

// Overflow culling + smooth horizontal scroll helpers for <StTabs> (underline & segmented pills).
window.arrkTabs = (() => {
    const updateClasses = (wrap, track) => {
        if (!wrap || !track) return;
        const maxScroll = track.scrollWidth - track.clientWidth;
        const canLeft = track.scrollLeft > 3;
        const canRight = maxScroll - track.scrollLeft > 3;
        wrap.classList.toggle('st-tabs-wrap--overflow', maxScroll > 3);
        wrap.classList.toggle('st-tabs-wrap--can-left', canLeft);
        wrap.classList.toggle('st-tabs-wrap--can-right', canRight);
    };

    const centerActive = (wrap, track, smooth) => {
        if (!track) return;
        const active = track.querySelector('.st-tab.active');
        if (!active) return;
        const maxScroll = track.scrollWidth - track.clientWidth;
        if (maxScroll <= 3) {
            updateClasses(wrap, track);
            return;
        }
        const target = Math.max(0, Math.min(maxScroll, active.offsetLeft - (track.clientWidth - active.offsetWidth) / 2));
        track.scrollTo({ left: target, behavior: smooth ? 'smooth' : 'auto' });
        updateClasses(wrap, track);
    };

    return {
        init: (wrap, track) => {
            if (!wrap || !track) return;
            if (!track._stTabsBound) {
                track._stTabsBound = true;
                track.addEventListener('scroll', () => updateClasses(wrap, track), { passive: true });
                track.addEventListener('wheel', (e) => {
                    if (Math.abs(e.deltaY) > Math.abs(e.deltaX) && track.scrollWidth > track.clientWidth + 3) {
                        e.preventDefault();
                        track.scrollBy({ left: e.deltaY, behavior: 'auto' });
                    }
                }, { passive: false });
                if (typeof ResizeObserver !== 'undefined') {
                    const ro = new ResizeObserver(() => updateClasses(wrap, track));
                    ro.observe(track);
                    ro.observe(wrap);
                }
            }
            updateClasses(wrap, track);
            requestAnimationFrame(() => centerActive(wrap, track, false));
        },
        scroll: (wrap, track, direction) => {
            if (!track) return;
            const amount = Math.max(Math.round(track.clientWidth * 0.65), 150);
            track.scrollBy({ left: direction * amount, behavior: 'smooth' });
            setTimeout(() => updateClasses(wrap, track), 260);
        },
        focusActive: (wrap, track) => {
            if (!track) return;
            requestAnimationFrame(() => centerActive(wrap, track, true));
        }
    };
})();

// Lets a host ask layout-sensitive components (Monaco) to re-measure after a pane is shown.
window.arrkLayout = {
    resize: () => window.dispatchEvent(new Event('resize'))
};
