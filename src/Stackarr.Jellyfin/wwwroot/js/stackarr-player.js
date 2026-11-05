// In-site web player for Stackarr. It negotiates playback with Jellyfin (via Stackarr's API),
// then plays the returned HLS stream either natively (Safari) or through hls.js (Chromium/Firefox).
// The browser reports its real capabilities so Jellyfin can copy the video when possible and only
// transcode when the client genuinely cannot decode it. It also publishes Stackarr's own title and
// artwork to the OS media session (lock screen / Control Center), Jellyfin is used to stream only.
(function () {
    const state = {
        hls: null, video: null, sessionId: null, cleanup: [],
        itemId: null, meta: null, plan: null,
        audioIndex: null, subtitleIndex: null, burnedIn: false,
        switching: false, menu: null, subtitleSearchHandler: null,
        next: null, nextPromise: null, nextTimer: null,
        nextShown: false, nextCancelled: false,
        cueStyle: 'background', cueScale: 1, cueBackground: 68, cueOutline: 100
    };
    const mediaSession = ('mediaSession' in navigator) ? navigator.mediaSession : null;

    function probe() {
        const v = document.createElement('video');
        const ms = window.MediaSource;
        const canMs = (t) => !!(ms && ms.isTypeSupported && ms.isTypeSupported(t));
        const can = (t) => !!v.canPlayType && v.canPlayType(t) !== '';
        const hevc = [
            'video/mp4; codecs="hvc1.1.6.L120.B0"',
            'video/mp4; codecs="hvc1.2.4.L120.B0"',
            'video/mp4; codecs="hev1.1.6.L120.B0"'
        ];
        return {
            nativeHls: can('application/vnd.apple.mpegurl'),
            hevc: hevc.some((t) => canMs(t) || can(t)),
            av1: canMs('video/mp4; codecs="av01.0.05M.08"') || can('video/mp4; codecs="av01.0.05M.08"'),
            vp9: canMs('video/webm; codecs="vp9"') || canMs('video/mp4; codecs="vp09.00.10.08"') || can('video/webm; codecs="vp9"'),
            flac: canMs('audio/mp4; codecs="flac"') || can('audio/ogg; codecs="flac"'),
            opus: canMs('audio/webm; codecs="opus"') || can('audio/ogg; codecs="opus"'),
            ac3: canMs('audio/mp4; codecs="ac-3"') || can('audio/mp4; codecs="ac-3"'),
            eac3: canMs('audio/mp4; codecs="ec-3"') || can('audio/mp4; codecs="ec-3"'),
            maxStreamingBitrate: 120000000
        };
    }

    function ensureHls() {
        return new Promise((resolve, reject) => {
            if (window.Hls) { resolve(window.Hls); return; }
            const script = document.createElement('script');
            script.src = '/lib/hls.js/hls.min.js';
            script.onload = () => resolve(window.Hls);
            script.onerror = () => reject(new Error('Failed to load the media player library.'));
            document.head.appendChild(script);
        });
    }

    function el(id) { return document.getElementById(id); }

    function status(message, isError) {
        const node = el('st-player-status');
        if (!node) return;
        if (!message) { node.className = 'st-player-status'; node.textContent = ''; return; }
        node.className = 'st-player-status visible' + (isError ? ' error' : '');
        node.textContent = message;
    }

    function busy(show) {
        const node = el('st-player-spinner');
        if (node) node.classList.toggle('visible', !!show);
    }

    // Track selection (audio / subtitles)
    // Text subtitles arrive as ordinary <track> elements from the proxy (Jellyfin converts
    // them to WebVTT), so switching between them is instant. Audio, and image subtitles,
    // which Jellyfin has to burn into the video, need a fresh PlaybackInfo negotiation at
    // the current position.
    function languageName(code) {
        if (!code) return '';
        try {
            if (typeof Intl !== 'undefined' && Intl.DisplayNames) {
                const name = new Intl.DisplayNames([navigator.language || 'en'], { type: 'language' }).of(code);
                if (name) return name;
            }
        } catch (e) { /* fall back to the raw code */ }
        return code.toUpperCase();
    }

    function trackLabel(track) {
        return languageName(track.language) || track.displayTitle || ('Track ' + track.index);
    }

    function clearChildren(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    function checkIcon() {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('fill', 'currentColor');
        path.setAttribute('d', 'M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z');
        svg.appendChild(path);
        return svg;
    }

    function menuItem(label, detail, selected, onPick) {
        const item = document.createElement('button');
        item.type = 'button';
        item.className = 'st-player-menu-item' + (selected ? ' selected' : '');
        item.setAttribute('role', 'menuitemradio');
        item.setAttribute('aria-checked', selected ? 'true' : 'false');

        const check = document.createElement('span');
        check.className = 'st-player-menu-check';
        if (selected) check.appendChild(checkIcon());

        const text = document.createElement('span');
        text.className = 'st-player-menu-text';
        const main = document.createElement('span');
        main.className = 'st-player-menu-label';
        main.textContent = label;
        text.appendChild(main);
        if (detail && detail !== label) {
            const sub = document.createElement('span');
            sub.className = 'st-player-menu-detail';
            sub.textContent = detail;
            sub.title = detail;
            text.appendChild(sub);
        }

        item.appendChild(check);
        item.appendChild(text);
        item.addEventListener('click', (event) => { event.stopPropagation(); onPick(); });
        return item;
    }

    function renderMenus() {
        const plan = state.plan;
        const audioButton = el('st-player-audio');
        const subtitleButton = el('st-player-subtitles');
        const cueButton = el('st-player-cue');
        const audioMenu = el('st-player-audio-menu');
        const subtitleMenu = el('st-player-subtitles-menu');
        if (!audioMenu || !subtitleMenu) return;

        const audioTracks = (plan && plan.audioTracks) || [];
        const subtitles = (plan && plan.subtitles) || [];

        if (audioButton) audioButton.style.display = audioTracks.length > 1 ? '' : 'none';
        if (subtitleButton) subtitleButton.style.display = (subtitles.length > 0 || state.subtitleSearchHandler) ? '' : 'none';
        if (cueButton) cueButton.style.display = subtitles.some((track) => track.isText) ? '' : 'none';

        clearChildren(audioMenu);
        const audioHeading = document.createElement('div');
        audioHeading.className = 'st-player-menu-heading';
        audioHeading.textContent = 'Audio';
        audioMenu.appendChild(audioHeading);
        audioTracks.forEach((track) => {
            audioMenu.appendChild(menuItem(
                trackLabel(track),
                track.displayTitle,
                track.index === state.audioIndex,
                () => chooseAudio(track.index)));
        });

        clearChildren(subtitleMenu);
        const subtitleHeading = document.createElement('div');
        subtitleHeading.className = 'st-player-menu-heading';
        subtitleHeading.textContent = 'Subtitles';
        subtitleMenu.appendChild(subtitleHeading);
        subtitleMenu.appendChild(menuItem(
            'Off',
            'No subtitles',
            state.subtitleIndex == null,
            () => chooseSubtitle(-1)));
        subtitles.forEach((track) => {
            const detail = (track.displayTitle || '') + (track.isForced ? ' · Forced' : '');
            subtitleMenu.appendChild(menuItem(
                trackLabel(track),
                detail,
                track.index === state.subtitleIndex,
                () => chooseSubtitle(track.index)));
        });

        if (state.subtitleSearchHandler) {
            const searchHeading = document.createElement('div');
            searchHeading.className = 'st-player-menu-heading';
            searchHeading.textContent = 'Find subtitles';
            subtitleMenu.appendChild(searchHeading);
            subtitleMenu.appendChild(menuItem(
                'Search subtitles…',
                'Find and download another language',
                false,
                () => openSubtitleSearch()));
        }

        renderCueMenu();
    }

    // Subtitle appearance
    // Native ::cue rendering cannot be styled from JS, so the page carries data attributes and
    // the stylesheet switches the cue rules. The choice is remembered per browser.
    const CueStyles = ['off', 'background', 'outline'];

    function loadCuePrefs() {
        try {
            const style = localStorage.getItem('stackarr.cue.style');
            if (CueStyles.indexOf(style) >= 0) state.cueStyle = style;

            const scale = Number(localStorage.getItem('stackarr.cue.scale'));
            if (isFinite(scale) && scale >= 0.25 && scale <= 4) {
                state.cueScale = scale;
            } else {
                // Fall back to the earlier three-preset preference.
                const legacy = localStorage.getItem('stackarr.cue.size');
                if (legacy === 'small') state.cueScale = 0.9;
                else if (legacy === 'large') state.cueScale = 1.35;
                else if (legacy === 'medium') state.cueScale = 1;
            }

            const background = Number(localStorage.getItem('stackarr.cue.background'));
            if (isFinite(background) && background >= 0 && background <= 100) state.cueBackground = background;

            const outline = Number(localStorage.getItem('stackarr.cue.outline'));
            if (isFinite(outline) && outline >= 100 && outline <= 400) state.cueOutline = outline;
        } catch (e) { /* storage can be unavailable */ }
        applyCueStyle();
    }

    function applyCueStyle() {
        const page = el('st-player-page');
        if (!page) return;
        page.setAttribute('data-st-cue-style', state.cueStyle);
        page.style.setProperty('--st-cue-scale', String(state.cueScale));
        page.style.setProperty('--st-cue-bg-alpha', String(state.cueBackground / 100));
        page.style.setProperty('--st-cue-outline-scale', String(state.cueOutline / 100));
    }

    function saveCuePrefs(key, value) {
        try { localStorage.setItem(key, value); } catch (e) { /* storage can be unavailable */ }
    }

    function chooseCueStyle(style) {
        if (CueStyles.indexOf(style) < 0) return;
        state.cueStyle = style;
        saveCuePrefs('stackarr.cue.style', style);
        applyCueStyle();
        renderCueMenu();
    }

    function chooseCueScale(scale) {
        if (!isFinite(scale) || scale < 0.25 || scale > 4) return;
        state.cueScale = scale;
        saveCuePrefs('stackarr.cue.scale', String(scale));
        applyCueStyle();
    }

    function chooseCueBackground(value) {
        if (!isFinite(value) || value < 0 || value > 100) return;
        state.cueBackground = value;
        saveCuePrefs('stackarr.cue.background', String(value));
        applyCueStyle();
    }

    function chooseCueOutline(value) {
        if (!isFinite(value) || value < 100 || value > 400) return;
        state.cueOutline = value;
        saveCuePrefs('stackarr.cue.outline', String(value));
        applyCueStyle();
    }

    function cueSliderRow(ariaLabel, min, max, step, value, onChange) {
        const row = document.createElement('div');
        row.className = 'st-player-cue-size';

        const slider = document.createElement('input');
        slider.type = 'range';
        slider.min = String(min);
        slider.max = String(max);
        slider.step = String(step);
        slider.value = String(value);
        slider.setAttribute('aria-label', ariaLabel);

        const label = document.createElement('span');
        label.className = 'st-player-cue-size-value';
        label.textContent = value + '%';

        slider.addEventListener('input', () => {
            const percent = Number(slider.value);
            label.textContent = percent + '%';
            onChange(percent);
        });

        row.appendChild(slider);
        row.appendChild(label);
        return row;
    }

    function cueHeading(text) {
        const heading = document.createElement('div');
        heading.className = 'st-player-menu-heading';
        heading.textContent = text;
        return heading;
    }

    function renderCueMenu() {
        const menu = el('st-player-cue-menu');
        if (!menu) return;

        clearChildren(menu);

        menu.appendChild(cueHeading('Text size'));
        menu.appendChild(cueSliderRow(
            'Subtitle text size', 25, 400, 5,
            Math.round(state.cueScale * 100),
            (percent) => chooseCueScale(percent / 100)));

        if (state.cueStyle === 'background') {
            menu.appendChild(cueHeading('Background opacity'));
            menu.appendChild(cueSliderRow(
                'Subtitle background opacity', 0, 100, 5, Math.round(state.cueBackground), chooseCueBackground));
        } else if (state.cueStyle === 'outline') {
            menu.appendChild(cueHeading('Outline size'));
            menu.appendChild(cueSliderRow(
                'Subtitle outline size', 100, 400, 5, Math.round(state.cueOutline), chooseCueOutline));
        }

        menu.appendChild(cueHeading('Text style'));

        [
            ['off', 'Off', 'No background'],
            ['background', 'Background', 'Dark box behind the text'],
            ['outline', 'Outline', 'Shadow outline, no box']
        ].forEach(([value, label, detail]) => {
            menu.appendChild(menuItem(label, detail, state.cueStyle === value, () => chooseCueStyle(value)));
        });
    }

    function closeMenus() {
        ['audio', 'subtitles', 'cue'].forEach((name) => {
            const menu = el('st-player-' + name + '-menu');
            const button = el('st-player-' + name);
            if (menu) {
                menu.classList.remove('open');
                menu.hidden = true;
            }
            if (button) button.setAttribute('aria-expanded', 'false');
        });
        state.menu = null;
    }

    function openMenu(name) {
        closeMenus();
        const menu = el('st-player-' + name + '-menu');
        const button = el('st-player-' + name);
        if (!menu || !button) return;

        state.menu = name;
        renderMenus();
        menu.hidden = false;
        menu.classList.add('open');
        button.setAttribute('aria-expanded', 'true');

        const page = el('st-player-page');
        if (page) page.classList.remove('st-player-idle');
    }

    function toggleMenu(name) {
        if (state.menu === name) closeMenus();
        else openMenu(name);
    }

    function removeSubtitleTracks(video) {
        if (!video) return;
        const tracks = video.querySelectorAll('track[data-st-subtitle]');
        for (let i = 0; i < tracks.length; i++) tracks[i].remove();
    }

    // Subtitle selection is pure client-side state: Jellyfin only supplies the WebVTT files.
    function applySubtitleMode() {
        const video = state.video;
        if (!video || !video.textTracks) return;
        const tracks = video.textTracks;
        for (let i = 0; i < tracks.length; i++) {
            const track = tracks[i];
            const id = track.id || '';
            // Ignore anything the HLS engine exposes; Stackarr owns its own side-loaded tracks.
            if (id.indexOf('st-sub-') !== 0) {
                try { track.mode = 'disabled'; } catch (e) { /* ignore */ }
                continue;
            }
            const index = Number(id.slice('st-sub-'.length));
            const show = !state.burnedIn
                && state.subtitleIndex != null
                && index === state.subtitleIndex;
            try { track.mode = show ? 'showing' : 'disabled'; } catch (e) { /* ignore */ }
        }
    }

    function loadSubtitleTracks(plan) {
        const video = state.video;
        removeSubtitleTracks(video);
        if (!video || !plan) return;

        (plan.subtitles || []).forEach((subtitle) => {
            if (!subtitle.isText || !subtitle.url) return;
            const track = document.createElement('track');
            track.kind = 'subtitles';
            track.id = 'st-sub-' + subtitle.index;
            track.label = trackLabel(subtitle);
            if (subtitle.language) track.srclang = subtitle.language;
            track.src = subtitle.url;
            track.setAttribute('data-st-subtitle', '1');
            video.appendChild(track);
        });

        applySubtitleMode();
    }

    async function requestPlan(itemId, ticks, audioIndex, subtitleIndex) {
        const body = {
            capabilities: probe(),
            startTicks: ticks || 0,
            audioStreamIndex: audioIndex == null ? null : audioIndex,
            subtitleStreamIndex: subtitleIndex == null ? null : subtitleIndex
        };
        const res = await fetch('/api/media/player/' + encodeURIComponent(itemId) + '/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: JSON.stringify(body)
        });
        if (!res.ok) throw new Error('This title could not be played here (' + res.status + ').');
        return await res.json();
    }

    function stopRemoteSession(sessionId) {
        if (!sessionId) return;
        const url = '/api/media/player/' + sessionId + '/stop';
        if (navigator.sendBeacon) navigator.sendBeacon(url);
        else fetch(url, { method: 'POST', credentials: 'same-origin' }).catch(() => { /* best effort */ });
    }

    // End-of-episode autoplay
    // The server resolves the following episode (if any) and a still image. When the video ends,
    // a small card shows what is coming, counts down, then negotiates the next episode in place;
    // no page reload and no lost fullscreen, the same way a streaming app behaves.
    function loadNext(itemId) {
        return fetch('/api/media/player/' + encodeURIComponent(itemId) + '/next', {
            credentials: 'same-origin'
        }).then((res) => {
            if (res.status === 204 || !res.ok) return null;
            return res.json();
        }).catch(() => null);
    }

    function padded(value) {
        return (value < 10 ? '0' : '') + value;
    }

    function hideNextOverlay() {
        if (state.nextTimer) {
            clearInterval(state.nextTimer);
            state.nextTimer = null;
        }
        const overlay = el('st-player-next');
        if (!overlay) return;
        overlay.hidden = true;
    }

    function showNextOverlay(next, seconds) {
        const overlay = el('st-player-next');
        if (!overlay) { playNext(); return; }

        const title = el('st-player-next-title');
        const sub = el('st-player-next-sub');
        const countdown = el('st-player-next-countdown');
        const image = el('st-player-next-image');

        if (title) title.textContent = next.title || '';
        if (sub) {
            sub.textContent = next.subtitle
                || ((next.seasonNumber || next.episodeNumber)
                    ? 'S' + padded(next.seasonNumber || 0) + 'E' + padded(next.episodeNumber || 0)
                    : '');
        }
        if (image) {
            if (next.imageUrl) {
                image.src = next.imageUrl;
                image.hidden = false;
                image.onerror = () => { image.hidden = true; };
            } else {
                image.removeAttribute('src');
                image.hidden = true;
            }
        }

        overlay.hidden = false;
        const page = el('st-player-page');
        if (page) page.classList.remove('st-player-idle');

        // Count down the playing time to the end, capped at ten seconds. Pausing the video pauses
        // the card too, and looping the transcode's tail (when Jellyfin has not closed the
        // playlist yet) cannot keep the user stuck: the switch still happens on schedule.
        let remaining = Math.max(1, Math.min(10, Math.ceil(seconds || 10)));
        const paint = () => {
            if (countdown) {
                const left = Math.max(0, Math.ceil(remaining));
                countdown.textContent = left > 0 ? 'Playing in ' + left + 's' : 'Starting…';
            }
        };

        paint();
        if (state.nextTimer) clearInterval(state.nextTimer);
        state.nextTimer = setInterval(() => {
            const video = state.video;
            if (video && video.paused && !video.ended) return;
            remaining -= 0.25;
            paint();
            if (remaining <= 0) {
                hideNextOverlay();
                playNext();
            }
        }, 250);
    }

    // Shows the next-episode card when the current episode is inside its last 10 seconds.
    function maybeShowNext() {
        const video = state.video;
        if (!video || state.nextShown || state.nextCancelled || !state.next) return;
        if (!isFinite(video.duration) || video.duration <= 0) return;

        const remaining = video.duration - (video.currentTime || 0);
        if (remaining <= 0 || remaining > 10) return;

        state.nextShown = true;
        showNextOverlay(state.next, remaining);
    }

    // Keeps the browser URL, page title and player header in sync when autoplay swaps the item,
    // so a refresh resumes the episode the user is actually watching.
    function applyNextMetadata(next) {
        const title = next.title || '';
        const subtitle = next.subtitle || '';
        const titleNode = document.querySelector('.st-player-title');
        const subtitleNode = document.querySelector('.st-player-subtitle');
        if (titleNode) titleNode.textContent = title;
        if (subtitleNode) subtitleNode.textContent = subtitle;
        document.title = title ? title + ' · Stackarr' : 'Stackarr';

        // The now-playing block lives in the control bar; autoplay swaps its art along with the text.
        const nowPlaying = el('st-player-nowplaying');
        if (nowPlaying && (title || next.imageUrl)) nowPlaying.hidden = false;
        const art = el('st-player-art');
        if (art) {
            if (next.imageUrl) {
                art.src = next.imageUrl;
                art.hidden = false;
            } else {
                art.removeAttribute('src');
                art.hidden = true;
            }
        }

        const jellyfin = el('st-player-jellyfin');
        if (jellyfin && next.jellyfinUrl) jellyfin.href = next.jellyfinUrl;

        try {
            const params = new URLSearchParams(window.location.search);
            params.set('title', title);
            if (next.imageUrl) params.set('art', next.imageUrl);
            else params.delete('art');
            history.replaceState(null, '', '/watch/' + encodeURIComponent(next.itemId)
                + (params.toString() ? '?' + params.toString() : ''));
        } catch (e) { /* URL sync is an enhancement */ }
    }

    async function playNext() {
        const next = state.next;
        hideNextOverlay();
        if (!next || !next.itemId) { stop(true); return; }

        applyNextMetadata(next);
        await play('st-player-video', next.itemId, 0, {
            title: next.title || '',
            subtitle: next.subtitle || '',
            artwork: next.imageUrl || ''
        });
    }

    async function loadStream(plan) {
        const video = state.video;
        if (!video) return;

        if (state.hls) {
            try { state.hls.destroy(); } catch (e) { /* ignore */ }
            state.hls = null;
        }

        if (video.canPlayType('application/vnd.apple.mpegurl') !== '') {
            video.src = plan.streamUrl;
        } else {
            const Hls = await ensureHls();
            if (Hls && Hls.isSupported()) {
                const hls = new Hls({ enableWorker: true, lowLatencyMode: false });
                state.hls = hls;
                hls.on(Hls.Events.ERROR, (_evt, data) => {
                    if (data && data.fatal) status('Playback error: ' + (data.details || 'unknown') + '.', true);
                });
                hls.loadSource(plan.streamUrl);
                hls.attachMedia(video);
            } else {
                video.src = plan.streamUrl;
            }
        }
    }

    // The source restarts at zero after a switch, so seek back to the negotiated position. The
    // listener is registered *before* loadStream assigns the new source: the old source's
    // metadata is still present at that moment, so seeking immediately would be discarded when
    // the new source loads.
    function prepareSeek(video, ticks) {
        if (!video || !ticks || ticks <= 0) return;
        const seconds = ticks / 10000000;
        // `loadedmetadata` is too early: the new source is not seekable until data arrives, and a
        // seek assigned there is silently reset to 0. `loadeddata` fires with a seekable range.
        video.addEventListener('loadeddata', () => {
            try { video.currentTime = seconds; } catch (e) { /* ignore */ }
        }, { once: true });
    }

    async function applyPlan(plan, autoplay) {
        const video = state.video;
        if (!video) return;

        state.plan = plan;
        state.audioIndex = plan.selectedAudioIndex == null ? null : plan.selectedAudioIndex;
        state.subtitleIndex = plan.selectedSubtitleIndex == null ? null : plan.selectedSubtitleIndex;
        state.burnedIn = !!plan.subtitlesBurnedIn;

        renderMenus();
        applyCueStyle();
        loadSubtitleTracks(plan);
        prepareSeek(video, plan.startTicks || 0);
        await loadStream(plan);
        // Loading a new source can reset the text-track modes, so re-apply the selection.
        applySubtitleMode();

        if (autoplay) {
            try { await video.play(); }
            catch (e) { busy(false); status('Press play to start.', false); }
        }
    }

    async function switchTracks(audioIndex, subtitleIndex) {
        const video = state.video;
        if (!video || state.switching || !state.itemId) return;

        state.switching = true;
        closeMenus();
        busy(true);
        status('Switching track…', false);

        const ticks = Math.round((video.currentTime || 0) * 10000000);
        const wantedSubtitle = subtitleIndex === undefined
            ? (state.subtitleIndex == null ? -1 : state.subtitleIndex)
            : subtitleIndex;
        const wasPlaying = !video.paused && !video.ended;

        try {
            const plan = await requestPlan(state.itemId, ticks, audioIndex, wantedSubtitle);
            const previous = state.sessionId;
            state.sessionId = plan.sessionId;
            await applyPlan(plan, wasPlaying);
            if (previous && previous !== plan.sessionId) stopRemoteSession(previous);
            if (!wasPlaying) {
                busy(false);
                status('', false);
            }
        } catch (err) {
            busy(false);
            status(err.message, true);
        } finally {
            state.switching = false;
        }
    }

    function chooseAudio(index) {
        closeMenus();
        if (index === state.audioIndex) return;
        switchTracks(index, undefined);
    }

    function chooseSubtitle(index) {
        closeMenus();
        const subtitles = (state.plan && state.plan.subtitles) || [];
        const target = index == null || index < 0
            ? null
            : subtitles.find((track) => track.index === index) || null;
        const off = !target;

        if (off) {
            if (state.subtitleIndex == null) return;
            // A burned-in subtitle can only be removed by a new negotiation.
            if (state.burnedIn) { switchTracks(state.audioIndex, -1); return; }
            state.subtitleIndex = null;
            applySubtitleMode();
            renderMenus();
            return;
        }

        // Text tracks are side-loaded: switching them is free. Image tracks (and leaving a
        // burned-in track) require Jellyfin to produce a new stream.
        if (target.isText && !state.burnedIn) {
            state.subtitleIndex = target.index;
            applySubtitleMode();
            renderMenus();
            return;
        }

        switchTracks(state.audioIndex, target.index);
    }

    function cycleSubtitles() {
        const subtitles = (state.plan && state.plan.subtitles) || [];
        if (!subtitles.length) return;
        const current = subtitles.findIndex((track) => track.index === state.subtitleIndex);
        if (current < 0) chooseSubtitle(subtitles[0].index);
        else if (current + 1 < subtitles.length) chooseSubtitle(subtitles[current + 1].index);
        else chooseSubtitle(-1);
    }

    // Subtitle search is owned by the shared subtitle module. The player only publishes its
    // identity, so nothing here knows about any provider UI.
    function openSubtitleSearch() {
        closeMenus();
        if (!state.subtitleSearchHandler) return;
        try {
            const promise = state.subtitleSearchHandler.invokeMethodAsync('OpenSubtitleSearchAsync');
            if (promise && promise.catch) promise.catch(() => { /* circuit gone */ });
        } catch (e) { /* circuit gone */ }
    }

    function sameLanguage(left, right) {
        if (!left || !right) return false;
        const a = String(left).toLowerCase().replace('_', '-');
        const b = String(right).toLowerCase().replace('_', '-');
        return a === b || a.split('-')[0] === b.split('-')[0];
    }

    // Re-negotiates the current position after a subtitle download so Jellyfin's refreshed sidecar
    // tracks appear in the menu, and selects the requested language when it is available.
    async function refreshPlan(preferredLanguage) {
        const video = state.video;
        if (!video || state.switching || !state.itemId) return;

        state.switching = true;
        busy(true);
        status('Refreshing subtitles…', false);
        const ticks = Math.round((video.currentTime || 0) * 10000000);
        const wasPlaying = !video.paused && !video.ended;

        try {
            const plan = await requestPlan(
                state.itemId,
                ticks,
                state.audioIndex,
                state.subtitleIndex == null ? -1 : state.subtitleIndex);
            const previous = state.sessionId;
            state.sessionId = plan.sessionId;
            await applyPlan(plan, wasPlaying);
            if (previous && previous !== plan.sessionId) stopRemoteSession(previous);

            if (preferredLanguage) {
                const subtitles = (plan && plan.subtitles) || [];
                const match = subtitles.find((track) => sameLanguage(track.language, preferredLanguage) && !track.isForced)
                    || subtitles.find((track) => sameLanguage(track.language, preferredLanguage));
                if (match) chooseSubtitle(match.index);
            }

            if (!wasPlaying) {
                busy(false);
                status('', false);
            }
        } catch (err) {
            busy(false);
            status(err.message, true);
        } finally {
            state.switching = false;
        }
    }

    function formatTime(seconds) {
        if (!isFinite(seconds) || seconds < 0) seconds = 0;
        const total = Math.floor(seconds);
        const h = Math.floor(total / 3600);
        const m = Math.floor((total % 3600) / 60);
        const s = total % 60;
        const mm = h > 0 && m < 10 ? '0' + m : String(m);
        const ss = s < 10 ? '0' + s : String(s);
        return h > 0 ? h + ':' + mm + ':' + ss : m + ':' + ss;
    }

    // OS media session (lock screen / Control Center / Bluetooth controls)
    // The title/artwork come from Stackarr's own metadata (TMDb artwork passed on the watch
    // URL), never from Jellyfin, Jellyfin is only used to negotiate and stream the media.
    function absoluteUrl(url) {
        if (!url) return '';
        try { return new URL(url, window.location.href).href; } catch (e) { return url; }
    }

    function updateMetadata(meta) {
        if (!mediaSession || typeof MediaMetadata === 'undefined' || !meta) return;
        const artwork = meta.artwork ? [{ src: absoluteUrl(meta.artwork), sizes: '512x512' }] : [];
        try {
            mediaSession.metadata = new MediaMetadata({
                title: meta.title || '',
                artist: meta.subtitle || '',
                album: '',
                artwork
            });
        } catch (e) { /* metadata is an enhancement */ }
    }

    function setPlaybackState(playing) {
        if (!mediaSession) return;
        try { mediaSession.playbackState = playing ? 'playing' : 'paused'; } catch (e) { /* enhancement */ }
    }

    function updatePositionState(video) {
        if (!mediaSession || typeof mediaSession.setPositionState !== 'function' || !video) return;
        if (!isFinite(video.duration) || video.duration <= 0) return;
        try {
            mediaSession.setPositionState({
                duration: video.duration,
                playbackRate: video.playbackRate || 1,
                position: Math.min(Math.max(video.currentTime || 0, 0), video.duration)
            });
        } catch (e) { /* position state is an enhancement */ }
    }

    // Video keeps the ±seek controls (which is the right pairing for a single item) rather than
    // the track-skip controls the music player registers.
    function wireMediaSession(video) {
        if (!mediaSession || typeof mediaSession.setActionHandler !== 'function' || !video) return;
        const handlers = {
            play: () => video.play().catch(() => { }),
            pause: () => video.pause(),
            seekbackward: (details) => {
                video.currentTime = Math.max(0, (video.currentTime || 0) - ((details && details.seekOffset) || 10));
            },
            seekforward: (details) => {
                const target = (video.currentTime || 0) + ((details && details.seekOffset) || 10);
                video.currentTime = isFinite(video.duration) ? Math.min(target, video.duration) : target;
            },
            seekto: (details) => {
                if (details && typeof details.seekTime === 'number') video.currentTime = details.seekTime;
            },
            stop: () => { video.pause(); try { video.currentTime = 0; } catch (e) { /* ignore */ } }
        };
        Object.keys(handlers).forEach((action) => {
            try { mediaSession.setActionHandler(action, handlers[action]); } catch (e) { /* unsupported action */ }
        });
    }

    function clearMediaSession() {
        if (!mediaSession) return;
        try { mediaSession.metadata = null; } catch (e) { /* ignore */ }
        try { mediaSession.playbackState = 'none'; } catch (e) { /* ignore */ }
    }

    function on(target, event, handler, options) {
        target.addEventListener(event, handler, options);
        state.cleanup.push(() => target.removeEventListener(event, handler, options));
    }

    // Custom control bar
    function wireControls(video) {
        const page = el('st-player-page');
        const controls = el('st-player-controls');
        const progress = el('st-player-progress');
        const fill = el('st-player-fill');
        const buffer = el('st-player-buffer');
        const time = el('st-player-time');
        const toggle = el('st-player-toggle');
        const mute = el('st-player-mute');
        const volume = el('st-player-volume');
        const fullscreen = el('st-player-fullscreen');
        if (!page || !video) return;

        let dragging = false;
        let idleTimer = null;

        // `currentTime` advances in coarse pipeline steps, so on a fast display several frames would
        // read the same value and then jump, stutter. Render one step behind, interpolated between
        // the last two samples, so the bar moves on every frame without ever running ahead.
        let posPrev = null;
        let posCur = null;
        const smoothPos = (real) => {
            const now = performance.now();
            if (posCur && Math.abs(real - posCur.v) > 0.3) { posPrev = null; posCur = null; }
            if (!posCur || posCur.v !== real) { posPrev = posCur; posCur = { v: real, t: now }; }
            if (!posPrev || !posCur) return real;
            const span = posCur.t - posPrev.t;
            if (span <= 0 || span > 150) return real;
            const alpha = Math.min(1, Math.max(0, (now - posCur.t) / span));
            return posPrev.v + (posCur.v - posPrev.v) * alpha;
        };

        const render = () => {
            const dur = isFinite(video.duration) ? video.duration : 0;
            const real = video.currentTime || 0;
            const pos = (!video.paused && !video.ended) ? smoothPos(real) : real;
            const pct = dur > 0 ? Math.min(100, (pos / dur) * 100) : 0;
            if (fill) fill.style.width = pct + '%';
            if (time) {
                const text = formatTime(real) + ' / ' + formatTime(dur);
                if (time.textContent !== text) time.textContent = text;
            }
            if (buffer && video.buffered && video.buffered.length) {
                const end = video.buffered.end(video.buffered.length - 1);
                if (buffer) buffer.style.width = (dur > 0 ? Math.min(100, (end / dur) * 100) : 0) + '%';
            }
            updatePositionState(video);

            // The autoplay card belongs to the last ten seconds. Seeking back out of that window
            // cancels the countdown instead of switching episodes unexpectedly.
            if (state.nextShown && !state.nextCancelled && dur > 0 && dur - real > 12) {
                hideNextOverlay();
                state.nextShown = false;
            }
            maybeShowNext();
        };

        // The seek bar is painted every animation frame while playing, not on the ~4Hz timeupdate,
        // so it glides instead of stepping. Reading the video's own clock each frame is exact.
        let progressRaf = 0;
        const progressLoop = () => {
            progressRaf = 0;
            if (!video || video.paused || video.ended) return;
            render();
            progressRaf = requestAnimationFrame(progressLoop);
        };
        const startProgress = () => { if (!progressRaf) { posPrev = null; posCur = null; progressRaf = requestAnimationFrame(progressLoop); } };
        const stopProgress = () => { if (progressRaf) { cancelAnimationFrame(progressRaf); progressRaf = 0; } };
        state.cleanup.push(stopProgress);

        const refreshPlaying = () => page.classList.toggle('is-playing', !video.paused && !video.ended);
        const refreshMuted = () => page.classList.toggle('is-muted', video.muted || video.volume === 0);
        const paintVolume = () => {
            if (!volume) return;
            const v = video.muted ? 0 : video.volume;
            volume.style.background = 'linear-gradient(90deg, var(--st-pink,#e879a0) 0%, var(--st-purple,#c084fc) '
                + (v * 100) + '%, rgba(255,255,255,0.2) ' + (v * 100) + '%)';
        };

        const seekFromEvent = (e) => {
            if (!progress) return;
            const rect = progress.getBoundingClientRect();
            if (rect.width <= 0) return;
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            if (isFinite(video.duration) && video.duration > 0) {
                video.currentTime = ratio * video.duration;
                render();
            }
        };

        const showControls = () => {
            page.classList.remove('st-player-idle');
            if (idleTimer) clearTimeout(idleTimer);
            idleTimer = setTimeout(() => {
                if (!video.paused && !video.ended && !state.menu) page.classList.add('st-player-idle');
            }, 2600);
        };

        on(video, 'play', () => { refreshPlaying(); setPlaybackState(true); startProgress(); });
        on(video, 'pause', () => { refreshPlaying(); showControls(); setPlaybackState(false); stopProgress(); render(); });
        on(video, 'ended', async () => {
            refreshPlaying();
            setPlaybackState(false);
            stopProgress();

            const sessionId = state.sessionId;
            if (sessionId) {
                try {
                    await fetch('/api/media/player/' + sessionId + '/complete', {
                        method: 'POST',
                        credentials: 'same-origin'
                    });
                } catch (e) { /* best effort */ }
            }

            if (state.nextCancelled) {
                stop(true);
                return;
            }

            const next = state.next || (state.nextPromise ? await state.nextPromise : null);
            if (!next) {
                stop(true);
                return;
            }

            // The pre-end countdown already owns the switch. If it never appeared (metadata was
            // still loading), show a short card now rather than silently stopping.
            state.next = next;
            if (!state.nextShown) {
                state.nextShown = true;
                showNextOverlay(next, 5);
            }
        });
        on(video, 'timeupdate', render);
        on(video, 'durationchange', render);
        on(video, 'loadedmetadata', () => { render(); applySubtitleMode(); });
        on(video, 'progress', render);
        on(video, 'seeked', () => updatePositionState(video));
        on(video, 'waiting', () => busy(true));
        on(video, 'playing', () => { busy(false); refreshPlaying(); showControls(); startProgress(); status('', false); });
        on(video, 'canplay', () => busy(false));
        on(video, 'volumechange', () => {
            refreshMuted();
            paintVolume();
            if (volume) volume.value = String(video.muted ? 0 : video.volume);
        });
        on(video, 'click', () => togglePlay());

        if (toggle) on(toggle, 'click', () => togglePlay());
        if (el('st-player-back10')) on(el('st-player-back10'), 'click', () => { video.currentTime = Math.max(0, video.currentTime - 10); });
        if (el('st-player-fwd10')) on(el('st-player-fwd10'), 'click', () => { video.currentTime = Math.min(video.duration || video.currentTime + 10, video.currentTime + 10); });

        if (progress) {
            on(progress, 'pointerdown', (e) => {
                dragging = true;
                try { progress.setPointerCapture(e.pointerId); } catch (x) { /* ignore */ }
                seekFromEvent(e);
            });
            on(progress, 'pointermove', (e) => { if (dragging) seekFromEvent(e); });
            const endDrag = (e) => {
                dragging = false;
                try { progress.releasePointerCapture(e.pointerId); } catch (x) { /* ignore */ }
            };
            on(progress, 'pointerup', endDrag);
            on(progress, 'pointercancel', endDrag);
        }

        if (mute) on(mute, 'click', () => { video.muted = !video.muted; refreshMuted(); });
        if (volume) on(volume, 'input', () => {
            try { video.volume = Number(volume.value); video.muted = Number(volume.value) === 0; } catch (x) { /* iOS ignores */ }
            refreshMuted();
            paintVolume();
        });

        if (fullscreen) on(fullscreen, 'click', () => {
            if (document.fullscreenElement) document.exitFullscreen();
            else if (page.requestFullscreen) page.requestFullscreen();
        });
        on(document, 'fullscreenchange', () => page.classList.toggle('is-fullscreen', !!document.fullscreenElement));

        const audioButton = el('st-player-audio');
        const subtitleButton = el('st-player-subtitles');
        const cueButton = el('st-player-cue');
        if (audioButton) on(audioButton, 'click', (event) => { event.stopPropagation(); toggleMenu('audio'); });
        if (subtitleButton) on(subtitleButton, 'click', (event) => { event.stopPropagation(); toggleMenu('subtitles'); });
        if (cueButton) on(cueButton, 'click', (event) => { event.stopPropagation(); toggleMenu('cue'); });

        const audioMenu = el('st-player-audio-menu');
        const subtitleMenu = el('st-player-subtitles-menu');
        const cueMenu = el('st-player-cue-menu');
        if (audioMenu) on(audioMenu, 'click', (event) => event.stopPropagation());
        if (subtitleMenu) on(subtitleMenu, 'click', (event) => event.stopPropagation());
        if (cueMenu) on(cueMenu, 'click', (event) => event.stopPropagation());

        on(document, 'click', (event) => {
            if (!state.menu) return;
            if (event.target && event.target.closest && event.target.closest('.st-player-menu-anchor')) return;
            closeMenus();
        });

        const nextClose = el('st-player-next-close');
        const nextPlay = el('st-player-next-play');
        if (nextClose) on(nextClose, 'click', (event) => {
            event.stopPropagation();
            hideNextOverlay();
            state.nextCancelled = true;
            if (video.ended) stop(true);
        });
        if (nextPlay) on(nextPlay, 'click', (event) => {
            event.stopPropagation();
            playNext();
        });

        wireAirPlay(video);

        on(page, 'pointermove', showControls);
        on(page, 'pointerdown', showControls);

        on(document, 'keydown', (e) => {
            if (!el('st-player-page')) return;
            if (e.target && /INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) return;
            switch (e.key) {
                case ' ': case 'k': e.preventDefault(); closeMenus(); togglePlay(); break;
                case 'ArrowRight': video.currentTime = Math.min(video.duration || video.currentTime + 10, video.currentTime + 10); break;
                case 'ArrowLeft': video.currentTime = Math.max(0, video.currentTime - 10); break;
                case 'm': video.muted = !video.muted; refreshMuted(); break;
                case 'f': if (fullscreen) fullscreen.click(); break;
                case 'c': cycleSubtitles(); break;
                case 'Escape':
                    {
                        const nextOverlay = el('st-player-next');
                        if (nextOverlay && !nextOverlay.hidden) {
                            hideNextOverlay();
                            state.nextCancelled = true;
                            if (video.ended) stop(true);
                        } else {
                            closeMenus();
                        }
                        return;
                    }
                default: return;
            }
            showControls();
        });

        refreshPlaying();
        refreshMuted();
        paintVolume();
        render();
        showControls();
    }

    // AirPlay
    // AirPlay is a Safari/WebKit feature: the receiver (Apple TV) fetches the stream URL itself, so
    // the proxy hands it a signed, cookie-free URL (see StreamTokenService). Custom controls hide
    // Safari's built-in AirPlay button, so we expose our own that opens the native route picker.
    function wireAirPlay(video) {
        const button = el('st-player-airplay');
        if (!button) return;
        // Not WebKit: no AirPlay at all (Chrome/Edge/Firefox), so leave the button hidden.
        if (typeof video.webkitShowPlaybackTargetPicker !== 'function') return;

        const refreshAvailability = (event) => {
            const available = !event || !event.availability || event.availability === 'available';
            button.style.display = available ? '' : 'none';
        };

        on(video, 'webkitplaybacktargetavailabilitychanged', refreshAvailability);
        on(video, 'webkitcurrentplaybacktargetiswirelesschanged', () => {
            button.classList.toggle('is-wireless', !!video.webkitCurrentPlaybackTargetIsWireless);
        });
        on(button, 'click', () => { try { video.webkitShowPlaybackTargetPicker(); } catch (e) { /* ignore */ } });

        // Show it up-front on Safari so it is tappable before the availability event fires.
        refreshAvailability(null);
    }

    function togglePlay() {
        const video = state.video || el('st-player-video');
        if (!video) return;
        if (video.paused) video.play().catch(() => { });
        else video.pause();
    }

    // Persist the resume position for the session's real user.
    function sendProgress(sessionId, video, beacon) {
        if (!sessionId || !video) return;
        if (!isFinite(video.duration) || video.duration <= 0) return;
        const ticks = Math.round((video.currentTime || 0) * 10000000);
        if (ticks <= 0) return;

        const url = '/api/media/player/' + sessionId + '/progress';
        const payload = JSON.stringify({ positionTicks: ticks, isPaused: video.paused });

        if (beacon && navigator.sendBeacon) {
            navigator.sendBeacon(url, new Blob([payload], { type: 'application/json' }));
        } else {
            fetch(url, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: payload,
                keepalive: true
            }).catch(() => { /* best effort */ });
        }
    }

    // Lifecycle
    async function play(videoId, itemId, startTicks, meta) {
        stop(true);

        // The video takes over the tab. The music player shares the tab's audio element and its
        // component is not even mounted under this layout, so silence it and drop its UI here;
        // otherwise the song keeps playing behind the video and the bar comes back broken.
        try { if (window.stackarrMusic && window.stackarrMusic.stop) window.stackarrMusic.stop(); } catch (e) { /* music is optional */ }

        status('Preparing playback…', false);
        busy(true);

        const video = el(videoId);
        if (!video) { busy(false); status('Player element missing.', true); return; }
        state.video = video;
        state.itemId = itemId;
        state.meta = meta || null;
        video.muted = false;

        let plan;
        try {
            plan = await requestPlan(itemId, startTicks, null, null);
        } catch (err) {
            busy(false);
            status('Could not reach the server: ' + err.message, true);
            return;
        }

        state.sessionId = plan.sessionId;
        state.next = null;
        state.nextShown = false;
        state.nextCancelled = false;
        state.nextPromise = loadNext(itemId).then((next) => {
            state.next = next;
            maybeShowNext();
            return next;
        });

        if (!video.dataset.stControlsWired) {
            video.dataset.stControlsWired = '1';
            wireControls(video);
        }

        // Publish our own title/artwork to the OS media session and wire the device controls.
        updateMetadata(meta);
        wireMediaSession(video);
        setPlaybackState(false);

        try {
            await applyPlan(plan, true);
        } catch (err) {
            busy(false);
            status(err.message, true);
            return;
        }

        // Save the position periodically, on pause/seek, and on the way out. A track switch
        // swaps the source, so skip the position events it causes.
        let lastSaved = 0;
        const tick = () => {
            if (state.switching) return;
            if (!video.paused && Date.now() - lastSaved > 10000) {
                lastSaved = Date.now();
                sendProgress(state.sessionId, video, false);
            }
        };
        on(video, 'timeupdate', tick);
        on(video, 'pause', () => {
            if (state.switching) return;
            lastSaved = Date.now();
            sendProgress(state.sessionId, video, false);
        });
        on(video, 'seeked', () => {
            if (state.switching) return;
            lastSaved = Date.now();
            sendProgress(state.sessionId, video, false);
        });
    }

    function stop(silent) {
        closeMenus();
        hideNextOverlay();
        state.cleanup.forEach((fn) => { try { fn(); } catch (e) { /* ignore */ } });
        state.cleanup = [];

        if (state.hls) {
            try { state.hls.destroy(); } catch (e) { /* ignore */ }
            state.hls = null;
        }

        removeSubtitleTracks(state.video);

        // Flush the final position before dropping the session (unless it already finished).
        if (state.sessionId && state.video && !state.video.ended)
            sendProgress(state.sessionId, state.video, true);

        const sid = state.sessionId;
        if (sid) {
            state.sessionId = null;
            const url = '/api/media/player/' + sid + '/stop';
            if (navigator.sendBeacon) navigator.sendBeacon(url);
            else fetch(url, { method: 'POST', credentials: 'same-origin' });
        }

        const video = state.video;
        if (video) {
            delete video.dataset.stControlsWired;
            if (!silent) {
                try {
                    video.removeAttribute('src');
                    video.load();
                } catch (e) { /* ignore */ }
            }
        }

        state.video = null;
        state.itemId = null;
        state.meta = null;
        state.plan = null;
        state.audioIndex = null;
        state.subtitleIndex = null;
        state.burnedIn = false;
        state.switching = false;
        state.next = null;
        state.nextPromise = null;
        state.nextShown = false;
        state.nextCancelled = false;

        clearMediaSession();
        busy(false);
    }

    loadCuePrefs();

    window.addEventListener('pagehide', () => stop(true));
    window.stackarrPlayer = {
        play,
        stop: () => stop(false),
        refreshPlan: (language) => refreshPlan(language),
        setSubtitleSearchHandler: (handler) => {
            state.subtitleSearchHandler = handler || null;
            renderMenus();
        },
        showError: (m) => status(m, true),
        isPlaying: () => {
            const video = state.video || el('st-player-video');
            return !!(video && !video.paused && !video.ended);
        },
        pause: () => {
            const video = state.video || el('st-player-video');
            if (video && !video.paused) video.pause();
        },
        resume: () => {
            const video = state.video || el('st-player-video');
            if (video && video.paused && !video.ended) video.play().catch(() => { });
        }
    };
})();
