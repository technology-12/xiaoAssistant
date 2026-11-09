using System.Text;

namespace WorkAssistant.Tools
{
    /// <summary>输出防护：CSV 公式注入与 Markdown 特殊字符转义。</summary>
    public static class OutputSanitizer
    {
        /// <summary>
        /// 对用户提供的文本单元格防公式注入：以 = + - @ 制表符或回车开头的值前置单引号。
        /// 金额单元格由代码生成，无需处理。
        /// </summary>
        public static string CsvCell(string value)
        {
            if (value.Length == 0) return value;
            var c = value[0];
            if (c == '=' || c == '+' || c == '-' || c == '@' || c == '\t' || c == '\r')
            {
                return "'" + value;
            }
            return value;
        }

        /// <summary>Markdown 表格单元格转义：竖线与换行。</summary>
        public static string MarkdownCell(string value)
        {
            return value.Replace("|", "\\|").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// 进入成果文档的纯文本：去控制字符（保留常规空白），
        /// 防止把不可见字符或伪造的域代码带进 Word/Excel。
        /// </summary>
        public static string PlainText(string value)
        {
            if (value.Length == 0) return value;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c == '\n' || c == '\t')
                {
                    sb.Append(' ');
                }
                else if (c < 0x20)
                {
                    // 其它控制字符丢弃。
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
