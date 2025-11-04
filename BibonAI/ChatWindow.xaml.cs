using Shared; // FirebaseRtdb
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization; // System.Web.Extensions
using System.Windows;
using System.Windows.Input;

namespace SharedChat
{
    public partial class ChatWindow : Window
    {
        private readonly string _baseUrl;
        private readonly string _auth;
        private readonly string _pcKey;
        private readonly string _me;
        private readonly bool _iAmAdmin;
        private string PathCtrl(string leaf) => $"PC List/{_pcKey}/Chat/Control/{leaf}";
        private string PathPres(string leaf) => $"PC List/{_pcKey}/Chat/Presence/{leaf}";
        private System.Timers.Timer _peerPoll;

        private Timer _poll;
        private int _lastCount = 0;

        public ChatWindow(string baseUrl, string authToken, string pcKey, string myDisplayName, bool iAmAdmin)
        {
            InitializeComponent();
            _baseUrl = baseUrl;
            _auth = authToken;
            _pcKey = pcKey;
            _me = myDisplayName;
            _iAmAdmin = iAmAdmin;

            Title = $"Chat – {pcKey} ({_me})";
            Loaded += ChatWindow_Loaded;
            Closing += ChatWindow_Closing;
        }

        private string PathChat(string leaf) => $"PC List/{_pcKey}/Chat/{leaf}";
        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string NowStr() => DateTime.Now.ToString("HH:mm");

        private async void ChatWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Сбрасываем визуальное состояние перед стартом таймеров
            Dispatcher.Invoke(() =>
            {
                MsgList.ItemsSource = null;
                _lastCount = 0;
            });

            using (var fb = new FirebaseRtdb(_baseUrl, _auth))
            {
                if (_iAmAdmin)
                    await fb.PutRawJsonAsync(PathPres("AdminOnline"), "1");
                else
                    await fb.PutRawJsonAsync(PathPres("ClientOnline"), "1");

                if (_iAmAdmin)
                    await fb.PutRawJsonAsync(PathCtrl("AdminOpen"), "1");
                else
                    await fb.PutRawJsonAsync(PathCtrl("ClientOpen"), "1");
            }

            StartPoll();
            StartPeerPresencePoll();
        }

        private void StartPeerPresencePoll()
        {
            _peerPoll = new System.Timers.Timer(1000) { AutoReset = true };
            _peerPoll.Elapsed += async (_, __) =>
            {
                try
                {
                    using (var fb = new FirebaseRtdb(_baseUrl, _auth))
                    {
                        // читаем Control и Presence
                        var presNode = _iAmAdmin ? "ClientOnline" : "AdminOnline";
                        var openPeer = _iAmAdmin ? "ClientOpen" : "AdminOpen";

                        var pres = await fb.GetJsonAsync(PathPres(presNode));
                        var open = await fb.GetJsonAsync(PathCtrl(openPeer));

                        bool peerOnline = pres == "1";
                        bool peerOpen = open == "1";

                        Dispatcher.Invoke(() =>
                        {
                            // Покажем статус в заголовке
                            Title = $"Chat – {_pcKey} ({_me}) — " +
                                    (peerOnline ? "собеседник онлайн" : "клиент офлайн");

                            // Если я клиент и админ закрыл окно — закрываемся
                            if (!_iAmAdmin && !peerOpen && IsVisible)
                                Close();

                            // Если я админ и клиент закрыл окно/ушёл — просто подсветим «офлайн»
                            // (здесь ничего не закрываем у админа — только статус)
                        });
                    }
                }
                catch { }
            };
            _peerPoll.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _peerPoll?.Stop(); _peerPoll?.Dispose(); } catch { }
            base.OnClosed(e);
        }
        private void StartPoll()
        {
            _poll = new Timer(1000) { AutoReset = true };
            _poll.Elapsed += async (_, __) =>
            {
                try
                {
                    using (var fb = new FirebaseRtdb(_baseUrl, _auth))
                    {
                        var json = await fb.GetJsonAsync(PathChat("Messages"));
                        if (string.IsNullOrEmpty(json) || json == "null")
                        {
                            Dispatcher.Invoke(() => { MsgList.ItemsSource = null; _lastCount = 0; });
                            return;
                        }

                        var ser = new JavaScriptSerializer();
                        var root = ser.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();

                        var msgs = new List<Message>();
                        foreach (var kv in root.OrderBy(k => k.Key))
                        {
                            var obj = kv.Value as Dictionary<string, object>;
                            if (obj == null) continue;

                            string sender = obj.ContainsKey("Sender") ? Convert.ToString(obj["Sender"]) : "";
                            string raw = obj.ContainsKey("Text") ? Convert.ToString(obj["Text"]) : "";

                            // парсим "[HH:mm] message"
                            string time = "";
                            string body = raw ?? "";
                            if (!string.IsNullOrEmpty(body) && body.Length > 2 && body[0] == '[')
                            {
                                int close = body.IndexOf(']');
                                if (close > 0 && close + 1 < body.Length)
                                {
                                    time = body.Substring(1, close - 1);
                                    int start = close + 1;
                                    if (start < body.Length && body[start] == ' ') start++;
                                    body = body.Substring(start);
                                }
                            }

                            msgs.Add(new Message
                            {
                                Sender = sender,
                                Body = body,
                                Time = time,
                                IsMine = string.Equals(sender, _me, StringComparison.OrdinalIgnoreCase)
                            });
                        }

                        Dispatcher.Invoke(() =>
                        {
                            MsgList.ItemsSource = msgs;
                            if (msgs.Count > _lastCount)
                            {
                                _lastCount = msgs.Count;
                                if (MsgList.Items.Count > 0)
                                    MsgList.ScrollIntoView(MsgList.Items[MsgList.Items.Count - 1]);
                            }
                        });
                    }
                }
                catch { /* не роняем UI */ }
            };
            _poll.Start();
        }

        private async Task SendAsync()
        {
            var text = (Input.Text ?? "").Trim();
            if (text.Length == 0) return;
            Input.Clear();

            int nextN = 1;
            using (var fb = new FirebaseRtdb(_baseUrl, _auth))
            {
                var json = await fb.GetJsonAsync(PathChat("Messages"));
                var ser = new JavaScriptSerializer();
                var root = (!string.IsNullOrEmpty(json) && json != "null")
                    ? ser.Deserialize<Dictionary<string, object>>(json)
                    : null;

                if (root != null && root.Count > 0)
                {
                    var max = root.Keys.Select(k =>
                    {
                        int n = 0;
                        for (int i = 0; i < k.Length && char.IsDigit(k[i]); i++)
                            n = n * 10 + (k[i] - '0');
                        return n;
                    }).DefaultIfEmpty(0).Max();
                    nextN = max + 1;
                }

                var node = $"Messages/{nextN}message";
                var msgJson = "{"
                    + $"\"Sender\":\"{J(_me)}\","
                    + $"\"Text\":\"{J($"[{NowStr()}] {text}")}\""
                    + "}";
                await fb.PutRawJsonAsync(PathChat(node), msgJson);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            await SendAsync();
        }

        private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Enter — отправка; Shift+Enter — перенос строки
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;         // блокируем вставку перевода строки
                await SendAsync();        // отправляем
            }
        }


        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true; // Enter — отправить, Shift+Enter — перенос строки
                Send_Click(sender, e);
            }
        }

        private async void ChatWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _poll?.Stop(); _poll?.Dispose();

                using (var fb = new FirebaseRtdb(_baseUrl, _auth))
                {
                    if (_iAmAdmin)
                    {
                        await fb.PutRawJsonAsync(PathCtrl("AdminOpen"), "0");   // скажем клиенту закрыться
                        await fb.PutRawJsonAsync(PathPres("AdminOnline"), "0");
                    }
                    else
                    {
                        await fb.PutRawJsonAsync(PathCtrl("ClientOpen"), "0");  // админ увидит «офлайн»
                        await fb.PutRawJsonAsync(PathPres("ClientOnline"), "0");
                    }
                }
            }
            catch { }

        }

        public sealed class Message
        {
            public string Sender { get; set; }
            public string Body { get; set; } // текст без префикса времени
            public string Time { get; set; } // только время
            public bool IsMine { get; set; } // свои/чужие
        }
    }
}
