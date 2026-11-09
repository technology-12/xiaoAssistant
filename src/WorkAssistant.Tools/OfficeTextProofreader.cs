using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Tools
{
    // Adapted from darce07 docx-mit/pptx-mit spellcheck.py (MIT, Israel Mosquera, 2026).
    // Heuristic suspects only: never rewrite, certify factual correctness, or claim a rendered review.
    public static class OfficeTextProofreader
    {
        public static JArray Check(JObject[] items, int startIndex)
        {
            var findings = new JArray();
            for (int i = 0; i < items.Length && findings.Count < 40; i++)
            {
                string text = (string?)items[i]["text"] ?? "";
                void Add(string code, string message) { if (findings.Count < 40) findings.Add(new JObject { ["item_index"] = startIndex + i, ["code"] = code, ["message"] = message }); }
                if (text.Contains("�") || text.Contains("â€") || text.Contains("Ã©") || text.Contains("Ã±")) Add("ENCODING_SUSPECT", "疑似编码乱码，请核对原文；不要自动替换可能有意义的字符。");
                try
                {
                    if (XpRegex.IsMatch(text, @"\b([A-Za-z]{2,})\s+\1\b", RegexOptions.IgnoreCase)) Add("REPEATED_WORD", "疑似相邻重复英文词，需根据语境判断。");
                    if (XpRegex.IsMatch(text, @"\S +[,.;:!?，。；：！？]", RegexOptions.None)) Add("PUNCTUATION_SPACE", "标点前有空格，可能是排版问题；数值与引用等特殊语境需保留。");
                    if (XpRegex.IsMatch(text, @"\b(TODO|TBD)\b|待确认|待补充|\{\{[^{}]{1,64}\}\}", RegexOptions.IgnoreCase)) Add("REVIEW_PLACEHOLDER", "存在待确认信息或占位符，不得补造事实；交付时说明。");
                }
                catch (XpRegex.TimeoutException) { Add("CHECK_LIMIT", "本段启发式检查超时，仍需人工校读。"); }
            }
            return findings;
        }
    }
}
