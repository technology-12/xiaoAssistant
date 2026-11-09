using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class DocumentLimits
    {
        public const long MaxExpandedBytes = 64L * 1024 * 1024;
        public const int MaxEntries = 2000;
        public const int MaxColumns = 16384;
        private static readonly AsyncLocal<CancellationToken> Token = new();
        private static readonly AsyncLocal<Stopwatch?> Timer = new();
        private static readonly AsyncLocal<Action<string, string>?> Checkpoint = new();
        private static readonly AsyncLocal<string?> Tool = new();
        private static readonly AsyncLocal<Action<string>?> Progress = new();
        private static readonly AsyncLocal<string?> PdfCache = new();
        private static readonly AsyncLocal<int> BudgetSeconds = new();
        public static string ToolName => Tool.Value ?? "";
        public static void Begin(CancellationToken token, string toolName = "") { Token.Value = token; Timer.Value = Stopwatch.StartNew(); Tool.Value = toolName; BudgetSeconds.Value = 60; }
        public static void ConfigurePdfReading(string cacheDirectory, Action<string>? progress = null) { PdfCache.Value = cacheDirectory; Progress.Value = progress; }
        internal static string? PdfCacheDirectory => PdfCache.Value;
        internal static void BeginOcr() { BudgetSeconds.Value = 600; Check(); }
        internal static void ReportProgress(string message) { Check(); Progress.Value?.Invoke(message); }
        public static void ConfigureCheckpoint(Action<string, string> callback) => Checkpoint.Value = callback;
        public static void Report(string state, string manifest) => Checkpoint.Value?.Invoke(state, manifest);
        public static void Check()
        {
            Token.Value.ThrowIfCancellationRequested();
            int seconds = BudgetSeconds.Value > 0 ? BudgetSeconds.Value : 60;
            if (Timer.Value?.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new ToolException(ErrorCodes.LimitExceeded, "文档处理超过 " + seconds + " 秒限制。 ");
        }
        public static byte[] ReadBounded(Stream source, long limit = MaxExpandedBytes)
        {
            using var output = new MemoryStream(); var buffer = new byte[32768]; int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                Check();
                if (output.Length + read > limit) throw new ToolException(ErrorCodes.LimitExceeded, "解压后内容超过限制。 ");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        public static XmlReader Xml(Stream source) => XmlReader.Create(source, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxExpandedBytes });
    }
}
