<p align="center">
  <img src="extension/icons/128.png" width="96" alt="FastDL">
</p>

<h1 align="center">FastDL</h1>

<p align="center">
  <b>A 26&nbsp;KB download accelerator for Chrome that never stays open.</b><br>
  No window. No tray icon. No background process. Just faster downloads.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/arCt1cX/FastDL?label=download" alt="release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4" alt="windows">
  <img src="https://img.shields.io/badge/browser-Chrome%20%7C%20Edge%20%7C%20Brave-4285f4" alt="browsers">
  <img src="https://img.shields.io/badge/size-26%20KB-success" alt="size">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="license"></a>
</p>

<p align="center"><a href="#italiano">🇮🇹 Italiano più sotto</a></p>

---

## Why

Classic download managers (FDM, IDM, …) are fast, but they are **heavy, always running, and always in your face**: a big app, a tray icon, popups, autostart.

FastDL takes the one thing that makes them fast — **splitting a file across many parallel connections** — and drops everything else.

| | FastDL | Typical download manager |
|---|---|---|
| Size on disk | **26 KB** engine + 20 KB extension | 40–100 MB |
| RAM when idle | **0** (not running) | 50–150 MB, always |
| Tray icon / autostart | **none** | yes |
| Windows that pop up | **none** | progress dialogs, "download complete" windows |
| Setup | double-click + load extension | installer, browser integration, settings |
| Price / source | **free, MIT, ~1300 lines** | often paid / closed |

## How it works

```
 You click a download in Chrome
          │
          ▼
 FastDL extension (Manifest V3)
   • cancels Chrome's single-connection download
   • passes URL + cookies + referrer to the engine
          │  Chrome Native Messaging
          ▼
 fastdl.exe  ← started by Chrome *only now*
   • N parallel HTTP Range connections
   • work stealing: fast connections take over the rest of slow ones
   • sparse pre-allocated file, resume after crash
          │
          ▼
 File lands in your Downloads → fastdl.exe exits
```

The trick that removes the "always open" part is **Chrome Native Messaging**: Chrome itself launches the engine when a download starts, and the engine quits when it's done. Nothing waits in the background.

Progress is shown as a badge on the extension icon (`42%`), and a quiet notification appears when the file is ready (click it → opens the folder).

## Benchmarks

Measured on a ~100 Mbit/s home line. Same file, hashes verified identical.

| Server | 1 connection (like Chrome) | FastDL | Speed-up |
|---|---|---|---|
| Throttled server (1 MB/s per connection) | 0.93 MB/s | **12.8 MB/s** (16 conn) | **~14×** |
| Fast CDN (thinkbroadband 50 MB) | 9.6 MB/s | 12.1 MB/s (auto → 8 conn) | 1.25× (line is maxed) |

**Honest note:** no software can go faster than your internet line. FastDL shines when the *server* is the bottleneck (per-connection throttling, distant servers, lossy links). When your line is already saturated, it simply gets you to 100% of it.

## Features

- ⚡ **Multi-connection** downloads with dynamic work stealing (no slow "last segment" tail)
- 🎛️ **Auto connections** (default): starts with 4, doubles while speed keeps improving, stops when it doesn't (max 32). Fast servers get fewer connections, throttled ones get more
- 🛡️ **Polite by design**: if a server refuses extra connections, FastDL backs off instead of hammering it
- 🔁 **Resume**: interrupted downloads continue where they stopped
- 🍪 **Works with logins**: cookies, referrer and user-agent are forwarded from the browser
- 🕶️ **Incognito support**: uses the incognito cookie jar, never stores private downloads in history, no file names in notifications
- ↩️ **Automatic fallback**: small files (< 2 MB), `blob:` links, one-time links or anything that fails is handed back to Chrome — you never lose a download
- 📁 **Custom folder** (default: your Windows Downloads folder)
- 🧰 **CLI** included: `fastdl <url> [-o folder|file] [-c connections]`
- Works in **Chrome, Edge and Brave**

## Install

1. Download the latest **[release zip](../../releases/latest)** and extract it anywhere.
2. Double-click **`Installa.cmd`** (installs into `%USERPROFILE%\FastDL`, no admin needed).
3. Chrome opens `chrome://extensions`:
   - enable **Developer mode** (top right)
   - drag the **`extension`** folder from `%USERPROFILE%\FastDL` onto the page
     (or *Load unpacked* → select `%USERPROFILE%\FastDL\extension`)
4. Click the FastDL icon: the dot at the bottom must be **green**.

Done. Every download above 2 MB now goes through FastDL automatically.

> **Incognito:** in `chrome://extensions` → FastDL → *Details* → enable **Allow in Incognito**.

> **Red dot / "native messaging host not found"?** Run `Registra.cmd` from `%USERPROFILE%\FastDL` (it re-registers the engine for the current user).

> **Free Download Manager / IDM installed?** Disable their browser extension, otherwise both will try to grab the same download.

### Uninstall
Run `Disinstalla.cmd`, remove the extension from `chrome://extensions`, delete `%USERPROFILE%\FastDL`.

## FAQ

**Why not always use 32 connections?**
More connections only help when the server throttles each connection. Otherwise they add handshakes, can get your IP temporarily blocked, and starve other apps on your network (video calls, games). That's why the default is *auto*.

**Is it safe?** The engine only talks to the FastDL extension (Chrome enforces this via `allowed_origins`), only writes inside your download folder (or the one you pick), and the whole thing is ~1300 readable lines. No telemetry, no network calls other than the download itself.

**Windows SmartScreen / antivirus complains.** The exe is not code-signed (certificates cost money). You can build it yourself from source in 2 seconds — see below.

**What if I close Chrome mid-download?** The engine is a separate process and keeps a resume file next to the download; if it gets interrupted, downloading the same file again resumes from where it stopped.

## Build from source

No SDK needed — it uses the C# compiler that ships with every Windows 10/11:

```powershell
.\build.ps1          # → dist\fastdl.exe + dist\extension
.\dist\Installa.cmd
```

Project layout:

```
src/FastDL.cs        engine: HTTP Range, work stealing, resume, native messaging host, CLI
extension/           Chrome MV3 extension: interception, badge, popup, notifications
install.ps1          per-user install + native messaging registration (Chrome, Edge, Brave)
build.ps1            build script
```

## Roadmap

- [ ] **Multi-CDN**: spread connections across all IPs a hostname resolves to (each edge server has its own limit)
- [ ] **Bandwidth bonding**: use Wi-Fi + Ethernet + phone hotspot at the same time on one file
- [ ] **Mirrors / Metalink**: download the same file from several mirrors at once
- [ ] Chrome Web Store release (no developer mode needed)
- [ ] English / multi-language UI (currently Italian)
- [ ] Pause / resume buttons in the popup
- [ ] Firefox support

PRs welcome!

## License

[MIT](LICENSE)

---

<a id="italiano"></a>

## 🇮🇹 Italiano

**FastDL** è un acceleratore di download per Chrome da 26 KB che **non resta mai aperto**: niente finestre, niente icona nelle icone nascoste, niente processi in background.

Quando scarichi qualcosa, l'estensione lo passa al motore `fastdl.exe`, che Chrome avvia *solo in quel momento*. Il motore scarica con più connessioni in parallelo e poi si chiude da solo.

### Installazione
1. Scarica lo **[zip dell'ultima release](../../releases/latest)** ed estrailo.
2. Doppio clic su **`Installa.cmd`** (non servono permessi di amministratore).
3. In `chrome://extensions` attiva **Modalità sviluppatore** e trascina la cartella **`extension`** (da `%USERPROFILE%\FastDL`) dentro la pagina.
4. Clicca l'icona FastDL: il pallino in basso deve essere **verde**. Se è rosso, lancia `Registra.cmd`.

Per l'incognito: `chrome://extensions` → FastDL → *Dettagli* → **Consenti in modalità di navigazione in incognito**.

### Cosa fa
- download multi-connessione con connessioni **automatiche** (oppure 4/8/16/32 fisse)
- ripresa dopo un'interruzione, cookie e login del browser, cartella personalizzabile
- se qualcosa non va, il download torna a Chrome da solo
- in incognito non salva lo storico e non mette il nome del file nelle notifiche
