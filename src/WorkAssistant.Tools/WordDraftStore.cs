using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    internal static class WordDraftStore
    {
        public static (JArray Blocks, int Next, bool Final) Stage(WorkspacePolicy policy, string taskId, JObject args, string title, string fileName, JArray blocks)
        {
            string id = ToolArgs.RequireString(args, "draft_id");
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,40}$")) throw new ToolException(ErrorCodes.InvalidArguments, "draft_id 需为1~40位字母、数字、下划线或连字符。");
            int index = ToolArgs.OptionalIntInRange(args, "part_index", 0, 0, 2000);
            if (args["finalize"] != null && args["finalize"]!.Type != JTokenType.Boolean) throw new ToolException(ErrorCodes.InvalidArguments, "finalize 必须为布尔值。");
            bool final = (bool?)args["finalize"] ?? false;
            policy.ResolveTaskOutputPath(taskId, fileName);
            bool emptyFinal = final && blocks.Count == 0;
            if (!emptyFinal) WordAutomation.Validate(title, blocks); // Validate this batch before changing draft state.
            string path = policy.ResolveTaskOutputPath(taskId, ".word-draft-" + id + ".json");
            JObject record;
            if (File.Exists(path))
            {
                using var input = VerifiedFiles.OpenRead(path, Path.GetDirectoryName(path)!);
                if (input.Length > 16 * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "Word 草稿记录过大。");
                using var reader = new StreamReader(input); record = JObject.Parse(reader.ReadToEnd());
                if ((string?)record["task_id"] != taskId || (string?)record["title"] != title || (string?)record["file_name"] != fileName)
                    throw new ToolException(ErrorCodes.InvalidArguments, "同一 draft_id 的标题和文件名必须保持一致，请使用新的 draft_id 创建其他文档。");
            }
            else record = new JObject { ["task_id"] = taskId, ["title"] = title, ["file_name"] = fileName, ["parts"] = new JArray() };
            var parts = (JArray)record["parts"]!;
            if (index > parts.Count) throw new ToolException(ErrorCodes.InvalidArguments, "草稿批次不连续，下一批 part_index 应为 " + parts.Count + "。");
            if (index < parts.Count)
            {
                if (!JToken.DeepEquals(parts[index], blocks)) throw new ToolException(ErrorCodes.EditConflict, "该草稿批次已保存不同内容，拒绝覆盖；请使用新 draft_id。");
            }
            else if (!emptyFinal)
            {
                if (parts.Count >= 2000) throw new ToolException(ErrorCodes.LimitExceeded, "一个 Word 草稿最多2000批；已有草稿可用空 blocks 和 finalize=true 交付。");
                if ((bool?)record["finalized"] == true) throw new ToolException(ErrorCodes.InvalidArguments, "该草稿已经交付，请使用新 draft_id。");
                parts.Add(blocks.DeepClone());
            }
            var all = new JArray(parts.Cast<JArray>().SelectMany(part => part).Select(block => block.DeepClone()));
            var budget = OfficeDocumentBudget.Load(policy); WordAutomation.Validate(title, all, budget.MaxDocumentChars, budget.MaxBlocks); // Enforce document limits across all parts, not only this batch.
            if ((bool?)record["finalized"] == true && !final) throw new ToolException(ErrorCodes.InvalidArguments, "该草稿已经完成，不能改回未完成状态。");
            record["finalized"] = final; DurableFiles.WriteText(path, record.ToString());
            return (all, parts.Count, final);
        }
    }
}
