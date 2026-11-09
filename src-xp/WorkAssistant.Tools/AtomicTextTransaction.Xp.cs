// XP 变体（替换 src/WorkAssistant.Tools/AtomicTextTransaction.cs）：
// Vista+ 的 KTM 事务（KtmW32）不可用。R05 修复后的提交协议（独占写穿）：
// 1) 锁定各级目录句柄（拒绝重解析点）；
// 2) 以 share=FILE_SHARE_READ（拒绝并发写者/删除）独占打开原文件，**在句柄上**读取全文并
//    复核哈希 —— 批准自始至终绑定该句柄对象的内容版本；
// 3) prepared：同目录写入唯一临时文件（CreateNew）+ Flush(true) + 复读校验（保留候选）；
// 4) staged：此后任何并发修改者都无法打开写入（sharing violation，其写入被拒绝而非丢失）；
// 5) 在独占句柄上原位写入 replacement（Position=0、SetLength、Flush(true)），复读校验一致后
//    committed；任何失败保留临时文件为候选并按 NEEDS_REVIEW 处理，不自动重试、不回滚覆盖。
// 与 B0“批准绑定具体版本、并发修改不丢更新”的对应：复核与写入之间不存在可写入窗口
//（其他写者得到明确的 sharing violation），不存在 ReplaceFile 先关句柄的竞争窗口。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class AtomicTextTransaction
    {
        public static string CommitDescription => "经本次用户批准，锁定原文件并核对批准版本后原位写入文本提案；写入前持久保存原件备份、候选和提交意图。XP 不提供事务级原子替换，中断须复核，不自动重放或回滚。";
        public static string CommitNotice => "已在独占句柄上写入并校验；原件备份与日志已保留。XP 不保证断电时的事务原子性；输入快照仍为原版本，继续编辑请新建任务。";
        [StructLayout(LayoutKind.Sequential)] private struct Info
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1; // 允许读者；拒绝写者与删除，保证复核后版本不被改动

        public static void Commit(string source, string root, string expectedSha256, byte[] replacement,
            Action prepared, Action staged, Action committed, CancellationToken token)
        {
            if (replacement.Length > TextEditEngine.MaxBytes) throw new ToolException(ErrorCodes.LimitExceeded, "文本提交超过大小限制。");
            var locks = new List<SafeFileHandle>();
            string? temporary = null;
            SafeFileHandle? exclusive = null;
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

                // 独占（拒绝并发写者）打开原文件：批准绑定的对象即此句柄。
                exclusive = CreateFile(source, GenericRead | GenericWrite, FileShareRead, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (exclusive.IsInvalid)
                {
                    int openError = Marshal.GetLastWin32Error();
                    if (openError == 32 || openError == 33) throw new ToolException(ErrorCodes.EditConflict, "原文件正被其他程序占用，未提交；请稍后重试或人工核对。");
                    if (openError == 2 || openError == 3) throw new ToolException(ErrorCodes.EditConflict, "原文件已不存在或已变化，未提交。");
                    if (openError == 5) throw new ToolException(ErrorCodes.PathDenied, "无权提交此文本文件。");
                    throw new ToolException(ErrorCodes.AtomicWriteUnavailable, "无法独占原文件，未提交。系统代码：" + openError);
                }
                if (!GetFileInformationByHandle(exclusive, out var fileInfo) || fileInfo.Links != 1 || (fileInfo.Attributes & 0x400) != 0)
                    throw new ToolException(ErrorCodes.PathDenied, "文本提交拒绝硬链接、符号链接或越界文件。");

                using (var stream = new FileStream(exclusive, FileAccess.ReadWrite))
                {
                    exclusive = null; // FileStream 接管句柄生命周期
                    if (TextEditEngine.Hash(DocumentLimits.ReadBounded(stream, TextEditEngine.MaxBytes)) != expectedSha256)
                        throw new ToolException(ErrorCodes.EditConflict, "原文件版本已变化，批准已失效，未提交。");
                    stream.Position = 0;
                    prepared(); token.ThrowIfCancellationRequested();

                    // 候选写入同目录临时文件并复读校验（保留候选；失败可人工/恢复流程取用）。
                    temporary = source + ".xiao-tmp-" + Guid.NewGuid().ToString("N");
                    using (var candidate = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        candidate.Write(replacement, 0, replacement.Length);
                        candidate.Flush(true);
                    }
                    using (var verifyTemp = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (TextEditEngine.Hash(DocumentLimits.ReadBounded(verifyTemp, TextEditEngine.MaxBytes)) != TextEditEngine.Hash(replacement))
                            throw new IOException("临时候选校验失败。");
                    }
                    staged(); token.ThrowIfCancellationRequested();

                    // 原位写穿已复核的独占句柄：复核与写入之间不存在可写入窗口。
                    // 写穿一旦开始，任何失败都保留临时候选并按 NEEDS_REVIEW 处理（不自动重试/回滚）。
                    try
                    {
                        stream.Position = 0;
                        stream.Write(replacement, 0, replacement.Length);
                        stream.SetLength(replacement.Length);
                        stream.Flush(true);
                        stream.Position = 0;
                        if (TextEditEngine.Hash(DocumentLimits.ReadBounded(stream, TextEditEngine.MaxBytes)) != TextEditEngine.Hash(replacement))
                            throw new ToolException(ErrorCodes.NeedsReview, "提交后的句柄复核失败；原文件可能不完整，请对照临时候选人工核对。");
                    }
                    catch (Exception failure) when (failure is not ToolException)
                    {
                        throw new ToolException(ErrorCodes.NeedsReview,
                            "原位写入中断（" + failure.Message + "）；原文件可能不完整，已保留临时候选供人工核对。", failure);
                    }
                }
                exclusive = null;
                committed();
                TryDelete(temporary); temporary = null;
            }
            catch (Exception ex) when (ex is ToolException { Code: ErrorCodes.NeedsReview })
            {
                // 保留临时候选供人工核对（不删除）。NeedsReview 仅出现在写穿/复核阶段，
                // 此时原文件可能已变动，候选是唯一可信副本。
                temporary = null;
                throw;
            }
            finally
            {
                if (temporary != null) TryDelete(temporary);
                if (exclusive != null) exclusive.Dispose();
                foreach (var handle in locks) handle.Dispose();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
