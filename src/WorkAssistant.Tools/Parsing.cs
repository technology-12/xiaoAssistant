using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 金额解析（设计 §9.1）：invariant、最多两位小数、允许负数；
    /// 拒绝货币符号、千位分隔、加号、空白、非数字与超出两位小数。
    /// </summary>
    public static class AmountParser
    {
        /// <summary>解析失败返回 null（由调用方组装带路径与行号的错误）。</summary>
        public static decimal? TryParse(string? raw)
        {
            if (raw == null) return null;
            var s = raw.Trim();
            if (s.Length == 0) return null;
            if (s[0] == '+') return null;

            var negative = false;
            var body = s;
            if (body[0] == '-')
            {
                negative = true;
                body = body.Substring(1);
            }
            if (body.Length == 0) return null;

            var dot = body.IndexOf('.');
            string intPart, fracPart;
            if (dot < 0)
            {
                intPart = body;
                fracPart = "";
            }
            else
            {
                intPart = body.Substring(0, dot);
                fracPart = body.Substring(dot + 1);
                if (fracPart.Length > 2) return null;
            }
            if (intPart.Length == 0 && fracPart.Length == 0) return null;
            foreach (var c in intPart)
            {
                if (c < '0' || c > '9') return null;
            }
            foreach (var c in fracPart)
            {
                if (c < '0' || c > '9') return null;
            }
            if (intPart.Length == 0) intPart = "0";

            // decimal.Parse 对超出 decimal 范围的值会抛 OverflowException，按无效金额处理。
            try
            {
                var value = decimal.Parse(intPart + "." + (fracPart.Length == 0 ? "0" : fracPart),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                return negative ? -value : value;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        public static string Format(decimal value) =>
            value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>工具参数 JSON 的通用读取：拒绝未知参数（设计 §9）。</summary>
    public static class ToolArgs
    {
        public static JObject Parse(string argumentsJson, string toolName = "")
        {
            if (argumentsJson.Length > AgentLoopGuard.MaxArgumentsChars) throw new ToolException(ErrorCodes.LimitExceeded, "工具参数超过 256K 字符，请分批提交。");
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "参数不能为空。");
            }
            try
            {
                using var text = new System.IO.StringReader(argumentsJson);
                using var reader = new Newtonsoft.Json.JsonTextReader(text) { MaxDepth = 64, DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                var obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new Newtonsoft.Json.JsonReaderException("Trailing JSON", string.Empty, reader.LineNumber, reader.LinePosition, null);
                if (obj.Type != JTokenType.Object)
                {
                    throw new ToolException(ErrorCodes.InvalidArguments, "参数必须是 JSON 对象。");
                }
                return obj;
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Newtonsoft.Json.JsonReaderException ex)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "参数 JSON 结构无效（第 " + ex.LineNumber + " 行，第 " + ex.LinePosition + " 列）。根对象后不能有多余内容，数组、括号与字符串必须完整配对；请重发完整 JSON，不要拼接半截参数。" +
                    (toolName == "write_word" ? "长文档请使用 draft_id、part_index、finalize 分批提交，每批建议不超过12个块。" : ""));
            }
        }

        public static void RejectUnknown(JObject args, params string[] allowed)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var prop in args.Properties())
            {
                if (!allowedSet.Contains(prop.Name))
                {
                    throw new ToolException(ErrorCodes.InvalidArguments,
                        $"未知参数：{prop.Name}；允许的参数：{string.Join(", ", allowed)}。");
                }
            }
        }

        public static string RequireString(JObject args, string name)
        {
            if (args.TryGetValue(name, out var token) && token.Type == JTokenType.String)
            {
                var value = (string)token!;
                if (value.Length > 0) return value;
            }
            throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是非空字符串。");
        }

        public static List<string> RequireStringArray(JObject args, string name, int maxItems)
        {
            if (!args.TryGetValue(name, out var token) || token.Type != JTokenType.Array)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是字符串数组。");
            }
            var result = new List<string>();
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.String)
                {
                    throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 的元素必须是字符串。");
                }
                result.Add((string)item!);
            }
            if (result.Count == 0)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 不能为空数组。");
            }
            if (result.Count > maxItems)
            {
                throw new ToolException(ErrorCodes.LimitExceeded, $"参数 {name} 最多 {maxItems} 项。");
            }
            return result;
        }

        public static int RequireIntInRange(JObject args, string name, int min, int max)
        {
            if (!args.TryGetValue(name, out var token) || token.Type != JTokenType.Integer)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是整数。");
            }
            var value = (int)token;
            if (value < min || value > max)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    $"参数 {name} 超出范围 [{min}, {max}]。");
            }
            return value;
        }

        /// <summary>可选整数；缺省返回 fallback，越界报错。</summary>
        public static int OptionalIntInRange(JObject args, string name,
            int fallback, int min, int max)
        {
            if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (token.Type != JTokenType.Integer)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是整数。");
            }
            var value = (int)token;
            if (value < min || value > max)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    $"参数 {name} 超出范围 [{min}, {max}]。");
            }
            return value;
        }

        /// <summary>可选字符串；缺省返回 null（区别于空字符串）。</summary>
        public static string? OptionalString(JObject args, string name, int maxLength = 200)
        {
            if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是字符串。");
            }
            var value = (string)token!;
            if (value.Length > maxLength)
            {
                throw new ToolException(ErrorCodes.LimitExceeded,
                    $"参数 {name} 超过 {maxLength} 字符限制。");
            }
            return value;
        }

        /// <summary>可选字符串数组；缺省返回 null。</summary>
        public static List<string>? OptionalStringArray(JObject args, string name, int maxItems)
        {
            if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.Array)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是字符串数组。");
            }
            var result = new List<string>();
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.String)
                {
                    throw new ToolException(ErrorCodes.InvalidArguments,
                        $"参数 {name} 的元素必须是字符串。");
                }
                result.Add((string)item!);
            }
            if (result.Count > maxItems)
            {
                throw new ToolException(ErrorCodes.LimitExceeded, $"参数 {name} 最多 {maxItems} 项。");
            }
            return result;
        }

        /// <summary>可选对象参数（如字段映射）；缺省返回 null。</summary>
        public static JObject? OptionalObject(JObject args, string name, int maxProperties)
        {
            if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.Object)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, $"参数 {name} 必须是对象。");
            }
            var obj = (JObject)token;
            if (obj.Count > maxProperties)
            {
                throw new ToolException(ErrorCodes.LimitExceeded,
                    $"参数 {name} 最多 {maxProperties} 个键。");
            }
            return obj;
        }
    }
}
