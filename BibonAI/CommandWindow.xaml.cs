using Shared;
using SharedChat;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace BibonAI
{
    public partial class CommandWindow : Window
    {
        private readonly string _baseUrl;
        private readonly string _authToken;
        private readonly string _pcKey;
        private System.Timers.Timer _resultPoll;
        private string _currentCmdId;
        private int _pollTicks;

        public CommandWindow(string baseUrl, string authToken, string pcKey)
        {
            InitializeComponent();
            _baseUrl = baseUrl;
            _authToken = authToken;
            _pcKey = pcKey;
            Title = "Команда для: " + pcKey;
        }


        private async void OpenChat_Click(object sender, RoutedEventArgs e)
        {
            using (var fb = new FirebaseRtdb(_baseUrl, _authToken))
                await fb.PutRawJsonAsync($"PC List/{_pcKey}/Chat/Control/AdminOpen", "1");

            var chat = new ChatWindow(_baseUrl, _authToken, _pcKey, "Admin", true) { Owner = this };
            chat.Show();
        }

        private static string JsonEscape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private string PathCmdNode() => $"PC List/{_pcKey}/Comands";

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var cmd = (CmdBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(cmd)) { Status.Text = "Введите команду"; return; }

            string id = Guid.NewGuid().ToString("N");
            var json = "{"
                + $"\"id\":\"{JsonEscape(id)}\","
                + $"\"cmd\":\"{JsonEscape(cmd)}\","
                + $"\"ts\":\"{DateTime.Now:dd.MM.yyyy HH:mm:ss}\","
                + "\"status\":\"new\""
                + "}";

            try
            {
                using (var fb = new FirebaseRtdb(_baseUrl, _authToken))
                    await fb.PutRawJsonAsync(PathCmdNode(), json);

                _currentCmdId = id;
                Status.Text = "Отправлено. Жду ответа…";
                ResultBox.Text = "";
                StartResultPoll();


                // ждём результат до ~15 сек
                var ser = new JavaScriptSerializer();
                // ждём до 60 сек, опрашиваем быстрее
                for (int i = 0; i < 200; i++) // 200 * 300ms ~= 60s
                {
                    await Task.Delay(300);
                    using (var fb = new FirebaseRtdb(_baseUrl, _authToken))
                    {
                        var resp = await fb.GetJsonAsync(PathCmdNode());
                        if (string.IsNullOrEmpty(resp) || resp == "null") continue;

                        var d = ser.Deserialize<Dictionary<string, object>>(resp);
                        var rid = d.TryGetValue("id", out var v1) ? Convert.ToString(v1) : "";
                        var status = d.TryGetValue("status", out var v2) ? Convert.ToString(v2) : "";

                        // внутри цикла опроса
                        // если это наш ответ
                        if (rid == id)
                        {
                            if (status == "running")
                                Status.Text = "Выполняется…";

                            if (status == "done")
                            {
                                // Ждём, пока появится exitCode
                                if (!d.TryGetValue("exitCode", out var v3))
                                    continue; // ещё не долетело — подождём следующую итерацию

                                string exit = Convert.ToString(v3);
                                Status.Text = "Готово. exitCode = " + exit;

                                // stdout/stderr могут появиться на один-два такта позже — берём, если уже есть
                                string stdout = d.TryGetValue("stdout", out var v4) ? Convert.ToString(v4) : "";
                                string stderr = d.TryGetValue("stderr", out var v5) ? Convert.ToString(v5) : "";

                                ResultBox.Text = (string.IsNullOrEmpty(stdout) ? "" : ("STDOUT:\n" + stdout + "\n"))
                                               + (string.IsNullOrEmpty(stderr) ? "" : ("STDERR:\n" + stderr));

                                // Если хотите дождаться текста полностью — не выходите сразу, а дайте ещё пару итераций.
                                return;
                            }
                        }

                        if (status == "running")
                            Status.Text = "Выполняется…";   // мгновенная обратная связь

                        if (status == "done")
                        {
                            string exit = d.TryGetValue("exitCode", out var v3) ? Convert.ToString(v3) : "";
                            string stdout = d.TryGetValue("stdout", out var v4) ? Convert.ToString(v4) : "";
                            string stderr = d.TryGetValue("stderr", out var v5) ? Convert.ToString(v5) : "";
                            Status.Text = "Готово. exitCode = " + exit;
                            ResultBox.Text = (string.IsNullOrEmpty(stdout) ? "" : ("STDOUT:\n" + stdout + "\n"))
                                           + (string.IsNullOrEmpty(stderr) ? "" : ("STDERR:\n" + stderr));
                            return;
                        }
                    }
                }
                Status.Text = "Таймаут ожидания ответа.";


            }
            catch (Exception ex)
            {
                Status.Text = "Ошибка: " + ex.Message;
            }
        }

        private void StartResultPoll()
        {
            _resultPoll?.Stop();
            _resultPoll?.Dispose();

            _pollTicks = 0;
            _resultPoll = new System.Timers.Timer(300) { AutoReset = true };
            _resultPoll.Elapsed += async (s, e) =>
            {
                _pollTicks++;
                if (_pollTicks > 400) // ~2 мин. максимум
                {
                    _resultPoll.Stop();
                    Dispatcher.Invoke(() => Status.Text = "Таймаут ожидания ответа.");
                    return;
                }

                try
                {
                    using (var fb = new FirebaseRtdb(_baseUrl, _authToken))
                    {
                        var json = await fb.GetJsonAsync(PathCmdNode());
                        if (string.IsNullOrEmpty(json) || json == "null") return;

                        var ser = new JavaScriptSerializer();
                        var d = ser.Deserialize<Dictionary<string, object>>(json)
                                ?? new Dictionary<string, object>();

                        // читаем поля
                        string rid = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                        string status = d.ContainsKey("status") ? Convert.ToString(d["status"]) : "";
                        string exit = d.ContainsKey("exitCode") ? Convert.ToString(d["exitCode"]) : null;
                        string stdout = d.ContainsKey("stdout") ? Convert.ToString(d["stdout"]) : "";
                        string stderr = d.ContainsKey("stderr") ? Convert.ToString(d["stderr"]) : "";

                        if (rid != _currentCmdId) return; // не наша команда — ждём

                        Dispatcher.Invoke(() =>
                        {
                            if (status == "running")
                                Status.Text = "Выполняется…";

                            if (status == "done")
                            {
                                // exitCode может прилететь чуть позже — показываем когда он есть
                                Status.Text = "Готово. exitCode = " + (exit ?? "…");

                                // вывод может появиться на 1-2 тика позже — подменяем по мере поступления
                                var text = (string.IsNullOrEmpty(stdout) ? "" : ("STDOUT:\n" + stdout + "\n"))
                                         + (string.IsNullOrEmpty(stderr) ? "" : ("STDERR:\n" + stderr));
                                ResultBox.Text = text;

                                // если есть хоть что-то из вывода — можно завершать опрос
                                if (!string.IsNullOrEmpty(exit) && (!string.IsNullOrEmpty(stdout) || !string.IsNullOrEmpty(stderr)))
                                {
                                    _resultPoll.Stop();
                                    _resultPoll.Dispose();
                                }
                            }
                        });
                    }
                }
                catch { /* глушим, чтобы таймер не падал */ }
            };

            _resultPoll.Start();
        }
        protected override void OnClosed(EventArgs e)
        {
            try { _resultPoll?.Stop(); _resultPoll?.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private void pcoff(object sender, RoutedEventArgs e)
        {

        }
    }
}
