// 双线正则辅助：net48 使用原生 50ms 超时（超时按 CHECK_LIMIT 提示）；
// net40（XP 线由兼容层提供 XpRegex）无超时重载，模式为固定字面模式且输入有长度
// 上限（单条 ≤4000 字符），按无界执行并保留 TimeoutException 兼容类型。

using System;
using System.Text.RegularExpressions;

namespace WorkAssistant.Tools
{
    public static class XpRegex
    {
        public sealed class TimeoutException : Exception
        {
            public TimeoutException() { }
            public TimeoutException(string message) : base(message) { }
        }

        public static bool IsMatch(string input, string pattern, RegexOptions options) =>
            Regex.IsMatch(input, pattern, options, TimeSpan.FromMilliseconds(50));
    }
}
