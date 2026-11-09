using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    // TxF gives the legacy Windows target a transactional content update, including isolation
    // from non-cooperating writers. Unsupported volumes fail closed; there is no truncate fallback.
    public static class AtomicTextTransaction
    {
        public static string CommitDescription => "经本次用户批准，在 NTFS 事务内重新核对原文版本并提交本任务文本提案。批准绑定提案指纹和基线；不支持的卷明确拒绝，原文不会普通覆盖。";
        public static string CommitNotice => "已通过事务提交并校验；输入快照仍为原版本，继续编辑请新建任务。";
        [StructLayout(LayoutKind.Sequential)] private struct Info
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateTransaction(IntPtr security, IntPtr id, uint options, uint isolation, uint flags, uint timeout, string description);
        [DllImport("KtmW32.dll", SetLastError = true)] private static extern bool CommitTransaction(SafeFileHandle transaction);
        [DllImport("KtmW32.dll", SetLastError = true)] private static extern bool RollbackTransaction(SafeFileHandle transaction);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileTransacted(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template, SafeFileHandle transaction, IntPtr version, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

        public static void Commit(string source, string root, string expectedSha256, byte[] replacement,
            Action prepared, Action staged, Action committed, CancellationToken token)
        {
            if (replacement.Length > TextEditEngine.MaxBytes) throw new ToolException(ErrorCodes.LimitExceeded, "文本提交超过大小限制。");
            var locks = new List<SafeFileHandle>();
            try
            {
                root = Path.GetFullPath(root).TrimEnd('\\'); source = Path.GetFullPath(source);
                if (!source.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.PathDenied, "文本提交路径越界。");
                string directory = Path.GetDirectoryName(source)!, current = root;
                foreach (string part in new[] { "" }.Concat(directory.Substring(root.Length).TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)))
                {
                    if (part.Length > 0) current = Path.Combine(current, part);
                    var handle = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); locks.Add(handle);
                    if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0)
                        throw new ToolException(ErrorCodes.PathDenied, "文本提交目录无法锁定或存在链接。");
                }
                token.ThrowIfCancellationRequested();
                using var transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 45000, "Xiao approved text edit");
                if (transaction.IsInvalid) throw Unavailable();
                bool attemptedCommit = false;
                try
                {
                    using (var handle = CreateFileTransacted(source, 0xc0000000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (new[] { 2, 3, 32, 33, 6800 }.Contains(error)) throw new ToolException(ErrorCodes.EditConflict, "原文件已变化、被占用或存在事务冲突，未提交。");
                            if (error == 5) throw new ToolException(ErrorCodes.PathDenied, "无权提交此文本文件。");
                            throw Unavailable(error);
                        }
                        var path = new StringBuilder(32768);
                        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
                        if (length == 0 || length >= path.Capacity || !GetFileInformationByHandle(handle, out var info) || info.Links != 1 || (info.Attributes & 0x400) != 0 ||
                            !string.Equals(path.ToString(), "\\\\?\\" + source, StringComparison.OrdinalIgnoreCase))
                            throw new ToolException(ErrorCodes.PathDenied, "文本提交拒绝硬链接、符号链接或越界文件。");
                        using var stream = new FileStream(handle, FileAccess.ReadWrite);
                        var before = DocumentLimits.ReadBounded(stream, TextEditEngine.MaxBytes);
                        if (TextEditEngine.Hash(before) != expectedSha256) throw new ToolException(ErrorCodes.EditConflict, "原文件版本已变化，批准已失效，未提交。");
                        prepared(); token.ThrowIfCancellationRequested();
                        stream.Position = 0; stream.Write(replacement, 0, replacement.Length); stream.SetLength(replacement.Length); stream.Flush(true);
                        stream.Position = 0;
                        if (TextEditEngine.Hash(DocumentLimits.ReadBounded(stream, TextEditEngine.MaxBytes)) != TextEditEngine.Hash(replacement)) throw new IOException("事务内候选校验失败。");
                        staged(); token.ThrowIfCancellationRequested();
                    }
                    attemptedCommit = true;
                    if (!CommitTransaction(transaction)) throw new ToolException(ErrorCodes.NeedsReview, "事务提交未确认，需核对本地意图与原文件；不自动重放。");
                    committed();
                    using var output = VerifiedFiles.OpenRead(source, root);
                    if (TextEditEngine.Hash(DocumentLimits.ReadBounded(output, TextEditEngine.MaxBytes)) != TextEditEngine.Hash(replacement))
                        throw new ToolException(ErrorCodes.NeedsReview, "提交后的文件已再次变化或校验失败；不自动覆盖或回滚。");
                }
                catch
                {
                    if (!attemptedCommit) RollbackTransaction(transaction);
                    throw;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            { throw new ToolException(ErrorCodes.AtomicWriteUnavailable, "当前系统不支持事务文本提交，未使用非原子写入降级。"); }
            finally { foreach (var handle in locks) handle.Dispose(); }
        }
        private static ToolException Unavailable(int error = 0) => new(ErrorCodes.AtomicWriteUnavailable,
            "当前卷无法使用 NTFS 事务提交，未修改原文件；不会降级成普通覆盖。系统代码：" + (error == 0 ? Marshal.GetLastWin32Error() : error));
    }
}
