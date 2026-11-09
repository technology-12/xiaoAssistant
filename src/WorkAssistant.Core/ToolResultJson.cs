using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    /// <summary>
    /// 工具结果 -> 返回给模型的 JSON 字符串。
    /// 金额一律以两位小数字符串表示，避免浮点误差进入模型上下文。
    /// </summary>
    public static class ToolResultJson
    {
        public static string Serialize(ToolResult result)
        {
            var root = new JObject { ["ok"] = result.Ok };
            root["data"] = SerializeData(result.Data);
            if (result.Error != null)
            {
                root["error"] = new JObject
                {
                    ["code"] = result.Error.Code,
                    ["message"] = result.Error.Message
                };
            }
            root["artifacts"] = SerializeArtifacts(result.Artifacts);
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject SerializeData(Dictionary<string, object?> data)
        {
            var o = new JObject();
            foreach (var kv in data)
            {
                o[kv.Key] = ToToken(kv.Value);
            }
            return o;
        }

        private static JToken ToToken(object? value)
        {
            return value switch
            {
                null => JValue.CreateNull(),
                JToken token => token,
                decimal d => new JValue(d.ToString("0.00", CultureInfo.InvariantCulture)),
                double d => new JValue(d.ToString("0.00", CultureInfo.InvariantCulture)),
                string s => new JValue(s),
                bool b => new JValue(b),
                int i => new JValue(i),
                long l => new JValue(l),
                IEnumerable<string> list => new JArray(list),
                _ => new JValue(value.ToString())
            };
        }

        private static JArray SerializeArtifacts(IList<ArtifactInfo> artifacts)
        {
            var arr = new JArray();
            foreach (var a in artifacts)
            {
                arr.Add(new JObject
                {
                    ["path"] = a.Path,
                    ["type"] = a.Type,
                    ["bytes"] = a.Bytes,
                    ["sha256"] = a.Sha256,
                    ["source_snapshot_sha256"] = a.SourceSnapshotSha256, ["tool"] = a.ToolName, ["tool_version"] = a.ToolVersion
                });
            }
            return arr;
        }
    }
}
