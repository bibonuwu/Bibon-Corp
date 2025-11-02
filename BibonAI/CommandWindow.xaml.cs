using Shared;
using System;
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

        public CommandWindow(string baseUrl, string authToken, string pcKey)
        {
            InitializeComponent();
            _baseUrl = baseUrl;
            _authToken = authToken;
            _pcKey = pcKey;
            Title = "Команда для: " + pcKey;
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

                Status.Text = "Отправлено. Жду ответа...";
                ResultBox.Text = "";

                // ждём результат до ~15 сек
                var ser = new JavaScriptSerializer();
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(500);
                    using (var fb = new FirebaseRtdb(_baseUrl, _authToken))
                    {
                        var resp = await fb.GetJsonAsync(PathCmdNode());
                        if (string.IsNullOrEmpty(resp) || resp == "null") continue;

                        var d = ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(resp);
                        string rid = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                        string status = d.ContainsKey("status") ? Convert.ToString(d["status"]) : "";

                        if (rid == id && status == "done")
                        {
                            string exit = d.ContainsKey("exitCode") ? Convert.ToString(d["exitCode"]) : "";
                            string stdout = d.ContainsKey("stdout") ? Convert.ToString(d["stdout"]) : "";
                            string stderr = d.ContainsKey("stderr") ? Convert.ToString(d["stderr"]) : "";

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
    }
}
