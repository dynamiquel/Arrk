// In-site music player for Arrk. Streams audio through Arrk's /api/music/player proxy (so
// the browser never holds Navidrome credentials), keeps a queue with shuffle/repeat, a volume
// slider, and reports scrobbles. Clicking the title/art/artist notifies Blazor so it can open the
// album or artist popup. It also drives the OS media session (lock screen / Control Center) via the
// Media Session API so a phone shows the song, artist, artwork and track controls.
//
// One <audio> element drives two surfaces: the persistent mini bar (#st-music-player) and the
// full-screen now-playing view (#st-full-player). They are two faces of the same playback, so the
// full view is pure UI: it never starts, stops or re-negotiates anything of its own.
(function () {
    const state = { audio: null, queue: [], index: -1, ready: false, lastScrobble: 0, shuffle: false, loop: 'off', dotNet: null, streamToken: '', streamTokenExpiry: 0, nowPlaying: false, lyricsFor: '', lyricsModel: null, lyricsOffsetMs: 0, fullOpen: false, fullLyrics: false, fullLyricsManual: false, renderedSongId: null, airplayAvailable: true, airplayActive: false, progressRaf: 0, lastPositionState: 0, posPrev: null, posCur: null };
    const mediaSession = ('mediaSession' in navigator) ? navigator.mediaSession : null;
    const LYRICS_OFFSET_KEY = 'st_lyrics_offset_ms';
    const LYRICS_OFFSET_STEP = 250;

    function el(id) { return document.getElementById(id); }

    function ensureAudio() {
        if (state.audio) return state.audio;
        const audio = new Audio();
        audio.preload = 'metadata';
        // Opt in to AirPlay (WebKit only). The receiver fetches the signed stream URL itself.
        audio.setAttribute('x-webkit-airplay', 'allow');
        state.audio = audio;

        audio.addEventListener('timeupdate', render);
        audio.addEventListener('durationchange', render);
        audio.addEventListener('loadedmetadata', render);
        audio.addEventListener('play', () => setPlaying(true));
        audio.addEventListener('pause', () => setPlaying(false));
        // Re-assert the media-session handlers while the session is actually active: on iOS the
        // handlers registered before playback often don't take effect, so the lock screen falls
        // back to the ±seek buttons. Setting them on 'playing' is what makes prev/next appear.
        audio.addEventListener('playing', wireMediaSession);
        audio.addEventListener('ended', onEnded);
        audio.addEventListener('error', () => setStatus('Playback error'));
        // The lyrics clock jumps on a seek, so re-sync the highlight instead of animating through
        // every line the seek passed.
        audio.addEventListener('seeked', () => { if (window.arrkLyrics) window.arrkLyrics.resync(); });
        return audio;
    }

    function current() {
        return state.index >= 0 && state.index < state.queue.length ? state.queue[state.index] : null;
    }

    // Same artwork source as the on-page mini-player: prefer the ALBUM art (matching the song
    // pages) because Navidrome's per-song cover is often just a generic disc icon for untagged
    // files. Used both for the <img> and for the OS media-session artwork.
    function artUrl(song) {
        if (!song) return '';
        const cover = (song.artist || song.album)
            ? '/api/music/art/album?artist=' + encodeURIComponent(song.artist || '') +
              '&title=' + encodeURIComponent(song.album || '')
            : (song.cover || '');
        return cover || song.cover || '';
    }

    function absoluteUrl(url) {
        if (!url) return '';
        try { return new URL(url, window.location.href).href; } catch (e) { return url; }
    }

    // Signed stream URLs (AirPlay)
    // AirPlay hands the media URL to the receiver (Apple TV / HomePod), which fetches it with none
    // of the browser's cookies. The stream proxy is therefore token-authenticated: fetch a
    // short-lived token once and append it to every track URL so the receiver can authenticate.
    async function ensureStreamToken() {
        const now = Date.now();
        if (state.streamToken && state.streamTokenExpiry - now > 60000) return state.streamToken;
        try {
            const res = await fetch('/api/music/player/token', { credentials: 'same-origin' });
            if (!res.ok) return state.streamToken || '';
            const data = await res.json();
            state.streamToken = data.token || '';
            state.streamTokenExpiry = data.expiresAt ? Date.parse(data.expiresAt) : now + 300000;
        } catch (e) { /* keep the previous token if any */ }
        return state.streamToken || '';
    }

    async function streamUrl(song) {
        const base = song.stream || ('/api/music/player/stream/' + encodeURIComponent(song.id));
        const token = await ensureStreamToken();
        if (!token) return base;
        return base + (base.indexOf('?') >= 0 ? '&' : '?') + 'st=' + encodeURIComponent(token);
    }

    function format(seconds) {
        if (!isFinite(seconds) || seconds < 0) seconds = 0;
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        return m + ':' + (s < 10 ? '0' + s : s);
    }

    function setPlaying(playing) {
        const bar = el('st-music-player');
        if (bar) bar.classList.toggle('is-playing', playing);
        const full = el('st-full-player');
        if (full) full.classList.toggle('is-playing', playing);
        // The seek fills are driven per frame while playing (see startProgressLoop); when we stop,
        // paint once more so the bar lands exactly on the paused position.
        if (playing) startProgressLoop();
        else { stopProgressLoop(); if (state.audio) renderProgress(state.audio); }
        if (window.arrkLyrics) window.arrkLyrics.setPlaying(playing);
        if (mediaSession) {
            try { mediaSession.playbackState = playing ? 'playing' : 'paused'; } catch (e) { /* enhancement */ }
        }
    }

    function setStatus(text) {
        const node = el('st-music-status');
        if (node) node.textContent = text || '';
    }

    // Called on every tick, so it only touches the clock. The metadata is repainted by renderSong
    // when the track actually changes, re-assigning an <img> src per frame would churn the artwork
    // (and the full view's blurred backdrop) for no reason.
    function render() {
        const audio = state.audio;
        const song = current();
        const bar = el('st-music-player');
        if (!song || !audio) return;
        if (bar) bar.classList.add('visible');

        if (state.renderedSongId !== song.id) {
            state.renderedSongId = song.id;
            renderSong(song);
        }
        renderProgress(audio);
    }

    // Title, artist, artwork and backdrop for the track, painted on both surfaces at once.
    function renderSong(song) {
        const title = el('st-music-title');
        if (title) title.textContent = song.title || '';
        const artist = el('st-music-artist');
        if (artist) artist.textContent = song.artist || '';
        const art = el('st-music-art');
        if (art) applyArt(art, song);

        const fpTitle = el('st-fp-title');
        if (fpTitle) fpTitle.textContent = song.title || '';
        const fpArtist = el('st-fp-artist');
        if (fpArtist) fpArtist.textContent = song.artist || '';
        const fpArt = el('st-fp-art');
        if (fpArt) applyArt(fpArt, song);
        const backdrop = el('st-fp-backdrop');
        if (backdrop) {
            const url = artUrl(song);
            backdrop.style.backgroundImage = url ? 'url("' + url.replace(/"/g, '%22') + '")' : 'none';
        }

        // A new track may have lyrics (or none): re-apply the full view's art | lyrics alignment.
        syncFullLyrics();
    }

    // The seek bars are painted every animation frame while playing rather than on the ~4Hz
    // `timeupdate`, so they glide instead of stepping. Reading the element's own clock each frame is
    // exact, no easing, no extrapolation, and the work is one style write per surface.
    function startProgressLoop() {
        if (state.progressRaf || !current()) return;
        state.posPrev = null;
        state.posCur = null;
        state.progressRaf = requestAnimationFrame(progressFrame);
    }

    function progressFrame() {
        state.progressRaf = 0;
        const audio = state.audio;
        if (!audio || audio.paused || audio.ended || !current()) return;
        renderProgress(audio);
        state.progressRaf = requestAnimationFrame(progressFrame);
    }

    function stopProgressLoop() {
        if (!state.progressRaf) return;
        cancelAnimationFrame(state.progressRaf);
        state.progressRaf = 0;
    }

    // Only touch the DOM when the value actually changed (the time labels change once a second).
    function setText(node, text) {
        if (node && node.textContent !== text) node.textContent = text;
    }

    // `currentTime` advances in coarse pipeline steps (often only ~60Hz), so on a fast display several
    // frames would read the same value and then jump, stutter. Render one step behind, interpolated
    // between the last two samples, so the bar moves on every frame and never runs ahead of the audio.
    // Samples more than 150ms apart (or a seek) are left alone.
    function smoothPosition(real) {
        const now = performance.now();
        if (state.posCur && Math.abs(real - state.posCur.v) > 0.3) {
            state.posPrev = null;   // a seek: start again, do not glide across it
            state.posCur = null;
        }
        if (!state.posCur || state.posCur.v !== real) {
            state.posPrev = state.posCur;
            state.posCur = { v: real, t: now };
        }
        const prev = state.posPrev;
        const cur = state.posCur;
        if (!prev || !cur) return real;
        const span = cur.t - prev.t;
        if (span <= 0 || span > 150) return real;
        const alpha = Math.min(1, Math.max(0, (now - cur.t) / span));
        return prev.v + (cur.v - prev.v) * alpha;
    }

    function renderProgress(audio) {
        const dur = isFinite(audio.duration) ? audio.duration : 0;
        const real = audio.currentTime || 0;
        const playing = !audio.paused && !audio.ended;
        const pos = playing ? smoothPosition(real) : real;
        const pct = dur > 0 ? Math.min(100, pos / dur * 100) : 0;

        const fill = el('st-music-fill');
        if (fill) fill.style.width = pct + '%';
        setText(el('st-music-time'), format(real) + ' / ' + format(dur));

        const fpFill = el('st-fp-fill');
        if (fpFill) fpFill.style.width = pct + '%';
        setText(el('st-fp-elapsed'), format(real));
        setText(el('st-fp-duration'), format(dur));

        // The OS position state only needs a few updates a second, not one per frame.
        const now = Date.now();
        if (now - state.lastPositionState >= 250) {
            state.lastPositionState = now;
            updatePositionState();
        }
    }

    // Shared artwork resolution: prefer the ALBUM art (matching the song pages) because Navidrome's
    // per-song cover is often a generic disc icon for untagged files, and fall back to it on error.
    function applyArt(img, song) {
        const primary = artUrl(song);
        const fallback = song.cover || '';
        img.onerror = () => {
            if (fallback && img.dataset.fb !== '1') { img.dataset.fb = '1'; img.src = fallback; }
            else { img.style.display = 'none'; }
        };
        img.dataset.fb = '';
        if (primary) { img.src = primary; img.style.display = 'block'; }
        else if (fallback) { img.src = fallback; img.style.display = 'block'; }
        else { img.style.display = 'none'; }
    }

    // Lyrics
    // The lyrics are fetched once per track, on change, so the bar's lyrics button can appear only
    // when there is something to show. The model is cached for that track and handed to the renderer
    // when the card opens, no second request.
    async function syncLyrics(song) {
        if (!song || !song.id) {
            state.lyricsFor = '';
            state.lyricsModel = null;
            showLyricsButton(false);
            showLyricsKind(true);
            applyLyrics();
            syncFullLyrics();
            return;
        }

        if (state.lyricsFor === song.id) {
            showLyricsButton(!!state.lyricsModel);
            showLyricsKind(!state.lyricsModel || !!state.lyricsModel.synced);
            applyLyrics();
            syncFullLyrics();
            return;
        }

        const requested = song.id;
        state.lyricsFor = requested;
        state.lyricsModel = null;
        showLyricsButton(false);
        showLyricsKind(true);

        try {
            const res = await fetch('/api/music/player/lyrics/' + encodeURIComponent(song.id), { credentials: 'same-origin' });
            if (state.lyricsFor !== requested) return;
            const data = res.ok ? await res.json() : null;
            const main = data ? data.main : null;
            state.lyricsModel = (main && main.lines && main.lines.length > 0)
                ? { synced: !!main.synced, offset: main.offset || 0, lines: main.lines }
                : null;
        } catch (e) {
            if (state.lyricsFor !== requested) return;
            state.lyricsModel = null;
        }

        if (state.lyricsFor !== requested) return;
        showLyricsButton(!!state.lyricsModel);
        showLyricsKind(!state.lyricsModel || !!state.lyricsModel.synced);
        applyLyrics();
        syncFullLyrics();
    }

    function showLyricsButton(visible) {
        const button = el('st-music-lyrics');
        if (button) button.style.display = visible ? '' : 'none';
        const fpButton = el('st-fp-lyrics');
        if (fpButton) fpButton.style.display = visible ? '' : 'none';
    }

    // Plain lyrics carry no timings: the list will not follow the song, so say so rather than letting
    // the user wonder why nothing moves. The offset nudge is meaningless without timings, so it gives
    // way to the hint.
    function showLyricsKind(synced) {
        markLyricsKind('st-music-lyrics', 'st-lyrics-static', 'st-lyrics-earlier', 'st-lyrics-later', 'st-lyrics-offset', synced);
        markLyricsKind('st-fp-lyrics', 'st-fp-lyrics-static', 'st-fp-lyrics-earlier', 'st-fp-lyrics-later', 'st-fp-lyrics-offset', synced);
    }

    function markLyricsKind(buttonId, hintId, earlierId, laterId, valueId, synced) {
        const button = el(buttonId);
        if (button) {
            button.classList.toggle('is-static', !synced);
            button.title = synced ? 'Lyrics' : 'Lyrics (not synced)';
            button.setAttribute('aria-label', button.title);
        }
        const hint = el(hintId);
        if (hint) hint.style.display = synced ? 'none' : '';
        const earlier = el(earlierId);
        const later = el(laterId);
        const value = el(valueId);
        if (earlier) earlier.style.display = synced ? '' : 'none';
        if (later) later.style.display = synced ? '' : 'none';
        if (value) value.style.display = synced ? '' : 'none';
    }

    function applyLyrics() {
        if (!window.arrkLyrics) return;
        // Applied even while the pane is closed, so the engine never keeps the previous track's list
        // (and its scroll position) waiting to be replaced when the pane opens.
        if (state.lyricsModel) {
            window.arrkLyrics.set(state.lyricsModel);
            window.arrkLyrics.setOffset(state.lyricsOffsetMs);
        } else {
            window.arrkLyrics.clear(state.lyricsFor ? "No lyrics for this song yet." : "Lyrics aren't available.");
        }
    }

    // Expanding the bar
    // Expanding does not swap in another player: it adds the lyrics pane above the bar's existing
    // row, so the art, title, transport, volume and options all stay exactly where they were.
    function expandLyrics() {
        const bar = el('st-music-player');
        if (state.fullOpen) return;   // the full view owns the lyrics engine while it is up
        if (!isWide()) return;        // phones read lyrics in the full view, never in the bar
        if (!bar || !current() || !state.lyricsModel) return;
        state.nowPlaying = true;
        bar.classList.add('is-expanded');
        const button = el('st-music-lyrics');
        if (button) button.classList.add('active');
        if (window.arrkLyrics) {
            window.arrkLyrics.setVisible(true);
            window.arrkLyrics.setPlaying(!ensureAudio().paused);
        }
        updateOffsetLabel();
        applyLyrics();
    }

    function collapseLyrics() {
        const bar = el('st-music-player');
        state.nowPlaying = false;
        if (bar) bar.classList.remove('is-expanded');
        const button = el('st-music-lyrics');
        if (button) button.classList.remove('active');
        if (window.arrkLyrics) window.arrkLyrics.setVisible(false);
    }

    function toggleLyrics() {
        if (state.nowPlaying) collapseLyrics();
        else expandLyrics();
    }

    /** A new track invalidates the cached lyrics and, if the pane is open, refreshes it. */
    function afterTrackChanged(song) {
        syncLyrics(song);
    }

    function applyLyricsOffset(delta) {
        state.lyricsOffsetMs = Math.max(-10000, Math.min(10000, state.lyricsOffsetMs + delta));
        try { localStorage.setItem(LYRICS_OFFSET_KEY, String(state.lyricsOffsetMs)); } catch (e) { /* private mode */ }
        if (window.arrkLyrics) window.arrkLyrics.setOffset(state.lyricsOffsetMs);
        updateOffsetLabel();
    }

    function updateOffsetLabel() {
        const text = (state.lyricsOffsetMs > 0 ? '+' : '') + (state.lyricsOffsetMs / 1000).toFixed(1) + 's';
        const label = el('st-lyrics-offset');
        if (label) label.textContent = text;
        const fpLabel = el('st-fp-lyrics-offset');
        if (fpLabel) fpLabel.textContent = text;
    }

    function setActiveButtons() {
        const sh = el('st-music-shuffle');
        if (sh) sh.classList.toggle('active', state.shuffle);
        const fsh = el('st-fp-shuffle');
        if (fsh) fsh.classList.toggle('active', state.shuffle);

        // The repeat-one icon swap is driven by a class on each player's root.
        const bar = el('st-music-player');
        if (bar) bar.classList.toggle('loop-one', state.loop === 'one');
        const full = el('st-full-player');
        if (full) full.classList.toggle('loop-one', state.loop === 'one');
        const lp = el('st-music-loop');
        if (lp) lp.classList.toggle('active', state.loop !== 'off');
        const flp = el('st-fp-loop');
        if (flp) flp.classList.toggle('active', state.loop !== 'off');
    }

    // Full-screen now-playing view
    // The bar and this view are two faces of one playback. Desktop puts the art (and its metadata)
    // in the centre, and turns into an art | lyrics split when the song has lyrics; phones keep the
    // art and slide the live lyrics up over it when the Lyrics pill is pressed.
    const FULL_WIDE_QUERY = '(min-width: 960px)';

    function isWide() {
        return !!(window.matchMedia && window.matchMedia(FULL_WIDE_QUERY).matches);
    }

    function fullHasLyrics() {
        return !!state.lyricsModel;
    }

    /** Apply the art | lyrics alignment for the current track and viewport (unless the user chose). */
    function syncFullLyrics() {
        if (!state.fullOpen) return;
        if (!fullHasLyrics()) { setFullLyrics(false); return; }
        if (!state.fullLyricsManual) setFullLyrics(isWide());
    }

    function setFullLyrics(on) {
        state.fullLyrics = !!on && fullHasLyrics();
        const full = el('st-full-player');
        if (full) full.classList.toggle('is-lyrics', state.fullLyrics);
        const button = el('st-fp-lyrics');
        if (button) button.classList.toggle('active', state.fullLyrics);
        if (window.arrkLyrics) {
            window.arrkLyrics.setPlaying(!ensureAudio().paused);
            window.arrkLyrics.setVisible(state.fullLyrics);
        }
    }

    function toggleFullLyrics() {
        if (!fullHasLyrics()) return;
        state.fullLyricsManual = true;
        setFullLyrics(!state.fullLyrics);
    }

    function openFull() {
        const full = el('st-full-player');
        if (!full || !current()) return;
        if (state.nowPlaying) collapseLyrics();   // the inline pane and the full view are the same idea
        state.fullOpen = true;
        state.fullLyricsManual = false;
        state.renderedSongId = null;              // force the metadata/art to (re)paint
        full.classList.add('visible');
        full.setAttribute('aria-hidden', 'false');
        document.documentElement.classList.add('st-full-player-open');
        render();                                 // paints the song, progress and alignment
        moveLyricsToFull();
        syncFullLyrics();
    }

    function closeFull() {
        const full = el('st-full-player');
        if (!state.fullOpen || !full) return;
        state.fullOpen = false;
        state.fullLyricsManual = false;
        full.classList.remove('visible');
        full.classList.remove('is-lyrics');
        full.setAttribute('aria-hidden', 'true');
        document.documentElement.classList.remove('st-full-player-open');
        state.fullLyrics = false;
        const button = el('st-fp-lyrics');
        if (button) button.classList.remove('active');
        moveLyricsToBar();
    }

    // The full view lives in the same component as the bar, so a layout change tears both down while
    // this module's state lingers. A fresh mount must never inherit the old overlay (or the
    // scroll-lock class it put on <html>).
    function resetOverlayUi() {
        const full = el('st-full-player');
        if (full) {
            full.classList.remove('visible');
            full.classList.remove('is-lyrics');
            full.setAttribute('aria-hidden', 'true');
        }
        document.documentElement.classList.remove('st-full-player-open');
        state.fullOpen = false;
        state.fullLyrics = false;
        state.fullLyricsManual = false;
        const button = el('st-fp-lyrics');
        if (button) button.classList.remove('active');
    }

    // Silence and clear the player. The video player calls this when it takes over the tab: the two
    // players share the tab's audio element, and the music component is not even mounted under the
    // video player's layout, so nothing else would stop it.
    function stopPlayback() {
        const audio = ensureAudio();
        stopProgressLoop();
        try { audio.pause(); } catch (e) { /* ignore */ }
        try { audio.removeAttribute('src'); audio.load(); } catch (e) { /* ignore */ }

        state.queue = [];
        state.index = -1;
        state.renderedSongId = null;
        state.lyricsFor = '';
        state.lyricsModel = null;
        state.nowPlaying = false;
        state.lastScrobble = 0;

        clearMediaSession();
        if (window.arrkLyrics) {
            window.arrkLyrics.setVisible(false);
            window.arrkLyrics.clear("Lyrics aren't available.");
        }

        const bar = el('st-music-player');
        if (bar) bar.classList.remove('visible', 'is-playing', 'is-expanded');
        const lyricsButton = el('st-music-lyrics');
        if (lyricsButton) { lyricsButton.classList.remove('active'); lyricsButton.style.display = 'none'; }
        const statusNode = el('st-music-status');
        if (statusNode) statusNode.textContent = '';
        resetOverlayUi();
        renderProgress(audio);
    }

    function playQueue(queue, index) {
        state.queue = Array.isArray(queue) ? queue : [];
        playIndex(index || 0);
    }

    async function playIndex(index) {
        if (index < 0 || index >= state.queue.length) return;
        state.index = index;
        const song = state.queue[index];
        const audio = ensureAudio();
        audio.src = await streamUrl(song);
        // Set metadata before starting playback so iOS has it when it builds the Now Playing UI.
        updateMetadata();
        audio.play().catch(() => { });
        setStatus('');
        render();
        afterTrackChanged(song);
        scrobble(song, false);
        saveQueue();
    }

    function nextIndex() {
        if (state.queue.length === 0) return -1;
        if (state.shuffle) return Math.floor(Math.random() * state.queue.length);
        return state.index + 1 < state.queue.length ? state.index + 1 : 0;
    }

    function onEnded() {
        const audio = ensureAudio();
        if (state.loop === 'one') { audio.currentTime = 0; audio.play().catch(() => { }); return; }
        if (state.loop === 'all' || state.shuffle || state.index + 1 < state.queue.length) {
            const n = nextIndex();
            if (n >= 0) playIndex(n);
        } else {
            setPlaying(false);
        }
    }

    function toggle() {
        const audio = ensureAudio();
        if (!current()) return;
        if (audio.paused) audio.play().catch(() => { });
        else audio.pause();
    }

    function next() {
        // A one-off queue (an explicitly-clicked preview, or a single song) has nowhere to advance;
        // don't wrap back onto itself.
        if (state.queue.length <= 1 && state.loop !== 'all' && !state.shuffle) return;
        const n = nextIndex();
        if (n >= 0) playIndex(n);
    }

    function prev() {
        const audio = ensureAudio();
        if (audio.currentTime > 3) { audio.currentTime = 0; return; }
        if (state.index > 0) playIndex(state.index - 1);
        else if (state.loop === 'all' && state.queue.length > 0) playIndex(state.queue.length - 1);
    }

    function seek(ratio) {
        const audio = ensureAudio();
        if (isFinite(audio.duration) && audio.duration > 0) {
            audio.currentTime = Math.max(0, Math.min(1, ratio)) * audio.duration;
            renderProgress(audio);   // immediate feedback, even while paused
        }
    }

    function toggleShuffle() { state.shuffle = !state.shuffle; setActiveButtons(); }
    function cycleLoop() { state.loop = state.loop === 'off' ? 'all' : state.loop === 'all' ? 'one' : 'off'; setActiveButtons(); }

    function notify(kind) {
        const song = current();
        if (!song || !state.dotNet) return;
        if (kind === 'artist') {
            if (song.artistDeezerId > 0) state.dotNet.invokeMethodAsync('OpenArtist', song.artistDeezerId).catch(() => { });
            else if (song.artist) state.dotNet.invokeMethodAsync('OpenArtistByName', song.artist).catch(() => { });
        } else {
            // Resolve by name, then by the song title (finds the release we actually hold, even when
            // the song's album name/id differs from our copy); Blazor falls back to Deezer.
            state.dotNet.invokeMethodAsync('OpenAlbumByName', song.artist || '', song.album || song.title || '', song.title || '').catch(() => { });
        }
    }

    function scrobble(song, submission) {
        if (!song || !song.id) return;
        fetch('/api/music/player/scrobble/' + encodeURIComponent(song.id) + '?submission=' + (submission ? 'true' : 'false'), {
            method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                title: song.title || '', artist: song.artist || '',
                albumArtist: song.albumArtist || song.artist || '',
                album: song.album || '', coverArtUrl: song.cover || ''
            })
        }).catch(() => { });
    }

    function saveQueue() {
        const ids = state.queue.map(s => s.id).filter(id => !!id);
        if (ids.length === 0 || !state.dotNet) return;
        const song = current();
        const audio = state.audio;
        const position = audio && Number.isFinite(audio.currentTime) ? Math.round(audio.currentTime * 1000) : 0;
        fetch('/api/music/player/queue', {
            method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ids, current: song ? song.id : '', position })
        }).catch(() => { });
    }

    // OS media session (lock screen / Control Center / Bluetooth controls)
    function updateMetadata() {
        if (!mediaSession || typeof MediaMetadata === 'undefined') return;
        const song = current();
        if (!song) return;
        const cover = absoluteUrl(artUrl(song));
        try {
            mediaSession.metadata = new MediaMetadata({
                title: song.title || '',
                artist: song.artist || '',
                album: song.album || '',
                artwork: cover ? [{ src: cover, sizes: '512x512' }] : []
            });
        } catch (e) { /* metadata is an enhancement */ }
    }

    function updatePositionState() {
        if (!mediaSession || typeof mediaSession.setPositionState !== 'function') return;
        const audio = state.audio;
        if (!audio || !isFinite(audio.duration) || audio.duration <= 0) return;
        try {
            mediaSession.setPositionState({
                duration: audio.duration,
                playbackRate: audio.playbackRate || 1,
                position: Math.min(Math.max(audio.currentTime || 0, 0), audio.duration)
            });
        } catch (e) { /* position state is an enhancement */ }
    }

    function wireMediaSession() {
        if (!mediaSession || typeof mediaSession.setActionHandler !== 'function') return;
        // iOS/iPadOS shows EITHER previous/next-track OR the ±seek buttons on the lock screen,
        // never both. We register the track handlers and then explicitly unset the ±seek pair
        // (setActionHandler(action, null)), even though we never set them, iOS keeps a default
        // seek action and only drops it when it is cleared, otherwise the next/previous buttons
        // never appear. See MediaSessionConventionsTests.
        const handlers = {
            play: () => { const a = ensureAudio(); if (current()) a.play().catch(() => { }); },
            pause: () => ensureAudio().pause(),
            previoustrack: prev,
            nexttrack: next,
            seekto: (details) => {
                const a = ensureAudio();
                if (details && typeof details.seekTime === 'number') a.currentTime = details.seekTime;
            },
            stop: () => { const a = ensureAudio(); a.pause(); a.currentTime = 0; }
        };
        Object.keys(handlers).forEach((action) => {
            try { mediaSession.setActionHandler(action, handlers[action]); } catch (e) { /* unsupported action */ }
        });
        // Force iOS to show the track buttons instead of the default ±seek buttons.
        try { mediaSession.setActionHandler('seekbackward', null); } catch (e) { /* ignore */ }
        try { mediaSession.setActionHandler('seekforward', null); } catch (e) { /* ignore */ }
    }

    function clearMediaSession() {
        if (!mediaSession) return;
        try { mediaSession.metadata = null; } catch (e) { /* ignore */ }
        try { mediaSession.playbackState = 'none'; } catch (e) { /* ignore */ }
    }

    // A pointer seek bar: click or drag anywhere on it to move the track.
    function wireSeekBar(node) {
        if (!node) return;
        let dragging = false;
        const seekFromEvent = (clientX) => {
            const rect = node.getBoundingClientRect();
            if (rect.width > 0) seek((clientX - rect.left) / rect.width);
        };
        node.addEventListener('pointerdown', (e) => {
            dragging = true;
            try { node.setPointerCapture(e.pointerId); } catch (x) { /* ignore */ }
            seekFromEvent(e.clientX);
        });
        node.addEventListener('pointermove', (e) => { if (dragging) seekFromEvent(e.clientX); });
        const end = (e) => {
            dragging = false;
            try { node.releasePointerCapture(e.pointerId); } catch (x) { /* ignore */ }
        };
        node.addEventListener('pointerup', end);
        node.addEventListener('pointercancel', end);
    }

    // Wiring
    // Bound by lifetime: the <audio> element and the global listeners live for the whole tab (the
    // component is remounted on every layout change, see the video player's layout), so only the
    // DOM is rebound each mount. `wireDom` guards with a marker on the bar so a stray second call
    // on the same DOM cannot double-bind.
    function wireDom() {
        const bar = el('st-music-player');
        if (!bar || bar.dataset.stWired === '1') return;
        bar.dataset.stWired = '1';

        // The bar's progress bar is a whole-bar click; the full view's is a drag bar.
        const progress = el('st-music-progress');
        if (progress) progress.addEventListener('click', (e) => {
            const rect = progress.getBoundingClientRect();
            if (rect.width > 0) seek((e.clientX - rect.left) / rect.width);
        });
        wireSeekBar(el('st-fp-progress'));

        const bind = (id, fn) => { const node = el(id); if (node) node.addEventListener('click', fn); };
        // Bar
        bind('st-music-toggle', toggle);
        bind('st-music-next', next);
        bind('st-music-prev', prev);
        bind('st-music-shuffle', toggleShuffle);
        bind('st-music-loop', cycleLoop);
        // Closing the bar is a full stop: pausing alone left the queue "current", so the next
        // render (a timeupdate, the video player taking over) would pop the bar back up.
        bind('st-music-close', stopPlayback);
        bind('st-music-title', () => notify('album'));
        bind('st-music-art', () => notify('album'));
        bind('st-music-artist', () => notify('artist'));
        bind('st-music-lyrics', toggleLyrics);
        bind('st-music-expand', openFull);
        bind('st-lyrics-earlier', () => applyLyricsOffset(-LYRICS_OFFSET_STEP));
        bind('st-lyrics-later', () => applyLyricsOffset(LYRICS_OFFSET_STEP));
        // Full view
        bind('st-fp-close', closeFull);
        bind('st-fp-collapse', closeFull);
        bind('st-fp-toggle', toggle);
        bind('st-fp-next', next);
        bind('st-fp-prev', prev);
        bind('st-fp-shuffle', toggleShuffle);
        bind('st-fp-loop', cycleLoop);
        bind('st-fp-lyrics', toggleFullLyrics);
        bind('st-fp-title', () => notify('album'));
        bind('st-fp-art', () => notify('album'));
        bind('st-fp-artist', () => notify('artist'));
        bind('st-fp-lyrics-earlier', () => applyLyricsOffset(-LYRICS_OFFSET_STEP));
        bind('st-fp-lyrics-later', () => applyLyricsOffset(LYRICS_OFFSET_STEP));

        // Volume: two sliders, one <audio>. Each write is mirrored back through volumechange.
        const vol = el('st-music-volume');
        if (vol) vol.addEventListener('input', () => setVolume(vol.value));
        const fpVol = el('st-fp-volume');
        if (fpVol) fpVol.addEventListener('input', () => setVolume(fpVol.value));

        wireAirPlayButtons();
    }

    // The <audio> element is created in JS and outlives the component, so its listeners (and the
    // media session) are bound once per tab.
    function wireAudio(audio) {
        audio.addEventListener('volumechange', syncVolumeSliders);
        audio.addEventListener('timeupdate', () => {
            const song = current();
            if (!song || audio.paused) return;
            if (Date.now() - state.lastScrobble > 30000) { state.lastScrobble = Date.now(); scrobble(song, true); saveQueue(); }
        });
        wireAirPlayAudio(audio);
        wireMediaSession();
    }

    function wireGlobal() {
        // Escape closes the full view first, then the bar's inline lyrics pane, never playback.
        document.addEventListener('keydown', (e) => {
            if (e.key !== 'Escape') return;
            if (state.fullOpen) { closeFull(); return; }
            if (state.nowPlaying) collapseLyrics();
        });

        // A viewport flip changes which layout the lyrics belong in.
        window.addEventListener('resize', () => {
            // The bar's inline pane is desktop-only; never leave it open on a phone-sized viewport.
            if (!isWide() && state.nowPlaying) collapseLyrics();
            if (!state.fullOpen) return;
            state.fullLyricsManual = false;
            syncFullLyrics();
        });
    }

    function setVolume(value) {
        const audio = ensureAudio();
        audio.volume = Number(value);
        audio.muted = Number(value) === 0;
    }

    // Mirror the <audio> volume (the single source of truth) onto both sliders.
    function syncVolumeSliders() {
        const audio = state.audio;
        if (!audio) return;
        const v = audio.muted ? 0 : audio.volume;
        const a = el('st-music-volume'); if (a) a.value = String(v);
        const b = el('st-fp-volume');
        if (b) {
            b.value = String(v);
            // Paints the full view's slider fill (the bar uses the native accent colour).
            b.style.setProperty('--st-fp-vol', (v * 100) + '%');
        }
    }

    // The lyrics engine draws the lines and owns the per-frame work; the player only supplies the
    // audio clock and a seek callback, so no lyrics state ever crosses the Blazor circuit. It renders
    // into exactly one scroller at a time, the bar pane or the full view, never both at once.
    function lyricsScrollerId() {
        return state.fullOpen ? 'st-fp-lyrics-scroll' : 'st-music-lyrics-scroll';
    }

    function wireLyricsEngine() {
        if (!window.arrkLyrics) return;
        window.arrkLyrics.attach({
            scroller: el(lyricsScrollerId()),
            getTime: () => (state.audio ? state.audio.currentTime || 0 : 0),
            onSeek: (seconds) => {
                if (state.audio && isFinite(state.audio.duration))
                    state.audio.currentTime = Math.max(0, Math.min(seconds, state.audio.duration));
            },
            offsetMs: state.lyricsOffsetMs
        });
    }

    function attachLyrics() {
        if (!window.arrkLyrics) return;
        try {
            const stored = parseInt(localStorage.getItem(LYRICS_OFFSET_KEY) || '0', 10);
            state.lyricsOffsetMs = isFinite(stored) ? stored : 0;
        } catch (e) { state.lyricsOffsetMs = 0; }

        wireLyricsEngine();
        updateOffsetLabel();
    }

    // The full view and the bar pane are never both on screen, so the engine is moved between them
    // rather than duplicated (one rAF loop, one model, no second request).
    function moveLyricsToFull() {
        if (!window.arrkLyrics) return;
        window.arrkLyrics.detach();
        wireLyricsEngine();
        window.arrkLyrics.setOffset(state.lyricsOffsetMs);
        updateOffsetLabel();
        applyLyrics();
        window.arrkLyrics.setPlaying(!ensureAudio().paused);
        window.arrkLyrics.setVisible(state.fullLyrics);
    }

    function moveLyricsToBar() {
        if (!window.arrkLyrics) return;
        window.arrkLyrics.detach();
        wireLyricsEngine();
        window.arrkLyrics.setOffset(state.lyricsOffsetMs);
        updateOffsetLabel();
        applyLyrics();
        window.arrkLyrics.setPlaying(!ensureAudio().paused);
        window.arrkLyrics.setVisible(state.nowPlaying);
    }

    // AirPlay is Safari/WebKit-only; custom controls hide Safari's built-in button, so expose our
    // own that opens the native route picker. The receiver then fetches the signed stream URL.
    function airplaySupported() {
        const audio = state.audio;
        return !!audio && typeof audio.webkitShowPlaybackTargetPicker === 'function';
    }

    function applyAirPlayState() {
        const supported = airplaySupported();
        ['st-music-airplay', 'st-fp-airplay'].forEach((id) => {
            const button = el(id);
            if (!button) return;
            button.style.display = supported && state.airplayAvailable ? '' : 'none';
            button.classList.toggle('active', !!state.airplayActive);
        });
    }

    // Bound once per DOM (the buttons are recreated on a remount).
    function wireAirPlayButtons() {
        if (!airplaySupported()) return;
        const audio = state.audio;
        ['st-music-airplay', 'st-fp-airplay'].forEach((id) => {
            const button = el(id);
            if (button) button.addEventListener('click', () => { try { audio.webkitShowPlaybackTargetPicker(); } catch (e) { /* ignore */ } });
        });
        applyAirPlayState();
    }

    // Bound once per tab (the availability/route state lives on the <audio> element).
    function wireAirPlayAudio(audio) {
        if (typeof audio.webkitShowPlaybackTargetPicker !== 'function') return;
        audio.addEventListener('webkitplaybacktargetavailabilitychanged', (event) => {
            state.airplayAvailable = !event || !event.availability || event.availability === 'available';
            applyAirPlayState();
        });
        audio.addEventListener('webkitcurrentplaybacktargetiswirelesschanged', () => {
            state.airplayActive = !!audio.webkitCurrentPlaybackTargetIsWireless;
            applyAirPlayState();
        });
    }

    // Called on every mount of the MusicPlayer component. The <audio> element and the global
    // listeners survive a remount, but the DOM does not: bind it fresh each time and drop any
    // overlay the previous DOM left behind.
    function init(dotNetRef) {
        state.dotNet = dotNetRef || null;
        const audio = ensureAudio();
        if (!state.ready) {
            state.ready = true;
            wireAudio(audio);
            wireGlobal();
        }
        resetOverlayUi();
        wireDom();
        setActiveButtons();
        attachLyrics();
        applyLyrics();
        updateOffsetLabel();
        syncVolumeSliders();
        if (current()) render();
    }

    window.arrkMusic = {
        init: init,
        play: playQueue,
        stop: stopPlayback,
        toggle: toggle,
        next: next,
        prev: prev,
        seek: seek,
        toggleLyrics: toggleLyrics,
        collapseLyrics: collapseLyrics,
        openFull: openFull,
        closeFull: closeFull,
        toggleFullLyrics: toggleFullLyrics
    };
})();

// Playlist icons (stored per-browser in localStorage, no server/DB storage).
(function () {
    window.arrkPlaylists = {
        _all: function () { try { return JSON.parse(localStorage.getItem('st_pl_icons') || '{}'); } catch (e) { return {}; } },
        getIcon: function (id) { return this._all()[id] || ''; },
        setIcon: function (id, icon) {
            const m = this._all();
            if (icon) { m[id] = icon; } else { delete m[id]; }
            try { localStorage.setItem('st_pl_icons', JSON.stringify(m)); } catch (e) { }
        }
    };
})();
