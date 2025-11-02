using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Shared
{
    public sealed class FirebaseRtdb : IDisposable
    {
        private readonly string _baseUrl;
        private readonly string _authToken;
        private readonly HttpClient _http = new HttpClient();

        public FirebaseRtdb(string baseUrl, string authToken)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _authToken = authToken;
        }

        private static string EncodePath(string path)
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        private string BuildUrl(string pathNoJson)
        {
            var url = _baseUrl + "/" + EncodePath(pathNoJson) + ".json";
            if (!string.IsNullOrEmpty(_authToken))
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "auth=" + _authToken;
            return url;
        }

        public async Task<bool> EnsureMapNodeAsync(string path)
        {
            var get = await _http.GetAsync(BuildUrl(path));
            if (!get.IsSuccessStatusCode) return false;

            var body = await get.Content.ReadAsStringAsync();
            if (body == "null")
            {
                var put = await _http.PutAsync(BuildUrl(path),
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                return put.IsSuccessStatusCode;
            }
            return true;
        }

        public async Task<bool> PutRawJsonAsync(string path, string json)
        {
            var resp = await _http.PutAsync(BuildUrl(path),
                new StringContent(json, Encoding.UTF8, "application/json"));
            return resp.IsSuccessStatusCode;
        }

        public async Task<string> GetJsonAsync(string path)
        {
            var resp = await _http.GetAsync(BuildUrl(path));
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }

        public void Dispose() { _http.Dispose(); }
    }
}
