// net40 Stream/StreamReader 异步形态扩展（实例方法在 4.5 才存在，扩展方法可补齐）。
// - ReadAsync/WriteAsync 基于 APM Begin/End 组合；调用方的 await 延续经
//   XpContinuation 排队并恢复逻辑上下文，本层无需重复流转。
// - 取消语义：取消时关闭底层流（与主线调用方“取消即关流”约定一致），
//   读取方将 ObjectDisposed/IOException 归并为取消处理。
// - ReadLineAsync：StreamReader 无 APM 行读取，使用池线程执行（每活动流至多 1 个）。

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    public static class XpStreamExtensions
    {
        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, int offset, int count)
            => stream.ReadAsync(buffer, offset, count, CancellationToken.None);

        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (token.IsCancellationRequested) return CancelledInt();
            // R04 修复：注册必须存活到 APM 操作终结；取消即关流，由调用方归并为取消。
            CancellationTokenRegistration registration = token.Register(DisposeQuietly, stream);
            Task<int> operation;
            try
            {
                operation = Task.Factory.FromAsync(stream.BeginRead, stream.EndRead, buffer, offset, count, null);
            }
            catch
            {
                registration.Dispose();
                return CancelledInt();
            }
            operation.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            return operation;
        }

        public static Task WriteAsync(this Stream stream, byte[] buffer, int offset, int count)
            => stream.WriteAsync(buffer, offset, count, CancellationToken.None);

        public static Task WriteAsync(this Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Cancelled();
            CancellationTokenRegistration registration = token.Register(DisposeQuietly, stream);
            Task operation;
            try
            {
                operation = Task.Factory.FromAsync(stream.BeginWrite, stream.EndWrite, buffer, offset, count, null);
            }
            catch
            {
                registration.Dispose();
                return Cancelled();
            }
            operation.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            return operation;
        }

        public static Task FlushAsync(this Stream stream) => XpTask.Run(stream.Flush);

        public static Task FlushAsync(this Stream stream, CancellationToken token) =>
            XpTask.Run(() => { token.ThrowIfCancellationRequested(); stream.Flush(); });

        public static Task CopyToAsync(this Stream source, Stream destination, int bufferSize, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Cancelled();
            var completion = new TaskCompletionSource<object?>();
            CopyChunk(source, destination, token, completion);
            return completion.Task;
        }

        private static void CopyChunk(Stream source, Stream destination, CancellationToken token,
            TaskCompletionSource<object?> completion)
        {
            var buffer = new byte[Math.Max(81920, Math.Min(4096, 1 << 20))];
            try
            {
                if (token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                source.ReadAsync(buffer, 0, buffer.Length, token).ContinueWith(read =>
                {
                    if (read.IsFaulted) { completion.TrySetException(read.Exception!.InnerExceptions); return; }
                    if (read.IsCanceled) { completion.TrySetCanceled(); return; }
                    int count = read.Result;
                    if (count == 0) { completion.TrySetResult(null); return; }
                    destination.WriteAsync(buffer, 0, count, token).ContinueWith(write =>
                    {
                        if (write.IsFaulted) completion.TrySetException(write.Exception!.InnerExceptions);
                        else if (write.IsCanceled) completion.TrySetCanceled();
                        else CopyChunk(source, destination, token, completion);
                    }, TaskScheduler.Default);
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        public static Task<string?> ReadLineAsync(this StreamReader reader) => XpTask.Run(reader.ReadLine);

        private static void DisposeQuietly(object state)
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

        private static Task<int> CancelledInt()
        {
            var source = new TaskCompletionSource<int>();
            source.TrySetCanceled();
            return source.Task;
        }
    }
}
