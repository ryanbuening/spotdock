// Reads Spotify Web playback state from open.spotify.com (navigator.mediaSession
// plus player controls/progress DOM) and reports it to the SpotDock extension
// service worker.
(() => {
  // The script arrives in two ways - from the manifest and by late injection
  // into tabs that are already open; the second pass must bail out silently
  if (window.__spotDockBridgeLoaded) return;
  window.__spotDockBridgeLoaded = true;

  const VERSION = chrome.runtime.getManifest().version;

  const TICK_MS = 500;
  const HEARTBEAT_MS = 3000;

  const one = sel => document.querySelector(sel);

  // Spotify Web renames its testids between releases, so the like button is
  // looked up by structure first: it is a toggle inside the now-playing bar.
  // The aria label is English (the extension matches the English UI anyway).
  function spotifyBar() {
    return one('[data-testid="now-playing-widget"]') ||
           one('[data-testid="now-playing-bar"]') ||
           document.querySelector('footer');
  }

  function spotifyLike() {
    const widget = one('[data-testid="now-playing-widget"]');
    const bar = widget || spotifyBar();
    if (!bar) return null;
    const byTestId = bar.querySelector(
      '[data-testid="add-button"], [data-testid*="like"], [data-testid*="save-personal-library"]');
    if (byTestId) return byTestId;
    const labelRe = widget
      ? /liked songs|your library|like this|remove from|playlist/i
      : /add to playlist|edit playlist|liked songs|your library|like this|remove from/i;
    return [...bar.querySelectorAll('button')].find(b =>
      labelRe.test(b.getAttribute('aria-label') || '')) || null;
  }

  function controls() {
    try {
      return {
        play: one('[data-testid="control-button-playpause"]'),
        next: one('[data-testid="control-button-skip-forward"]'),
        prev: one('[data-testid="control-button-skip-back"]'),
        like: spotifyLike(),
      };
    } catch {
      return {};
    }
  }

  let lastSent = 0;
  let lastKey = '';
  let timer = null;

  // The playing element; if nothing is playing - the last one that played
  // something (needed so the widget does not go dark instantly on pause)
  function pickMedia() {
    const all = [...document.querySelectorAll('video, audio')];
    if (!all.length) return null;
    const live = all.filter(e => !e.paused && !e.ended && e.readyState >= 2 && !e.muted && e.volume > 0);
    const pool = live.length ? live : all.filter(e => e.currentTime > 0 && !e.ended);
    if (!pool.length) return null;
    // The longest one is usually the main content, not an ad insert/preview
    pool.sort((a, b) => (b.duration || 0) - (a.duration || 0));
    return pool[0];
  }

  function metadata() {
    try {
      return (navigator.mediaSession && navigator.mediaSession.metadata) || null;
    } catch {
      return null;
    }
  }

  function biggestArt(meta) {
    if (!meta || !meta.artwork || !meta.artwork.length) return '';
    let best = '', bestPx = -1;
    for (const a of meta.artwork) {
      const px = parseInt(String(a.sizes || '0x0').split('x')[0], 10) || 0;
      if (px >= bestPx) { bestPx = px; best = a.src || ''; }
    }
    return best;
  }

  function playbackState() {
    try { return (navigator.mediaSession && navigator.mediaSession.playbackState) || 'none'; }
    catch { return 'none'; }
  }

  // A player without a DOM element still exposes its position: the progress
  // bar is a slider with aria-valuenow/aria-valuemax in seconds. We rule out
  // the volume slider by its maximum (which is 1)
  function sliderProgress() {
    let best = null;
    for (const e of document.querySelectorAll('[role="slider"], input[type="range"]')) {
      const now = parseFloat(e.getAttribute('aria-valuenow') ?? e.value);
      const max = parseFloat(e.getAttribute('aria-valuemax') ?? e.max);
      if (!isFinite(now) || !isFinite(max) || max <= 5) continue;
      if (!best || max > best.duration) best = { position: now, duration: max, el: e };
    }
    return best;
  }

  // The progress bar is a React-controlled <input type="range">.
  // Assigning value bypasses React (it has its own valueTracker), so we call
  // the native setter and dispatch input/change ourselves
  function setRangeValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (setter) setter.call(input, String(value));
    else input.value = String(value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // For role="slider" (not an input) the only option is a simulated click at the target point
  function clickSliderAt(prog, seconds) {
    const r = prog.el.getBoundingClientRect();
    if (!r.width) return;
    const x = r.left + r.width * Math.max(0, Math.min(1, seconds / prog.duration));
    const y = r.top + r.height / 2;
    const opts = { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 };
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
      const Ctor = type.startsWith('pointer') && window.PointerEvent ? PointerEvent : MouseEvent;
      prog.el.dispatchEvent(new Ctor(type, opts));
    }
  }

  function seekTo(prog, seconds) {
    const s = Math.max(0, Math.min(seconds, prog.duration));
    if (prog.el.tagName === 'INPUT') setRangeValue(prog.el, Math.round(s));
    else clickSliderAt(prog, s);
  }

  // Spotify keeps the favorite state in the button's aria-pressed / aria-checked,
  // or its aria-label when it opens a playlist menu; null means "no button"
  function likeState() {
    const b = controls().like;
    if (!b) return { canLike: false, liked: null };
    const pressed = b.getAttribute('aria-pressed') ?? b.getAttribute('aria-checked');
    if (pressed !== null) return { canLike: true, liked: pressed === 'true' };
    const label = b.getAttribute('aria-label') || '';
    if (/remove from|playlist/i.test(label)) return { canLike: true, liked: true };
    if (/liked songs|your library|like this/i.test(label)) return { canLike: true, liked: false };
    return { canLike: true, liked: null };
  }

  function snapshot() {
    const el = pickMedia();
    const meta = metadata();

    // Spotify Web keeps its audio in a sandboxed iframe/EME element and exposes
    // track metadata via mediaSession while the position comes from the slider.
    if (!el) {
      if (!meta || !meta.title) return null;
      const prog = sliderProgress();
      return {
        playing: playbackState() === 'playing',
        title: meta.title,
        artist: meta.artist || location.hostname.replace(/^www\./, ''),
        album: meta.album || '',
        art: biggestArt(meta),
        position: prog ? prog.position : 0,
        duration: prog ? prog.duration : 0,
        canSeek: !!prog,
        canNext: !!controls().next,
        canPrev: !!controls().prev,
        v: VERSION,
        ...likeState(),
        host: location.hostname,
      };
    }

    const duration = isFinite(el.duration) ? el.duration : 0;
    // Short sounds (notifications, autoplay previews) are not music
    if (duration > 0 && duration < 5) return null;
    const title = (meta && meta.title) || '';
    if (!title) return null;
    return {
      playing: !el.paused && !el.ended,
      title,
      artist: (meta && meta.artist) || location.hostname.replace(/^www\./, ''),
      album: (meta && meta.album) || '',
      art: biggestArt(meta),
      position: el.currentTime || 0,
      duration,
      canSeek: duration > 0 && !!el.seekable && el.seekable.length > 0,
      canNext: !!controls().next,
      canPrev: !!controls().prev,
      v: VERSION,
      ...likeState(),
      host: location.hostname,
    };
  }

  function key(s) {
    return [s.playing, s.title, s.artist, s.art, Math.round(s.duration),
            s.canNext, s.canPrev, s.canLike, s.liked].join('|');
  }

  function send(state, why) {
    lastSent = Date.now();
    try {
      chrome.runtime.sendMessage({ type: 'state', state, why, ts: Date.now() });
    } catch {
      // the extension was reloaded/unloaded - the tab survives it silently
      stop();
    }
  }

  function tick() {
    const s = snapshot();
    if (!s) {
      if (lastKey) {
        lastKey = '';
        try { chrome.runtime.sendMessage({ type: 'gone' }); } catch {}
      }
      return;
    }
    const k = key(s);
    if (k !== lastKey) {
      lastKey = k;
      send(s, 'change');
    } else if (Date.now() - lastSent >= HEARTBEAT_MS) {
      // The widget interpolates the position itself; the heartbeat exists so
      // it can see the tab is alive and correct drift after seeks
      send(s, 'beat');
    }
  }

  function stop() {
    if (timer) { clearInterval(timer); timer = null; }
  }

  chrome.runtime.onMessage.addListener((msg, _sender, respond) => {
    // Poll from the background script. Needed because the browser throttles
    // timers in a background tab as soon as it stops making sound: on pause
    // tick() starts firing once a minute. Message handlers are not throttled,
    // so the state is fetched by polling rather than by a heartbeat
    if (msg && msg.cmd === 'ping') {
      const cur = snapshot();
      if (cur) { lastKey = key(cur); send(cur, 'ping'); }
      else if (lastKey) { lastKey = ''; try { chrome.runtime.sendMessage({ type: 'gone' }); } catch {} }
      respond && respond({ ok: true, alive: !!cur });
      return false;
    }

    const el = pickMedia();
    const c = controls();
    if (msg && msg.cmd === 'playpause') {
      // The site's button is more reliable than the element: it also toggles the built-in player
      if (c.play) c.play.click();
      else if (el) { if (el.paused) el.play().catch(() => {}); else el.pause(); }
    } else if (msg && msg.cmd === 'seek' && isFinite(msg.pos)) {
      if (el) {
        try { el.currentTime = Math.max(0, Math.min(msg.pos, el.duration || msg.pos)); } catch {}
      } else {
        const prog = sliderProgress();
        if (prog) seekTo(prog, msg.pos);
      }
    } else if (msg && (msg.cmd === 'next' || msg.cmd === 'prev')) {
      const b = msg.cmd === 'next' ? c.next : c.prev;
      if (b) b.click();
    } else if (msg && msg.cmd === 'like') {
      if (c.like) c.like.click();
    }
    // Report the state after a command right away, without waiting for a tick
    const s = snapshot();
    if (s) { lastKey = key(s); send(s, 'cmd'); }
    if (msg && (msg.cmd === 'next' || msg.cmd === 'prev' || msg.cmd === 'like')) {
      setTimeout(tick, 300);
    }
    respond && respond({ ok: true });
    return false;
  });

  // Navigating to another page silently kills our timer - warn explicitly,
  // otherwise the background script keeps polling the dead tab for a minute
  addEventListener('pagehide', () => {
    stop();
    try { chrome.runtime.sendMessage({ type: 'gone' }); } catch {}
  });

  function start() {
    if (timer) return;
    tick();
    timer = setInterval(tick, TICK_MS);
  }

  start();
})();
