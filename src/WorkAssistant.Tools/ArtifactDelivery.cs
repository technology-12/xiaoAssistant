using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>将已校验的任务成果交付到 output 根目录，保留内部隔离副本，不覆盖用户文件。</summary>
    public static class ArtifactDelivery
    {
        public static void Publish(TaskRecord task)
        {
            var policy = new WorkspacePolicy(task.WorkspaceRoot);
            foreach (var artifact in task.Artifacts)
            {
                string source = new[] { task.TaskId }.Concat(task.ChildTaskIds)
                    .Select(owner => policy.ResolveTaskOutputPath(owner, Path.GetFileName(artifact.Path)))
                    .FirstOrDefault(path => string.Equals(policy.ToWorkspaceRelative(path), artifact.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (source == null)
                    throw new IOException("交付来源不属于当前任务。");
                using var input = VerifiedFiles.OpenRead(source, Path.GetDirectoryName(source)!);
                if (input.Length != artifact.Bytes || input.Length > 50L * 1024 * 1024) throw new IOException("成果大小已变化或超过交付上限。");
                if (!Matches(input, artifact.Sha256)) throw new IOException("成果内容已变化，未生成交付副本。");
                string name = Path.GetFileName(source), stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
                string temp = policy.ResolveTaskOutputPath(task.TaskId, ".deliver-" + Guid.NewGuid().ToString("N") + ".tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                try
                {
                    input.Position = 0;
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    bool delivered = false;
                    for (int version = 1; version <= 999; version++)
                    {
                        string target = policy.ResolveDeliveryPath(version == 1 ? name : stem + " (" + version + ")" + extension);
                        if (Directory.Exists(target)) continue;
                        if (File.Exists(target))
                        {
                            try
                            {
                                using var existing = VerifiedFiles.OpenRead(target, Path.GetDirectoryName(target)!);
                                if (existing.Length == artifact.Bytes && Matches(existing, artifact.Sha256))
                                { artifact.PublishedPath = policy.ToWorkspaceRelative(target); delivered = true; break; }
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            continue;
                        }
                        try { VerifiedFiles.MoveVerified(temp, target, policy.RootPath, artifact.Sha256); }
                        catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { continue; }
                        artifact.PublishedPath = policy.ToWorkspaceRelative(target); delivered = true; break;
                    }
                    if (!delivered) throw new IOException("同名版本过多，请整理 output 目录后重试。");
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        private static bool Matches(Stream stream, string expected)
        {
            using var sha = SHA256.Create();
            return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
