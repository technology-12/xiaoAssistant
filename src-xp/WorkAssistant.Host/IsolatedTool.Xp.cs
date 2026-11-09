// XP 变体（替换 src/WorkAssistant.Host/IsolatedTool.cs）。
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Host
{
    internal sealed class ToolExecutionRequest
    {
        public string TaskId { get; set; } = "";
        public AgentExecutionMode ExecutionMode { get; set; }
        public string Workspace { get; set; } = "";
        public string Snapshot { get; set; } = "";
        public string TaskDirectory { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Arguments { get; set; } = "";
        public ApprovedToolOperation? ApprovedOperation { get; set; }
    }
    internal sealed class IsolatedTool : IAgentTool  // XP 变体：去除 Vista+ PID 校验
    {
        public ToolDefinition Definition { get; }
        private readonly TaskRecord _task;
        private readonly string _directory;
        private readonly Action<TaskEvent>? _progress;
        public IsolatedTool(ToolDefinition definition, TaskRecord task, string directory, Action<TaskEvent>? progress = null) { Definition = definition; _task = task; _directory = directory; _progress = progress; }
        public async Task<ToolResult> ExecuteAsync(ToolContext context, string argumentsJson, CancellationToken token)
        {
            string id = Guid.NewGuid().ToString("N"), nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = new NamedPipeServerStream("WorkAssistant-tool-" + id, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, acl);
            var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkAssistant.Host.exe"), id + " --tool")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.EnvironmentVariables["WORKASSISTANT_HOST_NONCE"] = nonce;
            using var process = Process.Start(start) ?? throw new IOException("无法启动文档 Worker。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Definition.TimeoutSeconds));
            using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                // XP：无客户端 PID API；身份 = 随机管道名 + 当前 SID ACL + nonce 握手 + 进程生命周期绑定。
                using var channel = new HostChannel(pipe);
                var hello = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
                if ((string?)hello["type"] != "hello" || (string?)hello["payload"]?["nonce"] != nonce) throw new IOException("Worker 握手失败。");
                await channel.SendAsync("tool-run", new ToolExecutionRequest { TaskId = context.TaskId, Workspace = _task.WorkspaceRoot,
                    ExecutionMode = _task.ExecutionMode,
                    Snapshot = _task.SnapshotRoot ?? "", TaskDirectory = _directory, Tool = Definition.Name, Arguments = argumentsJson,
                    ApprovedOperation = context.ApprovedOperation }, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var frame = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
                    if ((string?)frame["type"] == "progress")
                    {
                        string message = (string?)frame["payload"]?["message"] ?? "";
                        if (message.Length > 300) throw new IOException("Worker 进度消息超限。");
                        _progress?.Invoke(new TaskEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow, _task.TaskId, TaskEventKind.ToolProgress, message, Definition.Name));
                        continue;
                    }
                    if ((string?)frame["type"] == "checkpoint")
                    {
                        var step = _task.Steps.Last(s => s.Tool == Definition.Name);
                        step.State = (string?)frame["payload"]?["state"] ?? "Prepared";
                        step.CommitManifestPath = (string?)frame["payload"]?["manifest"];
                        step.UpdatedUtc = DateTime.UtcNow;
                        new LocalTaskStore(Path.GetDirectoryName(_directory)).SaveTask(_task);
                        await channel.SendAsync("checkpoint-ack", new { saved = true }, timeout.Token).ConfigureAwait(false);
                        continue;
                    }
                    if ((string?)frame["type"] != "tool-result") throw new IOException("Worker 回执无效。");
                    return frame["payload"]!.ToObject<ToolResult>()!;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); } catch (InvalidOperationException) { }
                if (Definition.IsMutation || Definition.ProducesArtifacts) return ToolResult.Failure(ErrorCodes.NeedsReview, "文件 Worker 已停止，需根据持久化变更记录复核；不自动重试。");
                token.ThrowIfCancellationRequested();
                return ToolResult.Failure(ErrorCodes.LimitExceeded, "文档 Worker 已中止或超过 " + Definition.TimeoutSeconds + " 秒限制；没有把部分结果当作成功。");
            }
            finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
