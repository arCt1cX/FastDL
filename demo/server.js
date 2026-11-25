// Demo server for the README GIF: every connection is limited to 1 MB/s, /stats reports live throughput.
const http = require('http');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const SIZE = 120 * 1024 * 1024, RATE = 1024 * 1024, TICK = 50;
const data = crypto.randomBytes(SIZE);
const page = () => fs.readFileSync(path.join(__dirname, 'index.html'));

let open = 0, served = 0, peakConns = 0;
const samples = []; // [time, servedTotal]

setInterval(() => {
  samples.push([Date.now(), served]);
  while (samples.length > 20) samples.shift();
}, 100);

http.createServer((req, res) => {
  if (req.url.split('?')[0] === '/') { res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }); return res.end(page()); }
  if (req.url === '/stats') {
    const a = samples[Math.max(0, samples.length - 11)], b = samples[samples.length - 1] || a;
    const rate = a && b && b[0] > a[0] ? (b[1] - a[1]) * 1000 / (b[0] - a[0]) : 0;
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    return res.end(JSON.stringify({ conns: open, rate, served, size: SIZE, peakConns }));
  }
  if (req.url === '/reset') { served = 0; peakConns = 0; samples.length = 0; res.writeHead(204); return res.end(); }
  if (!req.url.startsWith('/demo-file.bin')) { res.writeHead(404); return res.end(); }

  let start = 0, end = SIZE - 1, code = 200;
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  if (m) {
    start = +m[1]; if (m[2]) end = Math.min(+m[2], SIZE - 1); code = 206;
    res.setHeader('Content-Range', `bytes ${start}-${end}/${SIZE}`);
  }
  res.writeHead(code, {
    'Content-Length': end - start + 1, 'Accept-Ranges': 'bytes',
    'Content-Type': 'application/octet-stream',
    'Content-Disposition': 'attachment; filename="fastdl-demo-120MB.bin"'
  });
  open++; peakConns = Math.max(peakConns, open);
  let pos = start, closed = false;
  const done = () => { if (!closed) { closed = true; open--; clearInterval(t); } };
  const t = setInterval(() => {
    if (pos > end) { done(); return res.end(); }
    const n = Math.min(Math.floor(RATE * TICK / 1000), end - pos + 1);
    res.write(data.subarray(pos, pos + n)); pos += n; served += n;
  }, TICK);
  req.on('close', done);
}).listen(8765, () => console.log('ready'));
