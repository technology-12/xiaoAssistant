// XP 变体（替换 src/WorkAssistant.Infrastructure/LimitedReadStream.cs）。
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WorkAssistant.Infrastructure
{
    internal sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;
        public LimitedReadStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
        private int Count(int count) { _read += count; if (_read > _limit) throw new InvalidDataException("读取数据超过配置的大小上限。"); return count; }
        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
        // XP：net40 Stream 无虚 ReadAsync；异步读取经兼容层扩展（BeginRead→Read）仍执行本上限检查。
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
