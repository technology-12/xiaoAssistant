using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class HostRequest
    {
        public TaskRecord Task { get; set; } = null!;
        public ModelSettings Model { get; set; } = new();
        public bool Fake { get; set; }
        public string StoreRoot { get; set; } = "";
        public AgentRunOptions Options { get; set; } = new();
        public string? PreviewPath { get; set; }
        public string PreviewSha256 { get; set; } = "";
        public bool LocalToolOperation { get; set; }
        public ToolCall? LocalToolCall { get; set; }
        public bool EnableSubAgents { get; set; }
        public ModelSettings? SubAgentModel { get; set; }
    }

    public sealed class LocalToolResponse
    {
        public System.Collections.Generic.IList<ToolDefinition> Definitions { get; set; } = new ToolDefinition[0];
        public string InputCatalog { get; set; } = "";
        public string SkillInstructions { get; set; } = "";
        public ToolResult? Result { get; set; }
    }

    // Length-prefixed UTF-8 JSON. No network socket and no credentials in arguments/logs.
    public sealed class HostChannel : IDisposable
    {
        public const int Version = 2;
        private const int MaximumBytes = 8 * 1024 * 1024;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writer = new(1, 1);
        public HostChannel(Stream stream) { _stream = stream; }
        public async Task SendAsync(string type, object payload, CancellationToken token = default)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { version = Version, type, payload }));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("IPC 消息超出限制。");
            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var header = BitConverter.GetBytes(bytes.Length);
                await _stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                await _stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
        }
        public async Task<JObject> ReadAsync(CancellationToken token)
        {
            var header = new byte[4];
            await ReadExact(header, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > MaximumBytes) throw new InvalidDataException("IPC 帧长度无效。");
            var bytes = new byte[length];
            await ReadExact(bytes, token).ConfigureAwait(false);
            var frame = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if ((int?)frame["version"] != Version) throw new InvalidDataException("IPC 协议版本不兼容。");
            return frame;
        }
        private async Task ReadExact(byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await _stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("任务进程连接已关闭。");
                offset += count;
            }
        }
        public void Dispose() { _stream.Dispose(); }
    }
}
