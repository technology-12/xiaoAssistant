using System;
using System.IO;

namespace WorkAssistant.Infrastructure
{
    /// <summary>便携发行版使用自己的用户数据，不自动读取本机已安装版本的历史或凭据。</summary>
    public static class ApplicationDataPaths
    {
        public static bool IsPortable => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.mode"));
        public static string PortableDirectory => IsPortable && Array.IndexOf(new[] { "parent-v1", "parent-v1-xp" }, File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable.mode")).Trim()) >= 0
            ? Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'))!.FullName : AppDomain.CurrentDomain.BaseDirectory;
        public static string Root => IsPortable
            ? Path.Combine(PortableDirectory, "用户数据")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win7WorkAssistant");

        public static string DefaultWorkspace(string settingsRoot)
        {
            // Explicitly injected stores (tests/QA) must never create a real user's workspace.
            if (!string.Equals(Path.GetFullPath(settingsRoot).TrimEnd('\\'), Path.GetFullPath(Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return Path.Combine(settingsRoot, "工作区");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "晓助手工作区");
        }
    }
}
