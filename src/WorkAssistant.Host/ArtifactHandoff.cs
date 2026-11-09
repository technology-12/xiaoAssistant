using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;

namespace WorkAssistant.Host
{
    // Only committed artifacts from an explicitly selected, successful sibling are transferable.
    internal static class ArtifactHandoff
    {
        public static void SaveManifest(string root, Dictionary<string, string> files, Dictionary<string, string> hashes)
        {
            var entries = new JArray(files.Select(f => new JObject { ["file_id"] = f.Key, ["path"] = f.Value,
                ["sha256"] = hashes[f.Key], ["bytes"] = new FileInfo(Path.Combine(root, f.Value)).Length }));
            DurableFiles.WriteText(Path.Combine(root, "manifest.json"), new JObject { ["utc"] = DateTime.UtcNow, ["files"] = entries }.ToString());
        }
        public static void CopyAssigned(string sourceRoot, string destinationRoot, Dictionary<string, string> files,
            Dictionary<string, string> hashes, CancellationToken token)
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, "inputs"));
            long bytes = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var path = new WorkspacePolicy(sourceRoot).ResolveOriginalReadablePath(file.Value);
                using var input = VerifiedFiles.OpenRead(path, sourceRoot);
                bytes += input.Length;
                if (input.Length > 10L * 1024 * 1024 || bytes > 100L * 1024 * 1024)
                    throw new ToolException(ErrorCodes.LimitExceeded, "子任务输入快照超限。");
                using var sha = SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(hash, hashes[file.Key], StringComparison.OrdinalIgnoreCase))
                    throw new ToolException(ErrorCodes.PathDenied, "分配文件的快照摘要已变化。");
                input.Position = 0;
                var target = new WorkspacePolicy(destinationRoot).ResolveOriginalReadablePath(file.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[32768]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            SaveManifest(destinationRoot, files, hashes);
        }
        public static JArray Copy(TaskRecord source, TaskRecord destination, Dictionary<string, string> files,
            Dictionary<string, string> hashes, CancellationToken token)
        {
            if (source.State != TaskState.Completed || source.SessionId != destination.SessionId ||
                source.ParentTaskId != (destination.ParentTaskId.Length == 0 ? destination.TaskId : destination.ParentTaskId) ||
                !string.Equals(Path.GetFullPath(source.WorkspaceRoot), Path.GetFullPath(destination.WorkspaceRoot), StringComparison.OrdinalIgnoreCase))
                throw new ToolException(ErrorCodes.PathDenied, "只能交接本轮已完整完成的子任务成果。");
            if (source.Artifacts.Count > 20) throw new ToolException(ErrorCodes.LimitExceeded, "单次成果交接最多 20 个文件。");
            long existingBytes = files.Values.Sum(path => new FileInfo(new WorkspacePolicy(destination.SnapshotRoot!).ResolveOriginalReadablePath(path)).Length);
            var policy = new WorkspacePolicy(source.WorkspaceRoot);
            var records = new JArray(); var staged = new List<(string id, string relative, string hash, string full)>();
            long bytes = 0;
            try
            {
                foreach (var artifact in source.Artifacts)
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(artifact.Path);
                    var full = policy.ResolveTaskOutputPath(source.TaskId, name);
                    var expectedPath = policy.ToWorkspaceRelative(full);
                    if (!string.Equals(artifact.Path.Replace('\\', '/'), expectedPath, StringComparison.Ordinal) || artifact.Sha256.Length != 64)
                        throw new ToolException(ErrorCodes.PathDenied, "成果来源路径无效，交接已拒绝。");
                    using var input = VerifiedFiles.OpenRead(full, Path.GetDirectoryName(full)!);
                    bytes += input.Length;
                    if (input.Length != artifact.Bytes || input.Length > 10L * 1024 * 1024 || bytes > 50L * 1024 * 1024)
                        throw new ToolException(ErrorCodes.LimitExceeded, "成果大小已变化或超过交接上限。");
                    using var sourceSha = SHA256.Create();
                    var sourceHash = BitConverter.ToString(sourceSha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(sourceHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new ToolException(ErrorCodes.PathDenied, "源成果摘要已变化，未复用旧副本。");
                    input.Position = 0;
                    using var identity = SHA256.Create();
                    var identityHash = BitConverter.ToString(identity.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source.TaskId + "\n" + artifact.Path + "\n" + sourceHash))).Replace("-", "").ToLowerInvariant();
                    var id = "file_" + identityHash.Substring(0, 32);
                    var relative = "inputs/handoff/" + id + Path.GetExtension(name);
                    var target = new WorkspacePolicy(destination.SnapshotRoot!).ResolveOriginalReadablePath(relative);
                    if (files.TryGetValue(id, out var existing))
                    {
                        if (existing != relative || !hashes.TryGetValue(id, out var hash) || hash != sourceHash)
                            throw new ToolException(ErrorCodes.PathDenied, "成果引用冲突，拒绝复用。");
                        using var cached = VerifiedFiles.OpenRead(target, destination.SnapshotRoot!);
                        using var cachedSha = SHA256.Create();
                        var cachedHash = BitConverter.ToString(cachedSha.ComputeHash(cached)).Replace("-", "").ToLowerInvariant();
                        if (cached.Length != artifact.Bytes || cachedHash != sourceHash)
                            throw new ToolException(ErrorCodes.PathDenied, "已有交接副本被修改，拒绝复用。");
                        records.Add(Record(id, name, source.TaskId, artifact.Path, sourceHash, relative));
                        continue;
                    }
                    if (files.Count + staged.Count + 1 > 1000 || existingBytes + input.Length > 100L * 1024 * 1024)
                        throw new ToolException(ErrorCodes.LimitExceeded, "交接后的输入引用或累计大小超限。");
                    existingBytes += input.Length;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Content-derived identity plus CreateNew prevents overwriting other snapshot inputs.
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        staged.Add((id, relative, sourceHash, target));
                        var buffer = new byte[32768]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                        output.Flush(true);
                    }
                    using var verify = VerifiedFiles.OpenRead(target, destination.SnapshotRoot!);
                    using var sha = SHA256.Create();
                    var actual = BitConverter.ToString(sha.ComputeHash(verify)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new ToolException(ErrorCodes.PathDenied, "成果在生成后被修改，未交接给模型。");
                    records.Add(Record(id, name, source.TaskId, artifact.Path, actual, relative));
                }
                if (staged.Count == 0) return records;
                // Persist provenance before exposing IDs. A failed copy exposes none of this source's files.
                var receipt = Path.Combine(destination.SnapshotRoot!, "handoff-" + Guid.NewGuid().ToString("N") + ".json");
                DurableFiles.WriteText(receipt, records.ToString());
                var nextFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
                var nextHashes = new Dictionary<string, string>(hashes, StringComparer.Ordinal);
                foreach (var item in staged) { nextFiles.Add(item.id, item.relative); nextHashes.Add(item.id, item.hash); }
                SaveManifest(destination.SnapshotRoot!, nextFiles, nextHashes);
                foreach (var item in staged) { files.Add(item.id, item.relative); hashes.Add(item.id, item.hash); }
                return records;
            }
            catch
            {
                foreach (var item in staged) { files.Remove(item.id); hashes.Remove(item.id); try { File.Delete(item.full); } catch (IOException) { } }
                throw;
            }
        }
        private static JObject Record(string id, string name, string taskId, string sourcePath, string hash, string path) =>
            new JObject { ["file_id"] = id, ["name"] = name, ["source_task_id"] = taskId,
                ["source_path"] = sourcePath, ["sha256"] = hash, ["path"] = path };
    }
}
