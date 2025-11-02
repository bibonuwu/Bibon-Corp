using Shared; // FirebaseRtdb
using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Web.Script.Serialization;
using System.Windows;

namespace SharedChat
{
    public partial class ChatWindow : Window
    {
        private readonly string _baseUrl;
        private readonly string _auth;
        private readonly string _pcKey;
        private readonly string _me;
        private readonly bool _iAmAdmin;

        private Timer _poll;
        private int _lastCount = 0;

        public ChatWindow(string baseUrl, string authToken, string pcKey, string myDisplayName, bool iAmAdmin)
        {
            InitializeComponent();
            _baseUrl = baseUrl;
            _auth = authToken;               // <-- было _authToken (ошибка)
            _pcKey = pcKey;
            _me = myDisplayName;
            _iAmAdmin = iAmAdmin;

            Title = $"Chat – {pcKey} ({_me})";
            Loaded += ChatWindow_Loaded;
            Closing += ChatWindow_Closing;
        }

        private string PathChat(string leaf) => $"PC List/{_pcKey}/Chat/{leaf}";
        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string NowStr() => DateTime.Now.ToString("HH:mm:ss");

        private async void ChatWindow_Loaded(object sender, RoutedEventArgs e)
        {
            using (var fb = new FirebaseRtdb(_baseUrl, _auth))   // <-- _baseUrl/_auth
            {
                await fb.PutRawJsonAsync(PathChat("Chat Online or ofline/ON"), "1");
                await fb.PutRawJsonAsync(PathChat("Chat Online or ofline/OFF"), "0");
            }
            StartPoll();
        }

        private void StartPoll()
        {
            _poll = new Timer(1000) { AutoReset = true };
            _poll.Elapsed += async (s, e) =>
            {
                try
                {
                    using (var fb = new FirebaseRtdb(_baseUrl, _auth))
                    {
                        var json = await fb.GetJsonAsync(PathChat("Messages"));
                        if (string.IsNullOrEmpty(json) || json == "null")
                        {
                            Dispatcher.Invoke(() => MsgList.ItemsSource = null);
                            _lastCount = 0;
                            return;
                        }

                        var ser = new JavaScriptSerializer();
                        var root = ser.Deserialize<Dictionary<string, object>>(json)
                                   ?? new Dictionary<string, object>();

                        var msgs = new List<Message>();
                        foreach (var kv in root.OrderBy(k => k.Key))
                        {
                            var obj = kv.Value as Dictionary<string, object>;
                            if (obj == null) continue;
                            msgs.Add(new Message
                            {
                                Sender = obj.ContainsKey("Sender") ? Convert.ToString(obj["Sender"]) : "",
                                Text = obj.ContainsKey("Text") ? Convert.ToString(obj["Text"]) : ""
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
                catch { }
            };
            _poll.Start();
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
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
                    var max = root.Keys
                        .Select(k =>
                        {
                            // ключи вида "1message", "2message" -> берём числовой префикс
                            int n = 0;
                            for (int i = 0; i < k.Length && char.IsDigit(k[i]); i++)
                                n = n * 10 + (k[i] - '0');
                            return n;
                        })
                        .DefaultIfEmpty(0).Max();
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

        private void Input_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) Send_Click(sender, e);
        }

        private async void ChatWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _poll?.Stop();
                _poll?.Dispose();
                if (_iAmAdmin)
                {
                    using (var fb = new FirebaseRtdb(_baseUrl, _auth))
                        await fb.PutRawJsonAsync(PathChat("Chat Online or ofline/ON"), "0");
                }
            }
            catch { }
        }

        public sealed class Message
        {
            public string Sender { get; set; }
            public string Text { get; set; }
        }
    }
}
