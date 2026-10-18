using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
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
        private const string VAULT_MASK = "__VAULT_PROTECTED__";
        private const string VAULT_CHECK_PLAIN = "DIGIRISK_VAULT_OK_V1";
        private const int PBKDF2_ITERATIONS = 100000;

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

        // Coffre-fort en mémoire vive (RAM uniquement, purgé à la fermeture ou au verrouillage)
        private static readonly object VaultLock = new object();
        private static string MasterPasswordInMemory = null;
        private static readonly Dictionary<string, string> DecryptedKeysBySessionId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string DecryptedActiveKey = "";
        private static int FailedUnlockAttempts = 0;

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
                    // Page sends a heartbeat every 4 seconds; if no activity for 25 seconds (and no close signal), exit
                    if (idleSecs > 25)
                        break;
                }
            }

            LockVaultInMemory();
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
                        LockVaultInMemory();
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

                    if (pathOnly == "/local/vault" && method == "POST")
                    {
                        if (!hasLocalHeader)
                        {
                            WriteHttpResponse(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Missing X-DigiRisk-Local header\"}"));
                            return;
                        }
                        HandleLocalVault(stream, bodyBytes);
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

        private static bool IsEncryptedValue(string val)
        {
            return !string.IsNullOrEmpty(val) && val.StartsWith("ENC:v1:", StringComparison.Ordinal);
        }

        private static string EncryptSecret(string plainText, string masterPassword)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            if (IsEncryptedValue(plainText)) return plainText;

            byte[] salt = new byte[16];
            byte[] iv = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            byte[] aesKey;
            byte[] hmacKey;
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(masterPassword, salt, PBKDF2_ITERATIONS))
            {
                aesKey = kdf.GetBytes(32);
                hmacKey = kdf.GetBytes(32);
            }

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes;
            using (AesManaged aes = new AesManaged())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                {
                    cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                }
            }

            byte[] macInput = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, macInput, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, macInput, iv.Length, cipherBytes.Length);

            byte[] macBytes;
            using (HMACSHA256 hmac = new HMACSHA256(hmacKey))
            {
                macBytes = hmac.ComputeHash(macInput);
            }

            return "ENC:v1:" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(macBytes) + ":" + Convert.ToBase64String(cipherBytes);
        }

        private static string DecryptSecret(string encryptedValue, string masterPassword)
        {
            if (string.IsNullOrEmpty(encryptedValue)) return "";
            if (!IsEncryptedValue(encryptedValue)) return encryptedValue;

            string[] parts = encryptedValue.Split(':');
            if (parts.Length != 6)
            {
                throw new CryptographicException("Format de clé chiffrée invalide.");
            }

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] iv = Convert.FromBase64String(parts[3]);
            byte[] expectedMac = Convert.FromBase64String(parts[4]);
            byte[] cipherBytes = Convert.FromBase64String(parts[5]);

            byte[] aesKey;
            byte[] hmacKey;
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(masterPassword, salt, PBKDF2_ITERATIONS))
            {
                aesKey = kdf.GetBytes(32);
                hmacKey = kdf.GetBytes(32);
            }

            byte[] macInput = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, macInput, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, macInput, iv.Length, cipherBytes.Length);

            byte[] computedMac;
            using (HMACSHA256 hmac = new HMACSHA256(hmacKey))
            {
                computedMac = hmac.ComputeHash(macInput);
            }

            if (!AreEqualConstantTime(expectedMac, computedMac))
            {
                throw new CryptographicException("Mot de passe maître ou code PIN incorrect.");
            }

            using (AesManaged aes = new AesManaged())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }

        private static bool AreEqualConstantTime(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static void LockVaultInMemory()
        {
            lock (VaultLock)
            {
                MasterPasswordInMemory = null;
                DecryptedKeysBySessionId.Clear();
                DecryptedActiveKey = "";
            }
        }

        private static Dictionary<string, object> ReadRawSettingsFromDisk(JavaScriptSerializer serializer)
        {
            if (!File.Exists(UserSettingsFile)) return new Dictionary<string, object>();
            try
            {
                string raw = File.ReadAllText(UserSettingsFile, Encoding.UTF8);
                var dict = serializer.Deserialize<Dictionary<string, object>>(raw);
                return dict ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static Dictionary<string, object> BuildClientSafeSettings(Dictionary<string, object> diskSettings, out bool vaultConfigured, out bool vaultUnlocked, out bool hasUnprotectedKeys)
        {
            vaultConfigured = false;
            hasUnprotectedKeys = false;

            string vaultCheck = diskSettings.ContainsKey("vaultCheck") ? Convert.ToString(diskSettings["vaultCheck"]) : "";
            if (IsEncryptedValue(vaultCheck))
            {
                vaultConfigured = true;
            }

            var safeCopy = new Dictionary<string, object>(diskSettings);
            safeCopy.Remove("vaultCheck");

            string activeId = diskSettings.ContainsKey("activeConnectionId") ? Convert.ToString(diskSettings["activeConnectionId"]) : "";
            string rootKey = diskSettings.ContainsKey("apiKey") ? Convert.ToString(diskSettings["apiKey"]) : "";
            if (!string.IsNullOrEmpty(rootKey))
            {
                if (IsEncryptedValue(rootKey)) vaultConfigured = true;
                else if (rootKey != VAULT_MASK) hasUnprotectedKeys = true;
                safeCopy["apiKey"] = VAULT_MASK;
                safeCopy["hasApiKey"] = true;
            }
            else
            {
                safeCopy["apiKey"] = "";
                safeCopy["hasApiKey"] = false;
            }

            if (diskSettings.ContainsKey("connections") && diskSettings["connections"] is IEnumerable)
            {
                var safeConns = new List<Dictionary<string, object>>();
                foreach (object item in (IEnumerable)diskSettings["connections"])
                {
                    var conn = item as Dictionary<string, object>;
                    if (conn == null) continue;
                    var sc = new Dictionary<string, object>(conn);
                    string ck = conn.ContainsKey("apiKey") ? Convert.ToString(conn["apiKey"]) : "";
                    if (!string.IsNullOrEmpty(ck))
                    {
                        if (IsEncryptedValue(ck)) vaultConfigured = true;
                        else if (ck != VAULT_MASK) hasUnprotectedKeys = true;
                        sc["apiKey"] = VAULT_MASK;
                        sc["hasApiKey"] = true;
                    }
                    else
                    {
                        sc["apiKey"] = "";
                        sc["hasApiKey"] = false;
                    }
                    safeConns.Add(sc);
                }
                safeCopy["connections"] = safeConns;
            }

            lock (VaultLock)
            {
                vaultUnlocked = !string.IsNullOrEmpty(MasterPasswordInMemory);
                // Si des clés en clair existent encore (avant configuration initiale du coffre), les charger en RAM pour ne pas bloquer l'application
                if (!vaultConfigured && hasUnprotectedKeys && DecryptedKeysBySessionId.Count == 0)
                {
                    PopulatePlainKeysInMemory(diskSettings, activeId);
                }
            }

            return safeCopy;
        }

        private static void PopulatePlainKeysInMemory(Dictionary<string, object> diskSettings, string activeId)
        {
            string rootKey = diskSettings.ContainsKey("apiKey") ? Convert.ToString(diskSettings["apiKey"]) : "";
            if (!string.IsNullOrEmpty(rootKey) && !IsEncryptedValue(rootKey) && rootKey != VAULT_MASK)
            {
                DecryptedActiveKey = rootKey;
            }
            if (diskSettings.ContainsKey("connections") && diskSettings["connections"] is IEnumerable)
            {
                foreach (object item in (IEnumerable)diskSettings["connections"])
                {
                    var conn = item as Dictionary<string, object>;
                    if (conn == null) continue;
                    string cid = conn.ContainsKey("id") ? Convert.ToString(conn["id"]) : "";
                    string ck = conn.ContainsKey("apiKey") ? Convert.ToString(conn["apiKey"]) : "";
                    if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(ck) && !IsEncryptedValue(ck) && ck != VAULT_MASK)
                    {
                        DecryptedKeysBySessionId[cid] = ck;
                        if (string.Equals(cid, activeId, StringComparison.OrdinalIgnoreCase))
                        {
                            DecryptedActiveKey = ck;
                        }
                    }
                }
            }
        }

        private static void HandleLocalSettings(NetworkStream stream, string method, byte[] bodyBytes)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (method == "GET")
            {
                var diskSettings = ReadRawSettingsFromDisk(serializer);
                bool vaultConfigured, vaultUnlocked, hasUnprotectedKeys;
                var safeSettings = BuildClientSafeSettings(diskSettings, out vaultConfigured, out vaultUnlocked, out hasUnprotectedKeys);
                string queueJson = File.Exists(UserQueueFile) ? File.ReadAllText(UserQueueFile, Encoding.UTF8) : "[]";

                string safeSettingsJson = serializer.Serialize(safeSettings);
                string vaultJson = "{\"configured\":" + (vaultConfigured ? "true" : "false") +
                                   ",\"unlocked\":" + (vaultUnlocked ? "true" : "false") +
                                   ",\"needsSetup\":" + ((!vaultConfigured && hasUnprotectedKeys) ? "true" : "false") + "}";
                string combined = "{\"settings\":" + safeSettingsJson + ",\"queue\":" + queueJson + ",\"vault\":" + vaultJson + "}";
                WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(combined));
            }
            else if (method == "POST")
            {
                string bodyStr = Encoding.UTF8.GetString(bodyBytes);
                var payload = serializer.Deserialize<Dictionary<string, object>>(bodyStr);
                if (payload != null)
                {
                    if (payload.ContainsKey("settings") && payload["settings"] is Dictionary<string, object>)
                    {
                        var incomingSettings = (Dictionary<string, object>)payload["settings"];
                        var existingDisk = ReadRawSettingsFromDisk(serializer);
                        SaveMergedSettingsToDisk(incomingSettings, existingDisk, serializer);
                    }
                    if (payload.ContainsKey("queue"))
                    {
                        File.WriteAllText(UserQueueFile, serializer.Serialize(payload["queue"]), Encoding.UTF8);
                    }
                }
                WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"));
            }
        }

        private static void SaveMergedSettingsToDisk(Dictionary<string, object> incoming, Dictionary<string, object> existingDisk, JavaScriptSerializer serializer)
        {
            lock (VaultLock)
            {
                var existingKeyByConnId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (existingDisk.ContainsKey("connections") && existingDisk["connections"] is IEnumerable)
                {
                    foreach (object item in (IEnumerable)existingDisk["connections"])
                    {
                        var c = item as Dictionary<string, object>;
                        if (c == null) continue;
                        string cid = c.ContainsKey("id") ? Convert.ToString(c["id"]) : "";
                        string ck = c.ContainsKey("apiKey") ? Convert.ToString(c["apiKey"]) : "";
                        if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(ck))
                        {
                            existingKeyByConnId[cid] = ck;
                        }
                    }
                }

                string existingRootKey = existingDisk.ContainsKey("apiKey") ? Convert.ToString(existingDisk["apiKey"]) : "";
                string existingVaultCheck = existingDisk.ContainsKey("vaultCheck") ? Convert.ToString(existingDisk["vaultCheck"]) : "";
                if (!string.IsNullOrEmpty(existingVaultCheck))
                {
                    incoming["vaultCheck"] = existingVaultCheck;
                }

                string activeId = incoming.ContainsKey("activeConnectionId") ? Convert.ToString(incoming["activeConnectionId"]) : "";

                if (incoming.ContainsKey("connections") && incoming["connections"] is IEnumerable)
                {
                    var updatedConns = new List<Dictionary<string, object>>();
                    foreach (object item in (IEnumerable)incoming["connections"])
                    {
                        var conn = item as Dictionary<string, object>;
                        if (conn == null) continue;
                        var copyConn = new Dictionary<string, object>(conn);
                        copyConn.Remove("hasApiKey");
                        string cid = copyConn.ContainsKey("id") ? Convert.ToString(copyConn["id"]) : "";
                        string inKey = copyConn.ContainsKey("apiKey") ? Convert.ToString(copyConn["apiKey"]) : "";

                        if (inKey == VAULT_MASK)
                        {
                            copyConn["apiKey"] = existingKeyByConnId.ContainsKey(cid) ? existingKeyByConnId[cid] : "";
                        }
                        else if (!string.IsNullOrEmpty(inKey) && !IsEncryptedValue(inKey))
                        {
                            // Nouvelle clé API saisie par l'utilisateur
                            DecryptedKeysBySessionId[cid] = inKey;
                            if (!string.IsNullOrEmpty(MasterPasswordInMemory))
                            {
                                copyConn["apiKey"] = EncryptSecret(inKey, MasterPasswordInMemory);
                            }
                            else
                            {
                                copyConn["apiKey"] = inKey;
                            }
                        }

                        if (string.Equals(cid, activeId, StringComparison.OrdinalIgnoreCase))
                        {
                            incoming["apiKey"] = copyConn["apiKey"];
                            if (DecryptedKeysBySessionId.ContainsKey(cid))
                            {
                                DecryptedActiveKey = DecryptedKeysBySessionId[cid];
                            }
                        }
                        updatedConns.Add(copyConn);
                    }
                    incoming["connections"] = updatedConns;
                }
                else
                {
                    string inRootKey = incoming.ContainsKey("apiKey") ? Convert.ToString(incoming["apiKey"]) : "";
                    if (inRootKey == VAULT_MASK)
                    {
                        incoming["apiKey"] = existingRootKey;
                    }
                    else if (!string.IsNullOrEmpty(inRootKey) && !IsEncryptedValue(inRootKey))
                    {
                        DecryptedActiveKey = inRootKey;
                        if (!string.IsNullOrEmpty(MasterPasswordInMemory))
                        {
                            incoming["apiKey"] = EncryptSecret(inRootKey, MasterPasswordInMemory);
                        }
                    }
                }

                incoming.Remove("hasApiKey");
                File.WriteAllText(UserSettingsFile, serializer.Serialize(incoming), Encoding.UTF8);
            }
        }

        private static void HandleLocalVault(NetworkStream stream, byte[] bodyBytes)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string bodyStr = Encoding.UTF8.GetString(bodyBytes);
            var req = serializer.Deserialize<Dictionary<string, object>>(bodyStr) ?? new Dictionary<string, object>();
            string action = req.ContainsKey("action") ? Convert.ToString(req["action"]) : "";

            if (action == "lock")
            {
                LockVaultInMemory();
                WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true,\"unlocked\":false}"));
                return;
            }

            if (action == "unlock" || action == "setup")
            {
                string masterPassword = req.ContainsKey("masterPassword") ? Convert.ToString(req["masterPassword"]) : "";
                if (string.IsNullOrEmpty(masterPassword) || masterPassword.Length < 4)
                {
                    WriteHttpResponse(stream, 400, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"Le code PIN ou mot de passe maître doit contenir au moins 4 caractères.\"}"));
                    return;
                }

                lock (VaultLock)
                {
                    if (FailedUnlockAttempts >= 5)
                    {
                        Thread.Sleep(1500);
                    }

                    var diskSettings = ReadRawSettingsFromDisk(serializer);
                    string existingVaultCheck = diskSettings.ContainsKey("vaultCheck") ? Convert.ToString(diskSettings["vaultCheck"]) : "";

                    try
                    {
                        if (IsEncryptedValue(existingVaultCheck))
                        {
                            string checkPlain = DecryptSecret(existingVaultCheck, masterPassword);
                            if (checkPlain != VAULT_CHECK_PLAIN)
                            {
                                throw new CryptographicException("Mot de passe maître ou code PIN incorrect.");
                            }
                        }

                        var newDecryptedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string activeId = diskSettings.ContainsKey("activeConnectionId") ? Convert.ToString(diskSettings["activeConnectionId"]) : "";
                        bool modifiedOnDisk = false;

                        if (!IsEncryptedValue(existingVaultCheck))
                        {
                            diskSettings["vaultCheck"] = EncryptSecret(VAULT_CHECK_PLAIN, masterPassword);
                            modifiedOnDisk = true;
                        }

                        if (diskSettings.ContainsKey("connections") && diskSettings["connections"] is IEnumerable)
                        {
                            var updatedConns = new List<Dictionary<string, object>>();
                            foreach (object item in (IEnumerable)diskSettings["connections"])
                            {
                                var conn = item as Dictionary<string, object>;
                                if (conn == null) continue;
                                var copyConn = new Dictionary<string, object>(conn);
                                string cid = copyConn.ContainsKey("id") ? Convert.ToString(copyConn["id"]) : "";
                                string ck = copyConn.ContainsKey("apiKey") ? Convert.ToString(copyConn["apiKey"]) : "";

                                if (!string.IsNullOrEmpty(ck))
                                {
                                    if (IsEncryptedValue(ck))
                                    {
                                        string plain = DecryptSecret(ck, masterPassword);
                                        newDecryptedMap[cid] = plain;
                                    }
                                    else if (ck != VAULT_MASK)
                                    {
                                        newDecryptedMap[cid] = ck;
                                        copyConn["apiKey"] = EncryptSecret(ck, masterPassword);
                                        modifiedOnDisk = true;
                                    }
                                }

                                if (string.Equals(cid, activeId, StringComparison.OrdinalIgnoreCase) && copyConn.ContainsKey("apiKey"))
                                {
                                    diskSettings["apiKey"] = copyConn["apiKey"];
                                }
                                updatedConns.Add(copyConn);
                            }
                            diskSettings["connections"] = updatedConns;
                        }

                        string rootKey = diskSettings.ContainsKey("apiKey") ? Convert.ToString(diskSettings["apiKey"]) : "";
                        string newActivePlain = "";
                        if (!string.IsNullOrEmpty(activeId) && newDecryptedMap.ContainsKey(activeId))
                        {
                            newActivePlain = newDecryptedMap[activeId];
                        }
                        else if (!string.IsNullOrEmpty(rootKey))
                        {
                            if (IsEncryptedValue(rootKey))
                            {
                                newActivePlain = DecryptSecret(rootKey, masterPassword);
                            }
                            else if (rootKey != VAULT_MASK)
                            {
                                newActivePlain = rootKey;
                                diskSettings["apiKey"] = EncryptSecret(rootKey, masterPassword);
                                modifiedOnDisk = true;
                            }
                        }

                        if (modifiedOnDisk)
                        {
                            File.WriteAllText(UserSettingsFile, serializer.Serialize(diskSettings), Encoding.UTF8);
                        }

                        MasterPasswordInMemory = masterPassword;
                        DecryptedKeysBySessionId.Clear();
                        foreach (var kv in newDecryptedMap)
                        {
                            DecryptedKeysBySessionId[kv.Key] = kv.Value;
                        }
                        DecryptedActiveKey = newActivePlain;
                        FailedUnlockAttempts = 0;

                        WriteHttpResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true,\"unlocked\":true,\"configured\":true}"));
                    }
                    catch (CryptographicException)
                    {
                        FailedUnlockAttempts++;
                        WriteHttpResponse(stream, 401, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"Code PIN ou mot de passe maître incorrect.\"}"));
                    }
                }
                return;
            }

            WriteHttpResponse(stream, 400, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"Action vault inconnue.\"}"));
        }

        private static string ResolveApiKeyForProxy(string sessionId, string providedHeaderKey)
        {
            if (!string.IsNullOrEmpty(providedHeaderKey) && providedHeaderKey != VAULT_MASK && !IsEncryptedValue(providedHeaderKey))
            {
                return providedHeaderKey;
            }
            lock (VaultLock)
            {
                if (!string.IsNullOrEmpty(sessionId) && DecryptedKeysBySessionId.ContainsKey(sessionId))
                {
                    return DecryptedKeysBySessionId[sessionId];
                }
                return DecryptedActiveKey ?? "";
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
            string sessionId = proxyReq != null && proxyReq.ContainsKey("sessionId") ? Convert.ToString(proxyReq["sessionId"]) : "";

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
                    else if (string.Equals(kv.Key, "DOLAPIKEY", StringComparison.OrdinalIgnoreCase))
                    {
                        string resolvedKey = ResolveApiKeyForProxy(sessionId, val);
                        if (string.IsNullOrEmpty(resolvedKey) && val == VAULT_MASK)
                        {
                            WriteHttpResponse(stream, 423, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Coffre verrouillé : veuillez saisir votre code PIN ou mot de passe maître.\"}}"));
                            return;
                        }
                        if (!string.IsNullOrEmpty(resolvedKey))
                        {
                            webReq.Headers["DOLAPIKEY"] = resolvedKey;
                        }
                    }
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
