// XpTask/XpArray：共享源码使用的调度辅助的 net40 实现（签名与主线
// WorkAssistant.Core/XpRuntime.cs 的 net48 透传实现一致）。
// - Run/StartNew 捕获调用方逻辑上下文并在任务内恢复。
// - WhenAll/WhenAny 基于 ContinueWhen* 实现，结果聚合语义与 4.5 一致。
// - CompletionSource：net40 无 RunContinuationsAsynchronously，由
//   XpContinuation 统一排队补偿（延续不内联）。
// - Cts/CancelAfter：Timer 驱动的取消源/延迟取消。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace System.Threading.Tasks
{
    public static class XpTask
    {
        public static Task Run(Action action)
        {
            var logical = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            return Task.Factory.StartNew(() => WorkAssistant.Compatibility.Xp.XpContinuation.Invoke(action, logical, null),
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        public static Task Run(Func<Task> function)
        {
            var logical = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            return Task.Factory.StartNew(() =>
                {
                    var saved = WorkAssistant.Compatibility.Xp.XpLogicalContext.Swap(logical);
                    try { return function(); }
                    finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(saved); }
                },
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap();
        }

        public static Task<TResult> Run<TResult>(Func<TResult> function)
        {
            var logical = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            return Task.Factory.StartNew(() =>
                {
                    var saved = WorkAssistant.Compatibility.Xp.XpLogicalContext.Swap(logical);
                    try { return function(); }
                    finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(saved); }
                },
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function)
        {
            var logical = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            return Task.Factory.StartNew(() =>
                {
                    var saved = WorkAssistant.Compatibility.Xp.XpLogicalContext.Swap(logical);
                    try { return function(); }
                    finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(saved); }
                },
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap();
        }

        public static Task<TResult> FromResult<TResult>(TResult value)
        {
            var completion = new TaskCompletionSource<TResult>();
            completion.TrySetResult(value);
            return completion.Task;
        }

        public static Task Delay(TimeSpan delay) => Delay(delay, CancellationToken.None);

        public static Task Delay(TimeSpan delay, CancellationToken token) =>
            Delay((int)Math.Max(0, delay.TotalMilliseconds), token);

        public static Task Delay(int millisecondsDelay) => Delay(millisecondsDelay, CancellationToken.None);

        public static Task Delay(int millisecondsDelay, CancellationToken token)
        {
            var completion = new TaskCompletionSource<object?>();
            if (millisecondsDelay < 0)
            {
                completion.TrySetException(new ArgumentOutOfRangeException("millisecondsDelay"));
                return completion.Task;
            }
            if (token.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                return completion.Task;
            }
            if (millisecondsDelay == 0)
            {
                completion.TrySetResult(null);
                return completion.Task;
            }
            var timer = new Timer(self =>
            {
                ((Timer)self!).Dispose();
                completion.TrySetResult(null);
            });
            var registration = token.Register(() =>
            {
                timer.Dispose();
                completion.TrySetCanceled();
            });
            // 完成后释放注册，避免长生命周期任务积累。
            var _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            timer.Change(TimeSpan.FromMilliseconds(millisecondsDelay), new TimeSpan(-1));
            return completion.Task;
        }

        public static Task WhenAll(params Task[] tasks) => WhenAll((IEnumerable<Task>)tasks);

        /// <summary>原生语义：等待全部任务终结后再汇总；任一 Faulted→聚合全部异常，
        /// 否则任一 Canceled→取消，否则成功。不因单个任务失败而提前完成（R03 修复）。</summary>
        public static Task WhenAll(IEnumerable<Task> tasks)
        {
            var array = tasks.ToArray();
            if (array.Length == 0) return FromResult<object?>(null);
            var completion = new TaskCompletionSource<object?>();
            var remaining = array.Length;
            var exceptions = new List<Exception>();
            var canceled = false;
            var gate = new object();
            foreach (var task in array)
            {
                task.ContinueWith(finished =>
                {
                    List<Exception>? collected = null;
                    lock (gate)
                    {
                        if (finished.IsFaulted && finished.Exception != null)
                        {
                            if (exceptions.Count == 0) exceptions = new List<Exception>();
                            exceptions.AddRange(finished.Exception.InnerExceptions);
                        }
                        else if (finished.IsCanceled) canceled = true;
                        if (--remaining > 0) return;
                        collected = exceptions;
                    }
                    // 全部终结后统一汇总（锁外完成 TCS）。
                    if (collected!.Count > 0) completion.TrySetException(collected);
                    else if (canceled) completion.TrySetCanceled();
                    else completion.TrySetResult(null);
                }, TaskScheduler.Default);
            }
            return completion.Task;
        }

        public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks) => WhenAllResult(tasks.ToArray());

        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks) => WhenAllResult(tasks);

        private static Task<TResult[]> WhenAllResult<TResult>(Task<TResult>[] array)
        {
            if (array.Length == 0) return FromResult(new TResult[0]);
            var completion = new TaskCompletionSource<TResult[]>();
            var results = new TResult[array.Length];
            var remaining = array.Length;
            var exceptions = new List<Exception>();
            var canceled = false;
            var gate = new object();
            for (var index = 0; index < array.Length; index++)
            {
                var position = index;
                array[position].ContinueWith(finished =>
                {
                    List<Exception> collected;
                    lock (gate)
                    {
                        if (finished.IsFaulted && finished.Exception != null)
                        {
                            if (exceptions.Count == 0) exceptions = new List<Exception>();
                            exceptions.AddRange(finished.Exception.InnerExceptions);
                        }
                        else if (finished.IsCanceled) canceled = true;
                        else results[position] = finished.Result;
                        if (--remaining > 0) return;
                        collected = exceptions;
                    }
                    if (collected.Count > 0) completion.TrySetException(collected);
                    else if (canceled) completion.TrySetCanceled();
                    else completion.TrySetResult(results);
                }, TaskScheduler.Default);
            }
            return completion.Task;
        }

        public static Task<Task> WhenAny(IEnumerable<Task> tasks)
        {
            var array = tasks.ToArray();
            var completion = new TaskCompletionSource<Task>();
            foreach (var task in array)
            {
                task.ContinueWith(_ => completion.TrySetResult(task), TaskScheduler.Default);
            }
            return completion.Task;
        }

        public static Task<Task> WhenAny(Task first, Task second) => WhenAny(new[] { first, second });

        public static TaskCompletionSource<T> CompletionSource<T>() => new TaskCompletionSource<T>();

        public static CancellationTokenSource Cts(TimeSpan delay)
        {
            var source = new CancellationTokenSource();
            if (delay <= TimeSpan.Zero) { source.Cancel(); return source; }
            var timer = new Timer(self =>
            {
                ((Timer)self!).Dispose();
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            });
            timer.Change(delay, new TimeSpan(-1));
            return source;
        }

        public static CancellationTokenSource Cts(int milliseconds) => Cts(TimeSpan.FromMilliseconds(milliseconds));
    }

    /// <summary>net40 缺少的 CancellationTokenSource.CancelAfter / SemaphoreSlim.WaitAsync 等实例形态扩展。</summary>
    public static class XpThreadingExtensions
    {
        public static void CancelAfter(this CancellationTokenSource source, TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) { source.Cancel(); return; }
            var timer = new Timer(self =>
            {
                ((Timer)self!).Dispose();
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            });
            timer.Change(delay, new TimeSpan(-1));
        }

        public static void CancelAfter(this CancellationTokenSource source, int milliseconds) =>
            source.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));

        /// <summary>SemaphoreSlim 的 net40 异步等待：池上受阻塞语义限制（IPC 写锁等低并发场景）。</summary>
        public static Task WaitAsync(this SemaphoreSlim semaphore, CancellationToken token) =>
            XpTask.Run(() => semaphore.Wait(token));
    }
}

namespace System
{
    public static class XpArray
    {
        private static class Cache<T>
        {
            public static readonly T[] Value = new T[0];
        }
        public static T[] Empty<T>() => Cache<T>.Value;
    }
}
