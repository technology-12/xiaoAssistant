// XP 变体（替换 src/WorkAssistant.Infrastructure/TaskHostClient.cs）：见类注释。
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

namespace WorkAssistant.Infrastructure
{
    // XP 变体：GetNamedPipeClientProcessId 为 Vista+。身份由当前 SID ACL 管道 +
    // 一次性 64 位十六进制 nonce 握手 + 宿主生命周期绑定（Job + 退出清理）构成。
    public sealed class TaskHostClient
    {
        public async Task<TaskRecord> RunAsync(HostRequest request, IApprovalProvider approvals,
            Action<TaskEvent> onEvent, CancellationToken token, IUserInputProvider? questions = null,
            Action<LocalToolResponse>? onLocalToolResponse = null)
        {
            var id = Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = new NamedPipeServerStream("WorkAssistant-" + id, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, acl);
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkAssistant.Host.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少独立任务宿主，请重新安装完整程序。", path);
            var start = new ProcessStartInfo(path, id) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
            start.EnvironmentVariables["WORKASSISTANT_HOST_NONCE"] = nonce;
            using var process = Process.Start(start) ?? throw new IOException("无法启动任务宿主。");
            using var job = new ProcessJob(process, (request.EnableSubAgents ? 1024UL : 768UL) * 1024 * 1024, request.EnableSubAgents ? 3U : 2U);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(AgentController.TotalTimeout + TimeSpan.FromMinutes(1));
            using var stop = lifetime.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                using var channel = new HostChannel(pipe);
                var hello = await channel.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if ((string?)hello["type"] != "hello" || (string?)hello["payload"]?["nonce"] != nonce)
                    throw new IOException("任务宿主握手失败。");
                await channel.SendAsync("run", request, lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    var frame = await channel.ReadAsync(lifetime.Token).ConfigureAwait(false);
                    switch ((string?)frame["type"])
                    {
                        case "tool-response":
                            if (!request.LocalToolOperation || onLocalToolResponse == null) throw new InvalidDataException("未请求本地工具响应。");
                            onLocalToolResponse(frame["payload"]!.ToObject<LocalToolResponse>()!); break;
                        case "event": onEvent(frame["payload"]!.ToObject<TaskEvent>()!); break;
                        case "question":
                            string? answer = questions == null ? null : await questions.RequestAsync(frame["payload"]!.ToObject<UserQuestion>()!, lifetime.Token).ConfigureAwait(false);
                            await channel.SendAsync("answer", new { answer }, lifetime.Token).ConfigureAwait(false); break;
                        case "approval":
                            var approvalRequest = frame["payload"]!.ToObject<ApprovalRequest>()!;
                            var approved = await approvals.RequestAsync(approvalRequest, lifetime.Token).ConfigureAwait(false);
                            await channel.SendAsync("decision", new { approved, requestId = approvalRequest.RequestId }, lifetime.Token).ConfigureAwait(false); break;
                        case "finished": return frame["payload"]!.ToObject<TaskRecord>()!;
                        case "finished-ref":
                            if ((string?)frame["payload"]?["taskId"] != request.Task.TaskId) throw new InvalidDataException("任务完成引用不匹配。");
                            return new LocalTaskStore(request.StoreRoot).LoadTask(request.Task.TaskId) ?? throw new InvalidDataException("任务完成记录未持久化。");
                        case "error": throw new HostExecutionException(frame["payload"]!.ToObject<HostFailureInfo>()!);
                        default: throw new InvalidDataException("未知的任务宿主消息。");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException)
            {
                // Stop the whole job before reconciling records; sibling Workers must not keep committing.
                job.Dispose();
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); } catch (InvalidOperationException) { }
                if (request.PreviewPath != null) throw new IOException("独立预览进程已停止，请检查成果或使用本地应用打开。", ex);
                var store = new LocalTaskStore(request.StoreRoot);
                if (ex is HostExecutionException hostFailure)
                {
                    try { store.SaveDocument("host-failure", request.Task.TaskId, Newtonsoft.Json.JsonConvert.SerializeObject(hostFailure.Failure)); }
                    catch (Exception) { /* Preserve the original failure even when diagnostic storage is unavailable. */ }
                }
                var latest = store.LoadTask(request.Task.TaskId) ?? request.Task;
                // A completed durable record wins over a pipe failure after commit.
                if (latest.FinishedUtc.HasValue && !TaskInterruption.IsActive(latest)) return latest;
                if (store.LoadTask(latest.TaskId) == null) store.SaveTask(latest);
                latest = store.InterruptTaskTree(latest.TaskId, token.IsCancellationRequested,
                    ex is HostExecutionException failure ? failure.Failure.Code : ErrorCodes.WorkerUnavailable);
                if (latest.State != TaskState.NeedsReview && !token.IsCancellationRequested && ex is HostExecutionException details)
                { latest.ErrorMessage += " " + details.Failure.Message + " 阶段：" + details.Failure.Stage; store.SaveTask(latest); }
                return latest;
            }
            finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
