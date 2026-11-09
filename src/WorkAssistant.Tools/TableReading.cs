using System;
using System.Collections.Generic;
using System.IO;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 按扩展名统一读取表格（正式版 §8.2：CSV 与 XLSX 同一管线）。
    /// 白名单之外的扩展直接拒绝，不猜格式。
    /// </summary>
    public static class TableReading
    {
        public static bool IsSupported(string relativePath)
        {
            return relativePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                   relativePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        }

        public static CsvTable Read(string fullPath, string relativePathForError)
        {
            if (relativePathForError.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return XlsxReading.ReadFirstSheet(fullPath, relativePathForError);
            }
            if (relativePathForError.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return CsvReading.Read(fullPath, relativePathForError);
            }
            throw new ToolException(ErrorCodes.UnsupportedFormat,
                "不支持的表格格式（仅支持 .csv / .xlsx）：" + relativePathForError);
        }
    }

    /// <summary>任务输出目录的原子提交助手：临时写入 → 校验 → 提交 → 登记成果。</summary>
    public static class TaskOutputs
    {
        /// <summary>
        /// 把 (文件名, 内容) 列表写入任务输出目录；全部写好并校验后才提交；
        /// 失败清理临时文件；不覆盖已有不同内容（幂等一致则复用）。
        /// </summary>
        public static IList<ArtifactInfo> CommitFiles(
            WorkspacePolicy policy, string taskId,
            IList<(string FileName, byte[] Content, string ArtifactType)> files,
            Action<string, string>? validateContent = null)
        {
            var planned = new List<(string Full, string Temp, byte[] Content, string Type)>();
            foreach (var (fileName, content, type) in files)
            {
                var full = policy.ResolveTaskOutputPath(taskId, fileName);
                planned.Add((full, full + ".tmp", content, type));
            }
            if (planned.Count == 0) throw new ToolException(ErrorCodes.InvalidArguments, "输出列表不能为空。 ");
            var directory = System.IO.Path.GetDirectoryName(planned[0].Full)!;
            System.IO.Directory.CreateDirectory(directory);
            var manifestPath = policy.ResolveTaskOutputPath(taskId, ".commit-" + Guid.NewGuid().ToString("N") + ".json");
            var entries = new Newtonsoft.Json.Linq.JArray();
            foreach (var item in planned)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                entries.Add(new Newtonsoft.Json.Linq.JObject { ["path"] = policy.ToWorkspaceRelative(item.Full),
                    ["type"] = item.Type, ["bytes"] = item.Content.Length,
                    ["sha256"] = BitConverter.ToString(sha.ComputeHash(item.Content)).Replace("-", "").ToLowerInvariant() });
                if (File.Exists(item.Full) && !ByteArrayEquals(File.ReadAllBytes(item.Full), item.Content))
                    throw new ToolException(ErrorCodes.OutputWriteFailed, "已有文件内容冲突，请使用新版本文件名：" + Path.GetFileName(item.Full));
            }
            string snapshotHash = policy.SnapshotRoot == null ? "" : Sha256File(Path.Combine(policy.SnapshotRoot, "manifest.json"));
            var manifest = new Newtonsoft.Json.Linq.JObject { ["task_id"] = taskId, ["state"] = "Prepared", ["files"] = entries,
                ["source_snapshot_sha256"] = snapshotHash, ["tool"] = DocumentLimits.ToolName, ["tool_version"] = "1" };
            DurableFiles.WriteText(manifestPath, manifest.ToString());
            DocumentLimits.Report("Prepared", manifestPath);
            try
            {
                foreach (var item in planned)
                {
                    DocumentLimits.Check();
                    if (File.Exists(item.Full)) { validateContent?.Invoke(item.Full, item.Type); continue; }
                    using (var output = new FileStream(item.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(item.Content, 0, item.Content.Length); output.Flush(true); }
                    if (!ByteArrayEquals(File.ReadAllBytes(item.Temp), item.Content)) throw new IOException("临时文件内容校验失败。 ");
                    if (item.Full.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || item.Full.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || item.Full.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                    {
                        PackageZip.AssertZipOpenable(item.Temp, Path.GetFileName(item.Full));
                        var part = item.Full.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xl/workbook.xml" : item.Full.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) ? "ppt/presentation.xml" : "word/document.xml";
                        var xml = PackageZip.ReadEntry(item.Temp, part) ?? throw new IOException("文档缺少主部件。 ");
                        using var input = new MemoryStream(xml); using var reader = DocumentLimits.Xml(input);
                        while (reader.Read()) DocumentLimits.Check();
                    }
                    validateContent?.Invoke(item.Temp, item.Type);
                }
                foreach (var item in planned) if (!File.Exists(item.Full)) Commit(item.Temp, item.Full);
                DocumentLimits.Report("Committed", manifestPath);
                foreach (var item in planned)
                    if (!ByteArrayEquals(File.ReadAllBytes(item.Full), item.Content)) throw new IOException("提交后内容不一致。 ");
                manifest["state"] = "Verified";
                DurableFiles.WriteText(manifestPath, manifest.ToString());
            }
            catch
            {
                foreach (var item in planned) TryDelete(item.Temp);
                // Durable Prepared manifest is retained for startup reconciliation.
                throw;
            }

            var artifacts = TryReadArtifacts(policy, planned);
            if (artifacts == null)
            {
                throw new ToolException(ErrorCodes.OutputWriteFailed, "成果读取校验失败。");
            }
            for (int i = 0; i < planned.Count; i++) DocumentWorkspaceTool.RecordOutput(policy, taskId, Path.GetFileName(planned[i].Full), artifacts[i].Sha256);
            return artifacts;
        }

        /// <summary>UTF-8 BOM + 内容（与 CSV 成果一致的编码约定）。</summary>
        public static byte[] Utf8WithBom(string text)
        {
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(text);
            var result = new byte[preamble.Length + body.Length];
            System.Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            System.Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
            return result;
        }

        public static string Sha256File(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = System.IO.File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void Commit(string tempPath, string finalPath)
        {
            if (System.IO.File.Exists(finalPath))
            {
                if (ByteArrayEquals(System.IO.File.ReadAllBytes(finalPath),
                        System.IO.File.ReadAllBytes(tempPath)))
                {
                    TryDelete(tempPath);
                    return;
                }
                throw new ToolException(ErrorCodes.OutputWriteFailed,
                    "目标文件已存在且内容不同，拒绝覆盖：" + System.IO.Path.GetFileName(finalPath));
            }
            System.IO.File.Move(tempPath, finalPath);
        }

        private static IList<ArtifactInfo>? TryReadArtifacts(
            WorkspacePolicy policy,
            List<(string Full, string Temp, byte[] Content, string Type)> planned)
        {
            try
            {
                var result = new List<ArtifactInfo>();
                foreach (var p in planned)
                {
                    var info = new System.IO.FileInfo(p.Full);
                    result.Add(new ArtifactInfo(
                        policy.ToWorkspaceRelative(p.Full), p.Type, info.Length, Sha256File(p.Full),
                        policy.SnapshotRoot == null ? "" : Sha256File(Path.Combine(policy.SnapshotRoot, "manifest.json")), DocumentLimits.ToolName));
                }
                return result;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (IOException)
            {
                // 清理失败不掩盖原始异常。
            }
        }
    }
}
