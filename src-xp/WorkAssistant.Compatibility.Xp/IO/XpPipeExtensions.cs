// net40 命名管道连接异步形态与 WPF Dispatcher.InvokeAsync 扩展。
// - WaitForConnectionAsync：NamedPipeServerStream 的 APM BeginWaitForConnection 组合。
// - ConnectAsync(int)：客户端连接无 APM 形式，使用池线程（一次性启动连接，低频）。
// - Dispatcher.InvokeAsync(Action, DispatcherPriority)：net40 只有 BeginInvoke，
//   包装为可等待 Task；关闭时按取消处理。

using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace System.IO.Pipes
{
    public static class XpPipeExtensions
    {
        public static Task WaitForConnectionAsync(this NamedPipeServerStream server, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Cancelled();
            // R04 修复：注册存活到等待终结；取消即关管道（等待以异常终结，由调用方归并）。
            CancellationTokenRegistration registration = token.Register(TryDispose, server);
            Task operation;
            try
            {
                operation = Task.Factory.FromAsync(server.BeginWaitForConnection, server.EndWaitForConnection, null);
            }
            catch
            {
                registration.Dispose();
                return Cancelled();
            }
            operation.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            return operation;
        }

        public static Task ConnectAsync(this NamedPipeClientStream client, int timeoutMilliseconds) =>
            XpTask.Run(() => client.Connect(timeoutMilliseconds));

        private static void TryDispose(object state)
        {
            try { ((Stream)state).Dispose(); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        private static Task Cancelled()
        {
            var source = new TaskCompletionSource<object?>();
            source.TrySetCanceled();
            return source.Task;
        }
    }
}

namespace System.Windows.Threading
{
    public static class XpDispatcherExtensions
    {
        public static Task InvokeAsync(this Dispatcher dispatcher, Action action, DispatcherPriority priority)
        {
            var completion = new TaskCompletionSource<object?>();
            var operation = dispatcher.BeginInvoke((Action)(() =>
            {
                try { action(); completion.TrySetResult(null); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }), priority);
            operation.Aborted += (_, __) => completion.TrySetCanceled();
            return completion.Task;
        }

        public static Task InvokeAsync(this Dispatcher dispatcher, Action action) =>
            dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
    }
}
