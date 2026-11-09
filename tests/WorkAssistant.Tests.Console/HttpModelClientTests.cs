using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Tests.Console
{
    /// <summary>HTTP 客户端协议测试（设计 §8 / 验收 14.1-6）：本地 HttpListener，不访问真实模型。</summary>
    public static class HttpModelClientTests
    {
        public static void Register()
        {
            Test.Add("HTTP：base_url 规范化合并", EndpointNormalization);
            Test.Add("HTTP：两协议客户端构造即验证（无限超时合法）", ConstructorAcceptsInfiniteTimeout);
            Test.Add("HTTP：请求体结构（stream=false/tools/tool_choice）", RequestShape);
            Test.Add("HTTP：正常响应解析 tool_calls", ParseSuccess);
            Test.Add("HTTP：401 → MODEL_AUTH_FAILED 且不重试", Auth401);
            Test.Add("HTTP：429 + Retry-After → 重试后成功", RetryOn429);
            Test.Add("HTTP：5xx 后 200 成功", RetryOn503);
            Test.Add("HTTP：非法 JSON → MODEL_PROTOCOL_ERROR", GarbageBody);
            Test.Add("HTTP：取消立即生效", Cancellation);
        }

        // R01 回归：Timeout 必须是两框架均接受的无限值（-1ms）。此前 new TimeSpan(-1)（-1 tick）
        // 使原生 HttpClient 在构造时抛 ArgumentOutOfRangeException，用户尚未请求 API 即失败。
        private static void ConstructorAcceptsInfiniteTimeout()
        {
            var openAi = new ModelSettings { BaseUrl = "http://127.0.0.1:9", ModelName = "m", ApiKey = "k", TimeoutSeconds = 5 };
            using (var client = new HttpModelClient(openAi))
            {
                Test.Check(true, "OpenAI 协议客户端构造成功");
            }
            var anthropic = new ModelSettings { BaseUrl = "http://127.0.0.1:9", ModelName = "m", ApiKey = "k",
                Protocol = "anthropic-messages", AnthropicVersion = "2023-06-01", TimeoutSeconds = 5 };
            using (var anthropicClient = new AnthropicModelClient(anthropic))
            {
                Test.Check(true, "Anthropic 协议客户端构造成功");
            }
        }

        private static void EndpointNormalization()
        {
            var a = HttpModelClient.BuildEndpoint("https://gw.example.com/v1/");
            Test.Equal("https://gw.example.com/v1/chat/completions", a.ToString(), "尾部斜杠规范化");
            var b = HttpModelClient.BuildEndpoint("https://gw.example.com/v1");
            Test.Equal(a.ToString(), b.ToString(), "有无尾斜杠结果一致");
            Test.ExpectToolError(ErrorCodes.InvalidArguments,
                () => HttpModelClient.BuildEndpoint("gw.example.com/v1"), "无协议头拒绝");
        }

        private static void RequestShape()
        {
            var request = new ModelRequest(
                "model-x",
                new[] { ChatMessage.User("hi") },
                new[] { new ToolDefinition("t1", "测试", new JObject { ["type"] = "object" }) });
            var json = HttpModelClient.SerializeRequest(request);
            var obj = JObject.Parse(json);
            Test.Equal(false, (bool)obj["stream"]!, "stream=false");
            Test.Equal("model-x", (string)obj["model"]!, "model 字段");
            Test.Equal("auto", (string)obj["tool_choice"]!, "tool_choice=auto");
            var tool = (JObject)((JArray)obj["tools"]!)[0]!;
            Test.Equal("function", (string)tool["type"]!, "工具类型");
            Test.Equal("t1", (string)tool["function"]!["name"]!, "工具名");

            // tool 消息必须带 tool_call_id。
            var req2 = new ModelRequest("m",
                new[] { new ChatMessage("tool", "{}", toolCallId: "call_9") },
                new ToolDefinition[0]);
            var obj2 = JObject.Parse(HttpModelClient.SerializeRequest(req2));
            Test.Equal("call_9", (string)((JArray)obj2["messages"]!)[0]!["tool_call_id"]!, "tool_call_id");
        }

        private static void ParseSuccess()
        {
            const string body = @"{
  ""choices"": [{
    ""finish_reason"": ""tool_calls"",
    ""message"": {
      ""role"": ""assistant"",
      ""content"": null,
      ""tool_calls"": [{
        ""id"": ""call_001"",
        ""type"": ""function"",
        ""function"": { ""name"": ""list_files"", ""arguments"": ""{\""directory\"":\""inputs\""}"" }
      }]
    }
  }]
}";
            var reply = HttpModelClient.ParseReply(body);
            Test.Check(reply.HasToolCalls, "应含 tool_calls");
            Test.Equal("call_001", reply.ToolCalls![0].Id, "id");
            Test.Equal("list_files", reply.ToolCalls[0].Name, "name");
            Test.Check(reply.ToolCalls[0].ArgumentsJson.Contains("inputs"), "arguments");
        }

        private static ModelSettings LocalSettings(int port) => new()
        {
            BaseUrl = "http://127.0.0.1:" + port,
            ModelName = "test-model",
            ApiKey = "sk-test",
            TimeoutSeconds = 5,
            UseSystemProxy = false
        };

        private sealed class MockServer : IDisposable
        {
            private HttpListener _listener;
            private readonly Func<HttpListenerContext, bool> _handler;
            public int Hits;

            public MockServer(Func<HttpListenerContext, bool> handler)
            {
                _handler = handler;
                _listener = LocalHttpTestListener.Start(out var port);
                Port = port;
                XpTask.Run(RunLoop);
            }

            public int Port { get; }

            private async Task RunLoop()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Interlocked.Increment(ref Hits);
                    var keepGoing = _handler(ctx);
                    if (!keepGoing) _listener.Stop();
                }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch (Exception) { }
                try { _listener.Close(); } catch (Exception) { }
            }
        }

        private static string OkBody(string text)
        {
            var content = JToken.FromObject(text).ToString(Newtonsoft.Json.Formatting.None);
            return "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":" +
                   content + "}}]}";
        }

        private static void Auth401()
        {
            using var server = new MockServer(ctx =>
            {
                ctx.Response.StatusCode = 401;
                Write(ctx, "{\"error\":{\"message\":\"bad key\"}}");
                return true;
            });
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var ex = Test.ExpectThrowAsync<ToolException>(() =>
                client.CompleteAsync(request, CancellationToken.None));
            Test.Equal(ErrorCodes.ModelAuthFailed, ex.Code, "401 → 认证失败");
            Test.Equal(1, server.Hits, "401 不重试");
        }

        private static void RetryOn429()
        {
            var attempts = 0;
            using var server = new MockServer(ctx =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n <= 2)
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers["Retry-After"] = "0";
                    Write(ctx, "{\"error\":{\"message\":\"rate\"}}");
                    return true;
                }
                Write(ctx, OkBody("ok"));
                return true;
            });
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var reply = client.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Test.Equal(3, server.Hits, "429 重试两次后成功");
            Test.Check(!reply.HasToolCalls, "纯文本回复");
        }

        private static void RetryOn503()
        {
            var attempts = 0;
            using var server = new MockServer(ctx =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1)
                {
                    ctx.Response.StatusCode = 503;
                    Write(ctx, "{}");
                    return true;
                }
                Write(ctx, OkBody("ok"));
                return true;
            });
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            client.CompleteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Test.Equal(2, server.Hits, "503 重试一次后成功");
        }

        private static void GarbageBody()
        {
            using var server = new MockServer(ctx =>
            {
                Write(ctx, "<html>not json</html>");
                return true;
            });
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var ex = Test.ExpectThrowAsync<ToolException>(() =>
                client.CompleteAsync(request, CancellationToken.None));
            Test.Equal(ErrorCodes.ModelProtocolError, ex.Code, "非法 JSON → 协议错误");
            Test.Equal(1, server.Hits, "协议错误不重试");
        }

        private static void Cancellation()
        {
            using var server = new MockServer(ctx =>
            {
                System.Threading.Thread.Sleep(3000);
                Write(ctx, OkBody("late"));
                return true;
            });
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            using var cts = XpTask.Cts(300);
            try
            {
                client.CompleteAsync(request, cts.Token).GetAwaiter().GetResult();
                Test.Check(false, "应被取消");
            }
            catch (OperationCanceledException)
            {
                Test.Check(true, "取消生效");
            }
        }

        private static void Write(HttpListenerContext ctx, string body)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
