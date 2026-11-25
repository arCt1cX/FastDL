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

  const send = msg => chrome.runtime.sendMessage(msg);
  const btn = (label, title, cls, fn) => {
    const b = el('button', 'x ' + cls, label); b.title = title;
    b.onclick = e => { e.stopPropagation(); fn(); };
    return b;
  };

  for (const j of active) {
    const paused = j.state === 'paused';
    const it = el('div', 'item' + (paused ? ' paused' : ''));
    it.append(el('div', 'name', j.name));
    const tools = el('div', 'tools');
    if (paused) tools.append(btn('▶', 'Riprendi', 'play', () => send({ cmd: 'resume', id: j.id })));
    else if (j.state === 'downloading' && j.ranged && !j.pausing)
      tools.append(btn('❚❚', 'Pausa', 'pause', () => send({ cmd: 'pause', id: j.id })));
    tools.append(btn('×', paused ? 'Annulla e cancella il file parziale' : 'Annulla e cancella', 'del', () => send({ cmd: 'cancel', id: j.id })));
    it.append(tools);
    const bar = el('div', 'bar'), fill = el('i');
    if (j.total > 0) fill.style.width = (j.done * 100 / j.total).toFixed(1) + '%';
    bar.append(fill); it.append(bar);
    const pct = j.total > 0 ? Math.floor(j.done * 100 / j.total) + '%  ·  ' : '';
    const sizes = `${fmtSize(j.done)}${j.total > 0 ? ' / ' + fmtSize(j.total) : ''}`;
    let meta;
    if (paused) meta = `in pausa  ·  ${pct}${sizes}`;
    else if (j.pausing) meta = 'metto in pausa…';
    else if (j.state === 'starting') meta = 'avvio…';
    else meta = `${pct}${sizes}  ·  ${fmtSize(j.speed)}/s  ·  ${j.conns} conn` +
      (j.total > 0 && j.speed > 0 ? '  ·  ' + fmtEta((j.total - j.done) / j.speed) : '');
    it.append(el('div', 'meta', meta));
    a.append(it);
  }

  for (const j of recent) {
    const it = el('div', 'item' + (j.path && j.state === 'done' ? ' click' : ''));
    it.append(el('div', 'name', j.name || '?'));
    it.append(btn('×', 'Togli dalla lista (il file resta)', 'del', () => send({ cmd: 'removeRecent', id: j.id })));
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
