using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WorkAssistant.Launcher
{
    internal static class Program
    {
        [STAThread] static int Main(string[] args)
        {
            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "程序文件");
                string executable = Path.Combine(folder, "WorkAssistant.Desktop.exe");
                if (!File.Exists(executable)) throw new IOException("程序文件不完整，请将整个 ZIP 解压到一个新文件夹后重试。");
                using var process = Process.Start(new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote))) { WorkingDirectory = folder, UseShellExecute = false });
                if (process == null) throw new IOException("无法启动晓助手。");
                if (args.Length > 0) { process.WaitForExit(); return process.ExitCode; }
                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)
            { MessageBox.Show(ex.Message + "\n如果缺少 .NET 4.8，请运行同目录的“准备运行环境.cmd”。", "晓助手启动失败"); return 1; }
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
    }
}
