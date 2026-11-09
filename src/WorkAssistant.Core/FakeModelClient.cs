using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    /// <summary>Fake 模型行为脚本。</summary>
    public enum FakeBehavior
    {
        /// <summary>自动路由：按用户请求关键词选择演示脚本（默认演示入口）。</summary>
        Auto,
        /// <summary>标准 CSV 演示：list_files → aggregate_csv → export_summary → 总结文本。</summary>
        StandardDemo,
        /// <summary>XLSX 演示：list_files(xlsx) → aggregate_table → export_xlsx。</summary>
        XlsxDemo,
        /// <summary>DOCX 演示：aggregate_table → generate_docx。</summary>
        DocxReport,
        /// <summary>PDF 演示：list_files(pdf) → extract_pdf_text → 总结。</summary>
        PdfExtract,
        /// <summary>文件整理演示：list_files → plan_file_organization → apply_file_plan（经审批门）。</summary>
        OrganizePlan,
        /// <summary>追问演示：对已有输入按“只保留 X”筛选重新汇总（连续对话 §5.4）。</summary>
        FollowUpFilter,
        /// <summary>纯问答：直接文本回答，不调用工具（问答不强制生成文件）。</summary>
        ChatAnswer,
        /// <summary>返回非法 JSON 参数，触发参数校验失败。</summary>
        BadJson,
        /// <summary>调用不存在的工具。</summary>
        UnknownTool,
        /// <summary>无限重复调用 list_files，触发预算上限。</summary>
        LoopForever,
        /// <summary>直接输出文本、不产生任何成果（文件类任务 → NO_ARTIFACTS）。</summary>
        NoArtifacts,
        /// <summary>连续两次给出违反业务约束的参数。</summary>
        ConsecutiveParamErrors
    }

    /// <summary>
    /// 确定性 Fake 模型：按脚本推进工具调用闭环，不访问网络。
    /// 用于无 Key 开发、演示与失败路径测试；同时实现流式接口以验证 UI 增量渲染。
    /// </summary>
    public sealed class FakeModelClient : IStreamingModelClient
    {
        private readonly FakeBehavior _behavior;
        public int RequestCount { get; private set; }

        public FakeModelClient(FakeBehavior behavior = FakeBehavior.StandardDemo)
        {
            _behavior = behavior;
        }

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var reply = BuildReply(request);
            // 模拟网络延迟，验证 UI 不被阻塞。
            return XpTask.Delay(120, cancellationToken).ContinueWith(
                _ => reply, TaskScheduler.Default);
        }

        public Task<ModelReply> CompleteStreamAsync(ModelRequest request,
            Action<string>? onTextDelta, CancellationToken cancellationToken)
        {
            RequestCount++;
            var reply = BuildReply(request);
            if (onTextDelta != null && !reply.HasToolCalls && !string.IsNullOrEmpty(reply.Content))
            {
                // 将文本拆成 3 段模拟流式增量。
                var text = reply.Content!;
                var cuts = new[] { text.Length / 3, text.Length * 2 / 3 };
                var parts = new[]
                {
                    text.Substring(0, cuts[0]),
                    text.Substring(cuts[0], cuts[1] - cuts[0]),
                    text.Substring(cuts[1])
                };
                foreach (var part in parts.Where(p => p.Length > 0))
                {
                    onTextDelta(part);
                }
            }
            return XpTask.Delay(60, cancellationToken).ContinueWith(
                _ => reply, TaskScheduler.Default);
        }

        private ModelReply BuildReply(ModelRequest request)
        {
            if (request.Tools.Any(t => t.Name == "draft_skill") && request.Tools.All(t => new[] {
                "draft_skill", "list_files", "ask_user", "update_task_plan", "read_task_history", "list_skills", "read_skill"
            }.Contains(t.Name)))
            {
                if (request.Messages.Reverse().TakeWhile(m => m.Role != "user").Any(m => m.Role == "tool")) return new ModelReply("演示技能草稿已生成。可在技能库导入 .skill.json，审阅后保存。在线模式可按你的具体需求创建技能。", null, "stop");
                return ToolCallReply("call_skill_demo", "draft_skill", new JObject {
                    ["id"] = "my-office-workflow", ["name"] = "我的办公工作流（演示）", ["description"] = "演示如何把办公方法保存为技能。",
                    ["instructions"] = "先确认用户的输入和预期交付。不明确时询问业务口径；用已授权的内置工具完成任务，说明实际结果与限制。",
                    ["tools"] = new JArray("list_files", "ask_user") }.ToString());
            }

            switch (_behavior)
            {
                case FakeBehavior.BadJson:
                    return ToolCallReply("call_bad_" + RequestCount, "list_files", "{oops" + RequestCount);
                case FakeBehavior.UnknownTool:
                    return ToolCallReply("call_unknown", "delete_everything", "{\"directory\":\"inputs\"}");
                case FakeBehavior.LoopForever:
                    return ToolCallReply("call_loop_" + RequestCount, "list_files", "{\"directory\":\"inputs\"}");
                case FakeBehavior.NoArtifacts:
                    return new ModelReply("我已经完成了汇总，请查收。", null, "stop");
                case FakeBehavior.ConsecutiveParamErrors:
                    return ToolCallReply("call_p" + RequestCount, "preview_csv",
                        "{\"path\":\"inputs/sales-a.csv\",\"max_rows\":999}");
            }

            var behavior = _behavior == FakeBehavior.Auto ? Route(request) : _behavior;
            var lastTool = LastToolResult(request);
            var request2 = request; // 闭包可读性
            string LastUserText()
            {
                for (var i = request2.Messages.Count - 1; i >= 0; i--)
                {
                    if (request2.Messages[i].Role == "user") return request2.Messages[i].Content ?? "";
                }
                return "";
            }

            // ---- 纯问答：任何阶段直接文本结束 ----
            if (behavior == FakeBehavior.ChatAnswer)
            {
                return new ModelReply(
                    "演示模式提供示例工作流。你可以在技能工作室选择表格整理、文档模板、PDF 提取、文件归档，或使用技能创建器生成示例草稿。", null, "stop");
            }

            // ---- 各演示脚本：依据最近一条工具结果推进 ----
            if (lastTool == null)
            {
                switch (behavior)
                {
                    case FakeBehavior.XlsxDemo:
                        return ToolCallReply("call_x1", "list_files",
                            "{\"directory\":\"inputs\",\"extensions\":[\"xlsx\"]}");
                    case FakeBehavior.PdfExtract:
                        return ToolCallReply("call_f1", "list_files",
                            "{\"directory\":\"inputs\",\"extensions\":[\"pdf\"]}");
                    case FakeBehavior.OrganizePlan:
                        return ToolCallReply("call_o1", "list_files", "{\"directory\":\"inputs\"}");
                    case FakeBehavior.DocxReport:
                        return ToolCallReply("call_d1", "list_files",
                            "{\"directory\":\"inputs\",\"extensions\":[\"csv\"]}");
                    case FakeBehavior.FollowUpFilter:
                        return ToolCallReply("call_fu1", "list_files", "{\"directory\":\"inputs\"}");
                    default:
                        return ToolCallReply("call_1", "list_files", "{\"directory\":\"inputs\"}");
                }
            }

            switch (behavior)
            {
                case FakeBehavior.XlsxDemo:
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        if (files.Count == 0)
                        {
                            return new ModelReply("inputs 目录中没有 XLSX 文件，无法汇总。", null, "stop");
                        }
                        return ToolCallReply("call_x2", "aggregate_table", AggregateArgs(files,
                            FollowUpDepartment(LastUserText())));
                    }
                    if (lastTool.Value.Name == "aggregate_table")
                    {
                        return Export("call_x3", "export_xlsx", lastTool, "summary.xlsx");
                    }
                    if (lastTool.Value.Name == "export_xlsx")
                    {
                        var total = (string?)lastTool?.Body["data"]?["total"];
                        return new ModelReply(
                            $"已生成 Excel 汇总表 summary.xlsx，合计 {total ?? "?"} 元，" +
                            "可通过下方文件卡片打开。", null, "stop");
                    }
                    break;

                case FakeBehavior.DocxReport:
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        if (files.Count == 0)
                        {
                            return new ModelReply("inputs 目录中没有 CSV 文件，无法生成报告。", null, "stop");
                        }
                        return ToolCallReply("call_d2", "aggregate_table", AggregateArgs(files, null));
                    }
                    if (lastTool.Value.Name == "aggregate_table")
                    {
                        var id = (string?)lastTool.Value.Body["data"]?["aggregation_id"];
                        return ToolCallReply("call_d3", "generate_docx",
                            new JObject { ["aggregation_id"] = id, ["title"] = "销售数据汇总报告" }
                                .ToString(Newtonsoft.Json.Formatting.None));
                    }
                    if (lastTool.Value.Name == "generate_docx")
                    {
                        var total = (string?)lastTool?.Body["data"]?["total"];
                        return new ModelReply(
                            $"已生成 Word 汇总报告 report.docx（合计 {total ?? "?"} 元），" +
                            "包含数据口径、分组汇总表与校验说明。", null, "stop");
                    }
                    break;

                case FakeBehavior.PdfExtract:
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        if (files.Count == 0)
                        {
                            return new ModelReply("inputs 目录中没有 PDF 文件。", null, "stop");
                        }
                        return ToolCallReply("call_f2", "extract_pdf_text",
                            new JObject { ["path"] = files[0], ["max_pages"] = 10 }
                                .ToString(Newtonsoft.Json.Formatting.None));
                    }
                    if (lastTool.Value.Name == "extract_pdf_text")
                    {
                        var chars = (int?)lastTool.Value.Body["data"]?["total_chars"];
                        var pages = (int?)lastTool.Value.Body["data"]?["page_count"];
                        return new ModelReply(
                            $"已提取 PDF 文本：共 {pages ?? 0} 页、{chars ?? 0} 个字符。" +
                            "内容摘要与页码引用已登记为文本成果，可打开查看。", null, "stop");
                    }
                    break;

                case FakeBehavior.OrganizePlan:
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        if (files.Count == 0)
                        {
                            return new ModelReply("inputs 目录中没有文件可整理。", null, "stop");
                        }
                        var ops = new JArray();
                        foreach (var token in files)
                        {
                            var f = (string)token!;
                            var name = (string?)(lastTool.Value.Body["data"]?["file_details"] as JArray)?.OfType<JObject>()
                                .FirstOrDefault(item => (string?)item["path"] == f)?["name"] ?? f.Substring(f.LastIndexOf('/') + 1);
                            ops.Add(new JObject
                            {
                                ["source"] = f,
                                ["action"] = "move",
                                ["target_directory"] = "inputs/已归档",
                                ["new_name"] = name
                            });
                        }
                        return ToolCallReply("call_o2", "plan_file_organization",
                            new JObject { ["operations"] = ops, ["reason"] = "按类型归档演示文件" }
                                .ToString(Newtonsoft.Json.Formatting.None));
                    }
                    if (lastTool.Value.Name == "plan_file_organization")
                    {
                        if (SystemPrompts.IsPlanOnly(request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? ""))
                            return new ModelReply("整理计划已生成，未移动或改名任何文件。", null, "stop");
                        var id = (string?)lastTool.Value.Body["data"]?["plan_id"];
                        return ToolCallReply("call_o3", "apply_file_plan",
                            new JObject { ["plan_id"] = id }
                                .ToString(Newtonsoft.Json.Formatting.None));
                    }
                    if (lastTool.Value.Name == "apply_file_plan")
                    {
                        var moved = (int?)lastTool.Value.Body["data"]?["moved"];
                        return new ModelReply(
                            $"文件整理完成：已移动 {moved ?? 0} 个文件到 inputs/已归档/，" +
                            "没有删除任何文件；变更清单见成果 organize_plan.md。", null, "stop");
                    }
                    break;

                case FakeBehavior.FollowUpFilter:
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        var dept = FollowUpDepartment(LastUserText()) ?? "销售一部";
                        return ToolCallReply("call_fu2", "aggregate_table",
                            AggregateArgs(files, dept));
                    }
                    if (lastTool.Value.Name == "aggregate_table")
                    {
                        return Export("call_fu3", "export_summary", lastTool, null);
                    }
                    if (lastTool.Value.Name == "export_summary")
                    {
                        var total = (string?)lastTool?.Body["data"]?["total"];
                        var records = (int?)lastTool?.Body["data"]?["record_count"];
                        return new ModelReply(
                            $"按新口径重新汇总完成：{records ?? 0} 条记录，合计 {total ?? "?"} 元。" +
                            "成果写入新的输出目录，此前版本未覆盖。", null, "stop");
                    }
                    break;

                default: // StandardDemo
                    if (lastTool.Value.Name == "list_files")
                    {
                        var files = FileArrayOf(lastTool);
                        if (files.Count == 0)
                        {
                            return new ModelReply("inputs 目录中没有 CSV 文件，无法汇总。", null, "stop");
                        }
                        return ToolCallReply("call_2", "aggregate_csv",
                            AggregateArgs(files, null));
                    }
                    if (lastTool.Value.Name == "aggregate_csv")
                    {
                        return Export("call_3", "export_summary", lastTool, null);
                    }
                    if (lastTool.Value.Name == "export_summary")
                    {
                        var total = (string?)lastTool.Value.Body["data"]?["total"];
                        var records = (int?)lastTool.Value.Body["data"]?["record_count"];
                        var groups = (int?)lastTool.Value.Body["data"]?["group_count"];
                        var filtered = (int?)lastTool.Value.Body["data"]?["filtered_out_count"] ?? 0;
                        var text = $"已完成汇总：共 {records ?? 0} 条记录、{groups ?? 0} 个部门，合计 {total ?? "?"} 元。" +
                                   (filtered > 0 ? $"（按筛选口径排除 {filtered} 条记录）" : "") +
                                   "成果为 summary.csv 与 report.md，可通过下方文件卡片查看。";
                        return new ModelReply(text, null, "stop");
                    }
                    break;
            }

            return new ModelReply("没有可执行的下一步。", null, "stop");
        }

        // ---- 脚本辅助 -------------------------------------------------------

        private static ModelReply Export(string callId, string tool, (string Name, JObject Body)? lastTool,
            string? fileName)
        {
            var id = (string?)lastTool?.Body["data"]?["aggregation_id"];
            var args = new JObject { ["aggregation_id"] = id };
            if (fileName != null) args["file_name"] = fileName;
            return ToolCallReply(callId, tool, args.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JArray FileArrayOf((string Name, JObject Body)? lastTool) =>
            lastTool?.Body["data"]?["files"] as JArray ?? new JArray();

        private static string AggregateArgs(JArray files, string? filterDepartment)
        {
            var args = new JObject
            {
                ["paths"] = files,
                ["group_by"] = "部门",
                ["sum_column"] = "销售额"
            };
            if (filterDepartment != null)
            {
                args["where_column"] = "部门";
                args["where_values"] = new JArray(filterDepartment);
            }
            return args.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>从“只保留销售一部”这类请求中提取筛选值（演示脚本用）。</summary>
        private static string? FollowUpDepartment(string request)
        {
            var marker = "只保留";
            var index = request.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return null;
            var rest = request.Substring(index + marker.Length).TrimStart();
            var end = rest.Length;
            foreach (var stop in new[] { "，", ",", "。", "重新", "再", " ", "的" })
            {
                var p = rest.IndexOf(stop, StringComparison.Ordinal);
                if (p >= 0 && p < end) end = p;
            }
            var value = rest.Substring(0, end).Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>自动路由：按最近一条用户消息关键词选择演示脚本。</summary>
        private static FakeBehavior Route(ModelRequest request)
        {
            string? text = null;
            for (var i = request.Messages.Count - 1; i >= 0; i--)
            {
                if (request.Messages[i].Role == "user")
                {
                    text = request.Messages[i].Content;
                    break;
                }
            }
            text ??= "";
            if (text.IndexOf("只保留", StringComparison.Ordinal) >= 0) return FakeBehavior.FollowUpFilter;
            if (text.IndexOf("整理", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("归档", StringComparison.Ordinal) >= 0) return FakeBehavior.OrganizePlan;
            if (text.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("PDF", StringComparison.Ordinal) >= 0) return FakeBehavior.PdfExtract;
            // Word 场景需要显式 word/docx 关键词（“汇总…生成报告”仍走 CSV 管线）。
            if (text.IndexOf("word", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("docx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Word", StringComparison.Ordinal) >= 0) return FakeBehavior.DocxReport;
            if (text.IndexOf("xlsx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("excel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Excel", StringComparison.Ordinal) >= 0) return FakeBehavior.XlsxDemo;
            if (text.IndexOf("汇总", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("统计", StringComparison.Ordinal) >= 0) return FakeBehavior.StandardDemo;
            return FakeBehavior.ChatAnswer;
        }

        /// <summary>取最近一条工具结果消息，并借助历史 assistant 消息解析其工具名。</summary>
        private static (string Name, JObject Body)? LastToolResult(ModelRequest request)
        {
            for (var i = request.Messages.Count - 1; i >= 0; i--)
            {
                var m = request.Messages[i];
                if (m.Role == "user") break; // Prior turns are context, not the current script's execution state.
                if (m.Role != "tool" || m.Content == null || m.ToolCallId == null) continue;
                JObject body;
                try
                {
                    body = JObject.Parse(m.Content);
                }
                catch
                {
                    return null;
                }
                var name = ResolveToolName(request, m.ToolCallId);
                return name == null ? null : (name, body);
            }
            return null;
        }

        private static string? ResolveToolName(ModelRequest request, string toolCallId)
        {
            for (var i = request.Messages.Count - 1; i >= 0; i--)
            {
                if (request.Messages[i].ToolCalls is not { } calls) continue;
                foreach (var c in calls)
                {
                    if (c.Id == toolCallId) return c.Name;
                }
            }
            return null;
        }

        private static ModelReply ToolCallReply(string id, string name, string argsJson)
        {
            var call = new ToolCall(id, name, argsJson);
            return new ModelReply(null, new List<ToolCall> { call }, "tool_calls");
        }
    }
}
