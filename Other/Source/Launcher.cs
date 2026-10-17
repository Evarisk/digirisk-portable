using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("DigiRisk Portable")]
[assembly: AssemblyDescription("Lanceur et proxy local DigiRisk pour PortableApps.com")]
[assembly: AssemblyCompany("Evarisk")]
[assembly: AssemblyProduct("DigiRiskPortable")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DigiRiskPortable
{
    static class Program
    {
        private static string RootDir;
        private static string AppWebDir;
        private static string DataSettingsDir;
        private static string DefaultSettingsFile;
        private static string UserSettingsFile;
        private static string UserQueueFile;
        private static int LocalPort;
        private static TcpListener ServerListener;
        private static volatile bool IsRunning = true;
        private static DateTime LastActivityUtc = DateTime.UtcNow;
        private static volatile bool HasConnectedAtLeastOnce = false;

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
            }
            catch { }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            // Support being launched either from DigiRiskPortable\DigiRiskPortable.exe or DigiRiskPortable\App\DigiRisk\DigiRiskApp.exe
            if (Directory.Exists(Path.Combine(exeDir, "App", "DigiRisk")))
            {
                RootDir = exeDir;
            }
            else
            {
                RootDir = Path.GetFullPath(Path.Combine(exeDir, "..", ".."));
            }

            AppWebDir = Path.Combine(RootDir, "App", "DigiRisk");
            DataSettingsDir = Path.Combine(RootDir, "Data", "settings");
            DefaultSettingsFile = Path.Combine(RootDir, "App", "DefaultData", "settings", "settings.json");
            UserSettingsFile = Path.Combine(DataSettingsDir, "settings.json");
            UserQueueFile = Path.Combine(DataSettingsDir, "queue.json");

            EnsurePortableDataDirectory();

            ServerListener = new TcpListener(IPAddress.Loopback, 0);
            ServerListener.Start();
            LocalPort = ((IPEndPoint)ServerListener.LocalEndpoint).Port;

            Thread serverThread = new Thread(AcceptLoop);
            serverThread.IsBackground = true;
            serverThread.Start();

            string appUrl = "http://127.0.0.1:" + LocalPort + "/index.html";
            LaunchAppWindow(appUrl);

            // Note: msedge.exe immediately delegates to an existing background Edge process and exits with code 0.
            // So we keep the local server running as long as the page sends heartbeats (or up to 60s before first load).
            DateTime startTime = DateTime.UtcNow;
            while (IsRunning)
            {
                Thread.Sleep(2000);
                double idleSecs = (DateTime.UtcNow - LastActivityUtc).TotalSeconds;
                if (!HasConnectedAtLeastOnce)
                {
                    if ((DateTime.UtcNow - startTime).TotalSeconds > 90)
                        break;
                }
                else
                {
                    // Page sends a heartbeat every 4 seconds; if no activity for 20 seconds (and no close signal), exit
                    if (idleSecs > 25)
                        break;
                }
            }

            IsRunning = false;
            try { ServerListener.Stop(); } catch { }
        }

        private static void EnsurePortableDataDirectory()
        {
            if (!Directory.Exists(DataSettingsDir))
            {
                Directory.CreateDirectory(DataSettingsDir);
            }
            if (!File.Exists(UserSettingsFile))
            {
                if (File.Exists(DefaultSettingsFile))
                {
                    File.Copy(DefaultSettingsFile, UserSettingsFile, false);
                }
                else
                {
                    File.WriteAllText(UserSettingsFile, "{\"serverUrl\":\"\",\"apiKey\":\"\",\"entity\":1,\"defaultElementId\":0,\"duProjectId\":1}", Encoding.UTF8);
                }
            }
            if (!File.Exists(UserQueueFile))
            {
                File.WriteAllText(UserQueueFile, "[]", Encoding.UTF8);
            }
        }

        private static Process LaunchAppWindow(string url)
        {
            string edgeProfileDir = Path.Combine(RootDir, "Data", "EdgeProfile");
            Directory.CreateDirectory(edgeProfileDir);

            string[] candidateBrowsers = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe")
            };

            foreach (string browserPath in candidateBrowsers)
            {
                if (File.Exists(browserPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = browserPath,
                        Arguments = "--app=\"" + url + "\" --window-size=960,760 --user-data-dir=\"" + edgeProfileDir + "\" --no-first-run --disable-extensions",
                        UseShellExecute = false
                    };
                    return Process.Start(psi);
                }
            }

            // Fallback to default system browser
            return Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        private static void AcceptLoop()
        {
            while (IsRunning)
            {
                try
                {
                    TcpClient client = ServerListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    if (!IsRunning) break;
                }
            }
        }

        private static void HandleClient(object state)
        {
            using (TcpClient client = (TcpClient)state)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;

                    string requestLine = ReadLine(stream);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0].ToUpperInvariant();
                    string rawPath = parts[1];

                    int contentLength = 0;
                    string hostHeader = "";
                    string originHeader = "";
                    bool hasLocalHeader = false;
                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = ReadLine(stream)))
                    {
                        if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(headerLine.Substring(15).Trim(), out contentLength);
                        }
                        else if (headerLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        {
                            hostHeader = headerLine.Substring(5).Trim();
                        }
                        else if (headerLine.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
                        {
                            originHeader = headerLine.Substring(7).Trim();
                        }
                        else if (headerLine.StartsWith("X-DigiRisk-Local:", StringComparison.OrdinalIgnoreCase))
                        {
                            hasLocalHeader = headerLine.Substring(17).Trim() == "1";
                        }
                    }

                    // Protection DNS-Rebinding : n'accepter que les requêtes adressées à 127.0.0.1 ou localhost
                    if (!string.IsNullOrEmpty(hostHeader) &&
                        !hostHeader.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
                        !hostHeader.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(hostHeader, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(hostHeader, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteHttpResponse(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Forbidden Host\"}"));
                        return;
                    }

                    // Protection CSRF : vérifier l'en-tête Origin s'il est présent
                    if (!string.IsNullOrEmpty(originHeader) &&
                        !originHeader.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
                        !originHeader.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteHttpResponse(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Forbidden Origin\"}"));
                        return;
                    }

                    // Limite de taille du corps HTTP à 50 Mo (protection mémoire tout en autorisant le multi-upload de photos)
                    if (contentLength < 0 || contentLength > 50 * 1024 * 1024)
                    {
                        WriteHttpResponse(stream, 413, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Payload Too Large\"}"));
                        return;
                    }

                    byte[] bodyBytes = new byte[contentLength];
                    int readTotal = 0;
                    while (readTotal < contentLength)
                    {
                        int n = stream.Read(bodyBytes, readTotal, contentLength - readTotal);
                        if (n <= 0) break;
                        readTotal += n;
                    }

                    string pathOnly = rawPath.Split('?')[0];
                    if (pathOnly == "/" || pathOnly == "") pathOnly = "/index.html";

                    LastActivityUtc = DateTime.UtcNow;
                    HasConnectedAtLeastOnce = true;

                    if (pathOnly == "/local/ping")
                    {
                        WriteHttpResponse(stream, 200, "application/json", Encoding.UTF8.GetBytes("{\"alive\":true}"));
                        return;
                    }

                    if (pathOnly == "/local/close")
                    {
                        WriteHttpResponse(stream, 200, "application/json", Encoding.UTF8.GetBytes("{\"closing\":true}"));
                        IsRunning = false;
                        return;
                    }

                    if (pathOnly == "/local/settings")
                    {
                        if (method == "POST" && !hasLocalHeader)
                        {
                            WriteHttpResponse(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Missing X-DigiRisk-Local header\"}"));
                            return;
                        }
                        HandleLocalSettings(stream, method, bodyBytes);
                        return;
                    }

                    if (pathOnly == "/local/proxy" && method == "POST")
                    {
                        if (!hasLocalHeader)
                        {
                            WriteHttpResponse(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Missing X-DigiRisk-Local header\"}"));
                            return;
                        }
                        HandleLocalProxy(stream, bodyBytes);
                        return;
                    }

                    ServeStaticFile(stream, pathOnly);
                }
                catch (Exception ex)
                {
                    WriteHttpResponse(stream, 500, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"" + EscapeJson(ex.Message) + "\"}"));
                }
            }
        }

        private static void HandleLocalSettings(NetworkStream stream, string method, byte[] bodyBytes)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (method == "GET")
            {
                string settingsJson = File.Exists(UserSettingsFile) ? File.ReadAllText(UserSettingsFile, Encoding.UTF8) : "{}";
                string queueJson = File.Exists(UserQueueFile) ? File.ReadAllText(UserQueueFile, Encoding.UTF8) : "[]";
                string combined = "{\"settings\":" + settingsJson + ",\"queue\":" + queueJson + "}";
                WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(combined));
            }
            else if (method == "POST")
            {
                string bodyStr = Encoding.UTF8.GetString(bodyBytes);
                var payload = serializer.Deserialize<Dictionary<string, object>>(bodyStr);
                if (payload != null)
                {
                    if (payload.ContainsKey("settings"))
                    {
                        File.WriteAllText(UserSettingsFile, serializer.Serialize(payload["settings"]), Encoding.UTF8);
                    }
                    if (payload.ContainsKey("queue"))
                    {
                        File.WriteAllText(UserQueueFile, serializer.Serialize(payload["queue"]), Encoding.UTF8);
                    }
                }
                WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"));
            }
        }

        private static void HandleLocalProxy(NetworkStream stream, byte[] bodyBytes)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string reqStr = Encoding.UTF8.GetString(bodyBytes);
            var proxyReq = serializer.Deserialize<Dictionary<string, object>>(reqStr);

            string targetUrl = proxyReq != null && proxyReq.ContainsKey("url") ? Convert.ToString(proxyReq["url"]) : "";
            string targetMethod = proxyReq != null && proxyReq.ContainsKey("method") ? Convert.ToString(proxyReq["method"]) : "GET";
            string targetBody = proxyReq != null && proxyReq.ContainsKey("body") && proxyReq["body"] != null ? Convert.ToString(proxyReq["body"]) : null;

            Uri validatedUri;
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out validatedUri) ||
                (validatedUri.Scheme != Uri.UriSchemeHttp && validatedUri.Scheme != Uri.UriSchemeHttps))
            {
                WriteHttpResponse(stream, 400, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Seules les URLs HTTP et HTTPS sont autorisées.\"}}"));
                return;
            }

            HttpWebRequest webReq = (HttpWebRequest)WebRequest.Create(validatedUri);
            webReq.Method = targetMethod;
            webReq.Timeout = 30000;
            webReq.UserAgent = "DigiRiskPortable/1.0";

            if (proxyReq != null && proxyReq.ContainsKey("headers") && proxyReq["headers"] is Dictionary<string, object>)
            {
                var hdrs = (Dictionary<string, object>)proxyReq["headers"];
                foreach (var kv in hdrs)
                {
                    string val = Convert.ToString(kv.Value);
                    if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                        webReq.Accept = val;
                    else if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        webReq.ContentType = val;
                    else
                        webReq.Headers[kv.Key] = val;
                }
            }

            if (!string.IsNullOrEmpty(targetBody) && (targetMethod == "POST" || targetMethod == "PUT"))
            {
                byte[] outBytes = Encoding.UTF8.GetBytes(targetBody);
                webReq.ContentLength = outBytes.Length;
                using (Stream reqStream = webReq.GetRequestStream())
                {
                    reqStream.Write(outBytes, 0, outBytes.Length);
                }
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)webReq.GetResponse())
                using (Stream respStream = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    respStream.CopyTo(ms);
                    WriteHttpResponse(stream, (int)resp.StatusCode, "application/json; charset=utf-8", ms.ToArray());
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    using (Stream errStream = errResp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        if (errStream != null) errStream.CopyTo(ms);
                        WriteHttpResponse(stream, (int)errResp.StatusCode, "application/json; charset=utf-8", ms.ToArray());
                    }
                }
                else
                {
                    string errJson = "{\"error\":{\"message\":\"" + EscapeJson(wex.Message) + "\"}}";
                    WriteHttpResponse(stream, 502, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(errJson));
                }
            }
        }

        private static void ServeStaticFile(NetworkStream stream, string relativePath)
        {
            string cleanRel = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string baseWebRoot = Path.GetFullPath(AppWebDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(AppWebDir, cleanRel));
            if (!fullPath.StartsWith(baseWebRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                WriteHttpResponse(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not Found"));
                return;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            string contentType = "application/octet-stream";
            if (ext == ".html") contentType = "text/html; charset=utf-8";
            else if (ext == ".png") contentType = "image/png";
            else if (ext == ".jpg" || ext == ".jpeg") contentType = "image/jpeg";
            else if (ext == ".svg") contentType = "image/svg+xml";
            else if (ext == ".css") contentType = "text/css; charset=utf-8";
            else if (ext == ".js") contentType = "application/javascript; charset=utf-8";
            else if (ext == ".ico") contentType = "image/x-icon";

            byte[] bytes = File.ReadAllBytes(fullPath);
            WriteHttpResponse(stream, 200, contentType, bytes);
        }

        private static string ReadLine(Stream stream)
        {
            List<byte> bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            if (b == -1 && bytes.Count == 0) return null;
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void WriteHttpResponse(NetworkStream stream, int statusCode, string contentType, byte[] body)
        {
            string statusText = statusCode == 200 ? "OK" : (statusCode == 404 ? "Not Found" : "Error");
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(statusCode).Append(" ").Append(statusText).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("X-Frame-Options: DENY\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
