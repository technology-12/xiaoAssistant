// XpRegex 的 net40 实现：无带超时重载（.NET 4.5 才有）；模式为固定字面模式且
// 输入长度受限，按无界执行。TimeoutException 类型保留以维持共享源码形态。

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
            Regex.IsMatch(input, pattern, options);
    }
}
