// FastDL - download manager multi-connessione, senza finestre e senza processi in background.
// Compilato col csc di .NET Framework incluso in Windows (C# 5). Vedi build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("FastDL")]
[assembly: System.Reflection.AssemblyProduct("FastDL")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]

namespace FastDL
{
    class Segment
    {
        public long Start, Pos, End; // [Pos, End) ancora da scaricare
        public bool Busy;
    }

    class Job
    {
        public string Url, Referrer, Cookie, UserAgent, NameHint, Mime, OutDir, OutFile;
        public int Connections = 16;
        public bool FromBrowser;
    }

    abstract class Reporter
    {
        public virtual void Started(string name, string path, long total, bool ranged, bool resumed) { }
        public virtual void Progress(long done, long total, double speed, int conns) { }
        public virtual void Done(string path, long total, double seconds, double avg) { }
        public virtual void Failed(string msg, bool fallback) { }
        public virtual void Paused(long done, long total) { }
    }

    static class Win
    {
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref byte inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

        public static string DownloadsDir()
        {
            try
            {
                IntPtr p;
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // File sparse: evita che NTFS riempia di zeri GB di file quando un segmento scrive in fondo.
        public static void SetSparse(SafeFileHandle h, bool on)
        {
            try { byte b = (byte)(on ? 1 : 0); int r; DeviceIoControl(h, 0x000900C4, ref b, 1, IntPtr.Zero, 0, out r, IntPtr.Zero); }
            catch { }
        }
    }

    class Downloader
    {
        const int BufSize = 256 * 1024;
        const long MinSplit = 256 * 1024; // non divide segmenti con meno di 512 KB rimanenti
        const int MaxAttempts = 6;

        readonly Job job;
        readonly Reporter rep;
        readonly object sync = new object();
        readonly List<Segment> segs = new List<Segment>();
        long total = -1, singleDone;
        string finalPath, partPath, statePath;
        FileStream lockFile;
        volatile bool cancelled, rangeBroken, pausing;
        int active, retired;
        volatile string lastError = "";

        public bool DeleteOnCancel;
        static readonly bool Dbg = Environment.GetEnvironmentVariable("FASTDL_DEBUG") == "1";
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static void Log(string s) { if (Dbg) Console.Error.WriteLine("[" + Clock.ElapsedMilliseconds.ToString().PadLeft(6) + "] T" + Thread.CurrentThread.ManagedThreadId + " " + s); }

        public Downloader(Job j, Reporter r) { job = j; rep = r; }
        public void Cancel() { cancelled = true; }
        // pausa = stop mantenendo .fdl + .fdl.state: un nuovo download dello stesso file riprende da qui
        public void Pause() { pausing = true; cancelled = true; }

        HttpWebRequest MakeReq(long from, long to)
        {
            var r = (HttpWebRequest)WebRequest.Create(job.Url);
            r.Method = "GET";
            r.AllowAutoRedirect = true;
            r.MaximumAutomaticRedirections = 15;
            r.Timeout = from > 0 ? 15000 : 30000;
            r.ReadWriteTimeout = 30000;
            r.AutomaticDecompression = DecompressionMethods.None;
            r.KeepAlive = true;
            r.Accept = "*/*";
            r.UserAgent = string.IsNullOrEmpty(job.UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
                : job.UserAgent;
            if (!string.IsNullOrEmpty(job.Referrer)) r.Referer = job.Referrer;
            if (!string.IsNullOrEmpty(job.Cookie)) r.Headers["Cookie"] = job.Cookie;
            if (from >= 0) { if (to >= 0) r.AddRange(from, to); else r.AddRange(from); }
            return r;
        }

        static HttpWebResponse Send(HttpWebRequest r)
        {
            try { return (HttpWebResponse)r.GetResponse(); }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp != null) return resp;
                throw;
            }
        }

        public bool Run()
        {
            HttpWebRequest req0 = MakeReq(0, -1);
            HttpWebResponse r0;
            try { r0 = Send(req0); }
            catch (Exception e) { rep.Failed("Connessione fallita: " + e.Message, true); return false; }

            int code = (int)r0.StatusCode;
            if (code >= 400) { req0.Abort(); rep.Failed("Il server ha risposto HTTP " + code, true); return false; }

            string ctype = r0.ContentType ?? "";
            if (job.FromBrowser && ctype.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                && !(job.Mime ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                req0.Abort();
                rep.Failed("Il server ha dato una pagina web invece del file", true);
                return false;
            }

            bool ranged = false;
            if (code == 206)
            {
                var m = Regex.Match(r0.Headers["Content-Range"] ?? "", @"bytes\s+(\d+)-(\d+)/(\d+)");
                if (m.Success && m.Groups[1].Value == "0") { total = long.Parse(m.Groups[3].Value); ranged = total > 0; }
            }
            if (!ranged) total = r0.ContentLength;
            // Alcuni server rispondono 200 (file intero) a "bytes=0-" ma accettano i Range veri:
            // proviamo un byte a meta' file prima di arrenderci alla connessione singola.
            if (!ranged && code == 200 && total > 4 * MinSplit) ranged = ProbeRanges(total);
            Program.HostLog("risposta " + code + " | dimensione " + total + " | multi-connessione " + (ranged ? "si" : "no"));

            try { ResolvePaths(PickName(r0, ctype), ranged); }
            catch (Exception e) { req0.Abort(); rep.Failed("Impossibile creare il file: " + e.Message, true); return false; }

            bool resumed = segs.Count > 0;
            rep.Started(Path.GetFileName(finalPath), finalPath, total, ranged, resumed);

            if (!ranged) return RunSingle(req0, r0);

            if (resumed) { req0.Abort(); return RunMulti(null, null); }

            try
            {
                using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    Win.SetSparse(fs.SafeFileHandle, true);
                    fs.SetLength(total);
                }
            }
            catch (Exception e) { req0.Abort(); Cleanup(true); rep.Failed("Impossibile creare il file: " + e.Message, true); return false; }

            segs.Add(new Segment { Start = 0, Pos = 0, End = total, Busy = true });
            SaveState();
            return RunMulti(req0, r0);
        }

        bool ProbeRanges(long len)
        {
            HttpWebRequest r = null;
            try
            {
                long mid = len / 2;
                r = MakeReq(mid, mid);
                var resp = Send(r);
                var m = Regex.Match(resp.Headers["Content-Range"] ?? "", @"bytes\s+(\d+)-(\d+)/(\d+)");
                bool ok = (int)resp.StatusCode == 206 && m.Success && long.Parse(m.Groups[1].Value) == mid
                          && long.Parse(m.Groups[3].Value) == len;
                return ok;
            }
            catch { return false; }
            finally { if (r != null) try { r.Abort(); } catch { } }
        }

        // File di controllo: l'estensione puo' mettere in pausa / annullare anche un motore
        // rimasto in background dopo la chiusura di Chrome (non ha piu' il canale con lui).
        void CheckControl(bool canPause)
        {
            string ctl = partPath + ".ctl";
            try
            {
                if (!File.Exists(ctl)) return;
                string a = File.ReadAllText(ctl).Trim();
                File.Delete(ctl);
                if (a == "cancel") Cancel();
                else if (a == "pause" && canPause) Pause();
            }
            catch { }
        }

        static void Hide(string p) { try { File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.Hidden); } catch { } }

        // ---------- nome / percorsi / stato ----------

        string PickName(HttpWebResponse r, string ctype)
        {
            string n = null;
            string cd = r.Headers["Content-Disposition"];
            if (!string.IsNullOrEmpty(cd))
            {
                var m = Regex.Match(cd, @"filename\*\s*=\s*([^']*)'[^']*'([^;]+)", RegexOptions.IgnoreCase);
                if (m.Success) { try { n = Uri.UnescapeDataString(m.Groups[2].Value.Trim().Trim('"')); } catch { } }
                if (string.IsNullOrEmpty(n))
                {
                    m = Regex.Match(cd, "filename\\s*=\\s*(?:\"([^\"]*)\"|([^;]+))", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        n = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                        n = FixLatin1(n);
                    }
                }
            }
            if (string.IsNullOrEmpty(n)) n = job.NameHint;
            if (string.IsNullOrEmpty(n))
            {
                try { n = Uri.UnescapeDataString(Path.GetFileName(r.ResponseUri.AbsolutePath)); } catch { }
            }
            if (string.IsNullOrEmpty(n)) n = "download";
            n = Sanitize(n);
            if (Path.GetExtension(n) == "")
            {
                string ext = ExtFromMime(ctype);
                if (ext != null) n += ext;
            }
            return n;
        }

        static string FixLatin1(string s)
        {
            try
            {
                byte[] b = Encoding.GetEncoding(28591).GetBytes(s);
                string u = new UTF8Encoding(false, true).GetString(b);
                return u;
            }
            catch { return s; }
        }

        static string ExtFromMime(string ct)
        {
            ct = (ct ?? "").ToLowerInvariant();
            if (ct.Contains("zip")) return ".zip";
            if (ct.Contains("pdf")) return ".pdf";
            if (ct.Contains("x-msdownload") || ct.Contains("x-msdos-program")) return ".exe";
            if (ct.Contains("x-7z")) return ".7z";
            if (ct.Contains("x-rar") || ct.Contains("vnd.rar")) return ".rar";
            if (ct.Contains("video/mp4")) return ".mp4";
            if (ct.Contains("audio/mpeg")) return ".mp3";
            if (ct.Contains("x-iso9660")) return ".iso";
            return null;
        }

        static string Sanitize(string n)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimEnd('.', ' ');
            if (n.Length > 180)
            {
                string ext = Path.GetExtension(n);
                if (ext.Length > 20) ext = "";
                n = n.Substring(0, 180 - ext.Length) + ext;
            }
            if (n.Length == 0) n = "download";
            return n;
        }

        void ResolvePaths(string name, bool ranged)
        {
            string dir;
            if (!string.IsNullOrEmpty(job.OutFile))
            {
                string full = Path.GetFullPath(job.OutFile);
                dir = Path.GetDirectoryName(full);
                name = Path.GetFileName(full);
            }
            else dir = string.IsNullOrEmpty(job.OutDir) ? Win.DownloadsDir() : Path.GetFullPath(job.OutDir);
            Directory.CreateDirectory(dir);

            string bn = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 0; i < 10000; i++)
            {
                string cand = Path.Combine(dir, i == 0 ? name : bn + " (" + i + ")" + ext);
                string part = cand + ".fdl", st = cand + ".fdl.state";
                bool hasPart = File.Exists(part);
                if (File.Exists(cand) && !hasPart) continue;
                if (!TryLock(cand)) continue; // un altro FastDL sta usando questo nome
                if (hasPart)
                {
                    if (ranged && File.Exists(st) && TryLoadState(st, part)) { Set(cand); return; }
                    ReleaseLock();
                    continue;
                }
                Set(cand);
                return;
            }
            throw new IOException("nessun nome libero");
        }

        void Set(string cand) { finalPath = cand; partPath = cand + ".fdl"; statePath = cand + ".fdl.state"; }

        bool TryLock(string cand)
        {
            try
            {
                lockFile = new FileStream(cand + ".fdl.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                Hide(cand + ".fdl.lock");
                return true;
            }
            catch { lockFile = null; return false; }
        }

        void ReleaseLock()
        {
            if (lockFile != null) { try { lockFile.Dispose(); } catch { } lockFile = null; }
        }

        bool TryLoadState(string st, string part)
        {
            try
            {
                string[] lines = File.ReadAllLines(st);
                if (lines.Length < 3 || lines[0] != "FDL1") return false;
                if (long.Parse(lines[1]) != total) return false;
                if (new FileInfo(part).Length != total) return false;
                var list = new List<Segment>();
                for (int i = 2; i < lines.Length; i++)
                {
                    if (lines[i].Trim().Length == 0) continue;
                    string[] p = lines[i].Split(' ');
                    var s = new Segment { Start = long.Parse(p[0]), Pos = long.Parse(p[1]), End = long.Parse(p[2]) };
                    if (s.Start < 0 || s.Pos < s.Start || s.End > total || s.Pos > s.End) return false;
                    list.Add(s);
                }
                long covered = 0;
                foreach (var s in list) covered += s.End - s.Start;
                if (covered != total) return false;
                segs.AddRange(list);
                return true;
            }
            catch { return false; }
        }

        void SaveState()
        {
            if (statePath == null || total <= 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("FDL1\n").Append(total).Append('\n');
                lock (sync) foreach (var s in segs) sb.Append(s.Start).Append(' ').Append(s.Pos).Append(' ').Append(s.End).Append('\n');
                string tmp = statePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(statePath)) File.Replace(tmp, statePath, null); else File.Move(tmp, statePath);
                Hide(statePath);
            }
            catch { }
        }

        void Cleanup(bool deletePart)
        {
            try { if (statePath != null && File.Exists(statePath)) File.Delete(statePath); } catch { }
            if (deletePart) try { if (partPath != null && File.Exists(partPath)) File.Delete(partPath); } catch { }
            ReleaseLock();
        }

        long DoneBytes()
        {
            if (total <= 0 || segs.Count == 0) return Interlocked.Read(ref singleDone);
            long rem = 0;
            lock (sync) foreach (var s in segs) rem += s.End - s.Pos;
            return total - rem;
        }

        // ---------- download a connessione singola (server senza Range) ----------

        bool RunSingle(HttpWebRequest req, HttpWebResponse resp)
        {
            var sw = Stopwatch.StartNew();
            double lastT = 0, speed = 0; long lastD = 0;
            try { File.WriteAllText(statePath, "FDL1\n" + total + "\n"); Hide(statePath); } catch { }
            try
            {
                using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                using (var s = resp.GetResponseStream())
                {
                    var buf = new byte[BufSize];
                    while (!cancelled)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        singleDone += n;
                        double t = sw.Elapsed.TotalSeconds;
                        if (t - lastT >= 0.5)
                        {
                            double inst = (singleDone - lastD) / (t - lastT);
                            speed = speed <= 0 ? inst : speed * 0.6 + inst * 0.4;
                            lastT = t; lastD = singleDone;
                            rep.Progress(singleDone, total, speed, 1);
                            CheckControl(false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                req.Abort();
                Cleanup(true);
                rep.Failed(cancelled ? "Annullato" : "Download interrotto: " + e.Message, !cancelled);
                return false;
            }
            if (cancelled) { req.Abort(); Cleanup(true); rep.Failed("Annullato", false); return false; }
            if (total > 0 && singleDone < total) { Cleanup(true); rep.Failed("Download incompleto", true); return false; }
            total = singleDone;
            return Finish(sw.Elapsed.TotalSeconds, singleDone);
        }

        // ---------- multi-connessione con work stealing ----------

        bool RunMulti(HttpWebRequest req0, HttpWebResponse r0)
        {
            // Connections <= 0 = auto: parte con 4, raddoppia finche' la velocita' sale (max 32)
            bool auto = job.Connections <= 0;
            int n = auto ? 4 : Math.Max(1, Math.Min(job.Connections, 64));
            const int AutoMax = 32;
            var sw = Stopwatch.StartNew();
            var threads = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                object[] init = i == 0 && r0 != null
                    ? new object[] { segs[0], req0, r0 }
                    : new object[] { null, null, null };
                var t = new Thread(Worker) { IsBackground = true };
                t.Start(init);
                threads.Add(t);
                if (i == 0 && r0 != null) Thread.Sleep(30); // il primo apre la strada
            }

            long lastD = DoneBytes(), roundStart = lastD, startDone = lastD;
            double lastT = 0, speed = 0, lastSave = 0;
            int deadRounds = 0;
            bool growing = auto;
            double levelSpeed = 0, lastEval = 0;
            long evalD = lastD;
            while (true)
            {
                bool alive = false;
                foreach (var t in threads) if (t.IsAlive) { alive = true; break; }
                if (!alive)
                {
                    if (cancelled || DoneBytes() >= total) break;
                    // tutti i worker hanno mollato: riprova se c'e' stato progresso
                    long d0 = DoneBytes();
                    if (d0 > roundStart) deadRounds = 0; else deadRounds++;
                    if (deadRounds >= 3) break;
                    roundStart = d0;
                    Thread.Sleep(2000);
                    threads.Clear();
                    int k = rangeBroken ? 1 : Math.Max(1, n - retired);
                    retired = 0;
                    for (int i = 0; i < k; i++)
                    {
                        var t = new Thread(Worker) { IsBackground = true };
                        t.Start(new object[] { null, null, null });
                        threads.Add(t);
                    }
                }
                Thread.Sleep(500);
                double now = sw.Elapsed.TotalSeconds;
                long d = DoneBytes();
                double inst = (d - lastD) / Math.Max(0.001, now - lastT);
                speed = speed <= 0 ? inst : speed * 0.6 + inst * 0.4;
                lastD = d; lastT = now;
                rep.Progress(d, total, speed, active);
                if (now - lastSave >= 2) { SaveState(); lastSave = now; }
                CheckControl(true);

                if (growing && now - lastEval >= 1.5)
                {
                    double sp = (d - evalD) / Math.Max(0.001, now - lastEval);
                    evalD = d; lastEval = now;
                    if (retired > 0 || rangeBroken || n >= AutoMax || total - d < 4 * MinSplit) growing = false;
                    else if (levelSpeed > 0 && sp < levelSpeed * 1.15) growing = false; // non migliora piu': basta
                    else
                    {
                        levelSpeed = sp;
                        int add = Math.Min(n, AutoMax - n);
                        Log("auto: " + Fmt.Size(sp) + "/s con " + n + " -> " + (n + add));
                        for (int i = 0; i < add; i++)
                        {
                            var t = new Thread(Worker) { IsBackground = true };
                            t.Start(new object[] { null, null, null });
                            threads.Add(t);
                        }
                        n += add;
                    }
                }
            }

            if (cancelled)
            {
                if (DeleteOnCancel && !pausing) { Cleanup(true); rep.Failed("Annullato", false); }
                else
                {
                    SaveState(); ReleaseLock();
                    if (pausing) rep.Paused(DoneBytes(), total);
                    else rep.Failed("Interrotto: rilancia lo stesso comando per riprendere", false);
                }
                return false;
            }
            if (DoneBytes() < total)
            {
                if (job.FromBrowser) Cleanup(true); else { SaveState(); ReleaseLock(); }
                rep.Failed("Download fallito: " + lastError, true);
                return false;
            }
            return Finish(sw.Elapsed.TotalSeconds, total - startDone);
        }

        Segment Acquire()
        {
            lock (sync)
            {
                foreach (var s in segs)
                    if (!s.Busy && s.Pos < s.End) { s.Busy = true; return s; }
                if (rangeBroken) return null;
                Segment best = null; long bestRem = 0;
                foreach (var s in segs)
                {
                    if (!s.Busy) continue;
                    long rem = s.End - s.Pos;
                    if (rem > bestRem) { best = s; bestRem = rem; }
                }
                if (best == null || bestRem < 2 * MinSplit) return null;
                long mid = best.Pos + bestRem / 2;
                mid -= mid % 16384;
                if (mid <= best.Pos + MinSplit / 2) mid = best.Pos + bestRem / 2;
                var ns = new Segment { Start = mid, Pos = mid, End = best.End, Busy = true };
                Log("steal " + mid + "-" + best.End + " rem=" + bestRem);
                best.End = mid;
                segs.Add(ns);
                return ns;
            }
        }

        void Worker(object o)
        {
            var init = (object[])o;
            var seg = (Segment)init[0];
            var req = (HttpWebRequest)init[1];
            var resp = (HttpWebResponse)init[2];
            Interlocked.Increment(ref active);
            FileStream fs = null;
            try
            {
                fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
                var buf = new byte[BufSize];
                while (!cancelled)
                {
                    if (seg == null) { seg = Acquire(); if (seg == null) break; }
                    bool ok = Fetch(seg, req, resp, fs, buf);
                    req = null; resp = null;
                    lock (sync) seg.Busy = false;
                    seg = null;
                    if (!ok) break;
                }
            }
            catch (Exception e)
            {
                lastError = e.Message;
                if (seg != null) lock (sync) seg.Busy = false;
            }
            finally
            {
                Interlocked.Decrement(ref active);
                if (fs != null) try { fs.Dispose(); } catch { }
                if (req != null) try { req.Abort(); } catch { }
            }
        }

        bool Fetch(Segment seg, HttpWebRequest req, HttpWebResponse resp, FileStream fs, byte[] buf)
        {
            for (int attempt = 0; attempt < MaxAttempts && !cancelled; attempt++)
            {
                if (attempt > 0)
                {
                    int wait = Math.Min(700 * attempt, 4000);
                    for (int w = 0; w < wait && !cancelled; w += 100) Thread.Sleep(100);
                    if (cancelled) return false;
                }
                long before;
                lock (sync) before = seg.Pos;
                try
                {
                    if (resp == null)
                    {
                        long from, to;
                        lock (sync) { from = seg.Pos; to = seg.End - 1; }
                        if (from > to) return true;
                        req = MakeReq(from, to);
                        Log("req " + from + "-" + to);
                        resp = Send(req);
                        Log("resp " + (int)resp.StatusCode);
                        int c = (int)resp.StatusCode;
                        if (c != 206)
                        {
                            req.Abort(); req = null; resp = null;
                            if (c == 200) { rangeBroken = true; lastError = "il server non accetta connessioni parallele"; return false; }
                            lastError = "HTTP " + c;
                            if (c >= 400 && c < 500 && c != 408 && c != 429) return false;
                            throw new WebException(lastError);
                        }
                    }

                    bool eof = false;
                    Stream s = resp.GetResponseStream();
                    while (!cancelled)
                    {
                        long want;
                        lock (sync) want = seg.End - seg.Pos;
                        if (want <= 0) break;
                        int n = s.Read(buf, 0, (int)Math.Min(buf.Length, want));
                        if (n <= 0) { eof = true; break; }
                        long at; int take;
                        lock (sync) { at = seg.Pos; take = (int)Math.Max(0, Math.Min(n, seg.End - seg.Pos)); }
                        if (take > 0)
                        {
                            fs.Seek(at, SeekOrigin.Begin);
                            fs.Write(buf, 0, take);
                            lock (sync) seg.Pos += take;
                        }
                    }
                    if (eof) resp.Close(); else req.Abort(); // abort = non scaricare il resto (e' di un altro segmento)
                    req = null; resp = null;

                    bool complete;
                    lock (sync) complete = seg.Pos >= seg.End;
                    if (complete) return true;
                    if (cancelled) return false;
                }
                catch (Exception e)
                {
                    lastError = e.Message; Log("err " + e.Message);
                    if (req != null) try { req.Abort(); } catch { }
                    req = null; resp = null;
                }
                long after;
                lock (sync) after = seg.Pos;
                if (after > before) { attempt = -1; continue; } // ha fatto progressi: riparte il conteggio tentativi
                // nessun byte e altre connessioni lavorano: il server limita le connessioni -> questo worker si ritira
                if (Thread.VolatileRead(ref active) > 1) { Log("retire"); Interlocked.Increment(ref retired); return false; }
            }
            return false;
        }

        bool Finish(double seconds, long sessionBytes)
        {
            try
            {
                using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    Win.SetSparse(fs.SafeFileHandle, false);
            }
            catch { }
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { }

            string dest = finalPath;
            if (File.Exists(dest))
            {
                string dir = Path.GetDirectoryName(dest), bn = Path.GetFileNameWithoutExtension(dest), ext = Path.GetExtension(dest);
                for (int i = 1; File.Exists(dest); i++) dest = Path.Combine(dir, bn + " (" + i + ")" + ext);
            }
            try { File.Move(partPath, dest); }
            catch (Exception e) { ReleaseLock(); rep.Failed("Impossibile rinominare il file: " + e.Message, false); return false; }
            ReleaseLock();
            rep.Done(dest, total, seconds, sessionBytes / Math.Max(0.001, seconds));
            return true;
        }
    }

    // ---------- output ----------

    static class Fmt
    {
        public static string Size(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    class ConsoleReporter : Reporter
    {
        public override void Started(string name, string path, long total, bool ranged, bool resumed)
        {
            Console.WriteLine("-> " + path);
            Console.WriteLine("   " + (total > 0 ? Fmt.Size(total) : "dimensione sconosciuta")
                + (ranged ? "  |  multi-connessione" : "  |  connessione singola (il server non supporta Range)")
                + (resumed ? "  |  RIPRESO" : ""));
        }

        public override void Progress(long done, long total, double speed, int conns)
        {
            string pct = total > 0 ? ((double)done * 100 / total).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) + "%" : "";
            string eta = "";
            if (total > 0 && speed > 1024 && (total - done) / speed < 360000)
            {
                var ts = TimeSpan.FromSeconds((total - done) / speed);
                eta = "  ETA " + (int)ts.TotalMinutes + ":" + ts.Seconds.ToString("00");
            }
            string line = "   " + pct + "  " + Fmt.Size(done) + (total > 0 ? " / " + Fmt.Size(total) : "")
                + "  " + Fmt.Size(speed) + "/s  " + conns + " conn" + eta;
            Console.Write("\r" + line.PadRight(78));
        }

        public override void Done(string path, long total, double seconds, double avg)
        {
            Console.WriteLine();
            Console.WriteLine("OK  " + Fmt.Size(total) + " in " + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " s  (media " + Fmt.Size(avg) + "/s)");
        }

        public override void Failed(string msg, bool fallback)
        {
            Console.WriteLine();
            Console.WriteLine("ERRORE  " + msg);
        }
    }

    class NativeReporter : Reporter
    {
        readonly Stream o;
        readonly object l = new object();
        readonly JavaScriptSerializer js = new JavaScriptSerializer();

        public NativeReporter(Stream output) { o = output; }

        public void Send(Dictionary<string, object> d)
        {
            lock (l)
            {
                try
                {
                    byte[] b = Encoding.UTF8.GetBytes(js.Serialize(d));
                    o.Write(BitConverter.GetBytes(b.Length), 0, 4);
                    o.Write(b, 0, b.Length);
                    o.Flush();
                }
                catch { } // Chrome chiuso: il download continua lo stesso
            }
        }

        public override void Started(string name, string path, long total, bool ranged, bool resumed)
        {
            Send(new Dictionary<string, object> { { "type", "start" }, { "name", name }, { "path", path }, { "total", total }, { "ranged", ranged }, { "resumed", resumed } });
        }

        public override void Progress(long done, long total, double speed, int conns)
        {
            Send(new Dictionary<string, object> { { "type", "progress" }, { "done", done }, { "total", total }, { "speed", Math.Round(speed) }, { "conns", conns } });
        }

        public override void Done(string path, long total, double seconds, double avg)
        {
            Send(new Dictionary<string, object> { { "type", "done" }, { "path", path }, { "total", total }, { "seconds", Math.Round(seconds, 2) }, { "avg", Math.Round(avg) } });
        }

        public override void Paused(long done, long total)
        {
            Send(new Dictionary<string, object> { { "type", "paused" }, { "done", done }, { "total", total } });
        }

        public override void Failed(string msg, bool fallback)
        {
            Send(new Dictionary<string, object> { { "type", "error" }, { "message", msg }, { "fallback", fallback } });
        }
    }

    // ---------- entry point ----------

    static class Program
    {
        const string Version = "1.3.0";

        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            ServicePointManager.DefaultConnectionLimit = 512;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            if (args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                return NativeMain();
            return CliMain(args);
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
        }

        static Dictionary<string, object> ReadMsg(Stream s, JavaScriptSerializer js)
        {
            byte[] len = ReadExact(s, 4);
            if (len == null) return null;
            int n = BitConverter.ToInt32(len, 0);
            if (n <= 0 || n > (8 << 20)) return null;
            byte[] body = ReadExact(s, n);
            if (body == null) return null;
            return js.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r;
                try { r = s.Read(b, off, n - off); } catch { return null; }
                if (r <= 0) return null;
                off += r;
            }
            return b;
        }

        public static void HostLog(string s)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fastdl.log");
                if (File.Exists(p) && new FileInfo(p).Length > 200 * 1024) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        // Stato di un download dal disco: running (motore vivo, tiene il .lock), resumable, partial, done, gone
        static Dictionary<string, object> Status(string p)
        {
            var d = new Dictionary<string, object> { { "type", "status" }, { "state", "gone" }, { "done", -1L }, { "total", -1L }, { "ranged", false } };
            if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p)) return d;
            string part = p + ".fdl", st = p + ".fdl.state", lk = p + ".fdl.lock";
            bool running = false;
            if (File.Exists(lk))
                try { using (new FileStream(lk, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { running = true; }
                catch { }
            long total = -1, done = -1; bool ranged = false;
            try
            {
                if (File.Exists(st))
                {
                    string[] lines = File.ReadAllLines(st);
                    if (lines.Length >= 2) long.TryParse(lines[1], out total);
                    long rem = 0; int n = 0;
                    for (int i = 2; i < lines.Length; i++)
                    {
                        string[] s = lines[i].Split(' ');
                        if (s.Length == 3) { rem += long.Parse(s[2]) - long.Parse(s[1]); n++; }
                    }
                    if (n > 0 && total > 0) { ranged = true; done = total - rem; }
                }
                if (!ranged && File.Exists(part)) done = new FileInfo(part).Length;
            }
            catch { }
            string state = running ? "running"
                : File.Exists(part) ? (ranged ? "resumable" : "partial")
                : File.Exists(p) ? "done" : "gone";
            d["state"] = state; d["done"] = done; d["total"] = total; d["ranged"] = ranged;
            return d;
        }

        static int NativeMain()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HostLog("CRASH " + e.ExceptionObject);
            HostLog("avvio");
            var js = new JavaScriptSerializer();
            Stream inp = Console.OpenStandardInput(), outp = Console.OpenStandardOutput();
            var rep = new NativeReporter(outp);
            var msg = ReadMsg(inp, js);
            if (msg == null) return 0;
            string cmd = S(msg, "cmd") ?? "download";
            HostLog("cmd=" + cmd); // niente URL nel log: privacy

            if (cmd == "ping")
            {
                rep.Send(new Dictionary<string, object> { { "type", "pong" }, { "version", Version }, { "downloads", Win.DownloadsDir() } });
                return 0;
            }
            if (cmd == "reveal")
            {
                string p = S(msg, "path");
                try
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                    else Process.Start("explorer.exe", "\"" + Win.DownloadsDir() + "\"");
                }
                catch { }
                rep.Send(new Dictionary<string, object> { { "type", "ok" } });
                return 0;
            }
            if (cmd == "status")
            {
                rep.Send(Status(S(msg, "path")));
                return 0;
            }
            if (cmd == "control")
            {
                // pausa / annulla per un motore che gira in background (vedi CheckControl)
                string p = S(msg, "path"), a = S(msg, "action");
                if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p) && (a == "pause" || a == "cancel") && File.Exists(p + ".fdl"))
                    try { File.WriteAllText(p + ".fdl.ctl", a); } catch { }
                rep.Send(new Dictionary<string, object> { { "type", "ok" } });
                return 0;
            }
            if (cmd == "discard")
            {
                // cancella il parziale di un download in pausa (solo i file .fdl / .fdl.state di quel percorso)
                string p = S(msg, "path");
                if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p))
                    foreach (string ext in new[] { ".fdl", ".fdl.state" })
                        try { if (File.Exists(p + ext)) File.Delete(p + ext); } catch { }
                rep.Send(new Dictionary<string, object> { { "type", "ok" } });
                return 0;
            }
            if (cmd != "download") return 1;

            int conns;
            if (!int.TryParse(S(msg, "connections"), out conns)) conns = 0; // 0 = auto
            var job = new Job
            {
                Url = S(msg, "url"),
                Referrer = S(msg, "referrer"),
                Cookie = S(msg, "cookie"),
                UserAgent = S(msg, "userAgent"),
                NameHint = S(msg, "nameHint"),
                Mime = S(msg, "mime"),
                Connections = conns,
                OutDir = string.IsNullOrWhiteSpace(S(msg, "outDir")) ? null : S(msg, "outDir").Trim(),
                FromBrowser = true
            };
            if (string.IsNullOrEmpty(job.Url)) { rep.Failed("URL mancante", false); return 1; }

            var dl = new Downloader(job, rep) { DeleteOnCancel = true };
            var reader = new Thread(() =>
            {
                while (true)
                {
                    var m = ReadMsg(inp, js);
                    if (m == null) return; // estensione disconnessa: continuiamo comunque
                    string mc = S(m, "cmd");
                    if (mc == "cancel") dl.Cancel();
                    else if (mc == "pause") dl.Pause();
                }
            }) { IsBackground = true };
            reader.Start();
            return dl.Run() ? 0 : 1;
        }

        static int CliMain(string[] args)
        {
            string url = null, outp = null;
            int conns = 0; // 0 = auto
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "-o" || a == "--out") && i + 1 < args.Length) outp = args[++i];
                else if ((a == "-c" || a == "--conn") && i + 1 < args.Length) int.TryParse(args[++i], out conns);
                else if (a == "-h" || a == "--help" || a == "/?") { url = null; break; }
                else if (url == null) url = a;
            }
            if (url == null)
            {
                Console.WriteLine("FastDL " + Version + " - download multi-connessione");
                Console.WriteLine("uso:  fastdl <url> [-o cartella|file] [-c connessioni (default: auto, max 32)]");
                Console.WriteLine("Ctrl+C interrompe; rilancia lo stesso comando per riprendere.");
                return 1;
            }
            var job = new Job { Url = url, Connections = conns };
            if (outp != null)
            {
                if (Directory.Exists(outp) || outp.EndsWith("\\") || outp.EndsWith("/")) job.OutDir = outp;
                else job.OutFile = outp;
            }
            var dl = new Downloader(job, new ConsoleReporter());
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; dl.Cancel(); };
            return dl.Run() ? 0 : 1;
        }
    }
}
