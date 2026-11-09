// net40 垫片：HttpListener.GetContextAsync（.NET 4.5 才有），池线程执行阻塞 Accept。

using System;
using System.Net;
using System.Threading.Tasks;

namespace System.Net
{
    public static class XpHttpListenerExtensions
    {
        public static Task<HttpListenerContext> GetContextAsync(this HttpListener listener) =>
            XpTask.Run(listener.GetContext);
    }
}
