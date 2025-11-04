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
            _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        }

        private static string EncodePath(string path)
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        private string BuildUrl(string pathNoJson, bool noCache = false)
        {
            var url = _baseUrl + "/" + EncodePath(pathNoJson) + ".json";
            if (!string.IsNullOrEmpty(_authToken))
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "auth=" + _authToken;

            if (noCache)
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "nc=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
            // для PUT кэш-бастер не нужен
            var resp = await _http.PutAsync(BuildUrl(path, noCache: false),
                new StringContent(json, Encoding.UTF8, "application/json"));
            return resp.IsSuccessStatusCode;
        }

        public async Task<string> GetJsonAsync(string path)
        {
            // ВАЖНО: noCache = true
            var resp = await _http.GetAsync(BuildUrl(path, noCache: true));
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }

        public void Dispose() { _http.Dispose(); }
    }
}
