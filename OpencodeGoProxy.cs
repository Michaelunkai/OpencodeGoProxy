using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpencodeGoProxy
{
    // HttpListener depends on machine-wide System.Web configuration on .NET Framework.
    // This loopback-only HTTP/1.1 implementation keeps the proxy self-contained.
    internal sealed class HttpListener
    {
        public readonly List<string> Prefixes = new List<string>();
        private TcpListener listener;
        private volatile bool listening;
        public bool IsListening { get { return listening; } }

        public void Start()
        {
            if (Prefixes.Count != 1) throw new InvalidOperationException("Exactly one loopback listener prefix is required.");
            Uri prefix = new Uri(Prefixes[0]);
            IPAddress address = IPAddress.Parse(prefix.Host);
            if (!IPAddress.IsLoopback(address)) throw new InvalidOperationException("The listener must bind to loopback.");
            listener = new TcpListener(address, prefix.Port);
            listener.Start(128);
            listening = true;
        }

        public async Task<HttpListenerContext> GetContextAsync()
        {
            TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            try { return await HttpListenerContext.ReadAsync(client).ConfigureAwait(false); }
            catch { client.Close(); throw; }
        }

        public void Close()
        {
            listening = false;
            if (listener != null) listener.Stop();
        }
    }

    internal sealed class HttpListenerContext
    {
        public readonly HttpListenerRequest Request;
        public readonly HttpListenerResponse Response;
        private HttpListenerContext(HttpListenerRequest request, HttpListenerResponse response)
        {
            Request = request;
            Response = response;
        }

        public static async Task<HttpListenerContext> ReadAsync(TcpClient client)
        {
            NetworkStream network = client.GetStream();
            byte[] headerBytes = await ReadHeadersAsync(network).ConfigureAwait(false);
            string headerText = Encoding.ASCII.GetString(headerBytes);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(new[] { ' ' }, 3);
            if (requestLine.Length < 3) throw new InvalidDataException("Malformed HTTP request line.");
            var headers = new HttpHeaders();
            int contentLength = 0;
            bool chunked = false;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int colon = lines[i].IndexOf(':');
                if (colon < 1) continue;
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                headers[name] = value;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    Int32.TryParse(value, out contentLength);
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
            }
            if (contentLength < 0 || contentLength > 32 * 1024 * 1024)
                throw new InvalidDataException("The request body is too large.");
            byte[] body = chunked ? await ReadChunkedBodyAsync(network).ConfigureAwait(false) :
                await ReadFixedBodyAsync(network, contentLength).ConfigureAwait(false);
            var request = new HttpListenerRequest(requestLine[0], requestLine[1], headers, body);
            var response = new HttpListenerResponse(client);
            return new HttpListenerContext(request, response);
        }

        private static async Task<byte[]> ReadHeadersAsync(Stream stream)
        {
            using (var memory = new MemoryStream())
            {
                int state = 0;
                while (memory.Length < 65536)
                {
                    int value = await ReadByteAsync(stream).ConfigureAwait(false);
                    if (value < 0) throw new EndOfStreamException("Connection ended before HTTP headers completed.");
                    memory.WriteByte((byte)value);
                    state = state == 0 && value == 13 ? 1 : state == 1 && value == 10 ? 2 :
                        state == 2 && value == 13 ? 3 : state == 3 && value == 10 ? 4 : (value == 13 ? 1 : 0);
                    if (state == 4)
                    {
                        byte[] all = memory.ToArray();
                        return all.Take(all.Length - 4).ToArray();
                    }
                }
                throw new InvalidDataException("HTTP headers exceed the size limit.");
            }
        }

        private static async Task<int> ReadByteAsync(Stream stream)
        {
            byte[] one = new byte[1];
            int read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
            return read == 0 ? -1 : one[0];
        }

        private static async Task<byte[]> ReadFixedBodyAsync(Stream stream, int length)
        {
            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(body, offset, length - offset).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Connection ended before the request body completed.");
                offset += read;
            }
            return body;
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream)
        {
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    string sizeLine = await ReadAsciiLineAsync(stream).ConfigureAwait(false);
                    string sizeText = sizeLine.Split(';')[0].Trim();
                    int size = Int32.Parse(sizeText, System.Globalization.NumberStyles.HexNumber);
                    if (size == 0)
                    {
                        while ((await ReadAsciiLineAsync(stream).ConfigureAwait(false)).Length != 0) { }
                        return body.ToArray();
                    }
                    if (body.Length + size > 32 * 1024 * 1024) throw new InvalidDataException("The request body is too large.");
                    byte[] chunk = await ReadFixedBodyAsync(stream, size).ConfigureAwait(false);
                    body.Write(chunk, 0, chunk.Length);
                    string ending = await ReadAsciiLineAsync(stream).ConfigureAwait(false);
                    if (ending.Length != 0) throw new InvalidDataException("Malformed HTTP chunk boundary.");
                }
            }
        }

        private static async Task<string> ReadAsciiLineAsync(Stream stream)
        {
            using (var line = new MemoryStream())
            {
                while (line.Length < 8192)
                {
                    int value = await ReadByteAsync(stream).ConfigureAwait(false);
                    if (value < 0) throw new EndOfStreamException("Connection ended inside a chunked request.");
                    if (value == 10)
                    {
                        byte[] bytes = line.ToArray();
                        int length = bytes.Length > 0 && bytes[bytes.Length - 1] == 13 ? bytes.Length - 1 : bytes.Length;
                        return Encoding.ASCII.GetString(bytes, 0, length);
                    }
                    line.WriteByte((byte)value);
                }
                throw new InvalidDataException("HTTP chunk line exceeds the size limit.");
            }
        }
    }

    internal sealed class HttpListenerRequest
    {
        public readonly Uri Url;
        public readonly string HttpMethod;
        public readonly HttpHeaders Headers;
        public readonly Stream InputStream;
        public readonly long ContentLength64;
        public readonly string ContentType;
        public HttpListenerRequest(string method, string target, HttpHeaders headers, byte[] body)
        {
            HttpMethod = method;
            Url = new Uri("http://127.0.0.1" + (target.StartsWith("/") ? target : "/" + target));
            Headers = headers;
            InputStream = new MemoryStream(body, false);
            ContentLength64 = body.Length;
            ContentType = headers["Content-Type"];
        }
    }

    internal sealed class HttpListenerResponse
    {
        public readonly HttpHeaders Headers = new HttpHeaders();
        public readonly MemoryStream OutputStream = new MemoryStream();
        public int StatusCode { get; set; }
        public long ContentLength64 { get; set; }
        public string ContentType { get; set; }
        private readonly TcpClient client;
        private bool closed;
        public HttpListenerResponse(TcpClient client) { this.client = client; }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try
            {
                byte[] body = OutputStream.ToArray();
                string contentType = ContentType ?? "application/octet-stream";
                var header = new StringBuilder();
                header.Append("HTTP/1.1 ").Append(StatusCode == 0 ? 200 : StatusCode).Append(' ')
                    .Append(ReasonPhrase(StatusCode == 0 ? 200 : StatusCode)).Append("\r\n")
                    .Append("Content-Length: ").Append(ContentLength64 > 0 ? ContentLength64 : body.Length).Append("\r\n")
                    .Append("Content-Type: ").Append(contentType).Append("\r\n");
                foreach (KeyValuePair<string, string> value in Headers.Values)
                    header.Append(value.Key).Append(": ").Append(value.Value).Append("\r\n");
                header.Append("Connection: close\r\n\r\n");
                byte[] head = Encoding.ASCII.GetBytes(header.ToString());
                NetworkStream stream = client.GetStream();
                stream.Write(head, 0, head.Length);
                if (body.Length > 0) stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            finally { client.Close(); }
        }

        public void Abort()
        {
            closed = true;
            client.Close();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 402: return "Payment Required";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 408: return "Request Timeout";
                case 413: return "Payload Too Large";
                case 425: return "Too Early";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                default: return "Response";
            }
        }
    }

    internal sealed class HttpHeaders
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string this[string name]
        {
            get { string value; return Values.TryGetValue(name, out value) ? value : null; }
            set { Values[name] = value; }
        }
    }

    internal sealed class ProxyConfig
    {
        public string listen_prefix { get; set; }
        public string upstream_base_url { get; set; }
        public string zen_upstream_base_url { get; set; }
        public string credential_source { get; set; }
        public string zen_credential_source { get; set; }
        public string local_api_key { get; set; }
        public string public_model { get; set; }
        public string upstream_model { get; set; }
        public int credential_count { get; set; }
        public int zen_credential_count { get; set; }
        public int retry_server_error_from { get; set; }
        public int[] retry_http_statuses { get; set; }
        public string streaming_mode { get; set; }
    }

    internal sealed class CredentialSnapshot
    {
        public readonly List<string> Keys;
        public readonly string Hash;

        public CredentialSnapshot(List<string> keys, string hash)
        {
            Keys = keys;
            Hash = hash;
        }
    }

    internal static class Program
    {
        private static string DefaultCredentialPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "api.txt"); }
        }
        private const string DefaultConfigPath = "config.json";
        private const string DefaultListenPrefix = "http://127.0.0.1:4001/";
        private const string DefaultUpstreamBaseUrl = "https://opencode.ai/zen/go/v1";
        private const string PublicModel = "opencode-go";
        private const string UpstreamModel = "kimi-k3";
        private const int MaximumRequestBytes = 32 * 1024 * 1024;
        private static readonly JavaScriptSerializer Json = CreateJsonSerializer();
        private static readonly HttpClient UpstreamClient = CreateHttpClient();

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                try { Console.Error.WriteLine("UNHANDLED " + DescribeException(eventArgs.ExceptionObject as Exception)); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
            {
                eventArgs.SetObserved();
            };
            try
            {
                string mode = GetArgument(args, "-Mode") ?? "Serve";
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                string configPath = Path.GetFullPath(Path.Combine(exeDir, GetArgument(args, "-ConfigPath") ?? DefaultConfigPath));
                if (mode.Equals("GenerateConfig", StringComparison.OrdinalIgnoreCase))
                {
                    string credentialPath = Path.GetFullPath(GetArgument(args, "-CredentialPath") ?? DefaultCredentialPath);
                    GenerateConfig(configPath, credentialPath, DefaultListenPrefix, DefaultUpstreamBaseUrl,
                        CreateLocalApiKey(), PublicModel, UpstreamModel);
                    Console.WriteLine("CONFIG_READY " + configPath);
                    return 0;
                }

                if (mode.Equals("Serve", StringComparison.OrdinalIgnoreCase))
                {
                    Serve(configPath).GetAwaiter().GetResult();
                    return 0;
                }

                if (mode.Equals("MockUpstream", StringComparison.OrdinalIgnoreCase))
                {
                    int port = Int32.Parse(GetArgument(args, "-Port") ?? "0");
                    int failureStatus = Int32.Parse(GetArgument(args, "-FailureStatus") ?? "429");
                    string reportPath = Path.GetFullPath(GetArgument(args, "-ReportPath") ?? "mock-upstream-report.json");
                    RunMockUpstream(port, reportPath, failureStatus).GetAwaiter().GetResult();
                    return 0;
                }

                if (mode.Equals("ProbeUpstream", StringComparison.OrdinalIgnoreCase))
                {
                    string credentialPath = Path.GetFullPath(GetArgument(args, "-CredentialPath") ?? DefaultCredentialPath);
                    string upstreamUrl = GetArgument(args, "-UpstreamUrl") ?? DefaultUpstreamBaseUrl;
                    string model = GetArgument(args, "-UpstreamModel") ?? UpstreamModel;
                    return ProbeUpstream(credentialPath, upstreamUrl, model);
                }

                Console.Error.WriteLine("Unknown mode.");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL " + DescribeException(ex));
                return 1;
            }
        }

        private static JavaScriptSerializer CreateJsonSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaximumRequestBytes, RecursionLimit = 128 };
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        }

        private static string GetArgument(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static string CreateLocalApiKey()
        {
            byte[] random = new byte[32];
            using (var generator = new System.Security.Cryptography.RNGCryptoServiceProvider())
                generator.GetBytes(random);
            return Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void GenerateConfig(string configPath, string credentialPath, string listenPrefix,
            string upstreamBaseUrl, string localApiKey, string publicModel, string upstreamModel)
        {
            CredentialSnapshot snapshot = ReadCredentials(credentialPath);
            if (snapshot.Keys.Count == 0)
                throw new InvalidOperationException("The configured credential file contains no non-empty API keys.");

            var config = new ProxyConfig
            {
                listen_prefix = listenPrefix,
                upstream_base_url = upstreamBaseUrl.TrimEnd('/'),
                credential_source = credentialPath,
                local_api_key = localApiKey,
                public_model = publicModel,
                upstream_model = upstreamModel,
                credential_count = snapshot.Keys.Count,
                retry_server_error_from = 500,
                retry_http_statuses = new[] { 401, 402, 403, 408, 425, 429 },
                streaming_mode = "buffer-before-response-to-preserve-failover"
            };
            WriteConfigAtomically(configPath, config);
        }

        private static void WriteConfigAtomically(string path, ProxyConfig config)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string json = Json.Serialize(config);
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }

        private static ProxyConfig ReadConfig(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Generated config file was not found.", fullPath);
            var config = Json.Deserialize<ProxyConfig>(File.ReadAllText(fullPath, Encoding.UTF8));
            if (config == null || String.IsNullOrWhiteSpace(config.listen_prefix) ||
                String.IsNullOrWhiteSpace(config.credential_source) ||
                String.IsNullOrWhiteSpace(config.upstream_base_url) ||
                String.IsNullOrWhiteSpace(config.local_api_key) ||
                String.IsNullOrWhiteSpace(config.public_model) ||
                String.IsNullOrWhiteSpace(config.upstream_model))
                throw new InvalidDataException("Generated config is missing a required setting.");
            // Resolve relative credential_source against the config file's directory,
            // so the proxy works from any working directory (e.g. launched hidden
            // by a scheduled task whose CWD is System32).
            if (!Path.IsPathRooted(config.credential_source))
                config.credential_source = Path.Combine(Path.GetDirectoryName(fullPath) ?? ".", config.credential_source);
            if (!String.IsNullOrEmpty(config.zen_credential_source) && !Path.IsPathRooted(config.zen_credential_source))
                config.zen_credential_source = Path.Combine(Path.GetDirectoryName(fullPath) ?? ".", config.zen_credential_source);
            return config;
        }

        private static CredentialSnapshot ReadCredentials(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text = new UTF8Encoding(false, true).GetString(bytes);
            var keys = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#") && !line.StartsWith("//"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            return new CredentialSnapshot(keys, hash);
        }

        private static int FindAvailablePort(int preferred)
        {
            try
            {
                var tcpl = new TcpListener(IPAddress.Loopback, preferred);
                tcpl.Start();
                int port = ((IPEndPoint)tcpl.LocalEndpoint).Port;
                tcpl.Stop();
                return port;
            }
            catch { }
            for (int p = preferred; p < 65535; p++)
            {
                try
                {
                    var tcpl = new TcpListener(IPAddress.Loopback, p);
                    tcpl.Start();
                    int port = ((IPEndPoint)tcpl.LocalEndpoint).Port;
                    tcpl.Stop();
                    return port;
                }
                catch { }
            }
            throw new InvalidOperationException("No available TCP port found.");
        }

        // Self-healing setup: on a fresh Windows, running the proxy once makes it
        // permanent — it imports keys from a known backup if the local api.txt is
        // empty, and registers a hidden logon autostart task when the launcher is
        // present. Safe to run on every start (idempotent, non-elevated).
        private static void EnsureSelfSetup()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                try
                {
                    string keysPath = Path.Combine(baseDir, "api.txt");
                    bool hasKeys = false;
                    if (File.Exists(keysPath))
                        foreach (string line in File.ReadAllLines(keysPath))
                        {
                            string k = line.Trim();
                            if (k.Length > 0 && !k.StartsWith("#") && !k.StartsWith("//")) { hasKeys = true; break; }
                        }
                    string backup = @"F:\backup\windowsapps\credentials\opencodego\api.txt";
                    if (!hasKeys && File.Exists(backup))
                    {
                        File.Copy(backup, keysPath, true);
                        Console.WriteLine("AUTOSETUP keys_imported=true");
                    }
                }
                catch { }
                string vbs = Path.Combine(baseDir, "run-hidden.vbs");
                if (!File.Exists(vbs)) return;
                if (TaskExists("OpenCodeGoProxy")) return;
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Create /F /TN \"OpenCodeGoProxy\" /SC ONLOGON /TR \"wscript.exe \\\"" + vbs + "\\\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    Console.WriteLine("AUTOSETUP task_register_exit=" + p.ExitCode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("AUTOSETUP_FAILED " + ex.GetType().Name);
            }
        }

        private static bool TaskExists(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Query /TN \"" + name + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static async Task Serve(string configPath)
        {
            ProxyConfig config = ReadConfig(configPath);
            Uri prefixUri = new Uri(config.listen_prefix);
            int requestedPort = prefixUri.Port;
            int actualPort = FindAvailablePort(requestedPort);
            string actualPrefix = "http://127.0.0.1:" + actualPort + "/";
            if (actualPort != requestedPort)
                Console.WriteLine("PORT_SHIFT from=" + requestedPort + " to=" + actualPort);
            var listener = new HttpListener();
            listener.Prefixes.Add(actualPrefix);
            listener.Start();
            CredentialSnapshot snapshot = ReadCredentials(config.credential_source);
            InitializeProtocolStore(configPath);
            StartUsagePoller(config);
            Console.WriteLine("READY listen=" + actualPrefix + " model=" + config.public_model +
                " configured_keys=" + snapshot.Keys.Count + " config=" + configPath);
            EnsureSelfSetup();
            // System tray icon (runs on its own STA thread with message pump).
            try
            {
                var trayThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var tray = new ProxyTrayIcon(config.credential_source, configPath);
                        System.Windows.Forms.Application.Run();
                    }
                    catch { }
                });
                trayThread.IsBackground = true;
                trayThread.SetApartmentState(System.Threading.ApartmentState.STA);
                trayThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("TRAY_INIT_FAILED " + ex.GetType().Name);
            }
            try
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // A single malformed, half-open, or aborted client connection must
                        // never tear down the listener. Log it and keep serving so that the
                        // failover brain stays reachable for every client and session flow.
                        Console.WriteLine("CONNECTION_ERROR type=" + ex.GetType().Name + " detail=omitted");
                        Thread.Sleep(20);
                        continue;
                    }
                    Task requestTask = Task.Run(() =>
                    {
                        try
                        {
                            HandleRequest(context, config, configPath).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            // A client that disconnects mid-response must not fault the worker.
                            Console.WriteLine("REQUEST_TASK_ERROR type=" + ex.GetType().Name + " detail=omitted");
                            try { context.Response.Abort(); } catch { }
                        }
                    });
                }
            }
            finally
            {
                listener.Close();
            }
        }

        private static async Task HandleRequest(HttpListenerContext context, ProxyConfig config, string configPath)
        {
            string path = "/";
            try
            {
                path = context.Request.Url.AbsolutePath;
                if (String.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    AddCorsHeaders(context.Response);
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                    context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    CredentialSnapshot healthSnapshot = RefreshCredentials(config, configPath);
                    WriteJson(context.Response, 200, new Dictionary<string, object>
                    {
                        { "status", "ok" }, { "model", config.public_model },
                        { "credential_count", healthSnapshot.Keys.Count }, { "bind", config.listen_prefix }
                    });
                    return;
                }

                if (!IsAuthorized(context.Request, config.local_api_key))
                {
                    WriteError(context.Response, 401, "unauthorized", "Use the local proxy bearer key.");
                    return;
                }

                if (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) &&
                    context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    CredentialSnapshot modelCredentials = RefreshCredentials(config, configPath);
                    if (modelCredentials.Keys.Count == 0)
                    {
                        WriteError(context.Response, 503, "no_upstream_credentials", "Add one API key per line to the configured credential file.");
                        return;
                    }
                    await WriteModelCatalog(context.Response, config, modelCredentials.Keys).ConfigureAwait(false);
                    return;
                }

                bool supportedCompletionPath =
                    path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase);
                if (!supportedCompletionPath ||
                    !context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    WriteError(context.Response, 404, "not_found", "Use a supported POST /v1 endpoint or GET /v1/models.");
                    return;
                }

                CredentialSnapshot credentials = RefreshCredentials(config, configPath);
                if (credentials.Keys.Count == 0)
                {
                    WriteError(context.Response, 503, "no_upstream_credentials", "Add one API key per line to the configured credential file.");
                    return;
                }

                byte[] inbound = await ReadRequestBody(context.Request).ConfigureAwait(false);
                byte[] outbound = RewriteModel(inbound, path, config.public_model, config.upstream_model, config, credentials.Keys);
                await ForwardWithFailover(context, config, credentials.Keys, outbound, configPath).ConfigureAwait(false);
            }
            catch (RequestTooLargeException)
            {
                WriteError(context.Response, 413, "request_too_large", "The request exceeded the configured size limit.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("REQUEST_ERROR path=" + path + " type=" + ex.GetType().Name + " message=" + (ex.Message ?? "null") + " stack=" + (ex.StackTrace ?? "null").Replace("\n", " ").Replace("\r", ""));
                SafeWriteError(context.Response, 502, "proxy_error", "The proxy could not complete the upstream request.");
            }
        }

        private static CredentialSnapshot RefreshCredentials(ProxyConfig config, string configPath)
        {
            CredentialSnapshot snapshot = ReadCredentials(config.credential_source);
            if (snapshot.Keys.Count != config.credential_count)
            {
                config.credential_count = snapshot.Keys.Count;
                WriteConfigAtomically(configPath, config);
                Console.WriteLine("CREDENTIALS_REFRESHED configured_keys=" + snapshot.Keys.Count);
            }
            return snapshot;
        }

        private static bool IsAuthorized(HttpListenerRequest request, string expected)
        {
            string header = request.Headers["Authorization"] ?? "";
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                FixedTimeEquals(header.Substring(7).Trim(), expected)) return true;
            string apiKey = request.Headers["x-api-key"] ?? "";
            return FixedTimeEquals(apiKey.Trim(), expected);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            int diff = left.Length ^ right.Length;
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private static async Task<byte[]> ReadRequestBody(HttpListenerRequest request)
        {
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + read > MaximumRequestBytes) throw new RequestTooLargeException();
                    memory.Write(buffer, 0, read);
                }
                if (memory.Length == 0) throw new InvalidDataException("Request body is empty.");
                return memory.ToArray();
            }
        }

        private static bool IsSafeModelId(string modelId, ProxyConfig config, IList<string> keys)
        {
            if (String.IsNullOrWhiteSpace(modelId) || modelId.Length > 200) return false;
            foreach (char character in modelId)
                if (Char.IsControl(character) || Char.IsWhiteSpace(character)) return false;
            if (!String.IsNullOrEmpty(config.local_api_key) && modelId.IndexOf(config.local_api_key, StringComparison.Ordinal) >= 0) return false;
            if (keys != null)
                foreach (string key in keys)
                    if (!String.IsNullOrEmpty(key) && modelId.IndexOf(key, StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        private static string RedactSecrets(string value, ProxyConfig config, IList<string> keys)
        {
            if (String.IsNullOrEmpty(value)) return value;
            string safe = value;
            if (!String.IsNullOrEmpty(config.local_api_key)) safe = safe.Replace(config.local_api_key, "[REDACTED]");
            if (keys != null)
                foreach (string key in keys)
                    if (!String.IsNullOrEmpty(key)) safe = safe.Replace(key, "[REDACTED]");
            return safe;
        }

        private static async Task WriteModelCatalog(HttpListenerResponse destination, ProxyConfig config, IList<string> keys)
        {
            var modelIds = new SortedSet<string>(StringComparer.Ordinal);
            Uri target = new Uri(config.upstream_base_url.TrimEnd('/') + "/models");
            for (int index = 0; index < keys.Count; index++)
            {
                int slot = index + 1;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, target))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys[index]);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        using (HttpResponseMessage response = await UpstreamClient.SendAsync(request,
                            HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            if (!response.IsSuccessStatusCode)
                            {
                                Console.WriteLine("MODEL_CATALOG status=" + status + " key_slot=" + slot + " detail=omitted");
                                continue;
                            }

                            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var root = Json.DeserializeObject(json) as IDictionary<string, object>;
                            object dataValue;
                            IList data = root != null && root.TryGetValue("data", out dataValue) ? dataValue as IList : null;
                            int beforeCount = modelIds.Count;
                            if (data != null)
                            {
                                foreach (object entry in data)
                                {
                                    string id = null;
                                    var model = entry as IDictionary<string, object>;
                                    if (model != null)
                                    {
                                        object idValue;
                                        if (model.TryGetValue("id", out idValue)) id = Convert.ToString(idValue);
                                    }
                                    else if (entry is string) id = (string)entry;
                                    if (IsSafeModelId(id, config, keys)) modelIds.Add(id);
                                }
                            }
                            Console.WriteLine("MODEL_CATALOG status=" + status + " key_slot=" + slot + " models_added=" + (modelIds.Count - beforeCount));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("MODEL_CATALOG transport_error=" + ex.GetType().Name + " key_slot=" + slot + " detail=omitted");
                }
            }

            if (modelIds.Count == 0)
            {
                WriteError(destination, 502, "model_catalog_unavailable", "The upstream model catalog is temporarily unavailable.");
                return;
            }

            var models = new List<Dictionary<string, object>>();
            foreach (string id in modelIds)
                models.Add(new Dictionary<string, object> { { "id", id }, { "object", "model" }, { "owned_by", "opencode-go" } });
            WriteJson(destination, 200, new Dictionary<string, object> { { "object", "list" }, { "data", models } });
        }

        private static byte[] RewriteModel(byte[] input, string requestPath, string publicModel, string upstreamModel, ProxyConfig config, IList<string> keys)
        {
            // Try parsing JSON to rewrite model name. If JavaScriptSerializer fails
            // (known .NET Framework issue with certain model IDs), use regex fallback
            // to rewrite just the model field and forward the rest unchanged.
            string inputText = Encoding.UTF8.GetString(input);
            object decoded = null;
            try { decoded = Json.DeserializeObject(inputText); }
            catch (Exception ex)
            {
                Console.WriteLine("REWRITE_MODEL json_parse_failed type=" + ex.GetType().Name + " using_regex_fallback");
                return RegexFallbackRewriteModel(input, inputText, publicModel, upstreamModel, config, keys);
            }
            var body = decoded as IDictionary<string, object>;
            if (body == null) return input;
            object model;
            if (!body.TryGetValue("model", out model) || model == null)
                return input;
            if (requestPath.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                object inputValue;
                if (!body.TryGetValue("input", out inputValue) || !IsNonEmptyInput(inputValue))
                    return input;
            }
            else
            {
                object messages;
                IList messageList = body.TryGetValue("messages", out messages) ? messages as IList : null;
                if (messageList == null || messageList.Count == 0)
                    return input;
            }

            string requestedModel = Convert.ToString(model);
            string selectedModel = requestedModel;
            if (String.Equals(requestedModel, publicModel, StringComparison.Ordinal))
            {
                selectedModel = upstreamModel;
            }
            else
            {
                string[] providerPrefixes = { publicModel + "/", "opencode-go-failover/" };
                foreach (string prefix in providerPrefixes)
                {
                    if (!String.IsNullOrEmpty(prefix) && requestedModel.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        selectedModel = requestedModel.Substring(prefix.Length);
                        break;
                    }
                }
            }
            if (!IsSafeModelId(selectedModel, config, keys))
                return input;
            body["model"] = selectedModel;
            return Encoding.UTF8.GetBytes(Json.Serialize(body));
        }

        private static byte[] RegexFallbackRewriteModel(byte[] input, string inputText, string publicModel, string upstreamModel, ProxyConfig config, IList<string> keys)
        {
            // Regex-based model rewrite when JSON parser fails.
            // Only rewrite if the model is "opencode-go" -> upstreamModel.
            if (!inputText.Contains(publicModel)) return input;
            string escapedUpstream = upstreamModel.Replace("$", "\\$");
            string rewritten = Regex.Replace(inputText,
                "(\\\"model\\\"\\s*:\\s*\")" + Regex.Escape(publicModel) + "(\\\")",
                "$1" + escapedUpstream + "$2");
            if (rewritten == inputText) return input;
            Console.WriteLine("REWRITE_MODEL_REGEX rewritten public_model=" + publicModel + " upstream=" + upstreamModel);
            return Encoding.UTF8.GetBytes(rewritten);
        }

        /// <summary>
        /// Fallback JSON deserializer that handles edge cases the
        /// JavaScriptSerializer chokes on (unquoted keys, lone surrogates, etc.).
        /// Returns null on total failure so callers can throw a clear error.
        /// </summary>
        private static object SafeDeserialize(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            // First try the stock serializer with a relaxed setting.
            try
            {
                var relaxed = new JavaScriptSerializer { MaxJsonLength = MaximumRequestBytes, RecursionLimit = 256 };
                return relaxed.Deserialize(text, typeof(object));
            }
            catch { }
            // If that fails, try trimming and re-quoting obvious problems.
            try
            {
                string trimmed = text.Trim();
                if (trimmed.Length > 1 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}')
                {
                    // Attempt a minimal fix: ensure all property names are quoted.
                    string fixedJson = Regex.Replace(trimmed, @"(?<=[{,])\s*(\w[\w\-\.]*)\s*:", @"""$1"":");
                    return Json.Deserialize(fixedJson, typeof(object));
                }
            }
            catch { }
            return null;
        }

        private static bool IsNonEmptyInput(object value)
        {
            if (value == null) return false;
            string text = value as string;
            if (text != null) return !String.IsNullOrWhiteSpace(text);
            IList list = value as IList;
            if (list != null) return list.Count > 0;
            IDictionary<string, object> map = value as IDictionary<string, object>;
            return map != null && map.Count > 0;
        }

        // ---------------------------------------------------------------------
        // Protocol ownership (model -> wire) and protocol repair.
        //
        // The OpenCode Go / Zen gateways are mixed-protocol: most models answer
        // Chat Completions, a documented set answers the Responses API only, and
        // some answer Anthropic Messages. If a caller picks the wrong SDK for a
        // model the gateway answers HTTP 400
        //   {"type":"error","error":{"type":"ModelProtocolUnsupported",
        //    "message":"Model does not support this protocol."}}
        // e.g. muse-spark-1.3-contributor reached on /chat/completions although
        // Go serves it only on /responses.
        //
        // The proxy therefore owns the mapping instead of echoing whatever wire
        // the caller happened to speak. It resolves the model's wire, translates
        // the request/response between the two OpenAI wires when they differ, and
        // repairs a wrong guess by retrying the model on its other OpenAI wire and
        // remembering the winner in model-protocols.json. That fixes the whole
        // class of failures for every model, not just the ones known today.
        // ---------------------------------------------------------------------

        private enum UpstreamWire { Chat, Responses, Messages }

        private static readonly object ProtocolStoreGate = new object();
        private static Dictionary<string, UpstreamWire> ProtocolStore =
            new Dictionary<string, UpstreamWire>(StringComparer.OrdinalIgnoreCase);
        private static string ProtocolStorePath;

        /// <summary>
        /// Seed the documented Go/Zen Responses-only models and merge any
        /// previously learned mapping. The file is created on first run so the
        /// table is inspectable and editable without rebuilding the proxy.
        /// </summary>
        private static void InitializeProtocolStore(string configPath)
        {
            ProtocolStorePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "model-protocols.json");
            var seed = new Dictionary<string, UpstreamWire>(StringComparer.OrdinalIgnoreCase);
            // Documented as served over POST {base}/responses (opencode.ai/docs/go).
            foreach (string id in new[]
            {
                "gpt-5.6-luna", "gpt-6-luna", "grok-4.5", "grok-4.6", "grok-4.7",
                "muse-spark-1.2-contributor", "muse-spark-1.3-contributor",
            })
                seed[id] = UpstreamWire.Responses;

            var store = new Dictionary<string, UpstreamWire>(seed, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ProtocolStorePath))
                {
                    var stored = Json.DeserializeObject(File.ReadAllText(ProtocolStorePath, Encoding.UTF8))
                        as IDictionary<string, object>;
                    if (stored != null)
                        foreach (KeyValuePair<string, object> pair in stored)
                        {
                            UpstreamWire wire;
                            if (!String.IsNullOrWhiteSpace(pair.Key) && TryParseWire(Convert.ToString(pair.Value), out wire))
                                store[pair.Key] = wire;
                        }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROTOCOL_STORE_LOAD_ERROR type=" + ex.GetType().Name + " detail=omitted");
            }
            lock (ProtocolStoreGate)
            {
                ProtocolStore = store;
                PersistProtocolStore();
            }
            Console.WriteLine("PROTOCOL_STORE_READY path=" + ProtocolStorePath + " entries=" + ProtocolStore.Count);
        }

        private static void PersistProtocolStore()
        {
            try
            {
                if (String.IsNullOrEmpty(ProtocolStorePath)) return;
                var map = new Dictionary<string, object>();
                foreach (KeyValuePair<string, UpstreamWire> pair in ProtocolStore) map[pair.Key] = WireName(pair.Value);
                string temporary = ProtocolStorePath + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(map) + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(ProtocolStorePath)) File.Replace(temporary, ProtocolStorePath, null);
                else File.Move(temporary, ProtocolStorePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROTOCOL_STORE_SAVE_ERROR type=" + ex.GetType().Name + " detail=omitted");
            }
        }

        private static bool TryGetRememberedWire(string model, out UpstreamWire wire)
        {
            wire = UpstreamWire.Chat;
            if (String.IsNullOrEmpty(model)) return false;
            lock (ProtocolStoreGate)
            {
                return ProtocolStore.TryGetValue(model, out wire);
            }
        }

        private static void RememberWire(string model, UpstreamWire wire)
        {
            if (String.IsNullOrEmpty(model)) return;
            lock (ProtocolStoreGate)
            {
                UpstreamWire current;
                if (ProtocolStore.TryGetValue(model, out current) && current == wire) return;
                ProtocolStore[model] = wire;
                PersistProtocolStore();
            }
        }

        private static UpstreamWire WireFromPath(string absolutePath)
        {
            if (absolutePath != null && absolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                return UpstreamWire.Responses;
            if (absolutePath != null && absolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                return UpstreamWire.Messages;
            return UpstreamWire.Chat;
        }

        private static string PathForWire(UpstreamWire wire)
        {
            if (wire == UpstreamWire.Responses) return "responses";
            if (wire == UpstreamWire.Messages) return "messages";
            return "chat/completions";
        }

        private static string WireName(UpstreamWire wire)
        {
            if (wire == UpstreamWire.Responses) return "responses";
            if (wire == UpstreamWire.Messages) return "messages";
            return "chat";
        }

        private static bool TryParseWire(string text, out UpstreamWire wire)
        {
            wire = UpstreamWire.Chat;
            if (String.IsNullOrWhiteSpace(text)) return false;
            if (text.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("openai-responses", StringComparison.OrdinalIgnoreCase)) { wire = UpstreamWire.Responses; return true; }
            if (text.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("anthropic-messages", StringComparison.OrdinalIgnoreCase)) { wire = UpstreamWire.Messages; return true; }
            if (text.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("completions", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("openai-chat", StringComparison.OrdinalIgnoreCase)) { wire = UpstreamWire.Chat; return true; }
            return false;
        }

        /// <summary>True for the gateway's model/protocol mismatch rejection.</summary>
        private static bool IsProtocolUnsupported(int status, string responseText)
        {
            if (status != 400 || String.IsNullOrEmpty(responseText)) return false;
            if (responseText.IndexOf("ModelProtocolUnsupported", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return responseText.IndexOf("does not support this protocol", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when the upstream rejects due to exhausted account balance.</summary>
        private static bool IsInsufficientFunds(int status, string responseText)
        {
            if (status != 400 && status != 402 && status != 403) return false;
            if (String.IsNullOrEmpty(responseText)) return false;
            return responseText.IndexOf("Insufficient account funds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   responseText.IndexOf("insufficient", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   responseText.IndexOf("funds", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when the upstream rejects due to workspace privacy policy.</summary>
        private static bool IsPrivacyPolicyBlocked(int status, string responseText)
        {
            return IsWorkspacePolicyRetryable(responseText);
        }

        /// <summary>True when a failure should trigger a Zen upstream fallback.</summary>
        private static bool ShouldZenFallback(int status, string responseText)
        {
            return IsInsufficientFunds(status, responseText) || IsPrivacyPolicyBlocked(status, responseText);
        }

        private static bool CanTranslate(UpstreamWire from, UpstreamWire to)
        {
            return (from == UpstreamWire.Chat && to == UpstreamWire.Responses) ||
                   (from == UpstreamWire.Responses && to == UpstreamWire.Chat);
        }

        /// <summary>
        /// Maps Go-tier model names to their Zen free-tier equivalents when
        /// falling back due to privacy/training policy blocks. Returns null
        /// if no mapping exists (keep the original model name).
        /// </summary>
        private static string RemapModelForZen(string goModel)
        {
            if (String.IsNullOrEmpty(goModel)) return null;
            if (goModel.Equals("muse-spark-1.2-contributor", StringComparison.OrdinalIgnoreCase))
                return "muse-spark-1.2-contributor-free";
            if (goModel.Equals("muse-spark-1.3-contributor", StringComparison.OrdinalIgnoreCase))
                return "muse-spark-1.3-contributor-free";
            return null;
        }

        private static byte[] TranslateRequest(byte[] payload, UpstreamWire from, UpstreamWire to, out bool translated)
        {
            translated = false;
            if (from == to || !CanTranslate(from, to)) return payload;
            var source = Json.DeserializeObject(Encoding.UTF8.GetString(payload)) as IDictionary<string, object>;
            if (source == null) return payload;
            IDictionary<string, object> target = from == UpstreamWire.Chat
                ? ChatRequestToResponses(source)
                : ResponsesRequestToChat(source);
            // The proxy buffers the upstream body before replying, so a translated turn is
            // requested non-streaming and re-framed locally into the caller's wire.
            target["stream"] = false;
            translated = true;
            return Encoding.UTF8.GetBytes(Json.Serialize(target));
        }

        private static string ExtractText(object content)
        {
            if (content == null) return null;
            string text = content as string;
            if (text != null) return text;
            var parts = content as IList;
            if (parts == null) return Convert.ToString(content);
            var builder = new StringBuilder();
            foreach (object entry in parts)
            {
                var part = entry as IDictionary<string, object>;
                if (part == null) continue;
                if (part.ContainsKey("text")) builder.Append(Convert.ToString(part["text"]));
            }
            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static string MessageText(IDictionary<string, object> item)
        {
            return item != null && item.ContainsKey("content") ? ExtractText(item["content"]) : null;
        }

        private static object TranslateToolChoice(object choice)
        {
            var map = choice as IDictionary<string, object>;
            if (map == null) return choice;
            var function = map.ContainsKey("function") ? map["function"] as IDictionary<string, object> : null;
            if (function == null) return choice;
            var result = new Dictionary<string, object>();
            result["type"] = "function";
            if (function.ContainsKey("name")) result["name"] = function["name"];
            return result;
        }

        private static IDictionary<string, object> ChatRequestToResponses(IDictionary<string, object> chat)
        {
            var target = new Dictionary<string, object>();
            if (chat.ContainsKey("model")) target["model"] = chat["model"];
            var input = new List<object>();
            var instructions = new StringBuilder();
            var messages = chat.ContainsKey("messages") ? chat["messages"] as IList : null;
            if (messages != null)
            {
                foreach (object entry in messages)
                {
                    var message = entry as IDictionary<string, object>;
                    if (message == null) continue;
                    string role = message.ContainsKey("role") ? Convert.ToString(message["role"]) : "user";
                    string text = ExtractText(message.ContainsKey("content") ? message["content"] : null);
                    if (String.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!String.IsNullOrEmpty(text))
                        {
                            if (instructions.Length > 0) instructions.Append("\n\n");
                            instructions.Append(text);
                        }
                        continue;
                    }
                    if (String.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                    {
                        input.Add(new Dictionary<string, object>
                        {
                            { "type", "function_call_output" },
                            { "call_id", message.ContainsKey("tool_call_id") ? message["tool_call_id"] : null },
                            { "output", text ?? String.Empty },
                        });
                        continue;
                    }
                    if (!String.IsNullOrEmpty(text))
                        input.Add(new Dictionary<string, object>
                        {
                            { "type", "message" },
                            { "role", role },
                            { "content", new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "type", String.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text" },
                                        { "text", text },
                                    },
                                }
                            },
                        });
                    var toolCalls = message.ContainsKey("tool_calls") ? message["tool_calls"] as IList : null;
                    if (toolCalls != null)
                        foreach (object callEntry in toolCalls)
                        {
                            var call = callEntry as IDictionary<string, object>;
                            if (call == null) continue;
                            var function = call.ContainsKey("function") ? call["function"] as IDictionary<string, object> : null;
                            input.Add(new Dictionary<string, object>
                            {
                                { "type", "function_call" },
                                { "call_id", call.ContainsKey("id") ? call["id"] : Guid.NewGuid().ToString("N") },
                                { "name", function != null && function.ContainsKey("name") ? function["name"] : "" },
                                { "arguments", function != null && function.ContainsKey("arguments") ? function["arguments"] : "{}" },
                            });
                        }
                }
            }
            if (instructions.Length > 0) target["instructions"] = instructions.ToString();
            target["input"] = input.ToArray();
            var tools = chat.ContainsKey("tools") ? chat["tools"] as IList : null;
            if (tools != null && tools.Count > 0)
            {
                var converted = new List<object>();
                foreach (object toolEntry in tools)
                {
                    var tool = toolEntry as IDictionary<string, object>;
                    if (tool == null) continue;
                    var function = tool.ContainsKey("function") ? tool["function"] as IDictionary<string, object> : null;
                    if (function == null) continue;
                    var item = new Dictionary<string, object>();
                    item["type"] = "function";
                    item["name"] = function.ContainsKey("name") ? function["name"] : "";
                    if (function.ContainsKey("description")) item["description"] = function["description"];
                    item["parameters"] = function.ContainsKey("parameters") && function["parameters"] != null
                        ? function["parameters"]
                        : new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } };
                    converted.Add(item);
                }
                if (converted.Count > 0) target["tools"] = converted.ToArray();
            }
            if (chat.ContainsKey("tool_choice") && chat["tool_choice"] != null) target["tool_choice"] = TranslateToolChoice(chat["tool_choice"]);
            foreach (string key in new[] { "temperature", "top_p", "parallel_tool_calls", "metadata" })
                if (chat.ContainsKey(key) && chat[key] != null) target[key] = chat[key];
            if (chat.ContainsKey("max_completion_tokens") && chat["max_completion_tokens"] != null) target["max_output_tokens"] = chat["max_completion_tokens"];
            else if (chat.ContainsKey("max_tokens") && chat["max_tokens"] != null) target["max_output_tokens"] = chat["max_tokens"];
            return target;
        }

        private static IDictionary<string, object> ResponsesRequestToChat(IDictionary<string, object> responses)
        {
            var target = new Dictionary<string, object>();
            if (responses.ContainsKey("model")) target["model"] = responses["model"];
            var messages = new List<object>();
            object instructions;
            if (responses.TryGetValue("instructions", out instructions) && instructions != null)
            {
                string text = Convert.ToString(instructions);
                if (!String.IsNullOrEmpty(text))
                    messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", text } });
            }
            object inputValue;
            if (responses.TryGetValue("input", out inputValue) && inputValue != null)
            {
                string single = inputValue as string;
                if (single != null)
                {
                    messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", single } });
                }
                else
                {
                    var items = inputValue as IList;
                    if (items != null)
                        foreach (object entry in items) AppendChatMessage(messages, entry);
                }
            }
            target["messages"] = messages.ToArray();
            var tools = responses.ContainsKey("tools") ? responses["tools"] as IList : null;
            if (tools != null && tools.Count > 0)
            {
                var converted = new List<object>();
                foreach (object toolEntry in tools)
                {
                    var tool = toolEntry as IDictionary<string, object>;
                    if (tool == null) continue;
                    string type = tool.ContainsKey("type") ? Convert.ToString(tool["type"]) : "";
                    if (!String.Equals(type, "function", StringComparison.OrdinalIgnoreCase)) continue;
                    var function = new Dictionary<string, object>();
                    function["name"] = tool.ContainsKey("name") ? tool["name"] : "";
                    if (tool.ContainsKey("description")) function["description"] = tool["description"];
                    function["parameters"] = tool.ContainsKey("parameters") && tool["parameters"] != null
                        ? tool["parameters"]
                        : new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } };
                    converted.Add(new Dictionary<string, object> { { "type", "function" }, { "function", function } });
                }
                if (converted.Count > 0) target["tools"] = converted.ToArray();
            }
            foreach (string key in new[] { "temperature", "top_p" })
                if (responses.ContainsKey(key) && responses[key] != null) target[key] = responses[key];
            if (responses.ContainsKey("max_output_tokens") && responses["max_output_tokens"] != null) target["max_tokens"] = responses["max_output_tokens"];
            return target;
        }

        private static void AppendChatMessage(IList messages, object entry)
        {
            var item = entry as IDictionary<string, object>;
            if (item == null) return;
            string type = item.ContainsKey("type") ? Convert.ToString(item["type"]) : "";
            if (String.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
            {
                var function = new Dictionary<string, object>();
                function["name"] = item.ContainsKey("name") ? item["name"] : "";
                function["arguments"] = item.ContainsKey("arguments") ? item["arguments"] : "{}";
                var call = new Dictionary<string, object>();
                call["id"] = item.ContainsKey("call_id") ? item["call_id"] : Guid.NewGuid().ToString("N");
                call["type"] = "function";
                call["function"] = function;
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", null },
                    { "tool_calls", new object[] { call } },
                });
                return;
            }
            if (String.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "tool" },
                    { "tool_call_id", item.ContainsKey("call_id") ? item["call_id"] : null },
                    { "content", item.ContainsKey("output") ? Convert.ToString(item["output"]) : String.Empty },
                });
                return;
            }
            if (String.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase)) return;
            string role = item.ContainsKey("role") ? Convert.ToString(item["role"]) : "user";
            messages.Add(new Dictionary<string, object>
            {
                { "role", role },
                { "content", ExtractText(item.ContainsKey("content") ? item["content"] : null) ?? String.Empty },
            });
        }

        private static string FinishReasonFor(IDictionary<string, object> responses, bool hasToolCalls)
        {
            string status = responses.ContainsKey("status") ? Convert.ToString(responses["status"]) : "";
            if (String.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                var details = responses.ContainsKey("incomplete_details") ? responses["incomplete_details"] as IDictionary<string, object> : null;
                string reason = details != null && details.ContainsKey("reason") ? Convert.ToString(details["reason"]) : "";
                return String.Equals(reason, "max_output_tokens", StringComparison.OrdinalIgnoreCase) ? "length" : "stop";
            }
            return hasToolCalls ? "tool_calls" : "stop";
        }

        private static IDictionary<string, object> ResponsesDocToChat(IDictionary<string, object> responses)
        {
            var content = new StringBuilder();
            var toolCalls = new List<object>();
            var output = responses.ContainsKey("output") ? responses["output"] as IList : null;
            if (output != null)
                foreach (object entry in output)
                {
                    var item = entry as IDictionary<string, object>;
                    if (item == null) continue;
                    string type = item.ContainsKey("type") ? Convert.ToString(item["type"]) : "";
                    if (String.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = MessageText(item);
                        if (!String.IsNullOrEmpty(text)) content.Append(text);
                    }
                    else if (String.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        var function = new Dictionary<string, object>();
                        function["name"] = item.ContainsKey("name") ? item["name"] : "";
                        function["arguments"] = item.ContainsKey("arguments") ? item["arguments"] : "{}";
                        var call = new Dictionary<string, object>();
                        call["id"] = item.ContainsKey("call_id") ? item["call_id"] : Guid.NewGuid().ToString("N");
                        call["type"] = "function";
                        call["function"] = function;
                        toolCalls.Add(call);
                    }
                }
            var message = new Dictionary<string, object>();
            message["role"] = "assistant";
            message["content"] = content.Length > 0 ? content.ToString() : null;
            if (toolCalls.Count > 0) message["tool_calls"] = toolCalls.ToArray();
            var choice = new Dictionary<string, object>();
            choice["index"] = 0;
            choice["message"] = message;
            choice["finish_reason"] = FinishReasonFor(responses, toolCalls.Count > 0);
            var usage = responses.ContainsKey("usage") ? responses["usage"] as IDictionary<string, object> : null;
            var document = new Dictionary<string, object>();
            document["id"] = responses.ContainsKey("id") ? responses["id"] : "chatcmpl-" + Guid.NewGuid().ToString("N");
            document["object"] = "chat.completion";
            document["created"] = responses.ContainsKey("created_at") ? responses["created_at"] : 0;
            document["model"] = responses.ContainsKey("model") ? responses["model"] : null;
            document["choices"] = new object[] { choice };
            if (usage != null)
                document["usage"] = new Dictionary<string, object>
                {
                    { "prompt_tokens", usage.ContainsKey("input_tokens") ? usage["input_tokens"] : 0 },
                    { "completion_tokens", usage.ContainsKey("output_tokens") ? usage["output_tokens"] : 0 },
                    { "total_tokens", usage.ContainsKey("total_tokens") ? usage["total_tokens"] : 0 },
                };
            return document;
        }

        private static IDictionary<string, object> ChatDocToResponses(IDictionary<string, object> chat)
        {
            var output = new List<object>();
            var choices = chat.ContainsKey("choices") ? chat["choices"] as IList : null;
            var first = choices != null && choices.Count > 0 ? choices[0] as IDictionary<string, object> : null;
            var message = first != null && first.ContainsKey("message") ? first["message"] as IDictionary<string, object> : null;
            string text = message != null && message.ContainsKey("content") ? Convert.ToString(message["content"]) : null;
            if (!String.IsNullOrEmpty(text))
                output.Add(new Dictionary<string, object>
                {
                    { "type", "message" },
                    { "role", "assistant" },
                    { "content", new object[]
                        {
                            new Dictionary<string, object> { { "type", "output_text" }, { "text", text } },
                        }
                    },
                });
            var toolCalls = message != null && message.ContainsKey("tool_calls") ? message["tool_calls"] as IList : null;
            if (toolCalls != null)
                foreach (object callEntry in toolCalls)
                {
                    var call = callEntry as IDictionary<string, object>;
                    if (call == null) continue;
                    var function = call.ContainsKey("function") ? call["function"] as IDictionary<string, object> : null;
                    output.Add(new Dictionary<string, object>
                    {
                        { "type", "function_call" },
                        { "call_id", call.ContainsKey("id") ? call["id"] : Guid.NewGuid().ToString("N") },
                        { "name", function != null && function.ContainsKey("name") ? function["name"] : "" },
                        { "arguments", function != null && function.ContainsKey("arguments") ? function["arguments"] : "{}" },
                    });
                }
            string finish = first != null && first.ContainsKey("finish_reason") ? Convert.ToString(first["finish_reason"]) : "stop";
            var document = new Dictionary<string, object>();
            document["id"] = chat.ContainsKey("id") ? chat["id"] : "resp_" + Guid.NewGuid().ToString("N");
            document["object"] = "response";
            document["created_at"] = chat.ContainsKey("created") ? chat["created"] : 0;
            document["model"] = chat.ContainsKey("model") ? chat["model"] : null;
            document["status"] = String.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) ? "incomplete" : "completed";
            if (String.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
                document["incomplete_details"] = new Dictionary<string, object> { { "reason", "max_output_tokens" } };
            document["output"] = output.ToArray();
            var usage = chat.ContainsKey("usage") ? chat["usage"] as IDictionary<string, object> : null;
            if (usage != null)
                document["usage"] = new Dictionary<string, object>
                {
                    { "input_tokens", usage.ContainsKey("prompt_tokens") ? usage["prompt_tokens"] : 0 },
                    { "output_tokens", usage.ContainsKey("completion_tokens") ? usage["completion_tokens"] : 0 },
                    { "total_tokens", usage.ContainsKey("total_tokens") ? usage["total_tokens"] : 0 },
                };
            return document;
        }

        private static void AppendSse(StringBuilder builder, string name, IDictionary<string, object> payload)
        {
            payload["type"] = name;
            builder.Append("event: ").Append(name).Append("\ndata: ").Append(Json.Serialize(payload)).Append("\n\n");
        }

        private static void AppendChatChunk(StringBuilder builder, object id, object created, object model,
            IDictionary<string, object> delta, object finishReason)
        {
            var choice = new Dictionary<string, object>();
            choice["index"] = 0;
            choice["delta"] = delta;
            choice["finish_reason"] = finishReason;
            var chunk = new Dictionary<string, object>();
            chunk["id"] = id;
            chunk["object"] = "chat.completion.chunk";
            chunk["created"] = created;
            chunk["model"] = model;
            chunk["choices"] = new object[] { choice };
            builder.Append("data: ").Append(Json.Serialize(chunk)).Append("\n\n");
        }

        private static byte[] ChatDocumentToSse(IDictionary<string, object> chat)
        {
            var builder = new StringBuilder();
            object id = chat.ContainsKey("id") ? chat["id"] : "chatcmpl-" + Guid.NewGuid().ToString("N");
            object created = chat.ContainsKey("created") ? chat["created"] : 0;
            object model = chat.ContainsKey("model") ? chat["model"] : null;
            var choices = chat.ContainsKey("choices") ? chat["choices"] as IList : null;
            var first = choices != null && choices.Count > 0 ? choices[0] as IDictionary<string, object> : null;
            var message = first != null && first.ContainsKey("message") ? first["message"] as IDictionary<string, object> : null;
            var delta = new Dictionary<string, object>();
            delta["role"] = "assistant";
            if (message != null)
            {
                if (message.ContainsKey("content") && message["content"] != null) delta["content"] = message["content"];
                if (message.ContainsKey("tool_calls") && message["tool_calls"] != null) delta["tool_calls"] = message["tool_calls"];
            }
            AppendChatChunk(builder, id, created, model, delta, null);
            AppendChatChunk(builder, id, created, model, new Dictionary<string, object>(),
                first != null && first.ContainsKey("finish_reason") ? first["finish_reason"] : "stop");
            if (chat.ContainsKey("usage") && chat["usage"] != null)
            {
                var usageChunk = new Dictionary<string, object>();
                usageChunk["id"] = id;
                usageChunk["object"] = "chat.completion.chunk";
                usageChunk["created"] = created;
                usageChunk["model"] = model;
                usageChunk["choices"] = new object[0];
                usageChunk["usage"] = chat["usage"];
                builder.Append("data: ").Append(Json.Serialize(usageChunk)).Append("\n\n");
            }
            builder.Append("data: [DONE]\n\n");
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static byte[] ResponsesDocumentToSse(IDictionary<string, object> responses)
        {
            var builder = new StringBuilder();
            var created = new Dictionary<string, object>(responses);
            created["status"] = "in_progress";
            created["output"] = new object[0];
            AppendSse(builder, "response.created", new Dictionary<string, object> { { "response", created } });
            var output = responses.ContainsKey("output") ? responses["output"] as IList : null;
            if (output != null)
                for (int index = 0; index < output.Count; index++)
                {
                    var item = output[index] as IDictionary<string, object>;
                    if (item == null) continue;
                    AppendSse(builder, "response.output_item.added", new Dictionary<string, object> { { "output_index", index }, { "item", item } });
                    string type = item.ContainsKey("type") ? Convert.ToString(item["type"]) : "";
                    if (String.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = MessageText(item);
                        if (!String.IsNullOrEmpty(text))
                            AppendSse(builder, "response.output_text.delta", new Dictionary<string, object>
                            {
                                { "item_id", item.ContainsKey("id") ? item["id"] : null },
                                { "output_index", index }, { "content_index", 0 }, { "delta", text },
                            });
                    }
                    AppendSse(builder, "response.output_item.done", new Dictionary<string, object> { { "output_index", index }, { "item", item } });
                }
            AppendSse(builder, "response.completed", new Dictionary<string, object> { { "response", responses } });
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static void WriteTranslatedResponse(HttpListenerResponse destination, UpstreamWire clientWire,
            string responseText, bool clientStreams)
        {
            var document = Json.DeserializeObject(responseText) as IDictionary<string, object>;
            if (document == null)
            {
                WriteJson(destination, 502, new Dictionary<string, object>
                {
                    { "error", new Dictionary<string, object>
                        {
                            { "type", "upstream_protocol_error" },
                            { "message", "The upstream response could not be re-framed for this endpoint." },
                        }
                    },
                });
                return;
            }
            IDictionary<string, object> translated = clientWire == UpstreamWire.Chat
                ? ResponsesDocToChat(document)
                : ChatDocToResponses(document);
            if (clientStreams)
            {
                destination.ContentType = "text/event-stream; charset=utf-8";
                WriteBytes(destination, 200, clientWire == UpstreamWire.Chat
                    ? ChatDocumentToSse(translated)
                    : ResponsesDocumentToSse(translated));
            }
            else
            {
                destination.ContentType = "application/json; charset=utf-8";
                WriteBytes(destination, 200, Encoding.UTF8.GetBytes(Json.Serialize(translated)));
            }
        }

        private static async Task ForwardWithFailover(HttpListenerContext context, ProxyConfig config,
            IList<string> keys, byte[] payload, string configPath)
        {
            UpstreamWire clientWire = WireFromPath(context.Request.Url.AbsolutePath);
            string payloadText = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : String.Empty;
            bool clientStreams = Regex.IsMatch(payloadText, "\\\"stream\\\"\\s*:\\s*true", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string model = null;
            try
            {
                var parsed = Json.DeserializeObject(payloadText) as IDictionary<string, object>;
                if (parsed != null && parsed.ContainsKey("model")) model = Convert.ToString(parsed["model"]);
            }
            catch
            {
                var m = Regex.Match(payloadText, "\\\"model\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                if (m.Success) model = m.Groups[1].Value;
            }

            UpstreamWire remembered;
            UpstreamWire primary = TryGetRememberedWire(model, out remembered) ? remembered : clientWire;
            // Harden the wire ladder: the client's own wire always comes first.
            // A remembered wire is only used when the proxy can translate the
            // client payload onto it; otherwise the request would arrive on an
            // endpoint that cannot parse its shape (invalid_union / empty error
            // frames). The upstream itself accepts every model on chat/responses,
            // so "untranslated-but-different-wire" attempts are never valid.
            if (primary != clientWire && !CanTranslate(clientWire, primary)) primary = clientWire;
            var attempts = new List<UpstreamWire>();
            attempts.Add(clientWire);
            if (primary != clientWire && CanTranslate(clientWire, primary))
                attempts.Add(primary);
            if ((clientWire == UpstreamWire.Chat || clientWire == UpstreamWire.Responses))
            {
                UpstreamWire alternative = clientWire == UpstreamWire.Chat ? UpstreamWire.Responses : UpstreamWire.Chat;
                if (!attempts.Contains(alternative)) attempts.Add(alternative);
            }

            string sessionId = context.Request.Headers["x-opencode-session"];
            if (String.IsNullOrWhiteSpace(sessionId)) sessionId = Guid.NewGuid().ToString("D");
            string query = context.Request.Url.Query ?? String.Empty;

            bool zenFallbackTriggered = false;
            int lastGoStatus = 0;
            string lastGoResponse = null;

            for (int wireIndex = 0; wireIndex < attempts.Count; wireIndex++)
            {
                UpstreamWire targetWire = attempts[wireIndex];
                bool translated;
                byte[] outbound = TranslateRequest(payload, clientWire, targetWire, out translated);
                string suffix = PathForWire(targetWire) + (translated ? String.Empty : query);
                Uri target = new Uri(config.upstream_base_url.TrimEnd('/') + "/" + suffix);
                List<int> keyOrder = OrderKeyIndexes(keys, model);
                for (int orderIndex = 0; orderIndex < keyOrder.Count; orderIndex++)
                {
                    int index = keyOrder[orderIndex];
                    int slot = index + 1;
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, target))
                        {
                            request.Content = new ByteArrayContent(outbound);
                            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            if (clientWire == UpstreamWire.Messages)
                                request.Headers.TryAddWithoutValidation("x-api-key", keys[index]);
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys[index]);
                            request.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
                            foreach (string headerName in new[] { "anthropic-version", "anthropic-beta", "openai-beta", "user-agent",
                                "x-opencode-project", "x-opencode-request", "x-opencode-client" })
                            {
                                string headerValue = context.Request.Headers[headerName];
                                if (!String.IsNullOrWhiteSpace(headerValue))
                                    request.Headers.TryAddWithoutValidation(headerName, headerValue);
                            }
                            // The free-tier gate only allows checks that look like
                            // they come from the OpenCode client; fall back to a
                            // matching client identity when the caller sent none.
                            if (String.IsNullOrWhiteSpace(context.Request.Headers["user-agent"]))
                                request.Headers.TryAddWithoutValidation("user-agent", "opencode/1.0.0");
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            using (HttpResponseMessage response = await UpstreamClient.SendAsync(request,
                                HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                            {
                                byte[] upstreamBody = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                string responseText = Encoding.UTF8.GetString(upstreamBody);
                                int status = (int)response.StatusCode;
                                Console.WriteLine("UPSTREAM_RECEIVED status=" + status + " key_slot=" + slot +
                                    " wire=" + WireName(targetWire) + " translated=" + translated);
                                bool workspacePolicyFailover = status == 400 && IsWorkspacePolicyRetryable(responseText);
                                bool insufficientFunds = IsInsufficientFunds(status, responseText);
                                bool zenTrigger = insufficientFunds || workspacePolicyFailover;
                                // A 2xx envelope can still carry an in-band error frame
                                // ("event: error" / error JSON). Relaying it to the
                                // client looks like a broken stream; treat it as an
                                // upstream failure and keep rotating.
                                bool errorFrame = status >= 200 && status < 300 && IsErrorFrameStream(responseText);
                                bool freeTier = IsFreeTierFailure(status, responseText);
                                bool retryStatus = ShouldRetry(status, config) || workspacePolicyFailover || freeTier || errorFrame;
                                Console.WriteLine("UPSTREAM_RETRY_DECISION status=" + status + " retry_status=" + retryStatus +
                                    " policy_failover=" + workspacePolicyFailover + " insufficient_funds=" + insufficientFunds +
                                    " free_tier=" + freeTier + " error_frame=" + errorFrame +
                                    " remaining_keys=" + (keyOrder.Count - orderIndex - 1));
                                if (zenTrigger && !String.IsNullOrEmpty(config.zen_upstream_base_url))
                                {
                                    // Workspace-level policy errors (Global regions, training
                                    // consent) affect ALL keys from the same account, so there
                                    // is no point trying the remaining keys — they will all
                                    // fail the same way. Trigger Zen fallback immediately.
                                    // Insufficient funds also triggers immediately because the
                                    // upstream balance is shared across all keys.
                                    lastGoStatus = status;
                                    lastGoResponse = responseText;
                                    zenFallbackTriggered = true;
                                    Console.WriteLine("ZEN_FALLBACK_QUEUED model=" + (model ?? "") +
                                        " reason=" + (insufficientFunds ? "funds" : "privacy") +
                                        " status=" + status + " triggered_on_slot=" + slot +
                                        " remaining_keys_skipped=" + (keyOrder.Count - orderIndex - 1));
                                    break;
                                }
                                if (retryStatus)
                                {
                                    if (workspacePolicyFailover) MarkModelPolicyBlock(keys[index], model);
                                    else MarkKeyCooldown(keys[index], status, false);
                                    if (orderIndex + 1 < keyOrder.Count)
                                    {
                                        Console.WriteLine("FAILOVER status=" + status + (workspacePolicyFailover ? " policy=training-data" : "") + (freeTier ? " reason=free-tier-gate" : "") +
                                            " failed_slot=" + slot + " next_slot=" + (keyOrder[orderIndex + 1] + 1) + " cooldown=" + (!workspacePolicyFailover).ToString().ToLowerInvariant());
                                        continue;
                                    }
                                    if (errorFrame && wireIndex + 1 < attempts.Count)
                                    {
                                        // Every key returned an in-band error frame; the
                                        // wire is likely rejected — escalate to the next wire.
                                        Console.WriteLine("WIRE_ESCALATE error_frame=all_keys wire=" + WireName(targetWire) +
                                            " next_wire=" + WireName(attempts[wireIndex + 1]));
                                        break;
                                    }
                                }
                                if (errorFrame)
                                {
                                    // Nothing left to try: synthesize a clean, valid SSE
                                    // error instead of relaying the zero-content error
                                    // frame, so client-side retry logic stays intact.
                                    string safeMessage = "upstream model temporarily unavailable (failover exhausted); retry shortly";
                                    if (clientStreams)
                                    {
                                        context.Response.ContentType = "text/event-stream; charset=utf-8";
                                        WriteBytes(context.Response, 200, Encoding.UTF8.GetBytes(
                                            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"upstream_unavailable\",\"message\":\"" + safeMessage + "\"}}\n\ndata: [DONE]\n\n"));
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = 429;
                                        context.Response.Headers["Retry-After"] = "10";
                                        WriteBytes(context.Response, 429, Encoding.UTF8.GetBytes(
                                            "{\"error\":{\"type\":\"upstream_unavailable\",\"message\":\"" + safeMessage + "\"}}"));
                                    }
                                    return;
                                }
                                if (IsProtocolUnsupported(status, responseText) && wireIndex + 1 < attempts.Count)
                                {
                                    Console.WriteLine("PROTOCOL_REPAIR_UNSUPPORTED model=" + (model ?? "") +
                                        " wire=" + WireName(targetWire) + " next_wire=" + WireName(attempts[wireIndex + 1]));
                                    break;
                                }
                                if (status >= 200 && status < 300) RememberWire(model, targetWire);
                                Console.WriteLine("UPSTREAM status=" + status + " key_slot=" + slot + " total_keys=" + keys.Count +
                                    " wire=" + WireName(targetWire) + " translated=" + translated);
                                CopyResponseHeaders(response, context.Response, config, keys);
                                if (translated && status == 200)
                                {
                                    WriteTranslatedResponse(context.Response, clientWire, responseText, clientStreams);
                                }
                                else
                                {
                                    WriteBytes(context.Response, status, Encoding.UTF8.GetBytes(RedactSecrets(responseText, config, keys)));
                                }
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (orderIndex + 1 < keyOrder.Count)
                        {
                            Console.WriteLine("FAILOVER transport_error=" + ex.GetType().Name + " failed_slot=" + slot + " next_slot=" + (keyOrder[orderIndex + 1] + 1));
                            continue;
                        }
                        Console.WriteLine("UPSTREAM transport_error=" + ex.GetType().Name + " key_slot=" + slot + " total_keys=" + keys.Count);
                        WriteError(context.Response, 502, "upstream_unavailable", "All configured upstream keys failed to connect.");
                        return;
                    }
                }
                if (zenFallbackTriggered) break;
            }

            // Zen upstream fallback: when Go fails with funds or privacy errors,
            // retry the same request on the Zen gateway which serves free-tier
            // and Zen-native models with different billing.
            if (zenFallbackTriggered && !String.IsNullOrEmpty(config.zen_upstream_base_url))
            {
                Console.WriteLine("ZEN_FALLBACK_START model=" + (model ?? "") + " go_status=" + lastGoStatus);
                try
                {
                    CredentialSnapshot zenSnapshot = !String.IsNullOrEmpty(config.zen_credential_source)
                        ? ReadCredentials(config.zen_credential_source)
                        : ReadCredentials(config.credential_source);
                    if (zenSnapshot.Keys.Count == 0)
                    {
                        Console.WriteLine("ZEN_FALLBACK_NO_KEYS");
                        WriteBytes(context.Response, lastGoStatus, Encoding.UTF8.GetBytes(
                            lastGoResponse ?? "{\"error\":{\"message\":\"Go upstream failed and no Zen keys configured.\"}}"));
                        return;
                    }
                    string zenPayloadText = Encoding.UTF8.GetString(payload);
                    IDictionary<string, object> zenBody = null;
                    try { zenBody = Json.DeserializeObject(zenPayloadText) as IDictionary<string, object>; } catch { }
                    byte[] zenOutbound;
                    if (zenBody == null)
                    {
                        // Can't parse to modify stream flag; forward raw bytes.
                        Console.WriteLine("ZEN_FALLBACK_RAW_PAYLOAD forwarding_unchanged");
                        zenOutbound = payload;
                    }
                    else
                    {
                        // Zen may not support the same model; remap to free-tier variants
                        // where the Go model requires privacy opt-in or training consent.
                        string zenModel = RemapModelForZen(model);
                        if (!String.IsNullOrEmpty(zenModel) && zenModel != model)
                        {
                            Console.WriteLine("ZEN_FALLBACK_REMAP from=" + model + " to=" + zenModel);
                            zenBody["model"] = zenModel;
                        }
                        zenBody["stream"] = false;
                        zenOutbound = Encoding.UTF8.GetBytes(Json.Serialize(zenBody));
                    }
                    // Try Zen with the same wire the client used.
                    string zenSuffix = PathForWire(clientWire);
                    Uri zenTarget = new Uri(config.zen_upstream_base_url.TrimEnd('/') + "/" + zenSuffix);
                    for (int z = 0; z < zenSnapshot.Keys.Count; z++)
                    {
                        try
                        {
                            using (var zenRequest = new HttpRequestMessage(HttpMethod.Post, zenTarget))
                            {
                                zenRequest.Content = new ByteArrayContent(zenOutbound);
                                zenRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                                zenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", zenSnapshot.Keys[z]);
                                zenRequest.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
                                foreach (string headerName in new[] { "anthropic-version", "anthropic-beta", "openai-beta", "user-agent",
                                    "x-opencode-project", "x-opencode-request", "x-opencode-client" })
                                {
                                    string headerValue = context.Request.Headers[headerName];
                                    if (!String.IsNullOrWhiteSpace(headerValue))
                                        zenRequest.Headers.TryAddWithoutValidation(headerName, headerValue);
                                }
                                zenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                                using (HttpResponseMessage zenResponse = await UpstreamClient.SendAsync(zenRequest,
                                    HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                                {
                                    byte[] zenBodyBytes = await zenResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                    string zenText = Encoding.UTF8.GetString(zenBodyBytes);
                                    int zenStatus = (int)zenResponse.StatusCode;
                                    Console.WriteLine("ZEN_FALLBACK_RESULT status=" + zenStatus + " key_slot=" + (z + 1));
                                    if (zenStatus >= 200 && zenStatus < 300)
                                    {
                                        CopyResponseHeaders(zenResponse, context.Response, config, zenSnapshot.Keys);
                                        IDictionary<string, object> zenDoc = null;
                                        try { zenDoc = Json.DeserializeObject(zenText) as IDictionary<string, object>; } catch { }
                                        if (clientStreams && zenDoc != null)
                                        {
                                            context.Response.ContentType = "text/event-stream; charset=utf-8";
                                            if (clientWire == UpstreamWire.Chat)
                                                WriteBytes(context.Response, 200, ChatDocumentToSse(zenDoc));
                                            else
                                                WriteBytes(context.Response, 200, ResponsesDocumentToSse(zenDoc));
                                        }
                                        else
                                        {
                                            WriteBytes(context.Response, 200, Encoding.UTF8.GetBytes(zenText));
                                        }
                                        return;
                                    }
                                    if (zenStatus >= 400 && z + 1 < zenSnapshot.Keys.Count) continue;
                                    WriteBytes(context.Response, zenStatus, Encoding.UTF8.GetBytes(RedactSecrets(zenText, config, zenSnapshot.Keys)));
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("ZEN_FALLBACK_TRANSPORT key_slot=" + (z + 1) + " error=" + ex.GetType().Name);
                            if (z + 1 < zenSnapshot.Keys.Count) continue;
                        }
                    }
                    // Zen also failed; return the original Go error.
                    Console.WriteLine("ZEN_FALLBACK_EXHAUSTED returning_go_error status=" + lastGoStatus);
                    WriteBytes(context.Response, lastGoStatus, Encoding.UTF8.GetBytes(
                        lastGoResponse ?? "{\"error\":{\"message\":\"All upstreams failed.\"}}"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ZEN_FALLBACK_ERROR type=" + ex.GetType().Name);
                    WriteBytes(context.Response, lastGoStatus, Encoding.UTF8.GetBytes(
                        lastGoResponse ?? "{\"error\":{\"message\":\"Zen fallback failed.\"}}"));
                }
            }
        }
        /// <summary>True when an SSE body carries an in-band error frame.</summary>
        private static bool IsErrorFrameStream(string responseText)
        {
            if (String.IsNullOrEmpty(responseText)) return false;
            if (responseText.IndexOf("event: error", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return responseText.IndexOf("\"error\"", StringComparison.Ordinal) >= 0 &&
                responseText.IndexOf("\"type\"", StringComparison.Ordinal) >= 0;
        }

        /// <summary>True when the upstream free-tier gate rejected the request.</summary>
        private static bool IsFreeTierFailure(int status, string responseText)
        {
            if (status != 400 && status != 402 && status != 403 && status != 429) return false;
            if (String.IsNullOrEmpty(responseText)) return false;
            return responseText.IndexOf("FreeTierError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("can only be used from within OpenCode", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldRetry(int status, ProxyConfig config)
        {
            return status >= config.retry_server_error_from ||
                (config.retry_http_statuses != null && config.retry_http_statuses.Contains(status));
        }

        private static bool IsWorkspacePolicyRetryable(string responseText)
        {
            if (String.IsNullOrEmpty(responseText)) return false;
            // Match any workspace privacy/policy rejection from the Go gateway:
            // 1. "trains on request data" + "Privacy"
            // 2. "Global regions" + "Privacy"
            // 3. "explicit opt in" + "quality"
            bool hasPrivacy = responseText.IndexOf("Privacy", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasTraining = responseText.IndexOf("train", StringComparison.OrdinalIgnoreCase) >= 0 &&
                responseText.IndexOf("request data", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasRegions = responseText.IndexOf("Global regions", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasOptIn = responseText.IndexOf("explicit opt in", StringComparison.OrdinalIgnoreCase) >= 0;
            return (hasTraining && hasPrivacy) || (hasRegions && hasPrivacy) || hasOptIn;
        }

        // Per-key cooldown so an exhausted or policy-blocked workspace is skipped
        // immediately on later requests while every other key (any number of lines)
        // keeps serving. The map is keyed by a SHA-256 of the key and never stores
        // the raw key. Entries expire on their own.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> KeyCooldowns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private static string KeyIdentity(string key)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
        }

        private static void MarkKeyCooldown(string key, int status, bool policy)
        {
            if (String.IsNullOrEmpty(key)) return;
            TimeSpan span;
            if (policy) span = TimeSpan.FromMinutes(10);
            else if (status == 401) span = TimeSpan.FromMinutes(10);
            else if (status == 402 || status == 403) span = TimeSpan.FromMinutes(5);
            else span = TimeSpan.FromSeconds(60);
            KeyCooldowns[KeyIdentity(key)] = DateTime.UtcNow.Add(span);
        }

        private static bool IsKeyCoolingDown(string key)
        {
            if (String.IsNullOrEmpty(key)) return false;
            DateTime until;
            string id = KeyIdentity(key);
            if (KeyCooldowns.TryGetValue(id, out until))
            {
                if (until > DateTime.UtcNow) return true;
                DateTime removed;
                KeyCooldowns.TryRemove(id, out removed);
            }
            return false;
        }

        // Healthy keys keep their file order; cooling keys move to the end so the
        // first attempt already uses an available workspace, and a cooling key is
        // still tried when every key is cooling (never a hard failure).
        private static List<int> OrderKeyIndexes(IList<string> keys, string model)
        {
            var healthy = new List<int>();
            var quotaCooling = new List<int>();
            var modelBlocked = new List<int>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (IsModelPolicyBlocked(keys[i], model)) modelBlocked.Add(i);
                else if (IsKeyCoolingDown(keys[i])) quotaCooling.Add(i);
                else healthy.Add(i);
            }
            var result = new List<int>();
            result.AddRange(healthy.OrderByDescending(i => KeyScore(keys[i])));
            result.AddRange(quotaCooling.OrderByDescending(i => KeyScore(keys[i])));
            result.AddRange(modelBlocked.OrderByDescending(i => KeyScore(keys[i])));
            return result;
        }

        // A workspace can block a specific model (e.g. "trains on request data")
        // while still serving other models. Remember that pairing so the blocked
        // key is skipped for that model without penalizing it for every model.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ModelPolicyBlocks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private static string ModelPolicyId(string key, string model)
        {
            return KeyIdentity(key) + "|" + (model ?? "");
        }

        private static void MarkModelPolicyBlock(string key, string model)
        {
            if (String.IsNullOrEmpty(key) || String.IsNullOrEmpty(model)) return;
            ModelPolicyBlocks[ModelPolicyId(key, model)] = DateTime.UtcNow.AddMinutes(30);
        }

        private static bool IsModelPolicyBlocked(string key, string model)
        {
            if (String.IsNullOrEmpty(key) || String.IsNullOrEmpty(model)) return false;
            DateTime until;
            string id = ModelPolicyId(key, model);
            if (ModelPolicyBlocks.TryGetValue(id, out until))
            {
                if (until > DateTime.UtcNow) return true;
                DateTime removed;
                ModelPolicyBlocks.TryRemove(id, out removed);
            }
            return false;
        }

        // Proactive usage awareness: a background poll records each key's remaining
        // headroom so the very next request already uses the best workspace instead
        // of discovering a limit through a failed attempt.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> KeyUsageScore =
            new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);
        private static System.Threading.Timer UsagePoller;
        private static ProxyConfig UsagePollerConfig;

        private static void StartUsagePoller(ProxyConfig config)
        {
            UsagePollerConfig = config;
            if (UsagePoller != null) return;
            UsagePoller = new System.Threading.Timer(_ => RefreshKeyUsage(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        }

        internal static double KeyScore(string key)
        {
            if (String.IsNullOrEmpty(key)) return 100.0;
            double score;
            if (KeyUsageScore.TryGetValue(KeyIdentity(key), out score)) return score;
            return 100.0; // No usage data yet: treat as fully available.
        }

        private static void RefreshKeyUsage()
        {
            try
            {
                ProxyConfig config = UsagePollerConfig;
                if (config == null) return;
                CredentialSnapshot snapshot = ReadCredentials(config.credential_source);
                for (int index = 0; index < snapshot.Keys.Count; index++)
                {
                    int slot = index + 1;
                    string key = snapshot.Keys[index];
                    try
                    {
                        Uri target = new Uri(config.upstream_base_url.TrimEnd('/') + "/usage");
                        using (var request = new HttpRequestMessage(HttpMethod.Get, target))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            using (HttpResponseMessage response = UpstreamClient.SendAsync(request,
                                HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                            {
                                int status = (int)response.StatusCode;
                                if (!response.IsSuccessStatusCode)
                                {
                                    KeyUsageScore[KeyIdentity(key)] = 0;
                                    Console.WriteLine("KEY_USAGE status=" + status + " key_slot=" + slot + " score=0");
                                    continue;
                                }
                                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                double maxPercent = ParseMaxUsagePercent(json);
                                double score = Math.Max(0, 100 - maxPercent);
                                KeyUsageScore[KeyIdentity(key)] = score;
                                Console.WriteLine("KEY_USAGE status=200 key_slot=" + slot +
                                    " max_percent=" + maxPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                                    " score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("KEY_USAGE transport_error=" + ex.GetType().Name + " key_slot=" + slot + " detail=omitted");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("KEY_USAGE_POLL_ERROR type=" + ex.GetType().Name + " detail=omitted");
            }
        }

        private static double ParseMaxUsagePercent(string json)
        {
            // Generic per-window scoring. Every limit window the upstream
            // reports (rolling 5-hour, daily/7-day, weekly, monthly, and any
            // future window) is collected and combined with weights that
            // prioritize the hardest-to-recover limits first. The score is
            // normalized by the total weight of the windows actually present,
            // so it stays comparable across providers and key counts.
            var windows = new List<KeyValuePair<string, double>>();
            try
            {
                var root = Json.DeserializeObject(json) as IDictionary<string, object>;
                object usageValue;
                var usage = root != null && root.TryGetValue("usage", out usageValue) ? usageValue as IDictionary<string, object> : null;
                if (usage == null) return 0;
                foreach (string windowName in usage.Keys)
                {
                    object value = usage[windowName];
                    if (!(value is double)) { }
                    windows.Add(new KeyValuePair<string, double>(windowName ?? "", ParseWindow(usage, windowName)));
                }
            }
            catch
            {
                return 0;
            }
            if (windows.Count == 0) return 0;
            // If any window is fully exhausted the key is rate-limited for that
            // window. Return 100 immediately so RefreshKeyUsage scores it as 0.
            foreach (KeyValuePair<string, double> window in windows)
                if (window.Value >= 100) return 100;
            double totalWeight = 0, weightedSum = 0;
            // Long-lived limits are hardest to recover: monthly > weekly >
            // daily/7-day > rolling (5-hour). Unknown windows get the rolling
            // weight so a brand-new window can never skew rotation.
            foreach (KeyValuePair<string, double> window in windows)
            {
                double weight = WindowWeight(window.Key);
                weightedSum += window.Value * weight;
                totalWeight += weight;
            }
            if (totalWeight <= 0) return 0;
            double weighted = weightedSum / totalWeight;
            Console.WriteLine("KEY_USAGE_WINDOWS windows=" + windows.Count +
                " weighted=" + weighted.ToString("0.#"));
            return weighted;
        }

        private static double WindowWeight(string windowName)
        {
            if (String.IsNullOrEmpty(windowName)) return 0.10;
            string name = windowName.ToLowerInvariant();
            if (name.IndexOf("month", StringComparison.Ordinal) >= 0) return 0.45;
            if (name.IndexOf("week", StringComparison.Ordinal) >= 0) return 0.30;
            if (name.IndexOf("roll", StringComparison.Ordinal) >= 0 || name.IndexOf("hour", StringComparison.Ordinal) >= 0) return 0.10;
            // daily / 7-day / unknown windows sit between weekly and rolling.
            return 0.15;
        }

        private static double ParseWindow(IDictionary<string, object> usage, string windowName)
        {
            object windowValue;
            var w = usage.TryGetValue(windowName, out windowValue) ? windowValue as IDictionary<string, object> : null;
            if (w == null) return 0;
            object percentValue;
            if (w.TryGetValue("percent", out percentValue) && percentValue != null)
            {
                double p;
                if (double.TryParse(Convert.ToString(percentValue), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out p)) return p;
            }
            return 0;
        }

        private static void CopyResponseHeaders(HttpResponseMessage source, HttpListenerResponse destination,
            ProxyConfig config, IList<string> keys)
        {
            IEnumerable<string> values;
            foreach (string name in new[] { "Retry-After", "x-request-id", "openai-processing-ms", "anthropic-request-id" })
            {
                if (source.Headers.TryGetValues(name, out values))
                {
                    string safeValue = RedactSecrets(String.Join(", ", values), config, keys);
                    if (safeValue.IndexOf('\r') < 0 && safeValue.IndexOf('\n') < 0)
                        destination.Headers[name] = safeValue;
                }
            }
            if (source.Content.Headers.ContentType != null)
                destination.ContentType = source.Content.Headers.ContentType.ToString();
            AddCorsHeaders(destination);
        }

        private static void AddCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-OpenCode-Session, X-Api-Key, Anthropic-Version, Anthropic-Beta, OpenAI-Beta";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        }

        private static void WriteJson(HttpListenerResponse response, int status, object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(value));
            response.ContentType = "application/json; charset=utf-8";
            AddCorsHeaders(response);
            WriteBytes(response, status, bytes);
        }

        private static void WriteError(HttpListenerResponse response, int status, string code, string message)
        {
            WriteJson(response, status, new Dictionary<string, object>
            {
                { "error", new Dictionary<string, object> { { "type", code }, { "message", message } } }
            });
        }

        private static void SafeWriteError(HttpListenerResponse response, int status, string code, string message)
        {
            try { WriteError(response, status, code, message); }
            catch { try { response.Abort(); } catch { } }
        }

        private static void WriteBytes(HttpListenerResponse response, int status, byte[] bytes)
        {
            try
            {
                response.StatusCode = status;
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.OutputStream.Flush();
            }
            finally { response.Close(); }
        }

        private static string DescribeException(Exception exception)
        {
            var chain = new List<string>();
            for (Exception current = exception; current != null; current = current.InnerException)
                chain.Add(current.GetType().Name);
            return String.Join(" --> ", chain);
        }

        private static int ProbeUpstream(string credentialPath, string upstreamUrl, string model)
        {
            CredentialSnapshot credentials = ReadCredentials(credentialPath);
            if (credentials.Keys.Count == 0) throw new InvalidOperationException("No upstream credential is configured.");
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", new[] { new Dictionary<string, object> { { "role", "user" }, { "content", "Reply with exactly: OK" } } } },
                { "max_tokens", 1 }
            };
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(body));
            Console.WriteLine("LIVE_PROBE endpoint_host=" + new Uri(upstreamUrl).Host + " model=" + model + " max_tokens=1 keys=" + credentials.Keys.Count);
            for (int index = 0; index < credentials.Keys.Count; index++)
            {
                int slot = index + 1;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl.TrimEnd('/') + "/chat/completions"))
                    {
                        request.Content = new ByteArrayContent(bytes);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Keys[index]);
                        request.Headers.TryAddWithoutValidation("x-opencode-session", Guid.NewGuid().ToString("D"));
                        using (HttpResponseMessage response = UpstreamClient.SendAsync(request,
                            HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                        {
                            Console.WriteLine("LIVE_PROBE status=" + (int)response.StatusCode + " key_slot=" + slot + " response_body=omitted");
                            if (response.IsSuccessStatusCode)
                            {
                                Console.WriteLine("LIVE_PROBE_SUCCESS key_slot=" + slot);
                                return 0;
                            }
                            int status = (int)response.StatusCode;
                            bool retry = status >= 500 || new[] { 401, 402, 403, 408, 425, 429 }.Contains(status);
                            if (retry && index + 1 < credentials.Keys.Count)
                            {
                                Console.WriteLine("LIVE_PROBE_FAILOVER failed_slot=" + slot + " next_slot=" + (slot + 1));
                                continue;
                            }
                            return 10;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("LIVE_PROBE_TRANSPORT_ERROR type=" + ex.GetType().Name + " key_slot=" + slot);
                    if (index + 1 < credentials.Keys.Count) continue;
                    return 11;
                }
            }
            return 10;
        }

        private static async Task RunMockUpstream(int port, string reportPath, int failureStatus)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            if (failureStatus != 400 && failureStatus != 402 && failureStatus != 429 && failureStatus < 500)
                throw new ArgumentOutOfRangeException("failureStatus");
            var listener = new HttpListener();
            string prefix = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(prefix);
            listener.Start();
            Console.WriteLine("MOCK_UPSTREAM_READY port=" + port);
            byte[] firstBody = null;
            var statuses = new List<int>();
            var authSlots = new List<string>();
            var apiKeySlots = new List<string>();
            var models = new List<string>();
            var paths = new List<string>();
            try
            {
                int completionCount = 0;
                while (completionCount < 3)
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    string authorization = context.Request.Headers["Authorization"] ?? "";
                    if (String.IsNullOrEmpty(authorization)) authorization = "Bearer " + (context.Request.Headers["x-api-key"] ?? "");
                    string upstreamApiKey = context.Request.Headers["x-api-key"] ?? "";
                    string authSlot = authorization == "Bearer test-key-1" ? "key-1" :
                        authorization == "Bearer test-key-2" ? "key-2" :
                        authorization == "Bearer test-key-3" ? "key-3" : "unexpected";
                    string apiKeySlot = upstreamApiKey == "test-key-1" ? "key-1" :
                        upstreamApiKey == "test-key-2" ? "key-2" :
                        upstreamApiKey == "test-key-3" ? "key-3" : "none";
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                        context.Request.Url.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    {
                        string catalogModel = authSlot == "key-1" ? "deepseek-v4-flash" :
                            authSlot == "key-2" ? "qwen3.6-plus" :
                            authSlot == "key-3" ? "muse-spark-1.3-contributor" : "unexpected-model";
                        string catalogJson = "{\"object\":\"list\",\"data\":[{\"id\":\"" + catalogModel + "\",\"object\":\"model\"}]}";
                        byte[] catalog = Encoding.UTF8.GetBytes(catalogJson);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = catalog.Length;
                        context.Response.OutputStream.Write(catalog, 0, catalog.Length);
                        context.Response.Close();
                        Console.WriteLine("MOCK_MODEL_CATALOG status=200 key_slot=" + authSlot);
                        continue;
                    }
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                        context.Request.Url.AbsolutePath.EndsWith("/usage", StringComparison.OrdinalIgnoreCase))
                    {
                        string usageJson = "{\"usage\":{\"rolling\":{\"percent\":0},\"weekly\":{\"percent\":0},\"monthly\":{\"percent\":0}}}";
                        byte[] usageBytes = Encoding.UTF8.GetBytes(usageJson);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = usageBytes.Length;
                        context.Response.OutputStream.Write(usageBytes, 0, usageBytes.Length);
                        context.Response.Close();
                        continue;
                    }
                    byte[] body;
                    using (var memory = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(memory).ConfigureAwait(false);
                        body = memory.ToArray();
                    }
                    int attempt = completionCount;
                    if (attempt == 0) firstBody = body;
                    bool sameBody = firstBody != null && firstBody.SequenceEqual(body);
                    authSlots.Add(authSlot);
                    apiKeySlots.Add(apiKeySlot);
                    paths.Add(context.Request.Url.AbsolutePath);
                    var parsed = Json.DeserializeObject(Encoding.UTF8.GetString(body)) as IDictionary<string, object>;
                    models.Add(parsed != null && parsed.ContainsKey("model") ? Convert.ToString(parsed["model"]) : "missing");
                    int status = attempt == 0 ? failureStatus : (attempt == 1 ? 429 : 200);
                    statuses.Add(status);
                    string payload = attempt < 2
                        ? "{\"error\":{\"message\":\"simulated upstream failure\"}}"
                        : "{\"credentials\":\"test-key-1 test-key-2 test-local-key\"} ";
                    if (failureStatus == 400 && attempt == 0)
                    {
                        payload = "{\"error\":{\"message\":\"This Go model trains on request data. Allow paid endpoints that train on request data in your workspace's Privacy settings to use it.\"}}";
                    }
                    byte[] response = Encoding.UTF8.GetBytes(payload);
                    context.Response.StatusCode = status;
                    Console.WriteLine("MOCK_SENDING status=" + status + " request_index=" + attempt);
                    context.Response.ContentType = "application/json";
                    context.Response.Headers["x-request-id"] = "trace-test-key-2";
                    context.Response.ContentLength64 = response.Length;
                    context.Response.OutputStream.Write(response, 0, response.Length);
                    context.Response.Close();
                    if (!sameBody) throw new InvalidDataException("Retry request body changed between key attempts.");
                    completionCount++;
                }

                var report = new Dictionary<string, object>
                {
                    { "statuses", statuses }, { "auth_slots", authSlots }, { "api_key_slots", apiKeySlots }, { "models", models }, { "paths", paths },
                    { "request_body_identical", firstBody != null }, { "request_count", statuses.Count }
                };
                File.WriteAllText(reportPath, Json.Serialize(report), new UTF8Encoding(false));
                Console.WriteLine("MOCK_UPSTREAM_COMPLETE requests=" + statuses.Count);
            }
            finally { listener.Close(); }
        }

        private sealed class RequestTooLargeException : Exception { }
    }

    // ======================================================================
    // System tray icon — single icon with full proxy management.
    // Merged from SystemTray.cs: health checks, key reorder, file watchers.
    // ======================================================================
    internal sealed class ProxyTrayIcon : IDisposable
    {
        private System.Windows.Forms.NotifyIcon trayIcon;
        private System.Windows.Forms.Timer refreshTimer;
        private System.Windows.Forms.Timer healthTimer;
        private string credentialPath;
        private string configPath;
        private FileSystemWatcher keyWatcher;

        public ProxyTrayIcon(string credentialFilePath, string configFilePath)
        {
            credentialPath = credentialFilePath;
            configPath = configFilePath;

            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = StatusIcon(Color.FromArgb(0, 200, 80));
            trayIcon.Text = "OpenCode Go Proxy";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = BuildMenu();
            trayIcon.DoubleClick += (s, e) => ShowStatus();

            trayIcon.BalloonTipTitle = "OpenCode Go Proxy";
            trayIcon.BalloonTipText = "Proxy running. Right-click for options.";

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 30000;
            refreshTimer.Tick += (s, e) => UpdateStatus();
            refreshTimer.Start();

            healthTimer = new System.Windows.Forms.Timer();
            healthTimer.Interval = 120000;
            healthTimer.Tick += (s, e) => CheckHealth();
            healthTimer.Start();

            SetupKeyWatcher();
            UpdateStatus();
        }

        private System.Windows.Forms.ContextMenuStrip BuildMenu()
        {
            var m = new System.Windows.Forms.ContextMenuStrip();
            m.Items.Add("Show Status", null, (s, e) => ShowStatus());
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("Reorder Keys by Quota", null, (s, e) => ReorderKeys());
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("Open Config", null, (s, e) => SafeStart("notepad.exe", configPath));
            m.Items.Add("Open Keys", null, (s, e) => SafeStart("notepad.exe", credentialPath));
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("Exit", null, (s, e) => ExitApp());
            return m;
        }

        private void UpdateStatus()
        {
            try
            {
                int keyCount = 0;
                double minScore = 100;
                if (!String.IsNullOrEmpty(credentialPath) && File.Exists(credentialPath))
                {
                    string[] lines = File.ReadAllLines(credentialPath);
                    var keys = new List<string>();
                    foreach (string line in lines)
                    {
                        string k = line.Trim();
                        if (k.Length > 0) keys.Add(k);
                    }
                    keyCount = keys.Count;
                    foreach (string k in keys)
                    {
                        double s = Program.KeyScore(k);
                        if (s < minScore) minScore = s;
                    }
                }
                string statusText = keyCount + " key(s), best=" + minScore.ToString("0");
                trayIcon.Text = "OpenCode Go Proxy — " + statusText;
                if (minScore <= 0 && keyCount > 0)
                    trayIcon.Icon = StatusIcon(Color.FromArgb(255, 193, 7));
                else
                    trayIcon.Icon = StatusIcon(Color.FromArgb(0, 200, 80));
            }
            catch
            {
                trayIcon.Icon = StatusIcon(Color.FromArgb(220, 50, 47));
            }
        }

        private void CheckHealth()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:4001/health");
                req.Timeout = 5000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    int ki = json.IndexOf("\"credential_count\"");
                    int kv = ki >= 0 ? json.IndexOf(":", ki) + 1 : -1;
                    string ks = kv >= 0 ? json.Substring(kv, 20).Trim().TrimEnd('}', ',') : "?";
                    trayIcon.Text = "OpenCode Go Proxy — " + ks + " key(s)";
                }
            }
            catch
            {
                trayIcon.Text = "OpenCode Go Proxy — offline";
                trayIcon.Icon = StatusIcon(Color.FromArgb(220, 50, 47));
            }
        }

        private void ShowStatus()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:4001/health");
                req.Timeout = 5000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    string keys = ExtractJson(json, "credential_count");
                    string model = ExtractJson(json, "model");
                    string lines = "Proxy: running\nKeys: " + keys + "\nModel: " + model;
                    try
                    {
                        if (!String.IsNullOrEmpty(credentialPath) && File.Exists(credentialPath))
                        {
                            int count = File.ReadAllLines(credentialPath).Length;
                            lines += "\nKey file lines: " + count;
                        }
                    }
                    catch { }
                    System.Windows.Forms.MessageBox.Show(lines, "OpenCode Go Proxy",
                        System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                }
            }
            catch
            {
                System.Windows.Forms.MessageBox.Show("Proxy is not responding.", "OpenCode Go Proxy",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        private void ReorderKeys()
        {
            try
            {
                if (String.IsNullOrEmpty(credentialPath) || !File.Exists(credentialPath))
                {
                    trayIcon.ShowBalloonTip(3000, "Keys", "api.txt not found.", System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }
                string[] lines = File.ReadAllLines(credentialPath);
                var scored = new List<KeyValuePair<string, double>>();
                foreach (string line in lines)
                {
                    string k = line.Trim();
                    if (k.Length == 0) continue;
                    double max = ProbeUsage(k);
                    scored.Add(new KeyValuePair<string, double>(k, 100.0 - max));
                }
                if (scored.Count < 2)
                {
                    trayIcon.ShowBalloonTip(3000, "Keys", "Need 2+ keys to reorder.", System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }
                scored.Sort((a, b) => b.Value.CompareTo(a.Value));
                string backup = credentialPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(credentialPath, backup, true);
                var sorted = new List<string>();
                foreach (var s in scored) sorted.Add(s.Key);
                File.WriteAllLines(credentialPath, sorted.ToArray());
                trayIcon.ShowBalloonTip(3000, "Keys Reordered", "Best key first. Backup saved.", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "Keys Error", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        private static double ProbeUsage(string key)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://opencode.ai/zen/go/v1/usage");
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
                        double p = ParseWindowPercent(json, w);
                        if (p > max) max = p;
                    }
                    return max;
                }
            }
            catch { return 50.0; }
        }

        private static double ParseWindowPercent(string json, string window)
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
            if (double.TryParse(json.Substring(s, e - s).Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out p)) return p;
            return 0;
        }

        private void SetupKeyWatcher()
        {
            try
            {
                if (String.IsNullOrEmpty(credentialPath)) return;
                string keyDir = Path.GetDirectoryName(credentialPath);
                string keyName = Path.GetFileName(credentialPath);
                if (!String.IsNullOrEmpty(keyDir) && Directory.Exists(keyDir))
                {
                    keyWatcher = new FileSystemWatcher(keyDir, keyName) { EnableRaisingEvents = true };
                    keyWatcher.Changed += (s, e) => { UpdateStatus(); };
                }
            }
            catch { }
        }

        private void ExitApp()
        {
            healthTimer.Stop();
            refreshTimer.Stop();
            trayIcon.Visible = false;
            Log("TRAY_EXIT");
            Environment.Exit(0);
        }

        private static void SafeStart(string file, string arg)
        {
            try { System.Diagnostics.Process.Start(file, arg); } catch { }
        }

        private static void Log(string msg)
        {
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, "tray-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(logFile, DateTime.Now.ToString("o") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        private static string ExtractJson(string json, string key)
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

        private static Icon CreateProjectIcon(Color color)
        {
            // Project mark: a failover carousel — three keys rotating around a
            // bright center, so the glyph reads as "rotation/failover" at 16px.
            Bitmap bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                // Rounded dark badge so the mark reads on both dark & light tray bg.
                using (System.Drawing.Drawing2D.GraphicsPath badge = RoundedRect(new RectangleF(0.5f, 0.5f, 15, 15), 4))
                using (var badgeBrush = new SolidBrush(Color.FromArgb(230, 24, 28, 32)))
                    g.FillPath(badgeBrush, badge);
                // Three orbit dots: failover slots (prime / cooling / last-resort).
                // Two keep the status color; the trailing one is always slate so
                // the slot pattern stays recognizable in every state.
                using (Brush statusBrush = new SolidBrush(color))
                using (Brush dimBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                {
                    g.FillEllipse(statusBrush, 6.5f, 2.5f, 3, 3);   // top slot
                    g.FillEllipse(dimBrush, 10.5f, 9.0f, 3, 3);     // lower-right slot
                    g.FillEllipse(statusBrush, 2.5f, 9.0f, 3, 3);   // lower-left slot
                }
                // Center chevron: the active request routing through the pool.
                using (Pen arrow = new Pen(color, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    g.DrawLine(arrow, 4.8f, 5.2f, 11.2f, 8.0f);
                    g.DrawLine(arrow, 11.2f, 8.0f, 4.8f, 10.8f);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            Icon icon = Icon.FromHandle(hIcon);
            return icon;
        }

        // Distinct color states reuse cached icons; HttpListener ticks would
        // otherwise create (and leak) a native handle every refresh.
        private static readonly Dictionary<int, Icon> CachedIcons = new Dictionary<int, Icon>();

        private static Icon StatusIcon(Color color)
        {
            int key = color.ToArgb();
            Icon icon;
            if (!CachedIcons.TryGetValue(key, out icon))
            {
                icon = CreateProjectIcon(color);
                CachedIcons[key] = icon;
            }
            return icon;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void ShowNotification(string title, string text, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
        {
            trayIcon.ShowBalloonTip(3000, title, text, icon);
        }

        public void Dispose()
        {
            if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer.Dispose(); }
            if (healthTimer != null) { healthTimer.Stop(); healthTimer.Dispose(); }
            if (keyWatcher != null) keyWatcher.Dispose();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
        }
    }
}
