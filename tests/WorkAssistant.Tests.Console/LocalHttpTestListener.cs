using System;
using System.Net;
using System.Net.Sockets;

namespace WorkAssistant.Tests.Console
{
    internal static class LocalHttpTestListener
    {
        internal static HttpListener Start(out int port)
        {
            // Obtain an OS-selected port, then retry if another process claims it
            // between TCP probing and HTTP.sys registration. Do not seed Random per call.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                var listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try { listener.Start(); return listener; }
                catch (HttpListenerException) { listener.Close(); if (attempt == 19) throw; }
            }
            throw new InvalidOperationException("No local test HTTP port available.");
        }
    }
}
