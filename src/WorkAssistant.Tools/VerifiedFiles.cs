using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class VerifiedFiles
    {
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
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr buffer, uint length);
        public static void MoveVerified(string source, string target, string root, string expectedHash)
        {
            var locks = new System.Collections.Generic.List<SafeFileHandle>();
            try
            {
                var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
                var parent = Path.GetDirectoryName(Path.GetFullPath(target))!;
                if (!parent.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase) && !string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ToolException(ErrorCodes.PathDenied, "目标目录越界。 ");
                var current = normalizedRoot;
                foreach (var part in new[] { "" }.Concat(parent.Substring(normalizedRoot.Length).TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)))
                {
                    if (part.Length > 0) current = Path.Combine(current, part);
                    if (part.Length > 0 && !Directory.Exists(current)) Directory.CreateDirectory(current);
                    var directory = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (directory.IsInvalid || !GetFileInformationByHandle(directory, out var dirInfo) || (dirInfo.Attributes & 0x400) != 0)
                    { directory.Dispose(); throw new ToolException(ErrorCodes.PathDenied, "无法锁定目标目录或存在目录联接。 "); }
                    locks.Add(directory);
                }
                using var handle = CreateFile(source, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("无法锁定源文件：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                var buffer = new StringBuilder(32768);
                if (GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0) == 0 ||
                    !GetFileInformationByHandle(handle, out var info) || info.Links != 1 || (info.Attributes & 0x400) != 0)
                    throw new ToolException(ErrorCodes.PathDenied, "源文件句柄不符合移动要求。 ");
                var final = buffer.ToString(); if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final.Substring(4);
                if (!final.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.PathDenied, "源文件最终路径越界。 ");
                using var stream = new FileStream(handle, FileAccess.Read);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.PlanInvalid, "源文件自生成计划后已变化，请重新确认。 ");
                var name = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
                var pointerOffset = IntPtr.Size == 8 ? 8 : 4;
                var lengthOffset = pointerOffset + IntPtr.Size; var nameOffset = lengthOffset + 4;
                // Include a deterministic terminating WCHAR; FileNameLength counts only the name bytes.
                // This also satisfies the Win32 FileName member's NUL-terminated string contract.
                int bufferLength = nameOffset + name.Length + 2;
                var memory = Marshal.AllocHGlobal(bufferLength);
                try
                {
                    Marshal.Copy(new byte[bufferLength], 0, memory, bufferLength);
                    Marshal.WriteInt32(memory, lengthOffset, name.Length);
                    Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
                    if (!SetFileInformationByHandle(handle, 3, memory, (uint)bufferLength))
                        throw new IOException("移动失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            finally { foreach (var directory in locks) directory.Dispose(); }
        }
        public static FileStream OpenRead(string path, string permittedRoot)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity || !GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                    throw new ToolException(ErrorCodes.PathDenied, "无法校验文件句柄。 ");
                var final = buffer.ToString();
                if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final.Substring(4);
                var root = Path.GetFullPath(permittedRoot).TrimEnd('\\') + "\\";
                if (!final.StartsWith(root, StringComparison.OrdinalIgnoreCase) || info.Links > 1)
                    throw new ToolException(ErrorCodes.PathDenied, "文件最终位置越界或存在硬链接，已拒绝。 ");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        public static string CaptureInputs(string workspace, string destination, CancellationToken token)
        {
            if (Directory.Exists(destination)) throw new IOException("快照目录已存在，拒绝覆盖。 ");
            Directory.CreateDirectory(Path.Combine(destination, "inputs"));
            var policy = new WorkspacePolicy(workspace);
            var files = new JArray(); long bytes = 0; var count = 0;
            var pending = new System.Collections.Generic.Stack<string>(); pending.Push(policy.InputsRoot);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var directory = pending.Pop();
                foreach (var child in Directory.GetDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new ToolException(ErrorCodes.PathDenied, "快照拒绝目录联接。 ");
                    pending.Push(child);
                }
                foreach (var file in Directory.GetFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 1000) throw new ToolException(ErrorCodes.LimitExceeded, "工作区输入超过 1000 个文件。 ");
                    var relative = policy.ToWorkspaceRelative(file);
                    policy.ResolveOriginalReadablePath(relative);
                    using var source = OpenRead(file, policy.InputsRoot);
                    bytes += source.Length;
                    if (bytes > 100L * 1024 * 1024 || source.Length > 10L * 1024 * 1024)
                        throw new ToolException(ErrorCodes.LimitExceeded, "输入快照超限（单文件 10 MiB，合计 100 MiB）。 ");
                    var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[32768]; int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                        output.Flush(true);
                    }
                    string fileHash = TaskOutputs.Sha256File(target);
                    using var identityHash = System.Security.Cryptography.SHA256.Create();
                    string identity = BitConverter.ToString(identityHash.ComputeHash(Encoding.UTF8.GetBytes(relative + "\0" + fileHash))).Replace("-", "").ToLowerInvariant();
                    files.Add(new JObject { ["file_id"] = "file_" + identity, ["path"] = relative,
                        ["bytes"] = source.Length, ["sha256"] = fileHash });
                }
            }
            DurableFiles.WriteText(Path.Combine(destination, "manifest.json"), new JObject { ["utc"] = DateTime.UtcNow, ["files"] = files }.ToString());
            return destination;
        }
    }
}
