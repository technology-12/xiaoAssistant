// XP 变体（替换 tests/WorkAssistant.Tests.Console/Program.cs）：追加 XP 专属回归用例注册。
using System;
using System.Diagnostics;
using System.IO;

namespace WorkAssistant.Tests.Console
{
    /// <summary>
    /// 控制台测试跑批（验收 14.1 + 正式版 W02/W05/W06 用例）。无测试框架依赖，
    /// 直接输出 PASS/FAIL，返回码 0 = 全部通过。
    /// 另支持 --write-samples &lt;dir&gt; 生成演示样例（xlsx/pdf）。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--xp-edit-crash-worker") return XpCompatTests.RunEditCrashWorker(args[1], args[2]);
            // 样例生成模式：dogfood 自身写出器。
            var sampleIndex = Array.IndexOf(args, "--write-samples");
            if (sampleIndex >= 0 && args.Length > sampleIndex + 1)
            {
                SampleWriter.WriteAll(Path.GetFullPath(args[sampleIndex + 1]));
                System.Console.WriteLine("样例已写入 " + Path.GetFullPath(args[sampleIndex + 1]));
                return 0;
            }

            var filter = args.Length > 0 ? args[0] : "";
            System.Console.OutputEncoding = System.Text.Encoding.UTF8;
            System.Console.WriteLine("== Win7WorkAssistant 测试跑批 ==");

            PermissionTests.Register();
            DocumentWorkflowTests.Register();
            CsvToolTests.Register();
            PathPolicyTests.Register();
            AgentFlowTests.Register();
            HttpModelClientTests.Register();
            OfficeFileTests.Register();
            ConversationTests.Register();
            StreamingTests.Register();
            XpCompatTests.Register(); // XP 专属回归（审查 R02—R07）
            Test.Add("持久化：接近 MAX_PATH 的目标名可更新并保留备份", DurableLongTarget);

            var failed = new System.Collections.Generic.List<string>();
            foreach (var (name, body) in Test.Cases)
            {
                if (filter.Length > 0 &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    body();
                    sw.Stop();
                    System.Console.WriteLine($"[PASS] {name}（{sw.ElapsedMilliseconds} ms）");
                    Test.Passed++;
                    TestEvidence.Record(name, true, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    System.Console.WriteLine($"[FAIL] {name}");
                    System.Console.WriteLine("       " + ex.GetType().Name + "：" + ex.Message);
                    if (ex.InnerException != null)
                    {
                        System.Console.WriteLine("       内部：" + ex.InnerException.Message);
                    }
                    failed.Add(name);
                    Test.Failed++;
                    TestEvidence.Record(name, false, sw.ElapsedMilliseconds);
                }
            }

            System.Console.WriteLine();
            System.Console.WriteLine($"通过 {Test.Passed}，失败 {Test.Failed}。");
            TestEvidence.Save();
            return Test.Failed == 0 ? 0 : 1;
        }
        internal static void DurableLongTarget()
        {
            var directory = Path.Combine(Path.GetTempPath(), "durable-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            while (directory.Length < 180) directory = Path.Combine(directory, "nested-1234567890");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, new string('a', 245 - directory.Length - 1 - 5) + ".json");
            Test.Equal(245, path.Length, "目标路径长度固定覆盖旧临时文件超限场景");
            WorkAssistant.Core.DurableFiles.WriteText(path, "before");
            WorkAssistant.Core.DurableFiles.WriteText(path, "after");
            Test.Equal("after", File.ReadAllText(path), "更新成功");
            Test.Equal("before", File.ReadAllText(path + ".bak"), "更新前版本保留");
            Test.Equal(0, Directory.GetFiles(directory, ".xiao-*.tmp").Length, "没有残留临时文件");
        }
    }
}
