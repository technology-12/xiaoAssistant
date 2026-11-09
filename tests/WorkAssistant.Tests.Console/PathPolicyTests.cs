using System;
using System.Diagnostics;
using System.IO;
using WorkAssistant.Core;
using WorkAssistant.Tools;

namespace WorkAssistant.Tests.Console
{
    /// <summary>路径边界与越界攻击（设计 §10 / 验收 14.1-4）。</summary>
    public static class PathPolicyTests
    {
        public static void Register()
        {
            Test.Add("路径：拒绝 ../ 穿越、绝对路径、UNC、冒号、前缀绕过", TraversalAndPrefix);
            Test.Add("路径：拒绝符号链接与目录联接", ReparsePoints);
            Test.Add("路径：内部输出限任务目录，用户交付限 output，越界拒绝", OutputBounds);
            Test.Add("路径：保留设备名与非法字符拒绝", InvalidNames);
        }

        private static string NewWs()
        {
            var root = Path.Combine(Path.GetTempPath(), "wa-path-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            File.WriteAllText(Path.Combine(root, "inputs", "ok.csv"), "部门,销售额\r\n甲,1.00\r\n");
            return root;
        }

        private static void TraversalAndPrefix()
        {
            var ws = NewWs();
            var policy = new WorkspacePolicy(ws);

            // 前缀绕过：inputs2 与 workspace2 式目录名。
            Directory.CreateDirectory(Path.Combine(ws, "inputs2"));
            File.WriteAllText(Path.Combine(ws, "inputs2", "x.csv"), "x");
            Directory.CreateDirectory(Path.Combine(ws, "workspace2"));

            string[] attacks =
            {
                "../secret.csv",
                "inputs/../../secret.csv",
                "inputs/../outputs/x.csv",
                "C:/Windows/win.ini",
                "C:\\Windows\\win.ini",
                "\\\\server\\share\\x.csv",
                "inputs/sales:stream.csv",
                "inputs2/x.csv",
                "outputs/../../secret.csv"
            };
            foreach (var attack in attacks)
            {
                Test.ExpectToolError(ErrorCodes.PathDenied,
                    () => policy.ResolveReadablePath(attack), "应拒绝：" + attack);
            }

            // 正常路径仍可用。
            var ok = policy.ResolveReadablePath("inputs/ok.csv");
            Test.Check(File.Exists(ok), "正常文件可解析");
        }

        private static void ReparsePoints()
        {
            var ws = NewWs();
            var junctionDir = Path.Combine(ws, "inputs", "junction");
            // 目录联接无需管理员权限。
            var psi = new ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + junctionDir + "\" \"" + Path.Combine(ws, "inputs") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(5000);
            Test.Check(Directory.Exists(junctionDir), "测试前置：联接已创建");

            var policy = new WorkspacePolicy(ws);
            // 直接访问联接下的文件（物理上存在于 inputs）也应被拒绝。
            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveReadablePath("inputs/junction/ok.csv"),
                "应拒绝联接路径");
            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveReadableDirectory("inputs/junction"),
                "应拒绝联接目录");
        }

        private static void OutputBounds()
        {
            var ws = NewWs();
            var policy = new WorkspacePolicy(ws);

            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveTaskOutputPath("../evil", "x.csv"), "拒绝 ../ 任务 ID");
            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveTaskOutputPath("t1", "../x.csv"), "拒绝 ../ 文件名");
            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveTaskOutputPath("t1", "a/b.csv"), "拒绝文件名含 /");
            Test.ExpectToolError(ErrorCodes.PathDenied,
                () => policy.ResolveTaskOutputPath("t1", "a\\b.csv"), "拒绝文件名含 \\");

            var ok = policy.ResolveTaskOutputPath("t1", "summary.csv");
            Test.Equal(Path.Combine(ws, ".claude", "outputs", "t1", "summary.csv"), ok, "内部成果位于当前任务目录");
            Test.Equal(Path.Combine(ws, "output", "summary.csv"), policy.ResolveDeliveryPath("summary.csv"), "用户成果直接位于 output");
            Test.ExpectToolError(ErrorCodes.PathDenied, () => policy.ResolveDeliveryPath("../escape.csv"), "交付目录拒绝越界");
        }

        private static void InvalidNames()
        {
            var ws = NewWs();
            var policy = new WorkspacePolicy(ws);
            // 保留设备名、非法字符、结尾点/空格（NTFS 实际禁用的形式）。
            string[] bad = { "inputs/CON.csv", "inputs/NUL", "inputs/a<b.csv", "inputs/a.csv." };
            foreach (var name in bad)
            {
                Test.ExpectToolError(ErrorCodes.PathDenied,
                    () => policy.ResolveReadablePath(name), "应拒绝：" + name);
            }
        }
    }
}
