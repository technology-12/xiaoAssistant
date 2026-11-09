// XP 变体（替换 src/WorkAssistant.Tools/VerifiedFiles.cs）：
// Vista+ 的 GetFinalPathNameByHandle/SetFileInformationByHandle 不可用，改为：
// - 边界：受控根目录 + 逐级目录句柄锁定 + 逐级重解析点拒绝（XP 支持目录联接）。
// - 文件身份：GetFileInformationByHandle（卷序列号 + 文件索引 + 链接数）在 XP 可用。
// - 移动：记录源文件身份 → 关闭句柄 → MoveFileEx（同卷、不覆盖）→ 重开目标复核身份与哈希。
//   close→move 之间的窗口由父目录锁定与“不覆盖已存在目标”约束缓解；复核失败按 NEEDS_REVIEW
//   处理，不自动重试。该残余风险记录于基线 XP-05，等待真机验证。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newFileName, uint flags);

        private const uint MoveReplaceExisting = 0x1;

        private sealed class FileIdentity
        {
            public uint Volume; public uint IndexHigh; public uint IndexLow; public uint Links;
            public bool SameAs(HandleInfo info) => info.Volume == Volume && info.IndexHigh == IndexHigh && info.IndexLow == IndexLow;
        }

        /// <summary>逐级打开目录并锁定（拒绝删除/改名父目录），拒绝重解析点。</summary>
        private static List<SafeFileHandle> LockAncestors(string root, string fullPath, bool createMissing)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
            var target = Path.GetFullPath(fullPath);
            if (!target.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new ToolException(ErrorCodes.PathDenied, "目标目录越界。 ");
            var handles = new List<SafeFileHandle>();
            try
            {
                var current = normalizedRoot;
                foreach (var part in new[] { "" }.Concat(target.Substring(normalizedRoot.Length).TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)))
                {
                    if (part.Length > 0) current = Path.Combine(current, part);
                    if (part.Length > 0 && !Directory.Exists(current))
                    {
                        if (!createMissing) throw new ToolException(ErrorCodes.PathDenied, "目录不存在，只读校验拒绝，且不创建目录。 ");
                        Directory.CreateDirectory(current);
                    }
                    var directory = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (directory.IsInvalid || !GetFileInformationByHandle(directory, out var dirInfo) || (dirInfo.Attributes & 0x400) != 0)
                    { directory.Dispose(); throw new ToolException(ErrorCodes.PathDenied, "无法锁定目标目录或存在目录联接。 "); }
                    handles.Add(directory);
                }
                return handles;
            }
            catch
            {
                foreach (var handle in handles) handle.Dispose();
                throw;
            }
        }

        /// <summary>XP 文件身份校验：拒绝重解析点与硬链接，返回身份指纹。</summary>
        private static FileIdentity VerifyHandle(SafeFileHandle handle, bool singleLinkRequired)
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new ToolException(ErrorCodes.PathDenied, "无法校验文件句柄。 ");
            if ((info.Attributes & 0x400) != 0)
                throw new ToolException(ErrorCodes.PathDenied, "文件或目录为重解析点，已拒绝。 ");
            if (singleLinkRequired && info.Links != 1)
                throw new ToolException(ErrorCodes.PathDenied, "文件存在硬链接，已拒绝。 ");
            return new FileIdentity { Volume = info.Volume, IndexHigh = info.IndexHigh, IndexLow = info.IndexLow, Links = info.Links };
        }

        public static void MoveVerified(string source, string target, string root, string expectedHash)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
            var locks = LockAncestors(normalizedRoot, Path.GetDirectoryName(Path.GetFullPath(target))!, createMissing: true);
            try
            {
                source = Path.GetFullPath(source);
                if (!source.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException(ErrorCodes.PathDenied, "源文件路径越界。 ");
                if (File.Exists(Path.GetFullPath(target)))
                    throw new ToolException(ErrorCodes.PlanInvalid, "目标文件已存在，拒绝覆盖。 ");

                FileIdentity identity;
                string actualHash;
                using (var handle = CreateFile(source, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new IOException("无法锁定源文件：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    identity = VerifyHandle(handle, singleLinkRequired: true);
                    using var stream = new FileStream(handle, FileAccess.Read);
                    using var sha = SHA256.Create();
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ToolException(ErrorCodes.PlanInvalid, "源文件自生成计划后已变化，请重新确认。 ");

                // close → move 存在窗口：目标不存在 + 父目录锁定 + 移动后身份复核共同缓解。
                if (!MoveFileEx(source, Path.GetFullPath(target), 0))
                    throw new IOException("移动失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);

                using (var verify = new FileStream(Path.GetFullPath(target), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!GetFileInformationByHandle(verify.SafeFileHandle, out var after))
                        throw new ToolException(ErrorCodes.NeedsReview, "移动后无法复核目标文件身份；请人工核对。 ");
                    if (!identity.SameAs(after) || after.Links != 1)
                        throw new ToolException(ErrorCodes.NeedsReview, "移动后文件身份与源不一致；请人工核对。 ");
                    using var sha2 = SHA256.Create();
                    var finalHash = BitConverter.ToString(sha2.ComputeHash(verify)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(finalHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new ToolException(ErrorCodes.NeedsReview, "移动后内容校验失败；请人工核对。 ");
                }
            }
            finally { foreach (var directory in locks) directory.Dispose(); }
        }

        public static System.IO.Stream OpenRead(string path, string permittedRoot)
        {
            // R06 修复：逐级锁定并验证 permittedRoot→文件的目录链（每级句柄 + 重解析点拒绝），
            // 再打开文件并校验其句柄身份。目录链句柄随文件流持有到关闭：检查后任何一级目录
            // 无法被替换/删除/改名（他人需 DELETE 访问，被共享模式拒绝），junction 无法事后
            // 插入到已验证的路径上。
            var full = Path.GetFullPath(path);
            // R06 修正：目录链锁只覆盖到文件的父目录（LockAncestors 的 CreateDirectory 分支
            // 仅对目录层级合法；此前把文件全路径传入会把文件名当目录创建，恰在 plan 文件上炸出
            // “无法创建…已存在”）。
            var locks = LockAncestors(permittedRoot, Path.GetDirectoryName(full)!, createMissing: false);
            try
            {
                var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    VerifyHandle(stream.SafeFileHandle, singleLinkRequired: false);
                    if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info) || info.Links > 1)
                        throw new ToolException(ErrorCodes.PathDenied, "文件存在硬链接，已拒绝。 ");
                    return new ChainedReadStream(stream, locks);
                }
                catch { stream.Dispose(); throw; }
            }
            catch
            {
                foreach (var handle in locks) handle.Dispose();
                throw;
            }
        }

        /// <summary>组合只读流：关闭时一并释放目录链锁句柄（消除检查后链替换窗口）。</summary>
        private sealed class ChainedReadStream : System.IO.Stream
        {
            private FileStream _inner;
            private List<SafeFileHandle>? _chain;
            public ChainedReadStream(FileStream inner, List<SafeFileHandle> chain)
            {
                _inner = inner;
                _chain = chain;
            }
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override int ReadByte() => _inner.ReadByte();
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _inner.Dispose(); }
                    finally
                    {
                        var chain = _chain;
                        _chain = null;
                        if (chain != null) foreach (var handle in chain) handle.Dispose();
                    }
                }
                base.Dispose(disposing);
            }
        }


        public static string CaptureInputs(string workspace, string destination, CancellationToken token)
        {
            if (Directory.Exists(destination)) throw new IOException("快照目录已存在，拒绝覆盖。 ");
            Directory.CreateDirectory(Path.Combine(destination, "inputs"));
            var policy = new WorkspacePolicy(workspace);
            var files = new JArray(); long bytes = 0; var count = 0;
            var pending = new Stack<string>(); pending.Push(policy.InputsRoot);
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
                    using var identityHash = SHA256.Create();
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
