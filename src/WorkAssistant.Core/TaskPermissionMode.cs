using System;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    public enum TaskPermissionMode { RequestApproval = 0, Assisted = 1, FullAccess = 2 }

    /// <summary>Host-owned approval policy. It does not expand workspace, tools, or input-mutation rights.</summary>
    public static class TaskPermissions
    {
        public static void Validate(TaskPermissionMode mode)
        {
            if (!Enum.IsDefined(typeof(TaskPermissionMode), mode)) throw new ToolException(ErrorCodes.ToolPolicyDenied, "权限档位无效，未执行任务。");
        }
        public static TaskPermissionMode ParseStored(JToken? token) => token == null ? TaskPermissionMode.Assisted :
            token.Type == JTokenType.String && Enum.TryParse<TaskPermissionMode>((string?)token, out var mode) && Enum.IsDefined(typeof(TaskPermissionMode), mode) ? mode : TaskPermissionMode.RequestApproval;
        public static string Label(TaskPermissionMode mode) => mode switch {
            TaskPermissionMode.RequestApproval => "请求批准", TaskPermissionMode.Assisted => "帮我批准", TaskPermissionMode.FullAccess => "完全访问权限", _ => "未知权限" };
        public static string Description(TaskPermissionMode mode) => mode switch {
            TaskPermissionMode.RequestApproval => "生成成果、修改或移动原文件前，请你确认。读取和内部草稿自动进行。",
            TaskPermissionMode.Assisted => "自动生成成果；修改、移动原文件或回滚时，请你确认。",
            TaskPermissionMode.FullAccess => "自动批准当前工作区内已开放工具的文件操作；项目明确要求确认的规则仍生效。",
            _ => "权限无效，不能执行。" };
        public static bool WritesUserFiles(ToolDefinition definition, ToolCall call)
        {
            if (!definition.ProducesArtifacts && !definition.IsMutation) return false;
            try
            {
                var args = JObject.Parse(call.ArgumentsJson);
                if (call.Name == "document_workspace") return (string?)args["action"] == "finalize";
                if (call.Name == "write_word" && args["draft_id"] != null && (bool?)args["finalize"] != true) return false;
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is InvalidCastException || ex is FormatException) { return false; } // Tool validation reports the error; malformed calls cannot write.
            return true;
        }
        public static ToolDefinition Effective(ToolDefinition definition, ToolCall call, TaskPermissionMode mode)
        {
            Validate(mode);
            if (definition.RequiresApproval || mode != TaskPermissionMode.RequestApproval || !WritesUserFiles(definition, call)) return definition;
            return new ToolDefinition(definition.Name, definition.Description, (JObject)definition.Parameters.DeepClone(), definition.ProducesArtifacts, true) { PolicyRequiresApproval = definition.PolicyRequiresApproval };
        }
        public static bool CanAutoApprove(TaskPermissionMode mode, ToolDefinition definition)
        {
            Validate(mode);
            return mode == TaskPermissionMode.FullAccess && !definition.PolicyRequiresApproval;
        }
    }
}
