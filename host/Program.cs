// Inventory host program.
//
// Serves the app page (embedded index.html) on a local address, shows it in a
// Chrome/Edge app window, and stores the data in Documents\Inventory.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4),
// so it builds and runs with nothing installed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    const string AppName = "Inventory";
    const string MutexName = "InventoryAppMutex"; // also checked by the installer
    const string Releases = "https://github.com/Kureijiotau/Inventory/releases/latest/download/";
    const int FirstPort = 47816;
    const int KeepDaily = 30;
    const int KeepSnapshots = 10;

    // INVENTORY_DATA_DIR overrides the data folder, for testing without touching real data.
    static readonly string DataDir = Environment.GetEnvironmentVariable("INVENTORY_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName);
    static readonly string DataFile = Path.Combine(DataDir, "data.json");
    static readonly string BackupDir = Path.Combine(DataDir, "backups");
    static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    static readonly string PidFile = Path.Combine(StateDir, "browser.pid");
    static readonly string Token = Guid.NewGuid().ToString("N");
    static readonly Regex DailyName = new Regex(@"^data-\d{4}-\d{2}-\d{2}\.json$");
    static readonly object IoLock = new object();

    static int holds;                 // open keep-alive connections from the page
    static bool everHeld;
    static DateTime lastHold = DateTime.UtcNow;
    static volatile bool quitting;
    static Process browser;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, MutexName, out first))
        {
            if (!first) { FocusExisting(); return; }
            try { Run(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    static void Run()
    {
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(StateDir);
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2

        string exe = FindBrowser();
        if (exe == null)
        {
            MessageBox.Show("Google Chrome or Microsoft Edge is needed to show the app, but neither was found on this computer.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int port;
        HttpListener listener = StartListener(out port);
        var accept = new Thread(delegate ()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                var t = new Thread(delegate () { Handle(ctx); });
                t.IsBackground = true;
                t.Start();
            }
        });
        accept.IsBackground = true;
        accept.Start();

        string args = "--app=http://127.0.0.1:" + port + "/"
            + " --user-data-dir=\"" + Path.Combine(StateDir, "browser") + "\""
            + " --no-first-run --no-default-browser-check --disable-background-mode"
            + " --disable-features=Translate --window-size=1280,860";
        browser = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        try { File.WriteAllText(PidFile, browser.Id.ToString()); } catch { }

        // Stay alive while the page keeps its keep-alive connection open.
        DateTime started = DateTime.UtcNow;
        while (!quitting)
        {
            Thread.Sleep(500);
            DateTime now = DateTime.UtcNow;
            if (everHeld && Thread.VolatileRead(ref holds) == 0 && (now - lastHold).TotalSeconds > 4) break;
            if (!everHeld && (now - started).TotalSeconds > 90) break;
        }
        try { listener.Stop(); } catch { }
        try { File.Delete(PidFile); } catch { }
    }

    static HttpListener StartListener(out int port)
    {
        for (port = FirstPort; port < FirstPort + 20; port++)
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { l.Start(); return l; } catch (HttpListenerException) { }
        }
        throw new Exception("Could not start: no free local port.");
    }

    static string FindBrowser()
    {
        var roots = new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)", "LocalAppData" }
            .Select(v => Environment.GetEnvironmentVariable(v)).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        foreach (string rel in new[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe" })
            foreach (string root in roots)
            {
                string p = Path.Combine(root, rel);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    static void FocusExisting()
    {
        try
        {
            IntPtr h = Process.GetProcessById(int.Parse(File.ReadAllText(PidFile).Trim())).MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9); // restore
            SetForegroundWindow(h);
        }
        catch { }
    }

    // ---------- HTTP ----------

    static void Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        try
        {
            string path = req.Url.AbsolutePath;
            res.Headers["Cache-Control"] = "no-store";

            if (path == "/")
            {
                string html = Encoding.UTF8.GetString(Resource("index.html"));
                string inject = "<script>window.HOST={token:\"" + Token + "\",version:\"" + BuildInfo.Version + "\"};</script>";
                Send(res, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html.Replace("<!--HOST-->", inject)));
                return;
            }
            if (path == "/logo.png" || path == "/icon.png")
            {
                Send(res, 200, "image/png", Resource(path.Substring(1)));
                return;
            }
            if (!path.StartsWith("/api/")) { Text(res, 404, "Not found"); return; }

            // Every API call must carry the token given only to our own page.
            if ((req.Headers["X-Token"] ?? req.QueryString["t"]) != Token) { Text(res, 403, "Forbidden"); return; }

            string key = req.HttpMethod + " " + path;
            switch (key)
            {
                case "GET /api/hold": Hold(res); return;
                case "GET /api/data":
                    lock (IoLock)
                    {
                        if (!File.Exists(DataFile)) { Send(res, 204, null, new byte[0]); return; }
                        Send(res, 200, "application/json; charset=utf-8", File.ReadAllBytes(DataFile));
                    }
                    return;
                case "PUT /api/data":
                    {
                        byte[] body = ReadBody(req);
                        if (body.Length == 0 || body[0] != (byte)'{') { Text(res, 400, "Bad data"); return; }
                        lock (IoLock) { WriteData(body); }
                        Text(res, 200, "ok");
                        return;
                    }
                case "POST /api/snapshot":
                    lock (IoLock) { Snapshot(req.QueryString["tag"]); }
                    Text(res, 200, "ok");
                    return;
                case "GET /api/backups":
                    {
                        var names = Directory.GetFiles(BackupDir, "data-*.json").Select(Path.GetFileName)
                            .Where(n => DailyName.IsMatch(n)).OrderByDescending(n => n);
                        Json(res, "[" + string.Join(",", names.Select(Quote)) + "]");
                        return;
                    }
                case "POST /api/restore":
                    {
                        string name = req.QueryString["name"] ?? "";
                        string src = Path.Combine(BackupDir, name);
                        if (!DailyName.IsMatch(name) || !File.Exists(src)) { Text(res, 404, "No such copy"); return; }
                        byte[] data;
                        lock (IoLock)
                        {
                            Snapshot("restore");
                            data = File.ReadAllBytes(src);
                            AtomicWrite(DataFile, data);
                        }
                        Send(res, 200, "application/json; charset=utf-8", data);
                        return;
                    }
                case "POST /api/open-folder":
                    Process.Start("explorer.exe", "\"" + DataDir + "\"");
                    Text(res, 200, "ok");
                    return;
                case "GET /api/info":
                    Json(res, "{\"dataDir\":" + Quote(DataDir) + ",\"version\":" + Quote(BuildInfo.Version) + "}");
                    return;
                case "GET /api/update":
                    {
                        string latest = LatestVersion();
                        bool newer = IsNewer(latest);
                        Json(res, "{\"current\":" + Quote(BuildInfo.Version) + ",\"latest\":" + Quote(latest ?? "") + ",\"available\":" + (newer ? "true" : "false") + "}");
                        return;
                    }
                case "POST /api/update":
                    {
                        string setup = Path.Combine(Path.GetTempPath(), "InventorySetup.exe");
                        Download(Releases + "InventorySetup.exe", setup);
                        Text(res, 200, "ok");
                        Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = false });
                        CloseBrowser();
                        quitting = true;
                        return;
                    }
            }
            Text(res, 404, "Not found");
        }
        catch (Exception ex)
        {
            try { Text(res, 500, ex.Message); } catch { }
        }
    }

    // Keep-alive stream: when the window closes the write fails and the program exits.
    static void Hold(HttpListenerResponse res)
    {
        Interlocked.Increment(ref holds);
        everHeld = true;
        try
        {
            res.ContentType = "text/event-stream";
            res.SendChunked = true;
            byte[] beat = Encoding.ASCII.GetBytes(":\n\n");
            while (!quitting)
            {
                res.OutputStream.Write(beat, 0, beat.Length);
                res.OutputStream.Flush();
                Thread.Sleep(1500);
            }
        }
        catch { }
        finally
        {
            lastHold = DateTime.UtcNow;
            Interlocked.Decrement(ref holds);
            try { res.Abort(); } catch { }
        }
    }

    static void CloseBrowser()
    {
        try
        {
            if (browser == null || browser.HasExited) return;
            browser.CloseMainWindow();
            if (!browser.WaitForExit(3000)) browser.Kill();
        }
        catch { }
    }

    // ---------- Data files ----------

    static void WriteData(byte[] body)
    {
        AtomicWrite(DataFile, body);
        // One copy per day, holding the state at the end of that day.
        AtomicWrite(Path.Combine(BackupDir, "data-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json"), body);
        Prune("data-*.json", KeepDaily);
    }

    // Copy of the current data taken before a risky step (restore, import, format upgrade).
    static void Snapshot(string tag)
    {
        if (!File.Exists(DataFile)) return;
        tag = Regex.Replace(tag ?? "", "[^a-z]", "");
        if (tag == "") tag = "manual";
        File.Copy(DataFile, Path.Combine(BackupDir, "before-" + tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
        Prune("before-*.json", KeepSnapshots);
    }

    static void Prune(string pattern, int keep)
    {
        foreach (string old in Directory.GetFiles(BackupDir, pattern).OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Skip(keep))
            try { File.Delete(old); } catch { }
    }

    static void AtomicWrite(string file, byte[] data)
    {
        string tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data, 0, data.Length);
            fs.Flush(true);
        }
        if (File.Exists(file)) File.Replace(tmp, file, null);
        else File.Move(tmp, file);
    }

    // ---------- Updates ----------

    static string LatestVersion()
    {
        try
        {
            var rq = (HttpWebRequest)WebRequest.Create(Releases + "version.txt");
            rq.Timeout = 8000;
            rq.UserAgent = AppName;
            using (var rs = rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream()))
                return sr.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    static bool IsNewer(string latest)
    {
        Version mine, theirs;
        if (latest == null || !Version.TryParse(latest, out theirs) || !Version.TryParse(BuildInfo.Version, out mine)) return false;
        if (mine.Major == 0) return false; // local development build
        return theirs > mine;
    }

    static void Download(string url, string file)
    {
        var rq = (HttpWebRequest)WebRequest.Create(url);
        rq.Timeout = 30000;
        rq.UserAgent = AppName;
        using (var rs = rq.GetResponse())
        using (var src = rs.GetResponseStream())
        using (var dst = File.Create(file))
            src.CopyTo(dst);
    }

    // ---------- Helpers ----------

    static byte[] Resource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static byte[] ReadBody(HttpListenerRequest req)
    {
        using (var ms = new MemoryStream())
        {
            req.InputStream.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static void Send(HttpListenerResponse res, int status, string type, byte[] body)
    {
        res.StatusCode = status;
        if (type != null) res.ContentType = type;
        res.ContentLength64 = body.Length;
        if (body.Length > 0) res.OutputStream.Write(body, 0, body.Length);
        res.Close();
    }

    static void Text(HttpListenerResponse res, int status, string text) { Send(res, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text)); }
    static void Json(HttpListenerResponse res, string json) { Send(res, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json)); }

    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
