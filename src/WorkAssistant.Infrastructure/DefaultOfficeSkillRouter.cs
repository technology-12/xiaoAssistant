using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WorkAssistant.Infrastructure
{
    public static class DefaultOfficeSkillRouter
    {
        public static string Select(string request, IEnumerable<string> fileNames)
        {
            if (Regex.IsMatch(request, @"(不要|不使用|禁用).{0,12}(skill|技能)|不加载技能", RegexOptions.IgnoreCase)) return "";
            bool ppt = Regex.IsMatch(request, @"(?<![A-Za-z])pptx?(?![A-Za-z])|\.pptx|幻灯片|演示文稿|课件", RegexOptions.IgnoreCase);
            bool word = Regex.IsMatch(request, @"(?<![A-Za-z])word(?![A-Za-z])|\.docx|新闻稿|教案", RegexOptions.IgnoreCase);
            // A request spanning both formats keeps its full tools; the route is method guidance only.
            if (ppt) return Regex.IsMatch(request, "审查|检查|审阅|校对|优化|review|audit", RegexOptions.IgnoreCase) ? "github-pptx-review" : "github-pptx";
            if (word) return "github-docx";
            if (request.Contains("文档"))
            {
                var names = fileNames.ToArray();
                bool hasWord = names.Any(n => n.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)), hasPpt = names.Any(n => n.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
                if (hasWord && !hasPpt) return "github-docx";
                if (hasPpt && !hasWord) return "github-pptx";
            }
            return "";
        }
    }
}
