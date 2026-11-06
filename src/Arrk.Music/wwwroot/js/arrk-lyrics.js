// Live lyrics renderer for the Arrk now-playing view. Apple-Music style: the active line is
// bright, its neighbours are dimmed, and the list glides so the active line stays centred.
//
// The list is a normal scroll container, so the user can read ahead with the wheel, a drag or a
// scrollbar and click any line to seek. While they are scrolling the auto-centring stands down, and
// it resumes once they have stopped.
//
// This runs entirely in JS off the <audio> element's clock. Blazor hands it a lyrics model once per
// track; it must never re-render per frame (that would saturate the SignalR circuit), so the
// per-frame work lives here, on requestAnimationFrame, and only while the view is open and playing.
(function () {
    const state = {
        scroller: null,
        scrollHandler: null,
        inner: null,
        getTime: null,
        onSeek: null,
        model: null,
        nodes: [],
        active: -1,
        playing: false,
        visible: false,
        raf: 0,
        offsetMs: 0,
        fileOffsetMs: 0,
        // While the user is reading ahead we must not yank the list back to the active line.
        userScrollUntil: 0,
        // ...and our own scrolls must not look like the user's.
        suppressScrollUntil: 0
    };

    const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    function el(tag, cls, text) {
        const node = document.createElement(tag);
        if (cls) node.className = cls;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    // Render
    function build() {
        if (!state.scroller) return;
        state.scroller.innerHTML = '';
        state.nodes = [];
        state.active = -1;

        const inner = el('div', 'st-lrc-inner');
        state.inner = inner;
        if (reduceMotion) inner.style.transition = 'none';

        const lines = state.model ? state.model.lines || [] : [];
        lines.forEach(function (line, i) {
            const node = el('div', 'st-lrc-line', line.text || '');
            node.dataset.index = String(i);
            node.dataset.start = String(line.start || 0);
            node.addEventListener('click', function () {
                if (state.onSeek) state.onSeek((line.start || 0) / 1000);
            });
            inner.appendChild(node);
            state.nodes.push(node);
        });

        state.scroller.appendChild(inner);
        // A new song always starts at the top. The browser's scroll anchoring would otherwise keep the
        // previous song's offset when the list is replaced in the same frame.
        state.suppressScrollUntil = Date.now() + 400;
        state.scroller.scrollTop = 0;
    }

    function empty(message) {
        if (!state.scroller) return;
        state.scroller.innerHTML = '';
        const inner = el('div', 'st-lrc-inner');
        state.inner = inner;
        inner.appendChild(el('div', 'st-lrc-empty', message || 'No lyrics yet.'));
        state.scroller.appendChild(inner);
        state.suppressScrollUntil = Date.now() + 400;
        state.scroller.scrollTop = 0;
        syncPadding();
    }

    // Clock
    function nowMs() {
        return (state.getTime ? state.getTime() : 0) * 1000 + state.fileOffsetMs + state.offsetMs;
    }

    function activeIndexFor(t) {
        const lines = state.model ? state.model.lines || [] : [];
        let index = -1;
        for (let i = 0; i < lines.length; i++) {
            if ((lines[i].start || 0) <= t) index = i;
            else break;
        }
        return index;
    }

    // The list needs half a viewport of padding top and bottom so the first and last lines can reach
    // the centre. A percentage would resolve against the pane's *width*, so measure it instead.
    // Unsynced lyrics never follow the song, so they read naturally from the top instead.
    function syncPadding() {
        if (!state.inner || !state.scroller) return;
        const pad = state.model && state.model.synced
            ? Math.round(state.scroller.clientHeight / 2) + 'px'
            : '10px';
        state.inner.style.paddingTop = pad;
        state.inner.style.paddingBottom = pad;
    }

    function center(index) {
        const node = state.nodes[index];
        if (!node || !state.scroller) return;
        const target = node.offsetTop + (node.offsetHeight / 2) - (state.scroller.clientHeight / 2);
        state.suppressScrollUntil = Date.now() + 700;
        state.scroller.scrollTop = Math.max(0, target);
    }

    // A scroll we did not initiate means the user is reading ahead: leave the list where they put it
    // for a while, then let it follow the song again.
    function onScroll() {
        if (Date.now() < state.suppressScrollUntil) return;
        state.userScrollUntil = Date.now() + 6000;
    }

    function update() {
        if (!state.model || !state.model.synced) return;
        const index = activeIndexFor(nowMs());
        if (index === state.active) return;

        if (state.active >= 0 && state.nodes[state.active]) state.nodes[state.active].classList.remove('is-active');
        state.active = index;
        if (index >= 0 && state.nodes[index]) {
            state.nodes[index].classList.add('is-active');
            if (Date.now() >= state.userScrollUntil) center(index);
        }
    }

    function loop() {
        state.raf = 0;
        update();
        if (state.playing && state.visible) state.raf = requestAnimationFrame(loop);
    }

    function startLoop() {
        if (state.raf || !state.visible || !state.playing || !state.model || !state.model.synced) return;
        state.raf = requestAnimationFrame(loop);
    }

    function stopLoop() {
        if (!state.raf) return;
        cancelAnimationFrame(state.raf);
        state.raf = 0;
    }

    window.addEventListener('resize', function () {
        syncPadding();
        if (state.active >= 0) center(state.active);
    });

    window.arrkLyrics = {
        /** Wire the engine to its scroller and the audio clock. */
        attach: function (opts) {
            // Re-attaching must not leave the previous scroller's listener behind.
            if (state.scroller && state.scrollHandler) state.scroller.removeEventListener('scroll', state.scrollHandler);
            state.scroller = opts.scroller;
            state.getTime = opts.getTime;
            state.onSeek = opts.onSeek;
            state.offsetMs = opts.offsetMs || 0;
            state.scrollHandler = onScroll;
            if (state.scroller) state.scroller.addEventListener('scroll', onScroll, { passive: true });
        },

        /**
         * Unhook the engine from its scroller so it can be moved to another surface (the bar pane and
         * the full-screen view each have their own). The model is kept, so the next attach can re-draw
         * without a second fetch.
         */
        detach: function () {
            stopLoop();
            state.visible = false;
            if (state.scroller && state.scrollHandler) state.scroller.removeEventListener('scroll', state.scrollHandler);
            state.scroller = null;
            state.scrollHandler = null;
            state.inner = null;
            state.nodes = [];
            state.active = -1;
        },

        /** Show a lyrics model ({ synced, offset, lines:[{start,text}] }). */
        set: function (model) {
            state.model = model || null;
            state.fileOffsetMs = model && model.offset ? model.offset : 0;
            build();
            syncPadding();
            state.active = -1;
            state.userScrollUntil = 0;   // a new song always starts centred
            update();
            if (state.model && state.model.synced && state.active >= 0) center(state.active);
            startLoop();
        },

        /** Show a placeholder instead of lyrics. */
        clear: function (message) {
            state.model = null;
            stopLoop();
            empty(message);
        },

        setPlaying: function (playing) {
            state.playing = !!playing;
            if (state.playing) startLoop();
            else stopLoop();
        },

        setVisible: function (visible) {
            state.visible = !!visible;
            if (!state.visible) {
                stopLoop();
                return;
            }
            // The pane has no size until it is visible, so measure and re-sync now.
            syncPadding();
            state.active = -1;
            state.userScrollUntil = 0;
            update();
            if (state.model && state.model.synced && state.active >= 0) center(state.active);
            startLoop();
        },

        /** Force a re-sync after a seek (the clock jumped, so the active line may have changed a lot). */
        resync: function () {
            state.active = -1;
            state.userScrollUntil = 0;   // a seek is a deliberate jump: follow it
            update();
        },

        setOffset: function (ms) {
            state.offsetMs = ms || 0;
            state.active = -1;
            update();
        },

        getOffset: function () {
            return state.offsetMs;
        }
    };
})();
