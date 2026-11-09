using System;
using System.IO;
using System.Text;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 工作区路径策略（设计 §10）：
    /// - 输入仅限 inputs 及其子目录；输出仅限 outputs/&lt;task_id&gt;。
    /// - 拒绝绝对路径、UNC、设备路径、驱动器号、ADS 冒号、保留设备名、非法字符与 ../ 穿越。
    /// - GetFullPath 规范化后按带目录分隔符的根做前缀检查，防 workspace2 式前缀绕过。
    /// - 逐级检查 ReparsePoint，P0 拒绝符号链接与目录联接。
    /// </summary>
    public sealed class WorkspacePolicy : IWorkspacePolicy
    {
        public const string InputsDir = "inputs";
        public const string OutputsDir = ".claude/outputs";
        public const string DeliveryDir = "output";
        public const string ProjectConfigDir = ".claude";

        private const int MaxRelativeLength = 200;
        private const int MaxPhysicalLength = 240;

        private static readonly char[] InvalidChars = { '<', '>', '"', '|', '?', '*', '\0' };

        public string RootPath { get; }
        public string? SnapshotRoot { get; }

        public WorkspacePolicy(string rootPath, string? snapshotRoot = null)
        {
            rootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(rootPath))
            {
                throw new ToolException(ErrorCodes.PathDenied, "工作区目录不存在：" + rootPath);
            }
            RootPath = rootPath;
            SnapshotRoot = snapshotRoot;
        }

        /// <summary>确保 inputs/outputs 子目录存在。</summary>
        public static void EnsureLayout(string rootPath)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, InputsDir));
            Directory.CreateDirectory(Path.Combine(rootPath, DeliveryDir));
            Directory.CreateDirectory(Path.Combine(rootPath, ProjectConfigDir));
            Directory.CreateDirectory(Path.Combine(rootPath, OutputsDir.Replace('/', Path.DirectorySeparatorChar)));
        }

        public string ResolveReadablePath(string relativePath)
        {
            return SnapshotRoot == null ? ResolveOriginalReadablePath(relativePath) :
                new WorkspacePolicy(SnapshotRoot).ResolveOriginalReadablePath(relativePath);
        }
        public string ResolveOriginalReadablePath(string relativePath)
        {
            var full = ResolveInsideRoot(relativePath, allowDirectory: false);
            var inputsRoot = Path.Combine(RootPath, InputsDir);
            if (!StartsWithSegment(full, inputsRoot) || string.Equals(full, inputsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "只允许读取 " + InputsDir + "/ 下的文件，被拒绝的路径：" + relativePath);
            }
            AssertNoReparsePoint(full);
            return full;
        }

        /// <summary>解析可读目录（inputs 本身或其子目录，必须已存在）。</summary>
        public string ResolveReadableDirectory(string relativePath)
        {
            if (SnapshotRoot != null) return new WorkspacePolicy(SnapshotRoot).ResolveReadableDirectory(relativePath);
            var full = ResolveInsideRoot(relativePath, allowDirectory: true);
            var inputsRoot = Path.Combine(RootPath, InputsDir);
            if (!StartsWithSegment(full, inputsRoot))
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "只允许访问 " + InputsDir + "/ 及其子目录，被拒绝的路径：" + relativePath);
            }
            if (!Directory.Exists(full))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "目录不存在：" + relativePath);
            }
            AssertNoReparsePoint(full);
            return full;
        }

        public string ResolveTaskOutputPath(string taskId, string fileName)
        {
            ValidateComponent(taskId, "task_id");
            if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
            {
                throw new ToolException(ErrorCodes.PathDenied, "输出文件名不允许包含路径分隔符。");
            }
            ValidateComponent(fileName, "输出文件名");
            var directory = GetTaskOutputDirectory(taskId);
            var full = ResolveInsideRoot(ToWorkspaceRelative(directory) + "/" + fileName, allowDirectory: false);
            if (!StartsWithSegment(full, directory))
            {
                throw new ToolException(ErrorCodes.PathDenied, "输出路径越界。");
            }
            AssertNoReparsePoint(full);
            return full;
        }

        public string GetTaskOutputDirectory(string taskId)
        {
            ValidateComponent(taskId, "task_id");
            string current = Path.Combine(RootPath, OutputsDir.Replace('/', Path.DirectorySeparatorChar), taskId);
            string legacy = Path.Combine(RootPath, "outputs", taskId);
            string directory = !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
            AssertNoReparsePoint(directory);
            return directory;
        }

        public string ResolveDeliveryPath(string fileName)
        {
            ValidateComponent(fileName, "交付文件名");
            if (fileName.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ToolException(ErrorCodes.PathDenied, "交付文件名不能包含目录。");
            string full = ResolveInsideRoot(DeliveryDir + "/" + fileName, false);
            AssertNoReparsePoint(full);
            return full;
        }

        /// <summary>
        /// 解析文件整理目标（正式版 §8.4）：仅允许 inputs/ 内移动或改名。
        /// 目标目录允许尚不存在（提交时创建）；目标文件不得已存在（防覆盖）。
        /// </summary>
        public string ResolveOrganizeTarget(string targetDirectory, string fileName)
        {
            if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
            {
                throw new ToolException(ErrorCodes.PathDenied, "目标文件名不允许包含路径分隔符。");
            }
            ValidateComponent(fileName, "目标文件名");
            var full = ResolveInsideRoot(targetDirectory, allowDirectory: true);
            var inputsRoot = Path.Combine(RootPath, InputsDir);
            if (!StartsWithSegment(full, inputsRoot) ||
                string.Equals(full, RootPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "整理目标必须位于 " + InputsDir + "/ 之内：" + targetDirectory);
            }
            var target = Path.Combine(full, fileName);
            AssertNoReparsePoint(target);
            return target;
        }

        public string ToWorkspaceRelative(string fullPath)
        {
            var norm = Path.GetFullPath(fullPath);
            if (SnapshotRoot != null && StartsWithSegment(norm, Path.GetFullPath(SnapshotRoot)))
                return norm.Substring(Path.GetFullPath(SnapshotRoot).TrimEnd('\\').Length + 1).Replace('\\', '/');
            if (!StartsWithSegment(norm, RootPath))
            {
                throw new ToolException(ErrorCodes.PathDenied, "路径不在工作区内。");
            }
            var rel = norm.Substring(RootPath.Length + 1);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        public string InputsRoot => Path.Combine(RootPath, InputsDir);

        // ---- 内部实现 -------------------------------------------------------

        /// <summary>解析并校验工作区相对路径，返回物理路径。仅接受相对路径。</summary>
        private string ResolveInsideRoot(string relativePath, bool allowDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "路径不能为空。");
            }
            if (relativePath.Length > MaxRelativeLength)
            {
                throw new ToolException(ErrorCodes.LimitExceeded, "路径过长。");
            }
            // 统一分隔符便于检查；拒绝所有其他形式的前导分隔与冒号。
            if (relativePath.IndexOf(':') >= 0)
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "路径不允许包含冒号（驱动器号或 ADS）：" + relativePath);
            }
            if (relativePath.StartsWith("/", StringComparison.Ordinal) ||
                relativePath.StartsWith("\\", StringComparison.Ordinal) ||
                relativePath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "只允许工作区相对路径，不接受绝对路径或 UNC：" + relativePath);
            }

            var parts = relativePath.Replace('/', '\\').Split('\\');
            for (var i = 0; i < parts.Length; i++)
            {
                ValidateComponent(parts[i], "路径段 #" + (i + 1));
                if (parts[i] == "." || parts[i] == "..")
                {
                    throw new ToolException(ErrorCodes.PathDenied,
                        "路径不允许包含 . 或 ..：" + relativePath);
                }
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(RootPath, relativePath));
            }
            catch (Exception ex)
            {
                throw new ToolException(ErrorCodes.PathDenied, "路径无法规范化：" + ex.Message);
            }
            if (full.Length > MaxPhysicalLength)
            {
                throw new ToolException(ErrorCodes.LimitExceeded, "物理路径过长。");
            }
            if (!StartsWithSegment(full, RootPath))
            {
                throw new ToolException(ErrorCodes.PathDenied,
                    "路径超出工作区边界：" + relativePath);
            }
            if (!allowDirectory && Directory.Exists(full))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "目标是一个目录，不是文件：" + relativePath);
            }
            return full;
        }

        private static void ValidateComponent(string component, string what)
        {
            if (string.IsNullOrEmpty(component))
            {
                throw new ToolException(ErrorCodes.PathDenied, what + "为空。");
            }
            if (component.IndexOfAny(InvalidChars) >= 0)
            {
                throw new ToolException(ErrorCodes.PathDenied, what + "包含非法字符。");
            }
            foreach (var c in component)
            {
                if (c < 0x20)
                {
                    throw new ToolException(ErrorCodes.PathDenied, what + "包含控制字符。");
                }
            }
            // 保留设备名（不含扩展名部分）：CON、PRN、AUX、NUL、COM1-9、LPT1-9。
            var stem = component.Split(new[] { '.' })[0];
            var upper = stem.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
                (upper.Length == 4 &&
                 ((upper.StartsWith("COM") && upper[3] >= '1' && upper[3] <= '9') ||
                  (upper.StartsWith("LPT") && upper[3] >= '1' && upper[3] <= '9'))))
            {
                throw new ToolException(ErrorCodes.PathDenied, what + "使用了保留设备名。");
            }
            if (component.EndsWith(" ", StringComparison.Ordinal) ||
                component.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ToolException(ErrorCodes.PathDenied, what + "不能以空格或点结尾。");
            }
        }

        /// <summary>带目录分隔符的前缀比较，避免 workspace2 前缀绕过。</summary>
        private static bool StartsWithSegment(string full, string root)
        {
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                   (full.Length > root.Length &&
                    full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    full[root.Length] == Path.DirectorySeparatorChar);
        }

        /// <summary>自工作区根到目标逐级检查 ReparsePoint（符号链接/目录联接）。</summary>
        private void AssertNoReparsePoint(string full)
        {
            var current = new StringBuilder(RootPath);
            var rel = full.Substring(RootPath.Length + 1);
            foreach (var part in rel.Split(Path.DirectorySeparatorChar))
            {
                current.Append(Path.DirectorySeparatorChar).Append(part);
                var path = current.ToString();
                if (File.Exists(path) || Directory.Exists(path))
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ToolException(ErrorCodes.PathDenied,
                            "路径中包含符号链接或目录联接，已被拒绝。");
                    }
                }
            }
        }
    }
}
