// XP 变体（替换 src/WorkAssistant.Infrastructure/EnvironmentReport.cs）：组件清单按 XP 交付调整。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Infrastructure
{
    public static class EnvironmentReport
    {
        public static JObject Capture(LocalTaskStore store)
        {
            object? release = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);
            var directory = AppDomain.CurrentDomain.BaseDirectory;
            var tasks = store.LoadTasks();
            return new JObject {
                ["generated_utc"] = DateTime.UtcNow, ["application_version"] = typeof(EnvironmentReport).Assembly.GetName().Version!.ToString(),
                ["os_version"] = Environment.OSVersion.Version.ToString(), ["service_pack"] = Environment.OSVersion.ServicePack,
                ["os_64bit"] = Environment.Is64BitOperatingSystem, ["process_64bit"] = Environment.Is64BitProcess,
                ["net_framework_release"] = release == null ? JValue.CreateNull() : JToken.FromObject(release),
                ["net48_installed"] = release != null && Convert.ToInt32(release) >= 528040,
                ["state_schema"] = 1, ["ipc_protocol"] = HostChannel.Version,
                ["publisher_configured"] = TrustedUpdates.PublisherCertificateSha256.Length == 64,
                ["components"] = new JArray(new[] { "WorkAssistant.Host.exe", "System.Data.SQLite.dll", "x86/SQLite.Interop.dll", "x64/SQLite.Interop.dll", "WorkAssistant.Compatibility.Xp.dll" }
                    .Select(name => new JObject { ["name"] = name, ["present"] = File.Exists(Path.Combine(directory, name)) })),
                ["task_states"] = JObject.FromObject(tasks.GroupBy(t => t.State.ToString()).ToDictionary(g => g.Key, g => g.Count())),
                ["error_codes"] = JObject.FromObject(tasks.Where(t => !string.IsNullOrWhiteSpace(t.ErrorCode)).GroupBy(t => t.ErrorCode!).ToDictionary(g => g.Key, g => g.Count())),
                ["xp_variant"] = true, ["excluded"] = "不含凭据、模型地址、请求正文、工具参数、文件内容、输入路径与输入摘要。兼容性仅为环境信息，尚未代表真机验收。"
            };
        }
    }
}
