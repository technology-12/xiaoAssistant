namespace WorkAssistant.Core
{
    public sealed class ModelOutputTruncatedException : ToolException
    {
        public ModelUsage? Usage { get; }
        public string PartialContent { get; }
        public string InputExcerpt { get; }
        public string? ReasoningContent { get; }
        public ModelOutputTruncatedException(ModelUsage? usage) : this(usage, null, null, null) { }
        public ModelOutputTruncatedException(ModelUsage? usage = null, string? partialContent = null, string? inputExcerpt = null, string? reasoningContent = null)
            : base(ErrorCodes.ModelOutputTruncated, "模型达到输出额度，响应被截断；本响应工具未执行。已保存的草稿可恢复。")
        { Usage = usage; PartialContent = partialContent ?? ""; InputExcerpt = inputExcerpt == null ? "" : inputExcerpt.Substring(0, System.Math.Min(2048, inputExcerpt.Length)); ReasoningContent = reasoningContent; }
    }
}
