# Demo / benchmark

The GIF in the main README was recorded with this page.

`server.js` serves a 120 MB random file and limits **every connection to 1 MB/s** — like many slow download sites. The page polls `/stats` and shows what the server is really sending: throughput, open connections, elapsed time.

```powershell
node demo/server.js
# open http://127.0.0.1:8765/ in Chrome with FastDL installed and click Download
# (or http://127.0.0.1:8765/?auto=3000 to start automatically after 3 s)
```

Without FastDL, Chrome uses one connection: 1 MB/s → about 2 minutes.
