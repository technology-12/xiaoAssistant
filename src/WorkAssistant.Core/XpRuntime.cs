// 双线可编译运行时辅助（主线 net48 透传实现）。
// XP 线（src-xp，net40）编译同一批共享源码，但 XpTask/XpArray 由
// WorkAssistant.Compatibility.Xp 提供真正的 net40 实现（Task.Factory 调度、
// ContinueWhenAll 协调、Timer 取消源等）；本文件不参与 XP 线编译。
// 两条产品线对源码的调用形式完全一致，仅解析到不同实现。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace System.Threading.Tasks
{
    /// <summary>调度与任务协调辅助；签名与 Compatibility.Xp 的 net40 实现一致。</summary>
    public static class XpTask
    {
        public static Task Run(Func<Task> function) => Task.Run(function);
        public static Task Run(Action action) => Task.Run(action);
        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function) => Task.Run(function);
        public static Task<TResult> Run<TResult>(Func<TResult> function) => Task.Run(function);
        public static Task WhenAll(IEnumerable<Task> tasks) => Task.WhenAll(tasks);
        public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks) => Task.WhenAll(tasks);
        public static Task WhenAll(params Task[] tasks) => Task.WhenAll(tasks);
        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks) => Task.WhenAll(tasks);
        public static Task WhenAny(Task first, Task second) => Task.WhenAny(first, second);
        public static Task<Task> WhenAny(IEnumerable<Task> tasks) => Task.WhenAny(tasks);
        public static Task<TResult> FromResult<TResult>(TResult value) => Task.FromResult(value);
        public static Task Delay(TimeSpan delay) => Task.Delay(delay);
        public static Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
        public static Task Delay(int milliseconds) => Task.Delay(milliseconds);
        public static Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);
        /// <summary>net48：延续不内联；net40：无此标志，由兼容层 awaiter 统一排队补偿。</summary>
        public static TaskCompletionSource<T> CompletionSource<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>net48：原生超时构造；net40：默认构造 + Timer 取消。</summary>
        public static CancellationTokenSource Cts(TimeSpan delay) => new CancellationTokenSource(delay);
        public static CancellationTokenSource Cts(int milliseconds) => new CancellationTokenSource(milliseconds);
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
