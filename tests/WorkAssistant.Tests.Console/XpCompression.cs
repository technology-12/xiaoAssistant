// 双线压缩辅助：net48 用 CompressionLevel 重载；net40 只有 CompressionMode（由兼容层提供）。

using System.IO;
using System.IO.Compression;

namespace WorkAssistant.Tests.Console
{
    public static class XpCompression
    {
        public static DeflateStream CreateDeflater(Stream stream, bool leaveOpen) =>
            new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen);
    }
}
