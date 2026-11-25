// FastDL - intercetta i download di Chrome e li passa a fastdl.exe (avviato da Chrome solo quando serve).
const HOST = 'com.fastdl.host';
const MIN_SIZE = 2 * 1024 * 1024; // sotto i 2 MB Chrome fa prima da solo

// connections: 0 = auto (il motore aggiunge connessioni finche' la velocita' sale)
const settings = { enabled: true, connections: 0, outDir: '' };
let recent = [];
const jobs = new Map();
const privatePaths = new Map(); // notifiche incognito: solo in memoria, mai salvate
let seq = 0;

const ready = chrome.storage.local.get(['enabled', 'connections', 'outDir', 'recent']).then(s => {
  if (typeof s.enabled === 'boolean') settings.enabled = s.enabled;
  if (typeof s.connections === 'number') settings.connections = s.connections;
  if (typeof s.outDir === 'string') settings.outDir = s.outDir;
  if (Array.isArray(s.recent)) recent = s.recent;
  publish();
});

chrome.storage.onChanged.addListener((ch, area) => {
  if (area !== 'local') return;
  if (ch.enabled) settings.enabled = ch.enabled.newValue;
  if (ch.connections) settings.connections = ch.connections.newValue;
  if (ch.outDir) settings.outDir = ch.outDir.newValue || '';
});

chrome.downloads.onCreated.addListener(async item => {
  await ready;
  const url = item.finalUrl || item.url;
  if (!settings.enabled || item.state !== 'in_progress') return;
  if (item.byExtensionId) return; // avviato da un'estensione (anche i nostri fallback): lascia fare a Chrome
  if (shouldSkip(item)) return;
  if (!/^https?:\/\//i.test(url)) return; // blob:, data:, file: -> Chrome
  if (item.totalBytes > 0 && item.totalBytes < MIN_SIZE) return;

  try { await chrome.downloads.cancel(item.id); } catch {}
  try { await chrome.downloads.erase({ id: item.id }); } catch {}
  start(url, item);
});

function baseName(p) { return (p || '').split(/[\\/]/).pop(); }

async function start(url, item) {
  let cookie = '';
  try {
    // incognito ha il suo archivio cookie separato
    const opts = { url };
    if (item.incognito) {
      const stores = await chrome.cookies.getAllCookieStores();
      const inc = stores.find(s => s.id !== '0');
      if (inc) opts.storeId = inc.id;
    }
    const cs = await chrome.cookies.getAll(opts);
    cookie = cs.map(c => c.name + '=' + c.value).join('; ');
  } catch {}

  const job = {
    id: ++seq, url, name: baseName(item.filename) || baseName(new URL(url).pathname) || 'download',
    done: 0, total: item.totalBytes || 0, speed: 0, conns: 0, state: 'starting', started: Date.now(),
    incognito: !!item.incognito
  };
  jobs.set(job.id, job);

  let port;
  try { port = chrome.runtime.connectNative(HOST); }
  catch (e) { jobs.delete(job.id); fallback(url); return; }
  job.port = port;

  port.onMessage.addListener(m => {
    if (m.type === 'start') {
      job.name = m.name; job.path = m.path; job.total = m.total; job.ranged = m.ranged; job.state = 'downloading';
    } else if (m.type === 'progress') {
      job.done = m.done; job.total = m.total; job.speed = m.speed; job.conns = m.conns;
    } else if (m.type === 'done') {
      job.state = 'done'; job.path = m.path; job.name = baseName(m.path); job.done = job.total = m.total; job.avg = m.avg;
      finish(job);
    } else if (m.type === 'error') {
      job.state = job.cancelled ? 'cancelled' : 'error'; job.error = m.message;
      finish(job);
      if (m.fallback && !job.cancelled) fallback(url, job.incognito);
    }
    publish();
  });

  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError ? chrome.runtime.lastError.message : '';
    if (job.state === 'starting' || job.state === 'downloading') {
      const neverStarted = job.state === 'starting';
      job.state = job.cancelled ? 'cancelled' : 'error';
      job.error = err || 'processo terminato';
      finish(job);
      // host non installato o crash prima di partire: il download lo fa Chrome come sempre
      if (neverStarted && !job.cancelled) fallback(url, job.incognito);
    }
    publish();
  });

  port.postMessage({
    cmd: 'download', url, referrer: item.referrer || '', cookie,
    userAgent: navigator.userAgent, nameHint: baseName(item.filename), mime: item.mime || '',
    connections: settings.connections, outDir: settings.outDir
  });
  publish();
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

function finish(job) {
  if (!jobs.has(job.id)) return;
  jobs.delete(job.id);
  try { job.port && job.port.disconnect(); } catch {}
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
  if (msg.cmd === 'cancel') {
    const j = jobs.get(msg.id);
    if (j && j.port) { j.cancelled = true; try { j.port.postMessage({ cmd: 'cancel' }); } catch {} }
  } else if (msg.cmd === 'reveal') {
    reveal(msg.path);
  } else if (msg.cmd === 'clear') {
    recent = []; chrome.storage.local.set({ recent }); publish();
  } else if (msg.cmd === 'ping') {
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
      done: j.done, total: j.total, speed: j.speed, conns: j.conns, state: j.state
    }));
    chrome.storage.session.set({ active, recent }).catch(() => {});

    if (!active.length) {
      chrome.action.setBadgeText({ text: '' });
      chrome.action.setTitle({ title: 'FastDL' + (settings.enabled ? '' : ' (in pausa)') });
      return;
    }
    let done = 0, total = 0, speed = 0;
    for (const j of active) { done += j.done; total += j.total > 0 ? j.total : 0; speed += j.speed || 0; }
    const pct = total > 0 ? Math.floor(done * 100 / total) : null;
    chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
    chrome.action.setBadgeTextColor && chrome.action.setBadgeTextColor({ color: '#ffffff' });
    chrome.action.setBadgeText({ text: pct === null ? '↓' : pct + '%' });
    chrome.action.setTitle({ title: `FastDL – ${active.length} download · ${fmtSize(speed)}/s` });
  }, 250);
}

function fmtSize(b) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; b = b || 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return (i ? b.toFixed(1) : b.toFixed(0)) + ' ' + u[i];
}
