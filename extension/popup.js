const $ = id => document.getElementById(id);

function fmtSize(b) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; b = b || 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return (i ? b.toFixed(1) : b.toFixed(0)) + ' ' + u[i];
}
function fmtEta(s) {
  if (!isFinite(s) || s <= 0) return '';
  s = Math.round(s);
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), ss = s % 60;
  return h ? `${h}h ${m}m` : m ? `${m}m ${ss}s` : `${ss}s`;
}
function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function render(active, recent) {
  const a = $('active'), r = $('recent');
  a.replaceChildren(); r.replaceChildren();
  $('activeSec').hidden = !active.length;
  $('recentSec').hidden = !recent.length;
  $('empty').hidden = !!(active.length || recent.length);

  for (const j of active) {
    const it = el('div', 'item');
    it.append(el('div', 'name', j.name));
    const x = el('button', 'x', '×'); x.title = 'Annulla';
    x.onclick = () => chrome.runtime.sendMessage({ cmd: 'cancel', id: j.id });
    it.append(x);
    const bar = el('div', 'bar'), fill = el('i');
    if (j.total > 0) fill.style.width = (j.done * 100 / j.total).toFixed(1) + '%';
    bar.append(fill); it.append(bar);
    const pct = j.total > 0 ? Math.floor(j.done * 100 / j.total) + '%  ·  ' : '';
    const meta = j.state === 'starting' ? 'avvio…'
      : `${pct}${fmtSize(j.done)}${j.total > 0 ? ' / ' + fmtSize(j.total) : ''}  ·  ${fmtSize(j.speed)}/s  ·  ${j.conns} conn` +
        (j.total > 0 && j.speed > 0 ? '  ·  ' + fmtEta((j.total - j.done) / j.speed) : '');
    it.append(el('div', 'meta', meta));
    a.append(it);
  }

  for (const j of recent) {
    const it = el('div', 'item' + (j.path && j.state === 'done' ? ' click' : ''));
    it.append(el('div', 'name', j.name || '?'));
    it.append(el('span'));
    let meta;
    if (j.state === 'done') meta = el('div', 'meta st-done', `✓ ${fmtSize(j.total)}${j.avg ? '  ·  media ' + fmtSize(j.avg) + '/s' : ''}  ·  apri cartella`);
    else if (j.state === 'cancelled') meta = el('div', 'meta', 'annullato');
    else meta = el('div', 'meta st-error', '✕ ' + (j.error || 'errore') + ' → passato a Chrome');
    it.append(meta);
    if (j.path && j.state === 'done') it.onclick = () => chrome.runtime.sendMessage({ cmd: 'reveal', path: j.path });
    r.append(it);
  }
}

async function load() {
  const [s, l] = await Promise.all([
    chrome.storage.session.get(['active', 'recent']),
    chrome.storage.local.get(['enabled', 'connections', 'outDir', 'recent'])
  ]);
  $('enabled').checked = l.enabled !== false;
  $('conns').value = String(typeof l.connections === 'number' ? l.connections : 0);
  $('outDir').value = l.outDir || '';
  render(s.active || [], s.recent || l.recent || []);
}

chrome.storage.onChanged.addListener((ch, area) => {
  if (area === 'session') chrome.storage.session.get(['active', 'recent']).then(s => render(s.active || [], s.recent || []));
});
$('enabled').onchange = e => chrome.storage.local.set({ enabled: e.target.checked });
$('conns').onchange = e => chrome.storage.local.set({ connections: +e.target.value });
$('clear').onclick = () => chrome.runtime.sendMessage({ cmd: 'clear' });
$('outDir').onchange = e => chrome.storage.local.set({ outDir: e.target.value.trim().replace(/^"|"$/g, '') });

chrome.runtime.sendMessage({ cmd: 'ping' }, res => {
  const ok = res && res.ok;
  $('dot').className = 'dot ' + (ok ? 'ok' : 'err');
  if (ok) $('outDir').placeholder = res.info.downloads + '  (predefinita)';
  $('status').textContent = ok
    ? `motore pronto · v${res.info.version}`
    : 'motore non raggiungibile: ' + (res ? res.error : chrome.runtime.lastError?.message || 'nessuna risposta');
});

load();
