// XpCompression 的 net40 实现：DeflateStream 仅有 CompressionMode 构造。

using System.IO;
using System.IO.Compression;

namespace WorkAssistant.Tests.Console
{
    public static class XpCompression
    {
        public static DeflateStream CreateDeflater(Stream stream, bool leaveOpen) =>
            new DeflateStream(stream, CompressionMode.Compress, leaveOpen);
    }
}
