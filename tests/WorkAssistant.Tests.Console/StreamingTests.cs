using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Tests.Console
{
    /// <summary>
    /// SSE 流式协议测试（正式版 §7.2）：片段聚合、文本增量、断流失败、
    /// 非事件流回退。全部走本地 HttpListener，不访问真实模型。
    /// </summary>
    public static class StreamingTests
    {
        public static void Register()
        {
            Test.Add("SSE：文本增量回调与完整组装", TextDeltas);
            Test.Add("SSE：工具调用片段跨块聚合后执行参数完整", ToolCallFragments);
            Test.Add("SSE：无结束信号断流 → MODEL_UNAVAILABLE", IncompleteStream);
            Test.Add("SSE：非事件流响应回退非流式解析", NonStreamFallback);
            Test.Add("SSE：请求体 stream=true", StreamFlagInRequest);
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
            private readonly HttpListener _listener;

            public MockServer(string contentType, string body)
            {
                _listener = LocalHttpTestListener.Start(out var port);
                Port = port;
                XpTask.Run(() =>
                {
                    try
                    {
                        var ctx = _listener.GetContext();
                        using var reader = new System.IO.StreamReader(ctx.Request.InputStream,
                            System.Text.Encoding.UTF8);
                        RequestBody = reader.ReadToEnd();
                        ctx.Response.ContentType = contentType;
                        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.OutputStream.Close();
                    }
                    catch (Exception)
                    {
                    }
                });
            }

            public int Port { get; }
            public string? RequestBody { get; private set; }

            public void Dispose()
            {
                try { _listener.Stop(); } catch (Exception) { }
                try { _listener.Close(); } catch (Exception) { }
            }
        }

        private static string Chunk(string json) => "data: " + json + "\n\n";

        private static void TextDeltas()
        {
            var sse = Chunk("{\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"你好\"}}]}") +
                      Chunk("{\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}") +
                      Chunk("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}") +
                      "data: [DONE]\n\n";
            using var server = new MockServer("text/event-stream", sse);
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var received = new List<string>();
            var reply = client.CompleteStreamAsync(request,
                delta => received.Add(delta), CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!reply.HasToolCalls, "无工具调用");
            Test.Equal("你好，世界", reply.Content ?? "", "文本组装完整");
            Test.Equal(2, received.Count, "两次增量回调");
            Test.Equal("你好", received[0], "增量 1");
            Test.Equal("，世界", received[1], "增量 2");
            Test.Equal("stop", reply.FinishReason, "finish_reason");
            Test.Check(reply.Streamed, "标记为流式");
        }

        private static void ToolCallFragments()
        {
            // 工具调用片段：id/name 与 arguments 拆成多块（真实网关常见行为）。
            var sse = Chunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"," +
                       "\"function\":{\"name\":\"agg\",\"arguments\":\"\"}}]}}]}") +
                      Chunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0," +
                       "\"function\":{\"arguments\":\"{\\\"path" + "s\\\":\"}}]}}]}") +
                      Chunk("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0," +
                       "\"function\":{\"arguments\":\"[\\\"inputs/a.csv\\\"]}\"}}]}}]}") +
                      Chunk("{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}") +
                      "data: [DONE]\n\n";
            using var server = new MockServer("text/event-stream", sse);
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var reply = client.CompleteStreamAsync(request, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Test.Check(reply.HasToolCalls, "工具调用已组装");
            Test.Equal(1, reply.ToolCalls!.Count, "一个调用");
            Test.Equal("call_1", reply.ToolCalls[0].Id, "id");
            Test.Equal("agg", reply.ToolCalls[0].Name, "名称");
            // 参数必须是完整 JSON（半截 JSON 不应出现）。
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(reply.ToolCalls[0].ArgumentsJson);
            Test.Equal("inputs/a.csv", (string)((Newtonsoft.Json.Linq.JArray)parsed["paths"]!)[0]!,
                "参数完整可解析");
        }

        private static void IncompleteStream()
        {
            // 没有 finish_reason 也没有 [DONE]：视为断流。
            var sse = Chunk("{\"choices\":[{\"delta\":{\"content\":\"部分文字\"}}]}");
            using var server = new MockServer("text/event-stream", sse);
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            // 已输出过增量 → 不重试，直接报 MODEL_UNAVAILABLE。
            var ex = Test.ExpectThrowAsync<ToolException>(() =>
                client.CompleteStreamAsync(request, _ => { }, CancellationToken.None));
            Test.Equal(ErrorCodes.ModelUnavailable, ex.Code, "断流错误码");
        }

        private static void NonStreamFallback()
        {
            // 端点忽略 stream 参数返回普通 JSON：回退非流式解析。
            const string body =
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{" +
                "\"role\":\"assistant\",\"content\":\"回退解析\"}}]}";
            using var server = new MockServer("application/json", body);
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            var reply = client.CompleteStreamAsync(request, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Test.Equal("回退解析", reply.Content, "回退解析成功");
            Test.Check(!reply.Streamed, "标记为非流式");
        }

        private static void StreamFlagInRequest()
        {
            var sse = Chunk("{\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}") +
                      "data: [DONE]\n\n";
            using var server = new MockServer("text/event-stream", sse);
            using var client = new HttpModelClient(LocalSettings(server.Port));
            var request = new ModelRequest("m", new[] { ChatMessage.User("x") }, new ToolDefinition[0]);
            client.CompleteStreamAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            // 请求体应带 stream=true。
            Test.Check(server.RequestBody != null && server.RequestBody.Contains("\"stream\":true"),
                "请求体 stream=true：" + (server.RequestBody ?? "(空)"));
        }
    }
}
