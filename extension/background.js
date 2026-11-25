// FastDL - intercetta i download di Chrome e li passa a fastdl.exe (avviato da Chrome solo quando serve).
const HOST = 'com.fastdl.host';
const MIN_SIZE = 2 * 1024 * 1024; // sotto i 2 MB Chrome fa prima da solo

// connections: 0 = auto (il motore aggiunge connessioni finche' la velocita' sale)
const settings = { enabled: true, connections: 0, outDir: '' };
let recent = [];
const jobs = new Map();
const privatePaths = new Map(); // notifiche incognito: solo in memoria, mai salvate
let seq = 0;

const ready = chrome.storage.local.get(['enabled', 'connections', 'outDir', 'recent', 'jobs', 'paused']).then(s => {
  if (typeof s.enabled === 'boolean') settings.enabled = s.enabled;
  if (typeof s.connections === 'number') settings.connections = s.connections;
  if (typeof s.outDir === 'string') settings.outDir = s.outDir;
  if (Array.isArray(s.recent)) recent = s.recent;
  // i download sopravvivono al riavvio di Chrome (gli incognito no: mai salvati su disco).
  // Al riavvio chiediamo al motore com'e' messo ognuno: ancora in corso in background, in pausa, finito...
  const saved = Array.isArray(s.jobs) ? s.jobs : (Array.isArray(s.paused) ? s.paused : []);
  for (const p of saved) {
    const job = { ...p, id: ++seq, state: 'checking', speed: 0, conns: 0 };
    jobs.set(job.id, job);
    refresh(job);
  }
  publish();
});

// fa partire il service worker all'avvio di Chrome, cosi' ritrova subito i download in background
chrome.runtime.onStartup.addListener(() => {});

async function status(path) {
  try { return await chrome.runtime.sendNativeMessage(HOST, { cmd: 'status', path }); }
  catch { return null; }
}

// aggiorna un job senza canale col motore (Chrome e' stato chiuso e riaperto)
async function refresh(job) {
  const st = job.path ? await status(job.path) : { state: 'gone' };
  if (!st || !jobs.has(job.id) || job.port) return;
  const now = Date.now();
  if (st.state === 'running') {
    if (job.state === 'background' && job.lastAt && st.done >= 0) {
      const inst = (st.done - job.done) * 1000 / Math.max(1, now - job.lastAt);
      job.speed = job.speed ? job.speed * 0.5 + inst * 0.5 : inst;
    }
    job.state = 'background';
    if (st.done >= 0) job.done = st.done;
    if (st.total > 0) job.total = st.total;
    if (st.ranged) job.ranged = true;
    job.conns = st.conns || (job.ranged ? job.conns : 1);
    job.lastAt = now;
  } else if (st.state === 'resumable') {
    job.state = 'paused'; job.ranged = true; job.speed = 0;
    if (st.done >= 0) job.done = st.done; if (st.total > 0) job.total = st.total;
  } else if (st.state === 'partial') {
    job.state = 'interrupted'; job.speed = 0;
    if (st.done >= 0) job.done = st.done;
  } else if (st.state === 'done') {
    job.state = 'done'; job.done = job.total; finish(job);
  } else {
    job.state = job.cancelling ? 'cancelled' : 'gone';
    finish(job, true);
  }
  saveJobs();
  publish();
  ensurePolling();
}

let pollTimer = null;
function ensurePolling() {
  const need = [...jobs.values()].some(j => j.state === 'background');
  if (need && !pollTimer) pollTimer = setInterval(() => {
    for (const j of jobs.values()) if (j.state === 'background') refresh(j);
  }, 2000);
  if (!need && pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

chrome.storage.onChanged.addListener((ch, area) => {
  if (area !== 'local') return;
  if (ch.enabled) settings.enabled = ch.enabled.newValue;
  if (ch.connections) settings.connections = ch.connections.newValue;
  if (ch.outDir) settings.outDir = ch.outDir.newValue || '';
});

// Intercettiamo in onDeterminingFilename (non onCreated): arriva PRIMA che Chrome mostri
// "Salva con nome". Per i download che prendiamo non chiamiamo mai suggest(), quindi
// la finestra non compare; per gli altri suggest() lascia proseguire Chrome normalmente.
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  decide(item).then(take => {
    if (!take) { suggest(); return; }
    const url = item.finalUrl || item.url;
    chrome.downloads.cancel(item.id)
      .then(() => chrome.downloads.erase({ id: item.id }))
      .catch(() => {});
    const job = {
      id: ++seq, url, name: baseName(item.filename) || baseName(new URL(url).pathname) || 'download',
      done: 0, total: item.totalBytes || 0, speed: 0, conns: 0, state: 'starting', started: Date.now(),
      incognito: !!item.incognito,
      // tutto quello che serve per riprendere dopo una pausa
      req: { url, referrer: item.referrer || '', mime: item.mime || '', nameHint: baseName(item.filename), outDir: settings.outDir }
    };
    jobs.set(job.id, job);
    run(job);
  }, () => suggest());
  return true; // risposta asincrona
});

async function decide(item) {
  await ready;
  const url = item.finalUrl || item.url;
  if (!settings.enabled || item.state !== 'in_progress') return false;
  if (item.byExtensionId) return false; // avviato da un'estensione (anche i nostri fallback): lascia fare a Chrome
  if (shouldSkip(item)) return false;
  if (!/^https?:\/\//i.test(url)) return false; // blob:, data:, file: -> Chrome
  if (item.totalBytes > 0 && item.totalBytes < MIN_SIZE) return false;
  return true;
}

function baseName(p) { return (p || '').split(/[\\/]/).pop(); }

async function cookiesFor(url, incognito) {
  try {
    const opts = { url };
    if (incognito) { // incognito ha il suo archivio cookie separato
      const stores = await chrome.cookies.getAllCookieStores();
      const inc = stores.find(s => s.id !== '0');
      if (inc) opts.storeId = inc.id;
    }
    const cs = await chrome.cookies.getAll(opts);
    return cs.map(c => c.name + '=' + c.value).join('; ');
  } catch { return ''; }
}

// Avvia (o riprende) un job: il motore trova da solo il file parziale e riparte da li'.
async function run(job) {
  const r = job.req;
  const cookie = await cookiesFor(r.url, job.incognito);
  const resuming = job.state === 'paused';
  job.state = 'starting'; job.cancelled = false; job.pausing = false; job.speed = 0; job.error = '';

  let port;
  try { port = chrome.runtime.connectNative(HOST); }
  catch (e) { if (!resuming) { jobs.delete(job.id); fallback(r.url, job.incognito); } return; }
  job.port = port;

  port.onMessage.addListener(m => {
    if (m.type === 'start') {
      job.name = m.name; job.path = m.path; job.total = m.total; job.ranged = m.ranged; job.state = 'downloading';
      // riscaricato dal sito con un link nuovo: il motore ha ripreso il file di una voce in pausa -> uniscile
      if (m.resumed) for (const o of [...jobs.values()])
        if (o !== job && o.path === m.path && !o.port) jobs.delete(o.id);
      saveJobs();
    } else if (m.type === 'progress') {
      job.done = m.done; job.total = m.total; job.speed = m.speed; job.conns = m.conns;
    } else if (m.type === 'paused') {
      job.state = 'paused'; job.done = m.done; job.total = m.total; job.speed = 0; job.conns = 0;
      try { port.disconnect(); } catch {}
      job.port = null;
      saveJobs();
    } else if (m.type === 'done') {
      job.state = 'done'; job.path = m.path; job.name = baseName(m.path); job.done = job.total = m.total; job.avg = m.avg;
      finish(job);
    } else if (m.type === 'error' && resuming && !job.cancelled) {
      // ripresa fallita (tipico: link scaduto dopo ore/giorni). Il parziale resta: niente voce persa.
      keepPaused(job, m.message);
      try { port.disconnect(); } catch {}
    } else if (m.type === 'error') {
      job.state = job.cancelled ? 'cancelled' : 'error'; job.error = m.message;
      finish(job);
      if (m.fallback && !job.cancelled && !resuming) fallback(r.url, job.incognito);
    }
    publish();
  });

  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError ? chrome.runtime.lastError.message : '';
    if (resuming && !job.cancelled && (job.state === 'starting' || job.state === 'downloading')) {
      keepPaused(job, err || 'processo terminato');
    } else if (job.state === 'starting' || job.state === 'downloading') {
      const neverStarted = job.state === 'starting';
      job.state = job.cancelled ? 'cancelled' : 'error';
      job.error = err || 'processo terminato';
      finish(job);
      // host non installato o crash prima di partire: il download lo fa Chrome come sempre
      if (neverStarted && !job.cancelled && !resuming) fallback(r.url, job.incognito);
    }
    publish();
  });

  port.postMessage({
    cmd: 'download', url: r.url, referrer: r.referrer, cookie,
    userAgent: navigator.userAgent, nameHint: r.nameHint, mime: r.mime,
    connections: settings.connections, outDir: r.outDir
  });
  publish();
}

function control(j, action) {
  return chrome.runtime.sendNativeMessage(HOST, { cmd: 'control', path: j.path, action }).catch(() => {});
}

function keepPaused(job, message) {
  job.port = null; job.speed = 0; job.conns = 0;
  job.state = job.ranged === false && job.total <= 0 ? 'interrupted' : 'paused';
  job.error = /HTTP 4\d\d/.test(message || '')
    ? 'link scaduto o non più valido: riscarica il file dal sito, FastDL riprende da qui'
    : 'ripresa non riuscita (' + message + '): riprova o riscarica dal sito';
  saveJobs();
}

function pause(id) {
  const j = jobs.get(id);
  if (j && j.state === 'background' && j.ranged) { j.pausing = true; control(j, 'pause'); publish(); return; }
  if (!j || !j.port || j.state !== 'downloading' || !j.ranged) return;
  j.pausing = true;
  try { j.port.postMessage({ cmd: 'pause' }); } catch {}
}

async function resume(id) {
  const j = jobs.get(id);
  if (!j) return;
  if (j.state === 'interrupted') {
    // server senza download a pezzi: non si puo' riprendere, si ricomincia da capo
    if (j.path) await chrome.runtime.sendNativeMessage(HOST, { cmd: 'discard', path: j.path }).catch(() => {});
    j.done = 0; j.state = 'paused';
  }
  if (j.state === 'paused') run(j);
}

function cancel(id) {
  const j = jobs.get(id);
  if (!j) return;
  if (j.state === 'background') { j.cancelling = true; control(j, 'cancel'); publish(); return; }
  if (j.state === 'paused' || j.state === 'interrupted') {
    // nessun processo attivo: chiediamo al motore di cancellare il file parziale
    if (j.path) chrome.runtime.sendNativeMessage(HOST, { cmd: 'discard', path: j.path }).catch(() => {});
    j.state = 'cancelled';
    finish(j);
    publish();
    return;
  }
  if (j.port) { j.cancelled = true; try { j.port.postMessage({ cmd: 'cancel' }); } catch {} }
}

function saveJobs() {
  const list = [...jobs.values()]
    .filter(j => !j.incognito && j.path && j.req)
    .map(({ url, name, path, done, total, ranged, started, req }) => ({ url, name, path, done, total, ranged, started, req }));
  chrome.storage.local.set({ jobs: list, paused: [] });
}

function fallback(url, incognito) {
  // chrome.downloads.download parte sempre dal profilo normale: in incognito
  // perderebbe i cookie privati, quindi apriamo il link in una scheda incognito.
  if (incognito) {
    chrome.windows.getAll({ windowTypes: ['normal'] }).then(ws => {
      const w = ws.find(x => x.incognito);
      if (!w) return;
      skipUrls.set(url, Date.now());
      chrome.tabs.create({ windowId: w.id, url, active: false }).then(t => {
        // la scheda serve solo a far partire il download: chiudila dopo
        setTimeout(() => chrome.tabs.remove(t.id).catch(() => {}), 15000);
      });
    }).catch(() => {});
    return;
  }
  chrome.downloads.download({ url }).catch(() => {});
}

// URL rimandati a Chrome dal fallback incognito: il download che generano non va reintercettato
const skipUrls = new Map();
function shouldSkip(item) {
  const now = Date.now();
  for (const [u, t] of skipUrls) if (now - t > 60000) skipUrls.delete(u);
  for (const u of [item.url, item.finalUrl]) if (u && skipUrls.has(u)) { skipUrls.delete(u); return true; }
  return false;
}

function finish(job, silent) {
  if (!jobs.has(job.id)) return;
  jobs.delete(job.id);
  try { job.port && job.port.disconnect(); } catch {}
  saveJobs();
  ensurePolling();
  if (silent && job.state === 'gone') return; // file sparito (cancellato a mano): niente voce nei recenti
  const r = { id: job.id + '-' + job.started, name: job.name, path: job.path, total: job.total, state: job.state, error: job.error, avg: job.avg, at: Date.now() };
  if (job.incognito) {
    if (job.path) privatePaths.set('fdl:' + r.id, job.path);
  } else {
    recent = [r, ...recent].slice(0, 20);
    chrome.storage.local.set({ recent });
  }
  if (job.state === 'done') {
    chrome.notifications.create('fdl:' + r.id, {
      type: 'basic', iconUrl: 'icons/128.png', title: 'Download completato',
      // in incognito niente nome file: il centro notifiche di Windows se lo ricorderebbe
      message: (job.incognito ? 'Download privato' : job.name) + '  ·  ' + fmtSize(job.total) + (job.avg ? '  ·  ' + fmtSize(job.avg) + '/s' : ''),
      silent: true
    });
  }
}

chrome.notifications.onClicked.addListener(id => {
  if (!id.startsWith('fdl:')) return;
  const r = recent.find(x => 'fdl:' + x.id === id);
  const path = (r && r.path) || privatePaths.get(id);
  if (path) reveal(path);
  privatePaths.delete(id);
  chrome.notifications.clear(id);
});

function reveal(path) {
  chrome.runtime.sendNativeMessage(HOST, { cmd: 'reveal', path }).catch(() => {});
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  ready.then(() => {
    if (msg.cmd === 'cancel') cancel(msg.id);
    else if (msg.cmd === 'pause') pause(msg.id);
    else if (msg.cmd === 'resume') resume(msg.id);
    else if (msg.cmd === 'reveal') reveal(msg.path);
    else if (msg.cmd === 'removeRecent') {
      // toglie solo la voce dalla lista: il file scaricato resta dov'e'
      recent = recent.filter(r => r.id !== msg.id);
      chrome.storage.local.set({ recent }); publish();
    } else if (msg.cmd === 'clear') {
      recent = []; chrome.storage.local.set({ recent }); publish();
    }
  });
  if (msg.cmd === 'ping') {
    chrome.runtime.sendNativeMessage(HOST, { cmd: 'ping' })
      .then(r => reply({ ok: true, info: r }))
      .catch(e => reply({ ok: false, error: String(e && e.message || e) }));
    return true;
  }
});

let pubTimer = null;
function publish() {
  if (pubTimer) return;
  pubTimer = setTimeout(() => {
    pubTimer = null;
    const active = [...jobs.values()].map(j => ({
      id: j.id, name: j.incognito ? '🕶 download privato' : j.name,
      done: j.done, total: j.total, speed: j.speed, conns: j.conns, state: j.state,
      ranged: !!j.ranged, pausing: !!j.pausing, cancelling: !!j.cancelling, error: j.error || ''
    }));
    chrome.storage.session.set({ active, recent }).catch(() => {});

    const running = active.filter(j => j.state !== 'paused' && j.state !== 'interrupted');
    if (!running.length) {
      chrome.action.setBadgeBackgroundColor({ color: '#6b7280' });
      chrome.action.setBadgeText({ text: active.length ? '❚❚' : '' });
      chrome.action.setTitle({ title: 'FastDL' + (active.length ? ` – ${active.length} in pausa` : '') + (settings.enabled ? '' : ' (disattivato)') });
      return;
    }
    let done = 0, total = 0, speed = 0;
    for (const j of running) { done += j.done; total += j.total > 0 ? j.total : 0; speed += j.speed || 0; }
    const pct = total > 0 ? Math.floor(done * 100 / total) : null;
    chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
    chrome.action.setBadgeTextColor && chrome.action.setBadgeTextColor({ color: '#ffffff' });
    chrome.action.setBadgeText({ text: pct === null ? '↓' : pct + '%' });
    chrome.action.setTitle({ title: `FastDL – ${running.length} download · ${fmtSize(speed)}/s` });
  }, 250);
}

function fmtSize(b) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; b = b || 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return (i ? b.toFixed(1) : b.toFixed(0)) + ' ' + u[i];
}
