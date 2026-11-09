using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    internal sealed class ProjectConfigurationFiles
    {
        private readonly string _root;
        private long _bytes;
        private int _count;
        public ProjectConfigurationFiles(string root) { _root = Path.GetFullPath(root).TrimEnd('\\'); }
        [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        // Lock every path component against rename/delete while enumerating or opening its child.
        private List<SafeFileHandle> Lock(string path)
        {
            path = Path.GetFullPath(path);
            if (path != _root && !path.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase)) throw MarkdownConfiguration.Invalid("Configuration path escapes workspace");
            var handles = new List<SafeFileHandle>(); var current = _root;
            try
            {
                foreach (var part in new[] { "" }.Concat(path.Substring(_root.Length).TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)))
                {
                    if (part.Length > 0) current = Path.Combine(current, part);
                    var handle = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    handles.Add(handle);
                    if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0)
                        throw MarkdownConfiguration.Invalid("Configuration directories cannot be links");
                }
                return handles;
            }
            catch { foreach (var handle in handles) handle.Dispose(); throw; }
        }
        public string[] Enumerate(string relative, bool directories)
        {
            string path = Path.Combine(_root, relative);
            if (!Directory.Exists(path)) return XpArray.Empty<string>();
            var handles = Lock(path);
            try
            {
                var entries = directories ? Directory.GetDirectories(path) : Directory.GetFiles(path, "*.md");
                if (entries.Length > 128) throw MarkdownConfiguration.Invalid("Too many configuration entries");
                return entries.OrderBy(p => p, StringComparer.Ordinal).Select(p => p.Substring(_root.Length + 1)).ToArray();
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }
        public string? Read(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(_root, relative));
            if (!File.Exists(path)) return null;
            var handles = Lock(Path.GetDirectoryName(path)!);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var final = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
                if (length == 0 || length >= final.Capacity || !GetFileInformationByHandle(stream.SafeFileHandle, out var info) || info.Links != 1 || (info.Attributes & 0x400) != 0 ||
                    !string.Equals(final.ToString(), "\\\\?\\" + path, StringComparison.OrdinalIgnoreCase)) throw MarkdownConfiguration.Invalid("Configuration file cannot be a link");
                if (++_count > 128 || stream.Length > 65536 || (_bytes += stream.Length) > 512 * 1024) throw MarkdownConfiguration.Invalid("Configuration exceeds size/count limit");
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                return reader.ReadToEnd();
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }
    }
}
