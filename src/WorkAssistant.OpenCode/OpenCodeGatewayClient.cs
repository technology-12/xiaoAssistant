using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.OpenCode
{
    public sealed class OpenCodeGatewayClient : IDisposable
    {
        private readonly HttpClient _client;
        public OpenCodeGatewayClient(OpenCodeConnection settings)
        {
            settings.Validate();
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Token);
        }
        public async Task<JObject> SendAsync(string route, JObject? payload, CancellationToken token)
        {
            using var request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, route);
            if (payload != null) request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ToolException(ErrorCodes.RemoteKernelUnavailable, "远程内核访问令牌未通过认证，请检查内核连接设置。");
            if (!response.IsSuccessStatusCode)
                throw new ToolException(ErrorCodes.RemoteKernelUnavailable, "远程内核请求失败，HTTP " + (int)response.StatusCode + "。任务不会自动重新提交。");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var memory = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = await input.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "远程内核响应超过 1 MiB。");
                memory.Write(bytes, 0, count);
            }
            return JObject.Parse(new UTF8Encoding(false, true).GetString(memory.ToArray()));
        }
        public void Dispose() => _client.Dispose();
    }
}
