// XP 专属回归测试（仅 XP 线编译；对应交付审查 R02—R07 的复现场景）。
// 主线无兼容层，不参与编译；共享用例仍在 tests/WorkAssistant.Tests.Console。

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Tools;
using WorkAssistant.Core;

namespace WorkAssistant.Tests.Console
{
    public static partial class XpCompatTests
    {
        public static void Register()
        {
            Test.Add("XP-R02：AsyncLocal 父子隔离（子方法首个 await 前修改不泄漏）", AsyncLocalParentChild);
            Test.Add("XP-R02：AsyncLocal 兄弟任务互不串扰", AsyncLocalSiblings);
            Test.Add("XP-R02：async 方法抛 OCE → 任务状态为 Canceled", AsyncCancelStatus);
            Test.Add("XP-R03：WhenAll 等待全部任务（一故障一挂起不提前完成）", WhenAllWaitsForAll);
            Test.Add("XP-R03：WhenAll 汇总异常而非首个", WhenAllAggregates);
            Test.Add("XP-R04：流读取中取消使任务终结", StreamReadCancelTerminates);
            Test.Add("XP-R04：管道等待中取消使任务终结", PipeWaitCancelTerminates);
            Test.Add("XP-R04：HTTP 响应等待中取消使任务终结", HttpCancelTerminates);
            Test.Add("XP-R05：独占提交拒绝并发写者（不丢更新）", AtomicCommitExcludesConcurrentWriter);
            Test.Add("XP-R06：目录联接越界读取被拒绝", JunctionEscapeRejected);
            Test.Add("XP-R07：两页 PDF 恰好两页且页码顺序正确", PdfTwoPagesExact);
            Test.Add("XP-R07：多字体两页 PDF 按字体解码互不污染", PdfPerFontDecoding);
            Test.Add("XP-R07：同页混排 CJK+西文不互相污染（S03）", PdfMixedFontSamePage);
            Test.Add("XP-R04：代理三路径配置正确（S05）", ProxyThreePaths);
            RegisterPdfUpdateCases();
            RegisterEditRecoveryCases();
        }

        private static void RegisterPdfUpdateCases()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "xp-pdf", "expected.json"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("缺少 PDF 增量更新回归样例，不能省略这些测试。");
            var root = Path.Combine(dir.FullName, "tests", "fixtures", "xp-pdf");
            var cases = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(root, "expected.json")));
            foreach (var item in cases)
            {
                var fixture = item;
                Test.Add("XP-T01：" + (string)fixture["file"]!, () =>
                {
                    var path = Path.Combine(root, (string)fixture["file"]!);
                    if ((string?)fixture["error"] != null)
                        Test.ExpectToolError((string)fixture["error"]!, () => PdfText.Extract(path, Path.GetFileName(path)), "失效引用不得读取旧内容");
                    else
                    {
                        var value = PdfText.Extract(path, Path.GetFileName(path));
                        Test.Equal(1, value.PageCount, "最新页树只有一页");
                        Test.Equal((string)fixture["text"]!, string.Concat(value.Pages[0].Text.Where(c => !char.IsWhiteSpace(c))), "读取有效对象版本");
                    }
                });
            }
        }

        // ---- R02 ----------------------------------------------------------------

        private static async Task<string> ChildModifiesAsyncLocal(AsyncLocal<string> slot)
        {
            slot.Value = "child"; // 首个 await 前修改
            await XpTask.Delay(50);
            return slot.Value;
        }

        private static void AsyncLocalParentChild()
        {
            var slot = new AsyncLocal<string>();
            slot.Value = "parent";
            var task = ChildModifiesAsyncLocal(slot);
            Test.Equal("parent", slot.Value, "子方法修改 AsyncLocal 后父上下文不受影响");
            Test.Equal("child", task.GetAwaiter().GetResult(), "子方法内读到自己的值");
            Test.Equal("parent", slot.Value, "子方法完成后父上下文仍为原值");
        }

        private static void AsyncLocalSiblings()
        {
            var slot = new AsyncLocal<string>();
            slot.Value = "root";
            var first = XpTask.Run(async () => { slot.Value = "a"; await XpTask.Delay(30); return slot.Value; });
            var second = XpTask.Run(async () => { slot.Value = "b"; await XpTask.Delay(30); return slot.Value; });
            Test.Equal("a", first.GetAwaiter().GetResult(), "兄弟 1 读到自己的值");
            Test.Equal("b", second.GetAwaiter().GetResult(), "兄弟 2 读到自己的值");
            Test.Equal("root", slot.Value, "发起方上下文不受兄弟任务影响");
        }

        private static void AsyncCancelStatus()
        {
            var source = XpTask.Cts(20);
            var task = DelayedThrow(source.Token);
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Test.Equal(TaskStatus.Canceled, task.Status, "async 方法未处理 OCE → Canceled（非 Faulted）");
        }

        private static async Task DelayedThrow(CancellationToken token)
        {
            await XpTask.Delay(80, CancellationToken.None); // 超过取消点
            token.ThrowIfCancellationRequested();
        }

        // ---- R03 ----------------------------------------------------------------

        private static void WhenAllWaitsForAll()
        {
            var pending = new TaskCompletionSource<int>();
            var faulted = new TaskCompletionSource<int>();
            faulted.TrySetException(new InvalidOperationException("boom"));
            var aggregate = XpTask.WhenAll(pending.Task, faulted.Task);
            XpTask.Delay(150).GetAwaiter().GetResult();
            Test.Check(!aggregate.IsCompleted, "存在未完成任务时 WhenAll 不得提前完成");
            pending.TrySetResult(7);
            try { aggregate.GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { }
            catch (AggregateException aggregateException)
            {
                Test.Check(aggregateException.InnerExceptions.Count == 1, "单个异常解包");
            }
            Test.Check(aggregate.IsCompleted, "全部终结后 WhenAll 完成");
        }

        private static void WhenAllAggregates()
        {
            var first = new TaskCompletionSource<int>(); first.TrySetException(new InvalidOperationException("one"));
            var second = new TaskCompletionSource<int>(); second.TrySetException(new InvalidOperationException("two"));
            var third = new TaskCompletionSource<int>(); third.TrySetResult(3);
            var aggregate = XpTask.WhenAll(first.Task, second.Task, third.Task);
            var collected = 0;
            try { aggregate.GetAwaiter().GetResult(); }
            catch (AggregateException exception) { collected = exception.InnerExceptions.Count; }
            catch (InvalidOperationException) { collected = 1; }
            Test.Equal(2, collected, "两个失败任务的异常都保留：" + collected);
        }

        // ---- R04 ----------------------------------------------------------------

        private static void StreamReadCancelTerminates()
        {
            using var server = new NamedPipeServerStream("xp-test-stream-" + Guid.NewGuid().ToString("N"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var source = XpTask.Cts(200);
            var task = server.ReadAsync(new byte[16], 0, 16, source.Token); // 无连接 → 阻塞
            var finished = task.ContinueWith(_ => true, TaskScheduler.Default);
            var completed = XpTask.WhenAny(finished, XpTask.Delay(1500)).GetAwaiter().GetResult() == finished;
            Test.Check(completed, "取消后读取任务必须终结（不永久 pending）");
            Test.Check(task.IsCompleted, "读取任务已完成");
        }

        private static void PipeWaitCancelTerminates()
        {
            using var server = new NamedPipeServerStream("xp-test-pipe-" + Guid.NewGuid().ToString("N"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var source = XpTask.Cts(200);
            var task = server.WaitForConnectionAsync(source.Token); // 无客户端 → 阻塞
            var finished = task.ContinueWith(_ => true, TaskScheduler.Default);
            var completed = XpTask.WhenAny(finished, XpTask.Delay(1500)).GetAwaiter().GetResult() == finished;
            Test.Check(completed, "取消后管道等待必须终结（不永久 pending）");
            Test.Check(task.IsCompleted, "等待任务已完成");
        }

        private static void HttpCancelTerminates()
        {
            var listener = LocalHttpTestListener.Start(out var port);
            var loop = XpTask.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { return; }
                    System.Threading.Thread.Sleep(1500); // 延迟响应，制造等待窗口
                    try
                    {
                        var body = Encoding.UTF8.GetBytes("{}");
                        context.Response.ContentLength64 = body.Length;
                        context.Response.OutputStream.Write(body, 0, body.Length);
                    }
                    catch { }
                }
            });
            try
            {
                var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false })
                { Timeout = TimeSpan.FromMilliseconds(-1) };
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                    new Uri("http://127.0.0.1:" + port + "/"));
                request.Content = new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json");
                using var source = XpTask.Cts(300);
                var task = client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, source.Token);
                var finished = task.ContinueWith(_ => true, TaskScheduler.Default);
                var completed = XpTask.WhenAny(finished, XpTask.Delay(2000)).GetAwaiter().GetResult() == finished;
                Test.Check(completed, "取消后 HTTP 请求必须终结（Abort 生效，不永久 pending）");
                Test.Check(task.IsCompleted, "请求任务已完成");
                client.Dispose();
            }
            finally
            {
                try { listener.Stop(); } catch { }
                try { listener.Close(); } catch { }
            }
        }

        // ---- S05 ----------------------------------------------------------------

        private static System.Net.Http.HttpRequestMessage Message()
            => new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, new Uri("http://127.0.0.1:9/"));

        private static System.Net.HttpWebRequest BuiltRequest(System.Net.Http.HttpClient client)
        {
            var method = client.GetType().GetMethod("BuildRequest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (System.Net.HttpWebRequest)method!.Invoke(client, new object[] { Message() })!;
        }

        private static void ProxyThreePaths()
        {
            var marker = new System.Net.WebProxy("http://127.0.0.1:3128");
            var previous = System.Net.WebRequest.DefaultWebProxy;
            try
            {
                System.Net.WebRequest.DefaultWebProxy = marker;
                // 1) 关闭代理：显式 null（不落回系统默认）
                using (var off = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false }))
                {
                    Test.Check(ReferenceEquals(BuiltRequest(off).Proxy, null), "UseProxy=false → 显式禁用代理");
                }
                // 2) 系统默认代理：保持未设置（读到系统默认）
                using (var systemDefault = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = true }))
                {
                    Test.Check(ReferenceEquals(BuiltRequest(systemDefault).Proxy, marker), "UseProxy=true+未指定 → 保留系统默认代理");
                }
                // 3) 指定代理：赋给请求
                using (var pinned = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = true, Proxy = marker }))
                {
                    Test.Check(ReferenceEquals(BuiltRequest(pinned).Proxy, marker), "UseProxy=true+指定 → 使用指定代理");
                }
            }
            finally { System.Net.WebRequest.DefaultWebProxy = previous; }
        }

        // ---- R05 ----------------------------------------------------------------

        private static void AtomicCommitExcludesConcurrentWriter()
        {
            var root = Path.Combine(Path.GetTempPath(), "xp-r05-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "doc.txt");
                var original = Encoding.UTF8.GetBytes("approved content\n");
                File.WriteAllBytes(path, original);
                var replacement = Encoding.UTF8.GetBytes("agent replacement\n");
                var concurrentWriterOpened = false;
                var concurrentWriteRejected = false;
                var contentDuringStaged = "";
                var order = new System.Collections.Generic.List<string>();
                var hash = TextEditEngine.Hash(original);

                AtomicTextTransaction.Commit(path, root, hash, replacement,
                    prepared: () => order.Add("prepared"),
                    staged: () =>
                    {
                        order.Add("staged");
                        // 审查复现场景：提交中途另一编辑器尝试写入同一文件。
                        try
                        {
                            using var concurrent = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Write);
                            concurrent.Write(Encoding.UTF8.GetBytes("unapproved concurrent change"), 0, 33);
                            concurrentWriterOpened = true;
                        }
                        catch (IOException) { concurrentWriteRejected = true; }
                        // 主流程阻塞在本回调内：此刻文件必然仍是批准的原文（并发者未写入成功）。
                        // 读取须允许与我们持有的读写句柄共存（FileShare.Read|Write）。
                        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
                        using var text = new StreamReader(reader);
                        contentDuringStaged = text.ReadToEnd();
                    },
                    committed: () => order.Add("committed"),
                    CancellationToken.None);

                Test.Check(concurrentWriteRejected && !concurrentWriterOpened,
                    "并发写者在批准窗口内必须被独占拒绝（其写入失败而非被覆盖丢失）");
                Test.Equal("approved content\n", contentDuringStaged.Replace("\r\n", "\n"), "staged 期间并发写入未生效");
                Test.Equal("agent replacement\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n"),
                    "提交结果为批准的 replacement");
                Test.Equal("prepared,staged,committed", string.Join(",", order), "回调顺序");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- R06 ----------------------------------------------------------------

        private static void JunctionEscapeRejected()
        {
            var root = Path.Combine(Path.GetTempPath(), "xp-r06-" + Guid.NewGuid().ToString("N"));
            var permitted = Path.Combine(root, "permitted");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(permitted);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "marker.txt"), "outside-secret");
            var link = Path.Combine(permitted, "link");
            var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
            process!.WaitForExit();
            if (!Directory.Exists(link))
            {
                Test.Check(true, "环境不支持创建联接，跳过（无法验证）");
                return;
            }
            try
            {
                VerifiedFiles.OpenRead(Path.Combine(link, "marker.txt"), permitted);
                throw new InvalidOperationException("断言失败：联接越界读取未被拒绝");
            }
            catch (WorkAssistant.Core.ToolException exception)
            {
                Test.Equal(WorkAssistant.Core.ErrorCodes.PathDenied, exception.Code, "目录联接越界 → PATH_DENIED");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- R07 ----------------------------------------------------------------

        private static void PdfTwoPagesExact()
        {
            var root = Path.Combine(Path.GetTempPath(), "xp-r07a-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "two.pdf");
                File.WriteAllBytes(path, SampleWriter.BuildTwoPagePdf());
                var result = PdfText.Extract(path, "two.pdf");
                Test.Equal(2, result.PageCount, "声明页数 2");
                Test.Equal(2, result.Pages.Count, "恰好两页正文（无重复提取）");
                Test.Equal("1,2", string.Join(",", result.Pages.Select(p => p.PageNumber)), "页码顺序 [1,2]");
                Test.Check(result.Pages[0].Text.Contains("Win7 Work Assistant"), "第 1 页内容在第 1 位");
                Test.Check(result.Pages[1].Text.Contains("你好世界"), "第 2 页内容在第 2 位");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void PdfPerFontDecoding()
        {
            // 构造两页、两字体（各有独立 ToUnicode，映射不同）的 PDF：
            // 页 1 用 F1（CID→"甲"），页 2 用 F2（同一 CID→"乙"）。按字体解码应分别得到甲/乙；
            // 若仍用全局合并表则两页同字。
            var root = Path.Combine(Path.GetTempPath(), "xp-r07b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "fonts.pdf");
                File.WriteAllBytes(path, BuildTwoFontTwoPagePdf());
                var result = PdfText.Extract(path, "fonts.pdf");
                Test.Equal(2, result.Pages.Count, "两页");
                var first = result.Pages[0].Text.Trim();
                var second = result.Pages[1].Text.Trim();
                Test.Check(first.Contains("甲") && !first.Contains("乙"), "页 1 按 F1 解码得 甲：" + first);
                Test.Check(second.Contains("乙") && !second.Contains("甲"), "页 2 按 F2 解码得 乙：" + second);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void PdfMixedFontSamePage()
        {
            // 复现审查 S03：同页先用带 ToUnicode 的 Type0 字体写 CJK，再切到无 ToUnicode 的
            // Helvetica 写西文。正确结果为“甲u2”；污染结果为“甲甲”。
            var root = Path.Combine(Path.GetTempPath(), "xp-s03-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "mixed.pdf");
                File.WriteAllBytes(path, BuildMixedFontPdf());
                var result = PdfText.Extract(path, "mixed.pdf");
                Test.Equal(1, result.Pages.Count, "单页");
                var text = result.Pages[0].Text.Trim();
                Test.Check(text.Contains("甲"), "含 甲：" + text);
                Test.Check(text.Contains("u2"), "西文按标准编码读出 u2：" + text);
                Test.Check(!text.Contains("甲甲"), "不得把西文写成 甲（字体污染）：" + text);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static byte[] BuildMixedFontPdf()
        {
            var objects = new System.Collections.Generic.Dictionary<int, byte[]>();
            var font1Cmap = Encoding.ASCII.GetBytes(
                "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CMapName /F1 def\n" +
                "1 begincodespacerange\n<00> <ff>\nendcodespacerange\n1 beginbfchar\n<41> <7532>\nendbfchar\n" +
                "endcmap\nCMapName currentdict /CMap defineresource pop end end\n");
            // 同页内容：先 F1（CMap：A→甲）写 A，再 F2（无 ToUnicode 的 Helvetica）写 u2。
            var content = Encoding.ASCII.GetBytes("BT /F1 12 Tf (A) Tj ET\nBT /F2 12 Tf (u2) Tj ET\n");
            objects[1] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
            objects[2] = Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
            objects[3] = Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 7 0 R >>");
            objects[4] = Encoding.ASCII.GetBytes("[placeholder]");
            objects[5] = Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /Identity-H /DescendantFonts [] /ToUnicode 8 0 R >>");
            objects[6] = Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            System.Func<byte[], byte[]> wrap = payload =>
            {
                var header = Encoding.ASCII.GetBytes("<< /Length " + payload.Length + " >>\nstream\n");
                var tail = Encoding.ASCII.GetBytes("\nendstream");
                var wrapped = new byte[header.Length + payload.Length + tail.Length];
                header.CopyTo(wrapped, 0);
                payload.CopyTo(wrapped, header.Length);
                tail.CopyTo(wrapped, header.Length + payload.Length);
                return wrapped;
            };
            objects[7] = wrap(content);
            objects[8] = wrap(font1Cmap);
            return Assemble(objects);
        }

        private static byte[] BuildTwoFontTwoPagePdf()
        {
            var objects = new System.Collections.Generic.Dictionary<int, byte[]>();
            // 1: catalog  2: pages  3/4: page1/2  5/6: font1/2  7/8: toUnicode  9/10: contents
            Func<byte[], byte[]> Stream = payload =>
            {
                var header = Encoding.ASCII.GetBytes("<< /Length " + payload.Length + " >>\nstream\n");
                var tail = Encoding.ASCII.GetBytes("\nendstream");
                var wrapped = new byte[header.Length + payload.Length + tail.Length];
                header.CopyTo(wrapped, 0);
                payload.CopyTo(wrapped, header.Length);
                tail.CopyTo(wrapped, header.Length + payload.Length);
                return wrapped;
            };

            var font1Cmap = Encoding.ASCII.GetBytes(
                "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
                "/CMapName /F1 def\n1 begincodespacerange\n<00> <ff>\nendcodespacerange\n" +
                "1 beginbfchar\n<41> <7532>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop end end\n");
            var font2Cmap = Encoding.ASCII.GetBytes(
                "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
                "/CMapName /F2 def\n1 begincodespacerange\n<00> <ff>\nendcodespacerange\n" +
                "1 beginbfchar\n<41> <4e59>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop end end\n");
            var content1 = Encoding.ASCII.GetBytes("BT /F1 12 Tf (A) Tj ET\n");
            var content2 = Encoding.ASCII.GetBytes("BT /F2 12 Tf (A) Tj ET\n");
            objects[1] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
            objects[2] = Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
            objects[3] = Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 9 0 R >>");
            objects[4] = Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F2 6 0 R >> >> /Contents 10 0 R >>");
            objects[5] = Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /X1 /Encoding /Identity-H /DescendantFonts [] /ToUnicode 7 0 R >>");
            objects[6] = Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /X2 /Encoding /Identity-H /DescendantFonts [] /ToUnicode 8 0 R >>");
            objects[7] = Stream(font1Cmap);
            objects[8] = Stream(font2Cmap);
            objects[9] = Stream(content1);
            objects[10] = Stream(content2);
            return Assemble(objects);
        }

        private static byte[] Assemble(System.Collections.Generic.Dictionary<int, byte[]> objects)
        {
            var pdf = new MemoryStream();
            var offsets = new System.Collections.Generic.Dictionary<int, long>();
            var latin1 = Encoding.GetEncoding(28591);
            pdf.Write(latin1.GetBytes("%PDF-1.4\n"), 0, 9);
            foreach (var number in objects.Keys.OrderBy(n => n))
            {
                offsets[number] = pdf.Position;
                var header = latin1.GetBytes(number + " 0 obj\n");
                pdf.Write(header, 0, header.Length);
                var body = objects[number];
                pdf.Write(body, 0, body.Length);
                var tail = latin1.GetBytes("\nendobj\n");
                pdf.Write(tail, 0, tail.Length);
            }
            var start = pdf.Position;
            var builder = new StringBuilder();
            builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var number in objects.Keys.OrderBy(n => n))
                builder.Append(offsets[number].ToString("0000000000")).Append(" 00000 n \n");
            builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
                .Append(start).Append("\n%%EOF");
            var tailBytes = latin1.GetBytes(builder.ToString());
            pdf.Write(tailBytes, 0, tailBytes.Length);
            return pdf.ToArray();
        }
    }
}
