// Collects state from Spotify Web tabs, picks the current one and keeps a
// WebSocket to SpotDock. The port listens on 127.0.0.1 only; nothing goes outside.
const PORT = 45219;
const STALE_MS = 30000;
// How often we ask a tab for its state ourselves. A heartbeat from the tab is
// no good: the browser throttles timers in a background page as soon as it
// goes quiet, and on pause reports arrive once a minute - long enough for the
// widget to fade out
const PULL_MS = 4000;
// A tab can be frozen (memory saving) and not answer the poll - that alone is
// no reason to bury it, so we wait a minute
const SILENCE_MS = 60000;

// "tabId:frameId" -> { state, ts, audible, tabId, windowId, frameId }
const tabs = new Map();

// Who is making sound right now. One query for all tabs, not one per tab
async function refreshAudible() {
  let ids;
  try {
    ids = new Set((await chrome.tabs.query({ audible: true })).map(t => t.id));
  } catch {
    return; // no permission, or the browser is closing - stay silent
  }
  for (const v of tabs.values()) {
    v.audible = ids.has(v.tabId);
  }
}
let ws = null;
let retry = 1000;
let retryTimer = null;
let lastSentKey = '';
let pullTimer = null;

function idOf(sender) {
  return `${sender.tab ? sender.tab.id : 0}:${sender.frameId || 0}`;
}

// Which of two entries wins. Order: a tab making sound, then one that reports
// itself as playing, then - within ONE tab - the main document over an embed,
// and only last the freshness of the report
function better(v, best) {
  if (!best) return true;
  if (!!v.audible !== !!best.audible) return !!v.audible;
  const vp = !!v.state.playing, bp = !!best.state.playing;
  if (vp !== bp) return vp;
  // content.js runs in every frame, and a page with an ad or an embedded
  // player has several. Their entries are separate (tabId:frameId), and they
  // competed for the widget on equal terms with the main document - hence the
  // titles jumping when the video changed in one and the same tab. The audible
  // signal does not help here: it belongs to the whole tab, shared by all its
  // frames
  if (v.tabId === best.tabId && (v.frameId === 0) !== (best.frameId === 0)) {
    return v.frameId === 0;
  }
  return v.ts > best.ts;
}

function current() {
  const now = Date.now();
  let best = null;
  for (const [k, v] of tabs) {
    if (now - v.ts > STALE_MS) { tabs.delete(k); continue; }
    if (better(v, best)) best = v;
  }
  return best;
}

function push(force) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  const cur = current();
  if (!cur) {
    if (lastSentKey !== 'idle') {
      lastSentKey = 'idle';
      ws.send(JSON.stringify({ type: 'idle' }));
    }
    return;
  }
  const s = cur.state;
  // Tab, frame and the number of known entries are for the widget's debug log
  // only: they show who exactly is competing for the display
  const meta = { tab: cur.tabId, frame: cur.frameId, n: tabs.size };
  const key = [s.playing, s.title, s.artist, s.art, Math.round(s.duration), s.canLike, s.liked].join('|');
  if (!force && key === lastSentKey) {
    // the position is still needed regularly - the widget corrects drift by it
    ws.send(JSON.stringify({ type: 'state', ...s, ...meta, ts: Date.now() }));
    return;
  }
  lastSentKey = key;
  ws.send(JSON.stringify({ type: 'state', ...s, ...meta, ts: Date.now() }));
}

// We ask every known tab directly: throttling does not touch message
// delivery, unlike timers inside the page. A tab answers with a normal state
// message, so all that matters here is whether the send failed
async function pull() {
  if (!tabs.size) return;
  // First work out who is making sound and drop the ones that went quiet -
  // there is no point polling or counting them any further
  await refreshAudible();
  // Recompute the display: during the past seconds someone may have gone
  // quiet for good, and the widget should learn about it
  push(false);
  const now = Date.now();
  for (const [k, v] of [...tabs]) {
    try {
      await chrome.tabs.sendMessage(v.tabId, { cmd: 'ping' }, { frameId: v.frameId });
      v.silentSince = 0;
    } catch {
      // The tab is gone - forget it at once; it exists but is silent - give it a minute
      let gone = false;
      try { await chrome.tabs.get(v.tabId); } catch { gone = true; }
      if (!gone) {
        if (!v.silentSince) v.silentSince = now;
        if (now - v.silentSince < SILENCE_MS) continue;
      }
      tabs.delete(k);
      push(true);
    }
  }
}

function startPull() {
  if (pullTimer) return;
  pullTimer = setInterval(pull, PULL_MS);
}

function connect() {
  if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  try {
    ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
  } catch {
    schedule();
    return;
  }
  ws.onopen = () => {
    retry = 1000;
    lastSentKey = '';
    push(true);
  };
  ws.onmessage = ev => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    const cur = current();
    if (!cur || !msg || !msg.cmd) return;
    if (msg.cmd === 'focus') {
      // Reveal the source of the sound: activate the tab and its window.
      // Without drawAttention - that was the taskbar flashing. Windows will
      // not let the browser bring its window to the foreground anyway (it is
      // a background process), so the widget does that: we hand it the tab
      // title, which it uses to find the right window among several
      const target = cur;
      (async () => {
        try { await chrome.tabs.update(target.tabId, { active: true }); } catch {}
        if (target.windowId != null) {
          try { await chrome.windows.update(target.windowId, { focused: true }); } catch {}
        }
        let title = '';
        try { title = (await chrome.tabs.get(target.tabId)).title || ''; } catch {}
        if (ws && ws.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ type: 'focused', title }));
        }
      })();
      return;
    }
    chrome.tabs.sendMessage(cur.tabId, msg, { frameId: cur.frameId }).catch(() => {});
  };
  ws.onclose = () => { ws = null; schedule(); };
  ws.onerror = () => { try { ws.close(); } catch {} };
}

function schedule() {
  if (retryTimer) return;
  // The widget may not be running - reconnect rarely and without panic
  retry = Math.min(retry * 2, 30000);
  retryTimer = setTimeout(() => { retryTimer = null; connect(); }, retry);
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (!msg) return false;
  const id = idOf(sender);
  if (msg.type === 'gone') {
    tabs.delete(id);
    push(true);
  } else if (msg.type === 'state') {
    const prev = tabs.get(id);
    tabs.set(id, {
      state: msg.state,
      ts: Date.now(),
      audible: prev ? prev.audible : false,
      tabId: sender.tab ? sender.tab.id : 0,
      windowId: sender.tab ? sender.tab.windowId : null,
      frameId: sender.frameId || 0,
    });
    connect();
    startPull();
    push(false);
  }
  return false;
});

chrome.tabs.onRemoved.addListener(tabId => {
  for (const k of [...tabs.keys()]) if (k.startsWith(`${tabId}:`)) tabs.delete(k);
  push(true);
});

// Sound appeared or disappeared - learn at once, without waiting for the next
// poll: switching between tabs has to be instant
chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (info.audible === undefined) return;
  let touched = false;
  for (const v of tabs.values()) {
    if (v.tabId !== tabId) continue;
    v.audible = info.audible;
    touched = true;
  }
  if (touched) push(false);
});

// The service worker falls asleep; the alarm wakes it and restores the socket
chrome.alarms.create('keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => { connect(); startPull(); pull(); });
// Content scripts from the manifest only reach tabs opened AFTER installation.
// The user opened Spotify Web earlier - so we inject late ourselves,
// otherwise the extension stays silent until the page is reloaded
async function injectAll() {
  try {
    const list = await chrome.tabs.query({ url: ['https://open.spotify.com/*'] });
    for (const t of list) {
      if (t.id == null) continue;
      chrome.scripting
        .executeScript({ target: { tabId: t.id, allFrames: false }, files: ['content.js'] })
        .catch(() => {}); // service pages forbid injection - that is normal
    }
  } catch {}
}

chrome.runtime.onStartup.addListener(() => { connect(); injectAll(); });
chrome.runtime.onInstalled.addListener(() => { connect(); injectAll(); });
connect();
startPull();
injectAll();
