using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace SystemTray
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }

    class TrayContext : ApplicationContext
    {
        string ProxyDir;
        string ProxyExe;
        string ConfigJson;
        string ApiTxt;
        string LogDir;
        string LogFile;
        NotifyIcon Tray;
        Timer HealthTimer;
        FileSystemWatcher CfgWatcher;
        FileSystemWatcher KeyWatcher;

        Process ProxyProcess;

        public TrayContext()
        {
            ProxyDir = Path.GetDirectoryName(typeof(TrayContext).Assembly.Location) ?? ".";
            ProxyExe = Path.Combine(ProxyDir, "OpencodeGoProxy.exe");
            ConfigJson = Path.Combine(ProxyDir, "config.json");
            ApiTxt = "F:\\backup\\windowsapps\\credentials\\opencodego\\api.txt";
            LogDir = Path.Combine(ProxyDir, "Logs");
            LogFile = Path.Combine(LogDir, "tray-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

            Directory.CreateDirectory(LogDir);
            // Write directly to file (Console redirect unreliable in WinExe mode)
            Log("TRAY_START pid=" + Process.GetCurrentProcess().Id);

            Tray = new NotifyIcon
            {
                Icon = CreateGreenIcon(),
                Text = "OpenCode Go Proxy",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            Tray.DoubleClick += (s, e) => ShowStatus();

            StartProxy();
            SetupWatchers();

            HealthTimer = new Timer { Interval = 120000 };
            HealthTimer.Tick += (s, e) => CheckHealth();
            HealthTimer.Start();
        }

        /* ── icon (16x16 green circle) ── */
        static Icon CreateGreenIcon()
        {
            Bitmap bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(Color.FromArgb(0, 200, 80)))
                    g.FillEllipse(b, 1, 1, 14, 14);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        /* ── menu ── */
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Show Status", null, (s, e) => ShowStatus());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Reorder Keys by Quota", null, (s, e) => ReorderKeys());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open Config", null, (s, e) => SafeStart("notepad.exe", ConfigJson));
            m.Items.Add("Open Keys", null, (s, e) => SafeStart("notepad.exe", ApiTxt));
            m.Items.Add("Restart Proxy", null, (s, e) => RestartProxy());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit", null, (s, e) => ExitApp());
            return m;
        }

        /* ── proxy management ── */
        void StartProxy()
        {
            if (!File.Exists(ProxyExe)) { Log("PROXY_EXE_MISSING " + ProxyExe); return; }
            try
            {
                ProxyProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = ProxyExe,
                    Arguments = "-Mode Serve -ConfigPath \"" + ConfigJson + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                ProxyProcess.EnableRaisingEvents = true;
                ProxyProcess.Exited += (s, e) => ProxyProcess = null;
                Log("PROXY_STARTED pid=" + ProxyProcess.Id);
            }
            catch (Exception ex) { Log("PROXY_START_ERROR " + ex.GetType().Name); }
        }

        void RestartProxy()
        {
            try
            {
                if (ProxyProcess != null && !ProxyProcess.HasExited)
                {
                    ProxyProcess.Kill();
                    ProxyProcess.WaitForExit(3000);
                }
            }
            catch { }
            ProxyProcess = null;
            System.Threading.Thread.Sleep(500);
            StartProxy();
            System.Threading.Thread.Sleep(500);
            CheckHealth();
        }

        /* ── status ── */
        void CheckHealth()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:4000/health");
                req.Timeout = 5000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    int ki = json.IndexOf("\"credential_count\"");
                    int kv = ki >= 0 ? json.IndexOf(":", ki) + 1 : -1;
                    string ks = kv >= 0 ? json.Substring(kv, 20).Trim().TrimEnd('}', ',') : "?";
                    Tray.Text = "OpenCode Go Proxy — " + ks + " key(s)";
                    Log("HEALTH keys=" + ks);
                }
            }
            catch
            {
                Tray.Text = "OpenCode Go Proxy — offline";
            }
        }

        void ShowStatus()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:4000/health");
                req.Timeout = 5000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    string keys = Extract(json, "credential_count");
                    string model = Extract(json, "model");
                    string lines = "Proxy: running\nKeys: " + keys + "\nModel: " + model;
                    try
                    {
                        if (File.Exists(ApiTxt))
                        {
                            int count = File.ReadAllLines(ApiTxt).Length;
                            lines += "\nKey lines: " + count;
                        }
                    }
                    catch { }
                    Balloon("OpenCode Go Proxy", lines, ToolTipIcon.Info);
                }
            }
            catch
            {
                Balloon("OpenCode Go Proxy", "Proxy is not responding.", ToolTipIcon.Error);
            }
        }

        /* ── key reorder by remaining quota ── */
        void ReorderKeys()
        {
            try
            {
                if (!File.Exists(ApiTxt)) { Balloon("Keys", "api.txt not found.", ToolTipIcon.Warning); return; }
                string[] keys = File.ReadAllLines(ApiTxt);
                var scored = new System.Collections.Generic.List<KeyValuePair<string, double>>();
                for (int i = 0; i < keys.Length; i++)
                {
                    string k = keys[i].Trim();
                    if (string.IsNullOrEmpty(k)) continue;
                    double max = ProbeUsage(k);
                    scored.Add(new KeyValuePair<string, double>(k, 100.0 - max));
                }
                if (scored.Count < 2) { Balloon("Keys", "Need 2+ keys to reorder.", ToolTipIcon.Warning); return; }
                scored.Sort((a, b) => b.Value.CompareTo(a.Value));
                string backup = ApiTxt + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(ApiTxt, backup, true);
                File.WriteAllLines(ApiTxt, scored.ConvertAll(s => s.Key).ToArray());
                Log("KEYS_REORDERED backup=" + backup);
                RestartProxy();
                Balloon("Keys Reordered", "Best key first. Proxy restarted.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Log("KEYS_ERROR " + ex.GetType().Name + " " + ex.Message); Balloon("Keys Error", ex.Message, ToolTipIcon.Error); }
        }

        double ProbeUsage(string key)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://opencode.ai/zen/go/v1/usage");
                req.Method = "GET";
                req.Headers["Authorization"] = "Bearer " + key;
                req.Accept = "application/json";
                req.Timeout = 20000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    double max = 0;
                    foreach (string w in new[] { "rolling", "weekly", "monthly" })
                    {
                        double p = ParsePercent(json, w);
                        if (p > max) max = p;
                    }
                    return max;
                }
            }
            catch { return 50.0; }
        }

        /* ── file watchers ── */
        void SetupWatchers()
        {
            try
            {
                CfgWatcher = new FileSystemWatcher(ProxyDir, "config.json") { EnableRaisingEvents = true };
                CfgWatcher.Changed += (s, e) => { Log("CONFIG_CHANGED"); RestartProxy(); };
            }
            catch (Exception ex) { Log("WATCHER_CFG_ERROR " + ex.Message); }
            try
            {
                string keyDir = Path.GetDirectoryName(ApiTxt);
                string keyName = Path.GetFileName(ApiTxt);
                if (keyDir != null && Directory.Exists(keyDir))
                {
                    KeyWatcher = new FileSystemWatcher(keyDir, keyName) { EnableRaisingEvents = true };
                    KeyWatcher.Changed += (s, e) => { Log("KEYS_CHANGED"); RestartProxy(); };
                }
            }
            catch (Exception ex) { Log("WATCHER_KEY_ERROR " + ex.Message); }
        }

        /* ── exit ── */
        void ExitApp()
        {
            HealthTimer.Stop();
            Tray.Visible = false;
            try { if (ProxyProcess != null && !ProxyProcess.HasExited) ProxyProcess.Kill(); } catch { }
            Log("TRAY_EXIT");
            Application.Exit();
        }

        /* ── helpers ── */
        static void SafeStart(string file, string arg)
        {
            try { Process.Start(file, arg); } catch { }
        }

        void Balloon(string title, string msg, ToolTipIcon icon)
        {
            try
            {
                if (Tray.Visible) Tray.ShowBalloonTip(5000, title, msg, icon);
            }
            catch { }
        }

        void Log(string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("o") + " " + msg;
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch { }
        }

        static string Extract(string json, string key)
        {
            string search = "\"" + key + "\"";
            int i = json.IndexOf(search);
            if (i < 0) return "?";
            int c = json.IndexOf(":", i + search.Length);
            if (c < 0) return "?";
            int s = c + 1;
            while (s < json.Length && (json[s] == ' ' || json[s] == '"')) s++;
            int e = s;
            while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\n' && json[e] != '"') e++;
            return json.Substring(s, e - s).Trim();
        }

        static double ParsePercent(string json, string window)
        {
            string search = "\"" + window + "\"";
            int wi = json.IndexOf(search);
            if (wi < 0) return 0;
            int pi = json.IndexOf("\"percent\"", wi);
            if (pi < 0) return 0;
            int c = json.IndexOf(":", pi + 8);
            if (c < 0) return 0;
            int s = c + 1;
            while (s < json.Length && json[s] == ' ') s++;
            int e = s;
            while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\n' && json[e] != '"') e++;
            double p;
            if (double.TryParse(json.Substring(s, e - s).Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out p))
                return p;
            return 0;
        }
    }
}
