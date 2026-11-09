using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class OutputRecovery
    {
        // Inspect only: never replay writes or move files after a crash.
        public static bool Reconcile(TaskRecord task)
        {
            if (string.IsNullOrEmpty(task.WorkspaceRoot)) return false;
            var policy = new WorkspacePolicy(task.WorkspaceRoot);
            var directory = policy.GetTaskOutputDirectory(task.TaskId);
            if (!Directory.Exists(directory)) return false;
            bool changed = false;
            foreach (var path in Directory.GetFiles(directory, ".commit-*.json"))
            {
                try
                {
                    policy.ResolveTaskOutputPath(task.TaskId, Path.GetFileName(path));
                    if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("提交清单超限。");
                    var manifest = JObject.Parse(File.ReadAllText(path));
                    if ((string?)manifest["task_id"] != task.TaskId) throw new IOException("提交清单任务不匹配。");
                    bool complete = true;
                    foreach (var entry in (JArray)manifest["files"]!)
                    {
                        string relative = (string)entry["path"]!;
                        string full = policy.ResolveTaskOutputPath(task.TaskId, Path.GetFileName(relative));
                        if (!string.Equals(policy.ToWorkspaceRelative(full), relative, StringComparison.OrdinalIgnoreCase)) throw new IOException("提交清单路径不匹配。");
                        if (!File.Exists(full)) { complete = false; continue; }
                        using (var input = VerifiedFiles.OpenRead(full, directory))
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        {
                            string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                            if (input.Length != (long)entry["bytes"]! || actual != (string)entry["sha256"]!) throw new IOException("成果已变化或提交不完整。");
                        }
                        if (!task.Artifacts.Any(a => a.Path == relative))
                        {
                            task.Artifacts.Add(new ArtifactInfo(relative, (string)entry["type"]!, (long)entry["bytes"]!, (string)entry["sha256"]!, (string?)manifest["source_snapshot_sha256"] ?? "", (string?)manifest["tool"] ?? "", (string?)manifest["tool_version"] ?? "1"));
                            changed = true;
                        }
                    }
                    if (!complete) throw new IOException("部分成果尚未提交；已保留可验证文件，请人工复核。");
                    if ((string?)manifest["state"] != "Verified")
                    { manifest["state"] = "Verified"; DurableFiles.WriteText(path, manifest.ToString()); changed = true; }
                }
                catch (Exception ex) when (ex is IOException || ex is ToolException || ex is Newtonsoft.Json.JsonException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidCastException || ex is NullReferenceException || ex is FormatException || ex is OverflowException)
                {
                    task.State = TaskState.NeedsReview;
                    task.ErrorCode = ErrorCodes.NeedsReview;
                    task.SubState = TaskSubState.None;
                    task.ErrorMessage = "成果恢复检查：" + ex.Message;
                    changed = true;
                }
            }
            return changed;
        }
    }
}
