// System.IO.Compression 的 net40 垫片：命名空间兼容的 ZipArchive/ZipFile 家族。
// 共享源码（OfficePackage/PackageZip/Xlsx*/Docx*/Word*/Presentation*）无需修改即可编译。
// - 读取：定位 EOCD → 中央目录枚举；拒绝加密、ZIP64 与非 Store/Deflate 方法；
//   解压经 DeflateStream（net40 System.dll 原生），读取结束校验 CRC32 与大小。
// - 写入：内存缓冲条目，Dispose 时输出确定字节流（固定 1980-01-01 时间戳、
//   保持传入顺序），同输入同输出，维持 SHA-256 可复现约束。
// - 上限（条目数/解压大小/压缩比）仍由共享 PackageZip.ValidateArchive 执行。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace System.IO.Compression
{
    public enum ZipArchiveMode { Read, Create, Update }
    public enum CompressionLevel { Optimal = 0, Fastest = 1, SmallestSize = 2, NoCompression = 3 }

    public sealed class ZipArchiveEntry
    {
        private readonly ZipArchive _owner;
        private byte[] _content = new byte[0];
        private DateTime _lastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public string FullName { get; }
        internal long StoredLength { get; set; }          // 未压缩大小（读：来自中央目录；写：内容长度）
        internal long CompressedLengthValue { get; set; } // 压缩大小
        internal uint Crc { get; set; }
        internal int Method { get; set; } = 8;
        internal long LocalHeaderOffset { get; set; }
        internal byte[] PendingContent => _content;

        internal ZipArchiveEntry(ZipArchive owner, string fullName)
        {
            _owner = owner;
            FullName = fullName;
        }

        public string Name { get { var index = FullName.LastIndexOf('/'); return index < 0 ? FullName : FullName.Substring(index + 1); } }

        public long Length => StoredLength;

        public long CompressedLength => CompressedLengthValue;

        public DateTime LastWriteTime
        {
            get => _lastWriteTime;
            set => _lastWriteTime = value;
        }

        public Stream Open()
        {
            if (_owner.Mode != ZipArchiveMode.Read)
            {
                var stream = new MemoryStream();
                return new EntryWriteStream(this, stream);
            }
            return _owner.OpenEntryStream(this);
        }

        private sealed class EntryWriteStream : Stream
        {
            private readonly ZipArchiveEntry _entry;
            private readonly MemoryStream _buffer;
            public EntryWriteStream(ZipArchiveEntry entry, MemoryStream buffer) { _entry = entry; _buffer = buffer; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _entry._content = _buffer.ToArray();
                    _entry.StoredLength = _entry._content.Length;
                    _entry.Crc = ZipCrc32Table.Compute(_entry._content);
                }
                base.Dispose(disposing);
            }
        }
    }

    public sealed class ZipArchive : IDisposable
    {
        private Stream _stream;
        private readonly bool _leaveOpen;
        private List<ZipArchiveEntry> _entries = new List<ZipArchiveEntry>();

        internal ZipArchiveMode Mode { get; }

        public ZipArchive(Stream stream) : this(stream, ZipArchiveMode.Read, false) { }

        public ZipArchive(Stream stream, ZipArchiveMode mode) : this(stream, mode, false) { }

        public ZipArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen)
        {
            _stream = stream ?? throw new ArgumentNullException("stream");
            Mode = mode;
            _leaveOpen = leaveOpen;
            if (mode == ZipArchiveMode.Read)
            {
                if (!_stream.CanRead || !_stream.CanSeek) throw new InvalidDataException("ZIP 读取需要可寻址流。");
                ReadCentralDirectory();
            }
            else if (!_stream.CanWrite)
            {
                throw new InvalidDataException("ZIP 写入需要可写流。");
            }
        }

        public IList<ZipArchiveEntry> Entries
        {
            get
            {
                if (Mode != ZipArchiveMode.Read && Mode != ZipArchiveMode.Update)
                    throw new InvalidDataException("仅读取模式支持条目枚举。");
                return _entries;
            }
        }

        public ZipArchiveEntry GetEntry(string entryName)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.FullName, entryName, StringComparison.Ordinal)) return entry;
            }
            return null!;
        }

        public ZipArchiveEntry CreateEntry(string entryName) => CreateEntry(entryName, CompressionLevel.Optimal);

        public ZipArchiveEntry CreateEntry(string entryName, CompressionLevel compressionLevel)
        {
            if (Mode == ZipArchiveMode.Read) throw new InvalidDataException("读取模式不支持创建条目。");
            var entry = new ZipArchiveEntry(this, entryName);
            _entries.Add(entry);
            return entry;
        }

        public void Dispose()
        {
            if (_stream == null) return;
            try
            {
                if (Mode == ZipArchiveMode.Create || Mode == ZipArchiveMode.Update) WriteArchive();
            }
            finally
            {
                if (!_leaveOpen) _stream.Dispose();
                _stream = null!;
                _entries = null!;
            }
        }

        // ---- 读取 ------------------------------------------------------------

        private void ReadCentralDirectory()
        {
            long length = _stream.Length;
            if (length < 22) throw new InvalidDataException("ZIP 过小。");
            var tailSize = (int)Math.Min(length, 65557);
            _stream.Seek(length - tailSize, SeekOrigin.Begin);
            var tail = new byte[tailSize];
            ReadExactly(tail, tailSize);
            int eocd = -1;
            for (int index = tailSize - 22; index >= 0; index--)
            {
                if (tail[index] == 0x50 && tail[index + 1] == 0x4b && tail[index + 2] == 0x05 && tail[index + 3] == 0x06)
                {
                    eocd = index;
                    break;
                }
            }
            if (eocd < 0) throw new InvalidDataException("缺少 ZIP 结束目录。");
            int entryCount = ReadUInt16(tail, eocd + 10);
            long directoryOffset = ReadUInt32(tail, eocd + 16);
            _stream.Seek(directoryOffset, SeekOrigin.Begin);
            var directory = new byte[ReadUInt32(tail, eocd + 12)];
            ReadExactly(directory, directory.Length);
            var entries = new List<ZipArchiveEntry>(entryCount);
            int position = 0;
            for (int index = 0; index < entryCount; index++)
            {
                if (position + 46 > directory.Length ||
                    directory[position] != 0x50 || directory[position + 1] != 0x4b || directory[position + 2] != 0x01 || directory[position + 3] != 0x02)
                    throw new InvalidDataException("ZIP 中央目录损坏。");
                int flags = ReadUInt16(directory, position + 8);
                int method = ReadUInt16(directory, position + 10);
                uint crc = ReadUInt32(directory, position + 16);
                long compressed = ReadUInt32(directory, position + 20);
                long uncompressed = ReadUInt32(directory, position + 24);
                int nameLength = ReadUInt16(directory, position + 28);
                int extraLength = ReadUInt16(directory, position + 30);
                int commentLength = ReadUInt16(directory, position + 32);
                long localOffset = ReadUInt32(directory, position + 42);
                if ((flags & 0x1) != 0) throw new InvalidDataException("不支持加密 ZIP 条目。");
                if (compressed == 0xFFFFFFFF || uncompressed == 0xFFFFFFFF || localOffset == 0xFFFFFFFF)
                    throw new InvalidDataException("不支持 ZIP64 条目。");
                if (method != 0 && method != 8) throw new InvalidDataException("不支持的压缩方法：" + method);
                if (position + 46 + nameLength > directory.Length) throw new InvalidDataException("ZIP 条目名损坏。");
                var nameBytes = new byte[nameLength];
                Array.Copy(directory, position + 46, nameBytes, 0, nameLength);
                string name = DecodeName(nameBytes, (flags & 0x800) != 0);
                var entry = new ZipArchiveEntry(this, name)
                {
                    StoredLength = uncompressed,
                    CompressedLengthValue = compressed,
                    Crc = crc,
                    Method = method,
                    LocalHeaderOffset = localOffset,
                };
                entries.Add(entry);
                position += 46 + nameLength + extraLength + commentLength;
            }
            _entries = entries;
        }

        internal Stream OpenEntryStream(ZipArchiveEntry entry)
        {
            _stream.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);
            var header = new byte[30];
            ReadExactly(header, header.Length);
            if (header[0] != 0x50 || header[1] != 0x4b || header[2] != 0x03 || header[3] != 0x04)
                throw new InvalidDataException("ZIP 本地头损坏。");
            int nameLength = ReadUInt16(header, 26);
            int extraLength = ReadUInt16(header, 28);
            long dataOffset = entry.LocalHeaderOffset + 30 + nameLength + extraLength;
            var window = new SubStream(_stream, dataOffset, entry.CompressedLengthValue);
            if (entry.Method == 0)
            {
                return new CrcStream(window, entry.Crc, entry.StoredLength);
            }
            return new CrcStream(new DeflateStream(window, CompressionMode.Decompress), entry.Crc, entry.StoredLength);
        }

        private void ReadExactly(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new InvalidDataException("ZIP 数据不完整。");
                offset += read;
            }
        }

        private static string DecodeName(byte[] bytes, bool utf8Flag)
        {
            if (utf8Flag) return Encoding.UTF8.GetString(bytes);
            foreach (var value in bytes)
            {
                if (value >= 0x80) return Encoding.UTF8.GetString(bytes);
            }
            return Encoding.ASCII.GetString(bytes);
        }

        // ---- 写入（确定字节输出）----------------------------------------------

        private void WriteArchive()
        {
            long baseOffset = _stream.Position;
            var central = new MemoryStream();
            foreach (var entry in _entries)
            {
                var content = entry.PendingContent;
                byte[] compressed;
                if (content.Length == 0)
                {
                    compressed = content;
                    entry.Method = 8; // 空内容压缩结果为空数据段
                }
                else
                {
                    var buffer = new MemoryStream();
                    using (var deflater = new DeflateStream(buffer, CompressionMode.Compress, leaveOpen: true))
                    {
                        deflater.Write(content, 0, content.Length);
                    }
                    compressed = buffer.ToArray();
                }
                if (compressed.Length >= content.Length && content.Length > 0)
                {
                    compressed = content; // 压缩无收益时改用 Store，保持确定行为
                    entry.Method = 0;
                }
                entry.CompressedLengthValue = compressed.Length;
                var nameBytes = Encoding.UTF8.GetBytes(entry.FullName);
                bool utf8Flag = nameBytes.Length != entry.FullName.Length || RequiresUtf8(entry.FullName);
                int flags = utf8Flag ? 0x800 : 0;

                entry.LocalHeaderOffset = _stream.Position - baseOffset;
                WriteUInt32(_stream, 0x04034b50);  // 本地头签名
                WriteUInt16(_stream, 20);
                WriteUInt16(_stream, flags);
                WriteUInt16(_stream, entry.Method);
                WriteUInt16(_stream, 0);       // 固定时间 00:00:00
                WriteUInt16(_stream, 0x0021);  // 固定日期 1980-01-01
                WriteUInt32(_stream, entry.Crc);
                WriteUInt32(_stream, (uint)compressed.Length);
                WriteUInt32(_stream, (uint)content.Length);
                WriteUInt16(_stream, nameBytes.Length);
                WriteUInt16(_stream, 0);
                _stream.Write(nameBytes, 0, nameBytes.Length);
                _stream.Write(compressed, 0, compressed.Length);

                WriteUInt32(central, 0x02014b50);  // 中央目录签名
                WriteUInt16(central, 20);           // version made by
                WriteUInt16(central, 20);           // version needed
                WriteUInt16(central, flags);
                WriteUInt16(central, entry.Method);
                WriteUInt16(central, 0);
                WriteUInt16(central, 0x0021);
                WriteUInt32(central, entry.Crc);
                WriteUInt32(central, (uint)compressed.Length);
                WriteUInt32(central, (uint)content.Length);
                WriteUInt16(central, nameBytes.Length);
                WriteUInt16(central, 0);            // extra
                WriteUInt16(central, 0);            // comment
                WriteUInt16(central, 0);            // disk start
                WriteUInt16(central, 0);            // internal attrs
                WriteUInt32(central, 0);            // external attrs
                WriteUInt32(central, (uint)entry.LocalHeaderOffset);
                central.Write(nameBytes, 0, nameBytes.Length);
            }
            long directoryOffset = _stream.Position - baseOffset;
            var directoryBytes = central.ToArray();
            _stream.Write(directoryBytes, 0, directoryBytes.Length);
            WriteUInt32(_stream, 0x06054b50);  // EOCD 签名
            WriteUInt16(_stream, 0);            // 当前磁盘号
            WriteUInt16(_stream, 0);            // 中央目录起始磁盘
            WriteUInt16(_stream, _entries.Count);
            WriteUInt16(_stream, _entries.Count);
            WriteUInt32(_stream, (uint)directoryBytes.Length);
            WriteUInt32(_stream, (uint)directoryOffset);
            WriteUInt16(_stream, 0);            // 注释长度
        }

        private static bool RequiresUtf8(string name)
        {
            foreach (char value in name)
            {
                if (value > 0x7F) return true;
            }
            return false;
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static int ReadUInt16(byte[] buffer, int offset) =>
            buffer[offset] | (buffer[offset + 1] << 8);

        private static uint ReadUInt32(byte[] buffer, int offset) =>
            (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

        private sealed class SubStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;
            public SubStream(Stream inner, long offset, long length)
            {
                _inner = inner;
                _inner.Seek(offset, SeekOrigin.Begin);
                _remaining = length;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int chunk = (int)Math.Min(count, _remaining);
                int read = _inner.Read(buffer, offset, chunk);
                _remaining -= read;
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>读取结束时校验 CRC32 与解压大小；不匹配按 InvalidDataException 处理。</summary>
        private sealed class CrcStream : Stream
        {
            private readonly Stream _inner;
            private readonly uint _expectedCrc;
            private readonly long _expectedLength;
            private readonly ZipCrc32 _crc = new ZipCrc32();
            private long _read;
            public CrcStream(Stream inner, uint expectedCrc, long expectedLength)
            {
                _inner = inner;
                _expectedCrc = expectedCrc;
                _expectedLength = expectedLength;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _expectedLength;
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                if (read > 0)
                {
                    _crc.Update(buffer, offset, read);
                    _read += read;
                }
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_read != _expectedLength || _crc.Value != _expectedCrc)
                        throw new InvalidDataException("ZIP 条目校验失败（CRC 或大小不符）。");
                }
                base.Dispose(disposing);
            }
        }
    }

    public static class ZipFile
    {
        public static ZipArchive OpenRead(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { return new ZipArchive(stream, ZipArchiveMode.Read, false); }
            catch { stream.Dispose(); throw; }
        }
    }

    internal static class ZipCrc32Table
    {
        public static readonly uint[] Values = Build();
        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }
                table[index] = value;
            }
            return table;
        }

        public static uint Compute(byte[] data)
        {
            uint value = 0xFFFFFFFFu;
            foreach (var octet in data)
            {
                value = Values[(byte)(value ^ octet)] ^ (value >> 8);
            }
            return ~value;
        }
    }

    internal sealed class ZipCrc32
    {
        private uint _value = 0xFFFFFFFFu;
        public uint Value => ~_value;
        public void Update(byte[] data, int offset, int count)
        {
            for (int index = 0; index < count; index++)
            {
                _value = ZipCrc32Table.Values[(byte)(_value ^ data[offset + index])] ^ (_value >> 8);
            }
        }
    }
}
