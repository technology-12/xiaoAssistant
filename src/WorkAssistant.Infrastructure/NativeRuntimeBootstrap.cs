using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Infrastructure
{
    /// <summary>Use package-local native dependencies in this process, without registry/system changes.</summary>
    public static class NativeRuntimeBootstrap
    {
        private static readonly object Gate = new object();
        private static bool configured;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        public static void Configure()
        {
            lock (Gate)
            {
                if (configured) return;
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var manifestPath = Path.Combine(root, "native-runtime.json");
                if (!File.Exists(manifestPath)) return; // XP's static SQLite and legacy distributions do not need VC.
                var architecture = IntPtr.Size == 8 ? "x64" : "x86";
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                var files = manifest["files"] as JObject ?? throw new IOException("本地运行库清单缺失，请重新完整解压软件。");
                var count = 0;
                foreach (var item in files.Properties())
                {
                    if (!item.Name.StartsWith(architecture + "/", StringComparison.Ordinal)) continue;
                    var name = item.Name.Substring(architecture.Length + 1);
                    if (Path.GetFileName(name) != name) throw new IOException("本地运行库清单路径无效。");
                    var path = Path.Combine(root, architecture, name);
                    using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                    var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(hash, (string?)item.Value, StringComparison.Ordinal)) throw new IOException("本地运行库不完整或被更改：" + name);
                    count++;
                }
                if (count < 5) throw new IOException("本地运行库清单不完整。");
                var directory = Path.Combine(root, architecture);
                if (!SetDllDirectory(directory)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置本地运行库目录。");
                foreach (var name in new[] { "vcruntime140.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll" })
                {
                    // Keep modules loaded for the lifetime of the process. ALTERED_SEARCH_PATH
                    // resolves their transitive dependencies from this same architecture directory.
                    if (LoadLibraryEx(Path.Combine(directory, name), IntPtr.Zero, 8) == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "本地运行库加载失败：" + name);
                }
                configured = true;
            }
        }
    }
}
