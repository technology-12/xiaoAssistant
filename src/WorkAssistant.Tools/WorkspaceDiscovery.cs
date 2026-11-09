using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class WorkspaceDiscovery
    {
        private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "inputs", "output", "outputs", ".claude", ".git", ".svn", ".vs", ".codex", ".aws", ".ssh", "node_modules", "bin", "obj", "用户数据", "程序文件", "prerequisites", "credentials" };
        public static void AddToSnapshot(string workspace, string destination, string request, CancellationToken token)
        {
            var policy = new WorkspacePolicy(workspace);
            var manifestPath = Path.Combine(destination, "manifest.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath)); var files = (JArray)manifest["files"]!;
            var candidates = new List<(string Path, string Relative, int Score)>();
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((policy.RootPath, 0));
            int entries = 0, directories = 0, skipped = 0; bool truncated = false;
            while (pending.Count > 0 && entries < 10000 && directories++ < 1000)
            {
                token.ThrowIfCancellationRequested(); var current = pending.Pop();
                try
                {
                    foreach (string directory in Directory.GetDirectories(current.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        token.ThrowIfCancellationRequested();
                        if (current.Depth >= 12 || Excluded.Contains(Path.GetFileName(directory)) || (File.GetAttributes(directory) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (Path.GetFullPath(destination).StartsWith(Path.GetFullPath(directory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        pending.Push((directory, current.Depth + 1));
                    }
                    foreach (string path in Directory.EnumerateFiles(current.Path))
                    {
                        if (++entries > 10000) { truncated = true; break; }
                        token.ThrowIfCancellationRequested();
                        if (!ListFilesTool.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ||
                            new[] { "app.defaults.json", "settings.json", "kernel.policy.json", "manifest.json", "opencode.connection.json" }.Contains(Path.GetFileName(path).ToLowerInvariant()) ||
                            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        string relative = policy.ToWorkspaceRelative(path), name = Path.GetFileName(path), stem = Path.GetFileNameWithoutExtension(path);
                        int score = request.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ? 1000 : stem.Length >= 2 && request.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0 ? 500 : 0;
                        foreach (string part in relative.Split('/').Where(p => p.Length >= 2))
                            if (request.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
                        candidates.Add((path, relative, score));
                    }
                }
                catch (UnauthorizedAccessException) { skipped++; }
                catch (IOException) { skipped++; }
                catch (ToolException) { skipped++; }
            }
            long total = files.Sum(f => (long)f["bytes"]!); int added = 0;
            foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Relative, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (added >= 100) { truncated = true; break; }
                try
                {
                    using var input = VerifiedFiles.OpenRead(candidate.Path, policy.RootPath);
                    if (input.Length > 10L * 1024 * 1024 || total + input.Length > 100L * 1024 * 1024) { skipped++; continue; }
                    string relative = "inputs/workspace/" + candidate.Relative;
                    string target = new WorkspacePolicy(destination).ResolveOriginalReadablePath(relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Existing user attachments win; discovery never replaces them.
                    if (File.Exists(target)) { skipped++; continue; }
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    string hash = TaskOutputs.Sha256File(target);
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    string id = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(relative + "\0" + hash))).Replace("-", "").ToLowerInvariant();
                    files.Add(new JObject { ["file_id"] = "file_" + id, ["path"] = relative, ["source_path"] = candidate.Relative, ["bytes"] = input.Length, ["sha256"] = hash });
                    total += input.Length; added++;
                }
                catch (UnauthorizedAccessException) { skipped++; }
                catch (IOException) { skipped++; }
                catch (ToolException) { skipped++; }
            }
            manifest["discovery"] = new JObject { ["matched_candidates"] = candidates.Count, ["added"] = added, ["skipped"] = skipped,
                ["partial"] = truncated || pending.Count > 0 || skipped > 0,
                ["notice"] = "已遍历授权工作区的常见文档，排除程序、配置及输出目录；按文件名/路径优先匹配。最多新增 100 个文件、单文件 10 MiB、快照合计 100 MiB。原件未修改；若缺少目标文件，请询问用户具体文件名或子目录。" };
            DurableFiles.WriteText(manifestPath, manifest.ToString());
        }
    }
}
