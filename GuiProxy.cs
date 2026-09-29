using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Concurrent;

namespace OpencodeGoProxy
{
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private System.Windows.Forms.Timer statusTimer;
        private ProxyServer server;
        private Config config;
        private List<string> apiKeys = new List<string>();
        private ConcurrentDictionary<string, KeyStatus> keyStatuses = new ConcurrentDictionary<string, KeyStatus>();
        private bool isRunning = false;
        private string configPath;
        private string apiKeyFilePath;

        private TextBox txtPort;
        private TextBox txtApiKey;
        private TextBox txtKeysFile;
        private Button btnStartStop;
        private Button btnBrowse;
        private Label lblStatus;
        private DataGridView dgvKeys;
        private Panel pnlHeader;
        private Label lblTitle;

        public MainForm()
        {
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            apiKeyFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api.txt");
            InitializeComponent();
            LoadConfig();
        }

        private void InitializeComponent()
        {
            this.Text = "OpenCode Go Proxy";
            this.Size = new Size(700, 550);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9f);

            pnlHeader = new Panel();
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = 80;
            pnlHeader.BackColor = Color.FromArgb(30, 41, 59);

            lblTitle = new Label();
            lblTitle.Text = "OpenCode Go Proxy";
            lblTitle.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(20, 15);
            lblTitle.AutoSize = true;

            lblStatus = new Label();
            lblStatus.Text = "● Stopped";
            lblStatus.Font = new Font("Segoe UI", 10f);
            lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            lblStatus.Location = new Point(20, 50);
            lblStatus.AutoSize = true;

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblStatus);

            var pnlSettings = new Panel();
            pnlSettings.Location = new Point(20, 100);
            pnlSettings.Size = new Size(640, 180);

            int y = 0;
            pnlSettings.Controls.Add(CreateLabel("Port:", 0, y));
            txtPort = new TextBox();
            txtPort.Text = "4001";
            txtPort.Location = new Point(120, y);
            txtPort.Size = new Size(100, 25);
            txtPort.BackColor = Color.FromArgb(30, 41, 59);
            txtPort.ForeColor = Color.White;
            txtPort.BorderStyle = BorderStyle.FixedSingle;
            pnlSettings.Controls.Add(txtPort);

            y += 35;
            pnlSettings.Controls.Add(CreateLabel("API Key:", 0, y));
            txtApiKey = new TextBox();
            txtApiKey.Location = new Point(120, y);
            txtApiKey.Size = new Size(400, 25);
            txtApiKey.BackColor = Color.FromArgb(30, 41, 59);
            txtApiKey.ForeColor = Color.White;
            txtApiKey.BorderStyle = BorderStyle.FixedSingle;
            pnlSettings.Controls.Add(txtApiKey);

            y += 35;
            pnlSettings.Controls.Add(CreateLabel("Keys File:", 0, y));
            txtKeysFile = new TextBox();
            txtKeysFile.Text = apiKeyFilePath;
            txtKeysFile.Location = new Point(120, y);
            txtKeysFile.Size = new Size(400, 25);
            txtKeysFile.BackColor = Color.FromArgb(30, 41, 59);
            txtKeysFile.ForeColor = Color.White;
            txtKeysFile.BorderStyle = BorderStyle.FixedSingle;
            pnlSettings.Controls.Add(txtKeysFile);
            btnBrowse = new Button();
            btnBrowse.Text = "...";
            btnBrowse.Location = new Point(530, y);
            btnBrowse.Size = new Size(30, 25);
            btnBrowse.FlatStyle = FlatStyle.Flat;
            btnBrowse.BackColor = Color.FromArgb(51, 65, 85);
            btnBrowse.ForeColor = Color.White;
            btnBrowse.Click += BtnBrowse_Click;
            pnlSettings.Controls.Add(btnBrowse);

            y += 40;
            btnStartStop = new Button();
            btnStartStop.Text = "▶ Start Proxy";
            btnStartStop.Location = new Point(120, y);
            btnStartStop.Size = new Size(150, 35);
            btnStartStop.FlatStyle = FlatStyle.Flat;
            btnStartStop.BackColor = Color.FromArgb(34, 197, 94);
            btnStartStop.ForeColor = Color.White;
            btnStartStop.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnStartStop.Cursor = Cursors.Hand;
            btnStartStop.Click += BtnStartStop_Click;
            pnlSettings.Controls.Add(btnStartStop);

            dgvKeys = new DataGridView();
            dgvKeys.Location = new Point(20, 300);
            dgvKeys.Size = new Size(640, 180);
            dgvKeys.BackgroundColor = Color.FromArgb(30, 41, 59);
            dgvKeys.ForeColor = Color.White;
            dgvKeys.GridColor = Color.FromArgb(51, 65, 85);
            dgvKeys.BorderStyle = BorderStyle.None;
            dgvKeys.AllowUserToAddRows = false;
            dgvKeys.AllowUserToDeleteRows = false;
            dgvKeys.ReadOnly = true;
            dgvKeys.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvKeys.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvKeys.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(51, 65, 85);
            dgvKeys.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dgvKeys.EnableHeadersVisualStyles = false;
            dgvKeys.Columns.Add("Key", "Key");
            dgvKeys.Columns.Add("Rolling", "Rolling");
            dgvKeys.Columns.Add("Weekly", "Weekly");
            dgvKeys.Columns.Add("Monthly", "Monthly");
            dgvKeys.Columns.Add("Score", "Score");
            dgvKeys.Columns.Add("Status", "Status");

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show", null, delegate(object s, EventArgs e) { ShowWindow(); });
            trayMenu.Items.Add("Start", null, delegate(object s, EventArgs e) { StartProxy(); });
            trayMenu.Items.Add("Stop", null, delegate(object s, EventArgs e) { StopProxy(); });
            trayMenu.Items.Add("-", null, null);
            trayMenu.Items.Add("Exit", null, delegate(object s, EventArgs e) { ExitApp(); });

            trayIcon = new NotifyIcon();
            trayIcon.Text = "OpenCode Go Proxy - Stopped";
            trayIcon.Icon = CreateStatusIcon(Color.FromArgb(239, 68, 68));
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate(object s, EventArgs e) { ShowWindow(); };

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 20000;
            statusTimer.Tick += StatusTimer_Tick;

            this.Controls.Add(pnlHeader);
            this.Controls.Add(pnlSettings);
            this.Controls.Add(dgvKeys);

            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    this.Hide();
                    trayIcon.ShowBalloonTip(2000, "OpenCode Go Proxy", "Running in system tray", ToolTipIcon.Info);
                }
            };
        }

        private Label CreateLabel(string text, int x, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.Location = new Point(x, y);
            lbl.Size = new Size(100, 25);
            lbl.ForeColor = Color.FromArgb(148, 163, 184);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            return lbl;
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    config = SimpleJson.Deserialize<Config>(json);
                    txtPort.Text = new Uri(config.listen_prefix).Port.ToString();
                    txtApiKey.Text = config.local_api_key;
                    txtKeysFile.Text = config.credential_source;
                }
                else
                {
                    txtApiKey.Text = GenerateApiKey();
                }

                if (File.Exists(apiKeyFilePath))
                {
                    apiKeys = File.ReadAllLines(apiKeyFilePath)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 10)
                        .Distinct()
                        .ToList();
                    RefreshKeysGrid();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading config: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveConfig()
        {
            try
            {
                Config cfg = new Config();
                cfg.listen_prefix = string.Format("http://127.0.0.1:{0}/", txtPort.Text);
                cfg.upstream_base_url = "https://opencode.ai/zen/go/v1";
                cfg.zen_upstream_base_url = "https://opencode.ai/zen/v1";
                cfg.credential_source = txtKeysFile.Text;
                cfg.local_api_key = txtApiKey.Text;
                cfg.public_model = "opencode-go";
                cfg.upstream_model = "kimi-k3";
                cfg.retry_server_error_from = 500;
                cfg.retry_http_statuses = new int[] { 401, 402, 403, 408, 425, 429 };
                File.WriteAllText(configPath, SimpleJson.Serialize(cfg));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving config: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "Text files|*.txt|All files|*.*";
            ofd.Title = "Select API Keys File";
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                txtKeysFile.Text = ofd.FileName;
                apiKeyFilePath = ofd.FileName;
                LoadApiKeys();
            }
        }

        private void LoadApiKeys()
        {
            try
            {
                if (File.Exists(apiKeyFilePath))
                {
                    apiKeys = File.ReadAllLines(apiKeyFilePath)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 10)
                        .Distinct()
                        .ToList();
                    RefreshKeysGrid();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading keys: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RefreshKeysGrid()
        {
            dgvKeys.Rows.Clear();
            for (int i = 0; i < apiKeys.Count; i++)
            {
                string key = apiKeys[i];
                string masked = key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
                KeyStatus status = keyStatuses.GetOrAdd(key, new KeyStatus());
                dgvKeys.Rows.Add(masked, status.Rolling + "%", status.Weekly + "%", status.Monthly + "%", status.Score.ToString("0"), status.IsHealthy ? "✓ Ready" : "✗ Limited");
            }
        }

        private void BtnStartStop_Click(object sender, EventArgs e)
        {
            if (isRunning) StopProxy();
            else StartProxy();
        }

        private void StartProxy()
        {
            try
            {
                SaveConfig();
                LoadApiKeys();

                if (apiKeys.Count == 0)
                {
                    MessageBox.Show("Please add at least one API key.", "No Keys", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int port = int.Parse(txtPort.Text);
                server = new ProxyServer(port, txtApiKey.Text, apiKeys);
                server.OnRequestProcessed = OnRequestProcessed;
                server.Start();

                isRunning = true;
                btnStartStop.Text = "■ Stop Proxy";
                btnStartStop.BackColor = Color.FromArgb(239, 68, 68);
                lblStatus.Text = "● Running";
                lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
                trayIcon.Text = "OpenCode Go Proxy - Running";
                trayIcon.Icon = CreateStatusIcon(Color.FromArgb(34, 197, 94));
                statusTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error starting proxy: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopProxy()
        {
            statusTimer.Stop();
            if (server != null) server.Stop();
            server = null;
            isRunning = false;
            btnStartStop.Text = "▶ Start Proxy";
            btnStartStop.BackColor = Color.FromArgb(34, 197, 94);
            lblStatus.Text = "● Stopped";
            lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            trayIcon.Text = "OpenCode Go Proxy - Stopped";
            trayIcon.Icon = CreateStatusIcon(Color.FromArgb(239, 68, 68));
        }

        private void OnRequestProcessed(string key, KeyStatus status)
        {
            keyStatuses.AddOrUpdate(key, status, (k, v) => status);
            if (this.InvokeRequired)
            {
                this.Invoke(new MethodInvoker(RefreshKeysGrid));
            }
            else
            {
                RefreshKeysGrid();
            }
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            RefreshKeysGrid();
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void ExitApp()
        {
            statusTimer.Stop();
            if (server != null) server.Stop();
            trayIcon.Visible = false;
            Application.Exit();
        }

        private string GenerateApiKey()
        {
            byte[] bytes = new byte[32];
            using (System.Security.Cryptography.RNGCryptoServiceProvider rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private Icon CreateStatusIcon(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(color))
                    g.FillEllipse(brush, 1, 1, 14, 14);
            }
            IntPtr hIcon = bmp.GetHicon();
            Icon icon = Icon.FromHandle(hIcon);
            return icon;
        }
    }

    public class Config
    {
        public string listen_prefix { get; set; }
        public string upstream_base_url { get; set; }
        public string zen_upstream_base_url { get; set; }
        public string credential_source { get; set; }
        public string local_api_key { get; set; }
        public string public_model { get; set; }
        public string upstream_model { get; set; }
        public int retry_server_error_from { get; set; }
        public int[] retry_http_statuses { get; set; }
    }

    public class KeyStatus
    {
        public double Rolling { get; set; }
        public double Weekly { get; set; }
        public double Monthly { get; set; }
        public double Score { get { return 100 - Math.Max(Rolling, Math.Max(Weekly, Monthly)); } }
        public bool IsHealthy { get { return Score > 0; } }
    }

    public class ProxyServer
    {
        private HttpListener listener;
        private int port;
        private string localApiKey;
        private List<string> apiKeys;
        private bool running;
        public Action<string, KeyStatus> OnRequestProcessed;

        public ProxyServer(int port, string localApiKey, List<string> apiKeys)
        {
            this.port = port;
            this.localApiKey = localApiKey;
            this.apiKeys = apiKeys;
        }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", port));
            listener.Start();
            running = true;
            Task.Factory.StartNew(ProcessRequests);
        }

        public void Stop()
        {
            running = false;
            if (listener != null) listener.Stop();
        }

        private void ProcessRequests()
        {
            while (running)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    HandleRequest(context);
                }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            // Forward request to upstream with key rotation
            // This is a simplified version - full implementation would include
            // the proxy logic from the main OpencodeGoProxy.cs
            try
            {
                string response = "{\"status\":\"ok\",\"message\":\"GUI proxy running. Use the main proxy for full functionality.\"}";
                byte[] buffer = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.Close();
            }
            catch { }
        }
    }

    public static class SimpleJson
    {
        public static string Serialize(object obj)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            var props = obj.GetType().GetProperties();
            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0) sb.Append(",");
                object val = props[i].GetValue(obj, null);
                sb.Append("\"" + props[i].Name + "\":");
                if (val is string) sb.Append("\"" + val + "\"");
                else if (val is int) sb.Append(val.ToString());
                else if (val is int[]) sb.Append("[" + string.Join(",", Array.ConvertAll((int[])val, x => x.ToString())) + "]");
                else if (val == null) sb.Append("null");
                else sb.Append("\"" + val.ToString() + "\"");
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static T Deserialize<T>(string json) where T : new()
        {
            T obj = new T();
            json = json.Trim('{', '}');
            string[] pairs = json.Split(',');
            foreach (string pair in pairs)
            {
                string[] kv = pair.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;
                string key = kv[0].Trim('"');
                string val = kv[1].Trim('"');
                var prop = typeof(T).GetProperty(key);
                if (prop != null)
                {
                     if (prop.PropertyType == typeof(string)) prop.SetValue(obj, val, null);
                     else if (prop.PropertyType == typeof(int))
                     {
                         int n;
                         if (int.TryParse(val, out n)) prop.SetValue(obj, n, null);
                     }
                     else if (prop.PropertyType == typeof(int[]))
                     {
                         int[] arr = Array.ConvertAll(val.Trim('[', ']').Split(','), int.Parse);
                         prop.SetValue(obj, arr, null);
                     }
                }
            }
            return obj;
        }
    }
}
