using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Tests.Console
{
    /// <summary>极简断言与用例注册。</summary>
    public static class Test
    {
        public static readonly List<(string Name, Action Body)> Cases = new();
        public static int Passed;
        public static int Failed;

        public static void Add(string name, Action body) => Cases.Add((name, body));

        public static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("断言失败：" + what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    $"断言失败：{what}（期望 {expected}，实际 {actual}）");
            }
        }

        public static ToolException ExpectToolError(string code, Action action, string what)
        {
            try
            {
                action();
            }
            catch (ToolException ex)
            {
                Equal(code, ex.Code, what);
                return ex;
            }
            throw new InvalidOperationException("断言失败：" + what + "（未抛出 ToolException）");
        }

        public static TException ExpectThrowAsync<TException>(Func<System.Threading.Tasks.Task> action)
            where TException : Exception
        {
            try
            {
                action().GetAwaiter().GetResult();
            }
            catch (TException ex)
            {
                return ex;
            }
            throw new InvalidOperationException("断言失败：（未抛出 " + typeof(TException).Name + "）");
        }
    }

    /// <summary>无 UI 的任务运行管线（与 MainViewModel 相同的装配）。</summary>
    public static class DemoRunner
    {
        public static string CopySamplesToTemp()
        {
            var root = Path.Combine(Path.GetTempPath(), "wa-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            var samples = FindSamplesWorkspace();
            foreach (var file in Directory.GetFiles(Path.Combine(samples, "inputs")))
            {
                File.Copy(file, Path.Combine(root, "inputs", Path.GetFileName(file)));
            }
            return root;
        }

        public static string FindSamplesWorkspace()
        {
            // 从测试程序集位置向上查找 samples/workspace。
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "workspace");
                if (Directory.Exists(Path.Combine(candidate, "inputs"))) return candidate;
                dir = dir.Parent!;
            }
            throw new InvalidOperationException("找不到 samples/workspace");
        }

        public static (TaskRecord Record, LocalTaskStore Store, LocalTaskEventSink Sink) Run(
            string workspaceRoot, FakeBehavior behavior = FakeBehavior.StandardDemo,
            string userRequest = "汇总 inputs 文件夹中的销售 CSV，按部门统计销售额，生成汇总表和报告。",
            string storeRoot = null!, IApprovalProvider? approvals = null)
        {
            var storeRootPath = storeRoot ?? Path.Combine(Path.GetTempPath(),
                "wa-store-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var store = new LocalTaskStore(storeRootPath);
            var taskId = Guid.NewGuid().ToString("N");
            var record = new TaskRecord(taskId, userRequest, workspaceRoot);
            store.SaveTask(record);

            var policy = new WorkspacePolicy(workspaceRoot);
            var aggregations = new InMemoryAggregationStore();
            var filePlans = new InMemoryFilePlanStore();
            var sink = new LocalTaskEventSink(store, taskId);
            var tools = new IAgentTool[]
            {
                new ListFilesTool(policy), new PreviewCsvTool(policy), new PreviewTableTool(policy),
                new AggregateCsvTool(policy), new AggregateTableTool(policy),
                new ExportSummaryTool(policy), new ExportXlsxTool(policy),
                new GenerateDocxTool(policy), new FillDocxTemplateTool(policy),
                new PreviewDocxFieldsTool(policy), new ExtractPdfTextTool(policy),
                new PlanFileOrganizationTool(policy), new ApplyFilePlanTool(policy)
            };
            var dispatcher = new ToolDispatcher(tools, sink);
            ToolContext Factory(string tid, CancellationToken ct) =>
                new ToolContext(tid, policy, aggregations, ct, filePlans,
                    approvals ?? new AutoApproveProvider());
            var model = new FakeModelClient(behavior);
            var controller = new AgentController(model, dispatcher, sink, Factory, "fake-model");

            var finished = controller.RunAsync(record, CancellationToken.None).GetAwaiter().GetResult();
            store.SaveTask(finished);
            return (finished, store, sink);
        }

        public static string ReadArtifactText(string workspaceRoot, string relativePath)
        {
            var full = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.ReadAllText(full, new System.Text.UTF8Encoding(false));
        }
    }
}
