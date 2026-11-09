using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public static class ModelResponseLimits
    {
        public const int JsonBytes = 16 * 1024 * 1024;
        public const int StreamBytes = 64 * 1024 * 1024;
        public const int TextChars = 256 * 1024;
        public static void DemandText(int chars)
        {
            if (chars > TextChars) throw new ToolException(ErrorCodes.ModelResponseTooLarge, "模型正文超过 256K 字符，请分章节或分文件生成；本次不完整响应不会执行工具。");
        }
        public static void DemandArguments(int chars)
        {
            if (chars > AgentLoopGuard.MaxArgumentsChars) throw new ToolException(ErrorCodes.ModelResponseTooLarge, "单次工具参数超过 256K 字符，请分批生成文档。");
        }
    }
}
