window.arrkAutoScroll = (() => {
    const SPEED = 20; // px per second
    const roots = new Set();

    function measure(root) {
        const inner = root.querySelector(':scope > .st-autoscroll-inner');
        if (!inner) return;

        root.classList.remove('is-overflowing');
        root.style.removeProperty('--st-autoscroll-distance');
        root.style.removeProperty('--st-autoscroll-duration');

        const distance = Math.ceil(inner.scrollWidth - root.clientWidth);
        if (distance > 4) {
            root.style.setProperty('--st-autoscroll-distance', distance + 'px');
            root.style.setProperty('--st-autoscroll-duration', (distance / SPEED) + 's');
            root.classList.add('is-overflowing');
        }
    }

    function init(root) {
        if (!root) return;
        roots.add(root);
        measure(root);
    }

    window.addEventListener('resize', () => roots.forEach(measure));

    return { init };
})();
