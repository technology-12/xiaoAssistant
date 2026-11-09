using System;

namespace WorkAssistant.Core
{
    public enum AgentExecutionMode { Execute, ReadOnly }

    /// <summary>One policy for model tool exposure and actual dispatch. Never inferred from model text.</summary>
    public sealed class AgentExecutionPolicy
    {
        public AgentExecutionMode Mode { get; }
        public bool AllowInputMutation { get; }
        public AgentExecutionPolicy(AgentExecutionMode mode = AgentExecutionMode.Execute, bool allowInputMutation = true)
        {
            if (!Enum.IsDefined(typeof(AgentExecutionMode), mode))
                throw new ToolException(ErrorCodes.InvalidArguments, "未知执行模式。");
            Mode = mode;
            AllowInputMutation = allowInputMutation;
        }

        public bool Allows(ToolDefinition definition) =>
            (AllowInputMutation || !definition.IsMutation) &&
            (Mode != AgentExecutionMode.ReadOnly ||
                !definition.ProducesArtifacts && !definition.RequiresApproval && !definition.IsMutation);

        public void Demand(ToolDefinition definition)
        {
            if (!Allows(definition))
                throw new ToolException(ErrorCodes.ToolPolicyDenied, "当前执行模式不允许调用：" + definition.Name + "。请结束本轮并切换模式后重新提交。");
        }
    }
}
