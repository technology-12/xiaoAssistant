// net40 异步/await 基础设施：等价于 Microsoft.Bcl.Async 所提供的编译器支撑类型。
// 设计要点（对应基线 XP-02 的不变量）：
// - TaskCompletionSource 在 Create() 中急切创建并被所有装箱拷贝共享；
//   否则状态机装箱后 SetResult 会完成另一个 TCS 实例，调用方任务永不完成。
// - 所有 await 延续绝不内联在完成线程上执行（等价 RunContinuationsAsynchronously
//   全局语义），审批与写锁释放路径不会同步重入。
// - 逻辑上下文（XpLogicalContext/AsyncLocal）随每个 await 边界捕获与恢复，
//   子 Agent 之间不串扰。
// - 异常传播：faulted 任务 GetResult 抛出内部异常；取消任务抛 OperationCanceledException。
// - async void 的未处理异常投递回捕获的同步上下文（与 4.5 行为一致）。

using System;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Compatibility.Xp;

namespace System.Runtime.CompilerServices
{
    public interface INotifyCompletion
    {
        void OnCompleted(Action continuation);
    }

    public interface ICriticalNotifyCompletion : INotifyCompletion
    {
        void UnsafeOnCompleted(Action continuation);
    }

    public interface IAsyncStateMachine
    {
        void MoveNext();
        void SetStateMachine(IAsyncStateMachine stateMachine);
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Delegate, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncMethodBuilderAttribute : Attribute
    {
        public Type? BuilderType { get; set; }
    }


    public struct TaskAwaiter : INotifyCompletion, ICriticalNotifyCompletion
    {
        private readonly Task _task;
        private readonly bool _captureContext;
        internal TaskAwaiter(Task task, bool captureContext) { _task = task; _captureContext = captureContext; }
        public bool IsCompleted => _task.IsCompleted;
        public void OnCompleted(Action continuation) => XpContinuation.AwaitOnCompleted(_task, continuation, _captureContext);
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        public void GetResult()
        {
            // await 协议只在 IsCompleted 时调用；直接调用的使用方（测试/宿主）需先等待完成。
            if (!_task.IsCompleted)
            {
                try { _task.Wait(); }
                catch (AggregateException) { }
            }
            XpContinuation.ValidateEnd(_task);
        }
    }

    public struct TaskAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
    {
        private readonly Task<TResult> _task;
        private readonly bool _captureContext;
        internal TaskAwaiter(Task<TResult> task, bool captureContext) { _task = task; _captureContext = captureContext; }
        public bool IsCompleted => _task.IsCompleted;
        public void OnCompleted(Action continuation) => XpContinuation.AwaitOnCompleted(_task, continuation, _captureContext);
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        public TResult GetResult()
        {
            // 同上：先等待完成，net40 的 Task.Result 会把异常包成 AggregateException。
            if (!_task.IsCompleted)
            {
                try { _task.Wait(); }
                catch (AggregateException) { }
            }
            XpContinuation.ValidateEnd(_task);
            return _task.Result;
        }
    }

    public struct ConfiguredTaskAwaitable
    {
        private readonly Task _task;
        private readonly bool _continueOnCapturedContext;
        internal ConfiguredTaskAwaitable(Task task, bool continueOnCapturedContext) { _task = task; _continueOnCapturedContext = continueOnCapturedContext; }
        public TaskAwaiter GetAwaiter() => new TaskAwaiter(_task, _continueOnCapturedContext);
    }

    public struct ConfiguredTaskAwaitable<TResult>
    {
        private readonly Task<TResult> _task;
        private readonly bool _continueOnCapturedContext;
        internal ConfiguredTaskAwaitable(Task<TResult> task, bool continueOnCapturedContext) { _task = task; _continueOnCapturedContext = continueOnCapturedContext; }
        public TaskAwaiter<TResult> GetAwaiter() => new TaskAwaiter<TResult>(_task, _continueOnCapturedContext);
    }
}

namespace System.Threading.Tasks
{
    public static class XpTaskAwaitableExtensions
    {
        public static System.Runtime.CompilerServices.TaskAwaiter GetAwaiter(this Task task) =>
            new System.Runtime.CompilerServices.TaskAwaiter(task, true);

        public static System.Runtime.CompilerServices.TaskAwaiter<TResult> GetAwaiter<TResult>(this Task<TResult> task) =>
            new System.Runtime.CompilerServices.TaskAwaiter<TResult>(task, true);

        public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable ConfigureAwait(this Task task, bool continueOnCapturedContext) =>
            new System.Runtime.CompilerServices.ConfiguredTaskAwaitable(task, continueOnCapturedContext);

        public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult> ConfigureAwait<TResult>(this Task<TResult> task, bool continueOnCapturedContext) =>
            new System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>(task, continueOnCapturedContext);
    }
}

namespace System.Runtime.CompilerServices
{
    public struct AsyncTaskMethodBuilder
    {
        // 急切创建、跨装箱拷贝共享（引用类型字段在结构体拷贝中仍指向同一实例）。
        private readonly TaskCompletionSource<object?> _completion;
        private IAsyncStateMachine? _stateMachine;

        private AsyncTaskMethodBuilder(TaskCompletionSource<object?> completion) { _completion = completion; _stateMachine = null; }

        public static AsyncTaskMethodBuilder Create() => new AsyncTaskMethodBuilder(new TaskCompletionSource<object?>());

        public Task Task => _completion.Task;

        public void SetStateMachine(IAsyncStateMachine stateMachine) { _stateMachine = stateMachine; }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // 逻辑上下文边界（原生语义）：方法内 AsyncLocal 修改只影响本方法及其子调用，
            // 调用方在 Start 返回后仍读到自己进入前的链。挂起后的恢复由 awaiter 捕获的链负责。
            var callerContext = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            try { stateMachine.MoveNext(); }
            finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(callerContext); }
        }

        private IAsyncStateMachine Box<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null) _stateMachine = stateMachine;
            return _stateMachine;
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void SetResult() => _completion.TrySetResult(null);

        public void SetException(Exception exception)
        {
            // 原生语义：async 方法内未处理的 OperationCanceledException 使任务进入 Canceled（非 Faulted）。
            if (exception is OperationCanceledException) _completion.TrySetCanceled();
            else _completion.TrySetException(exception);
        }
    }

    public struct AsyncTaskMethodBuilder<TResult>
    {
        private readonly TaskCompletionSource<TResult> _completion;
        private IAsyncStateMachine? _stateMachine;

        private AsyncTaskMethodBuilder(TaskCompletionSource<TResult> completion) { _completion = completion; _stateMachine = null; }

        public static AsyncTaskMethodBuilder<TResult> Create() => new AsyncTaskMethodBuilder<TResult>(new TaskCompletionSource<TResult>());

        public Task<TResult> Task => _completion.Task;

        public void SetStateMachine(IAsyncStateMachine stateMachine) { _stateMachine = stateMachine; }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            var callerContext = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            try { stateMachine.MoveNext(); }
            finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(callerContext); }
        }

        private IAsyncStateMachine Box<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null) _stateMachine = stateMachine;
            return _stateMachine;
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void SetResult(TResult result) => _completion.TrySetResult(result);

        public void SetException(Exception exception)
        {
            if (exception is OperationCanceledException) _completion.TrySetCanceled();
            else _completion.TrySetException(exception);
        }
    }

    public struct AsyncVoidMethodBuilder
    {
        private SynchronizationContext? _context;

        public static AsyncVoidMethodBuilder Create() => new AsyncVoidMethodBuilder();

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            _context = SynchronizationContext.Current;
            var callerContext = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            try { stateMachine.MoveNext(); }
            finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(callerContext); }
        }

        private IAsyncStateMachine Box<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            return stateMachine;
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.OnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
        {
            awaiter.UnsafeOnCompleted(Box(ref stateMachine).MoveNext);
        }

        public void SetResult() { }

        public void SetException(Exception exception)
        {
            // async void：未处理异常必须升级（进程/线程失败），不得静默吞掉。
            var context = _context ?? SynchronizationContext.Current;
            if (context != null) context.Post(state => throw (Exception)state!, exception);
            else ThreadPool.QueueUserWorkItem(state => throw (Exception)state!, exception);
        }
    }
}

namespace WorkAssistant.Compatibility.Xp
{
/// <summary>延续调度核心：捕获逻辑上下文与同步上下文，统一排队，不内联。</summary>
internal static class XpContinuation
{
    public static void AwaitOnCompleted(Task antecedent, Action continuation, bool continueOnCapturedContext)
    {
        var logical = XpLogicalContext.Capture();
        SynchronizationContext? context = continueOnCapturedContext ? SynchronizationContext.Current : null;
        // ThreadPool 调度保证不内联；捕获到 UI 同步上下文时 Post 回原线程。
        antecedent.ContinueWith(_ => Invoke(continuation, logical, context), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public static void Invoke(Action continuation, object? logical, SynchronizationContext? context)
    {
        if (context != null && !ReferenceEquals(context, SynchronizationContext.Current))
        {
            context.Post(state =>
            {
                var previous = XpLogicalContext.Swap(state);
                try { continuation(); }
                finally { XpLogicalContext.Restore(previous); }
            }, logical);
            return;
        }
        var saved = XpLogicalContext.Swap(logical);
        try { continuation(); }
        finally { XpLogicalContext.Restore(saved); }
    }

    public static void ValidateEnd(Task task)
    {
        if (task.Status == TaskStatus.Faulted)
        {
            var aggregate = task.Exception!;
            throw aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
        }
        if (task.Status == TaskStatus.Canceled) throw new OperationCanceledException("task_canceled");
    }
}
}
