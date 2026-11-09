using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WorkAssistant.Core
{
    public sealed class SubAgentJob
    {
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Context { get; set; } = "";
        public string ModelAlias { get; set; } = "worker";
        public string RoleId { get; set; } = "general";
        public bool RequireCalculation { get; set; }
        public List<string> FileIds { get; set; } = new();
        public List<string> DependsOn { get; set; } = new();
        public TaskRecord[] Dependencies { get; set; } = XpArray.Empty<TaskRecord>();
        public TaskRecord Record { get; set; } = null!;
    }
    public sealed class SubAgentCoordinator : IDisposable
    {
        public const int MaxChildren = 4;
        public const int MaxConcurrency = 2;
        private readonly SemaphoreSlim _slots = new(MaxConcurrency, MaxConcurrency);
        private readonly object _sync = new();
        private int _reserved, _active, _peak;
        public int PeakConcurrency => _peak;
        public async Task<TaskRecord[]> RunBatchAsync(IList<SubAgentJob> jobs,
            Action<SubAgentJob> queued, Func<SubAgentJob, CancellationToken, Task<TaskRecord>> runner,
            Action<SubAgentJob, TaskRecord> finished, CancellationToken token)
        {
            if (jobs.Count < 1 || jobs.Count > MaxChildren || jobs.Select(j => j.Name).Distinct(StringComparer.Ordinal).Count() != jobs.Count)
                throw new ToolException(ErrorCodes.InvalidArguments, "每批需要 1~4 个不同名称的子任务。");
            token.ThrowIfCancellationRequested();
            ValidateDependencies(jobs);
            lock (_sync)
            {
                if (_reserved + jobs.Count > MaxChildren) throw new ToolException(ErrorCodes.LimitExceeded, "本轮最多派发 4 个子任务，请合并任务范围。");
                _reserved += jobs.Count;
            }
            foreach (var job in jobs) queued(job);
            var completions = jobs.ToDictionary(j => j.Name, j => XpTask.CompletionSource<TaskRecord>(), StringComparer.Ordinal);
            return await XpTask.WhenAll(jobs.Select(async job =>
            {
                bool entered = false; var record = job.Record;
                try
                {
                    job.Dependencies = await XpTask.WhenAll(job.DependsOn.Select(name => completions[name].Task)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (job.Dependencies.Any(d => d.State != TaskState.Completed))
                        throw new ToolException(ErrorCodes.DependencyFailed, "前置子任务未完整成功，已阻止依赖任务执行；请由主 Agent 复核后另行派发。");
                    await _slots.WaitAsync(token).ConfigureAwait(false); entered = true;
                    lock (_sync) { _active++; _peak = Math.Max(_peak, _active); }
                    record = await runner(job, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    record.State = TaskState.Cancelled; record.ErrorCode = ErrorCodes.Cancelled;
                    record.ErrorMessage = "主任务已取消，子任务未继续执行。";
                    if (TaskInterruption.HasUncertainWrites(record)) TaskInterruption.Mark(record, true);
                }
                catch (Exception ex)
                {
                    record.State = TaskState.Failed; record.ErrorCode = ex is ToolException tool ? tool.Code : ErrorCodes.SubAgentFailed;
                    record.ErrorMessage = "子任务执行失败，请查看本地步骤记录或检查所选模型配置。";
                    if (TaskInterruption.HasUncertainWrites(record)) TaskInterruption.Mark(record, false);
                }
                finally { if (entered) { lock (_sync) _active--; _slots.Release(); } }
                record.FinishedUtc = DateTime.UtcNow;
                try { finished(job, record); }
                catch (Exception ex) { completions[job.Name].TrySetException(ex); throw; }
                completions[job.Name].TrySetResult(record); return record;
            })).ConfigureAwait(false);
        }
        public static void ValidateDependencies(IList<SubAgentJob> jobs)
        {
            var names = new HashSet<string>(jobs.Select(j => j.Name), StringComparer.Ordinal);
            if (names.Count != jobs.Count || jobs.Any(j => j.DependsOn.Count > MaxChildren || j.DependsOn.Distinct(StringComparer.Ordinal).Count() != j.DependsOn.Count || j.DependsOn.Any(d => !names.Contains(d) || d == j.Name)))
                throw new ToolException(ErrorCodes.InvalidArguments, "子任务依赖必须引用同批其他任务，名称不能重复。");
            var resolved = new HashSet<string>(StringComparer.Ordinal);
            while (resolved.Count < jobs.Count)
            {
                int before = resolved.Count;
                foreach (var job in jobs.Where(j => j.DependsOn.All(resolved.Contains))) resolved.Add(job.Name);
                if (before == resolved.Count) throw new ToolException(ErrorCodes.InvalidArguments, "子任务依赖存在循环，整批未执行。");
            }
        }
        public void Dispose() => _slots.Dispose();
    }
    public sealed class ModelCallBudget
    {
        private int _used;
        public const int Maximum = 256;
        public int Used => Volatile.Read(ref _used);
        public void Consume()
        {
            while (true)
            {
                int used = Volatile.Read(ref _used);
                if (used >= Maximum) throw new ToolException(ErrorCodes.LimitExceeded, "主任务与子任务合计达到 256 次模型编排调用上限。");
                if (Interlocked.CompareExchange(ref _used, used + 1, used) == used) return;
            }
        }
    }
    public sealed class BudgetedModelClient : IStreamingModelClient, IModelRetrySource
    {
        public event Action<ModelRetryInfo>? Retrying
        {
            add { if (_inner is IModelRetrySource source) source.Retrying += value; }
            remove { if (_inner is IModelRetrySource source) source.Retrying -= value; }
        }
        private readonly IModelClient _inner;
        private readonly ModelCallBudget _budget;
        public BudgetedModelClient(IModelClient inner, ModelCallBudget budget) { _inner = inner; _budget = budget; }
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken token)
        { _budget.Consume(); return _inner.CompleteAsync(request, token); }
        public Task<ModelReply> CompleteStreamAsync(ModelRequest request, Action<string>? delta, CancellationToken token)
        {
            _budget.Consume();
            return _inner is IStreamingModelClient stream ? stream.CompleteStreamAsync(request, delta, token) : _inner.CompleteAsync(request, token);
        }
    }
}
