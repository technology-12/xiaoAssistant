using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading; // XP 线：Dispatcher.InvokeAsync 扩展需要该导入
using WorkAssistant.Infrastructure;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Desktop
{
    public partial class App : Application
    {
        private Mutex? _instance;
        internal static bool IsHeadlessQa { get; private set; }
        public static AppSettings Settings { get; private set; } = new();
        public static SettingsStore SettingsStore { get; private set; } = null!;
        public static LocalTaskStore TaskStore { get; private set; } = null!;
        public static MainViewModel? MainVm { get; set; }

        /// <summary>最近一次连接测试结果（顶栏状态展示；配置了 Key 不代表连接成功）。</summary>
        public static string LastConnectionState { get; set; } = "未测试";
        public static bool LastConnectionOk { get; set; }
        public static string LastConnectionFingerprint { get; set; } = "";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            IsHeadlessQa = e.Args.Contains("--selftest-ui") || e.Args.Contains("--render-ui");
            if (e.Args.Length == 2 && e.Args[0] == "--wait-for-exit" && int.TryParse(e.Args[1], out int oldPid))
            {
                try { using var old = Process.GetProcessById(oldPid); if (!old.WaitForExit(15000)) { Shutdown(2); return; } } catch (ArgumentException) { }
            }
            if (e.Args.Length == 2 && e.Args[0] == "--backup-state")
            {
                try { new LocalTaskStore().Backup(e.Args[1]); Shutdown(0); } catch { Shutdown(3); }
                return;
            }
            string mutexName = @"Local\Win7WorkAssistant.Desktop";
            if (ApplicationDataPaths.IsPortable)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                mutexName += "." + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(ApplicationDataPaths.Root).ToUpperInvariant()))).Replace("-", "").Substring(0, 20);
            }
            _instance = new Mutex(true, mutexName, out bool created);
            if (!created)
            {
                bool acquired;
                try { acquired = _instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { MessageBox.Show("晓助手已在当前登录会话中运行。"); Shutdown(); return; }
            }

            try
            {
                NativeRuntimeBootstrap.Configure();
                SettingsStore = new SettingsStore();
                Settings = SettingsStore.Load();
                TaskStore = new LocalTaskStore();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开本地状态库或加载组件。请保留现有数据，检查完整安装、SQLite x86/x64 组件及状态库备份。\n错误类型：" + ex.GetType().Name, "启动失败");
                Shutdown(4); return;
            }
            var selfTest = e.Args.Contains("--selftest-ui", StringComparer.OrdinalIgnoreCase);
            if (selfTest)
                UseIsolatedStores(Path.Combine(Path.GetTempPath(), "xiao-qa-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            // Render-only QA uses isolated stores and never opens a foreground window.
            if (e.Args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase))
            {
                UseIsolatedStores(Path.Combine(Path.GetTempPath(), "xiao-qa-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
                var previewWindow = new MainWindow();
                MainWindow = previewWindow;
                RenderPreview(previewWindow, e.Args);
                return;
            }
            // 崩溃后遗留的 Running 任务标记为 Interrupted，不自动重新执行。
            TaskStore.MarkInterruptedTasks();
            foreach (var task in TaskStore.LoadTasks())
                if (WorkAssistant.Tools.OutputRecovery.Reconcile(task)) TaskStore.SaveTask(task);

            var window = new MainWindow();
            MainWindow = window;
            if (!selfTest) window.Show();

            // 开发/演示参数：--demo-ui 自动授权样例工作区并执行一次 Fake 标准演示。
            if (e.Args.Contains("--demo-ui", StringComparer.OrdinalIgnoreCase))
            {
                RunDemoUi(window);
            }

            // 自动化验证：--selftest-ui 校验类型化对话/历史回看/状态锁定，写结果文件退出。
            if (e.Args.Contains("--selftest-ui", StringComparer.OrdinalIgnoreCase))
            {
                RunSelfTestUi(window, e.Args);
            }
        }

        private static async void RunDemoUi(MainWindow window)
        {
            try
            {
                var vm = MainVm;
                if (vm == null) return;
                // 复制样例到临时目录并授权。
                var samples = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "..", "samples", "workspace");
                if (!Directory.Exists(Path.Combine(samples, "inputs")))
                {
                    samples = FindRepoSamples();
                }
                var temp = Path.Combine(Path.GetTempPath(), "wa-demo-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path.Combine(temp, "inputs"));
                foreach (var file in Directory.GetFiles(Path.Combine(samples, "inputs")))
                {
                    File.Copy(file, Path.Combine(temp, "inputs", Path.GetFileName(file)));
                }
                WorkspaceTools.EnsureLayout(temp);
                vm.AuthorizeWorkspace(temp);
                await vm.RunDemoAsync();
            }
            catch (Exception)
            {
                // 演示参数失败不影响主程序。
            }
        }

        private static string FindRepoSamples()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "workspace");
                if (Directory.Exists(Path.Combine(candidate, "inputs"))) return candidate;
                dir = dir.Parent!;
            }
            throw new InvalidOperationException("找不到 samples/workspace");
        }

        private static async void RenderPreview(MainWindow window, string[] args)
        {
            string Arg(string name, string fallback)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }
            try
            {
                var width = int.Parse(Arg("--width", "1180"));
                var height = int.Parse(Arg("--height", "760"));
                var output = Path.GetFullPath(Arg("--output", "ui-preview.png"));
                var scene = Arg("--scene", "home");
                var workspace = Path.Combine(SettingsStore.Root, "销售报表");
                Directory.CreateDirectory(Path.Combine(workspace, "inputs"));
                foreach (var file in Directory.GetFiles(Path.Combine(FindRepoSamples(), "inputs")))
                    File.Copy(file, Path.Combine(workspace, "inputs", Path.GetFileName(file)));
                var vm = MainVm!;
                vm.AuthorizeWorkspace(workspace);
                if (width < 1000) vm.ToggleSidebarCommand.Execute(null);
                if (scene == "completed" || scene == "drawer") await vm.RunDemoAsync();
                if (scene == "drawer")
                {
                    var artifact = vm.Conversation.SelectMany(x => x.Artifacts).First();
                    vm.ShowArtifactCommand.Execute(artifact);
                }
                System.Windows.FrameworkElement root = (System.Windows.FrameworkElement)window.Content;
                if (scene == "settings")
                {
                    var settings = new SettingsWindow(Settings, SettingsStore);
                    root = (System.Windows.FrameworkElement)settings.Content;
                }
                root.Width = width;
                root.Height = height;
                if (root is System.Windows.Controls.Panel panel) panel.Background = window.Background;
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                root.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using (var stream = File.Create(output)) encoder.Save(stream);
                Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(Arg("--output", "ui-preview.png")) + ".error.txt", ex.ToString());
                Current.Shutdown(1);
            }
        }

        /// <summary>
        /// UI 层自动化验证（GUI_REDESIGN_SPEC 验收：展示状态与历史加载）。
        /// 结果写入 --result 指定文件；全部通过退出码 0。
        /// </summary>
        private static async void RunSelfTestUi(MainWindow window, string[] args)
        {
            var resultPath = "ui-selftest-result.txt";
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--result") resultPath = args[i + 1];
            }
            var log = new System.Text.StringBuilder();
            var failures = 0;
            void Check(bool cond, string what)
            {
                log.AppendLine((cond ? "[PASS] " : "[FAIL] ") + what);
                if (!cond) failures++;
            }

            try
            {
                var vm = MainVm!;
                var temp = Path.Combine(Path.GetTempPath(),
                    "wa-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path.Combine(temp, "inputs"));
                foreach (var file in Directory.GetFiles(Path.Combine(FindRepoSamples(), "inputs")))
                {
                    File.Copy(file, Path.Combine(temp, "inputs", Path.GetFileName(file)));
                }
                vm.AuthorizeWorkspace(temp);
                Check(vm.IsAuthorized, "授权后 IsAuthorized=true");
                Check(!vm.HasConversation, "初始无会话（欢迎页）");
                Check(vm.InputLocked == false, "授权后输入可用");
                Check(vm.WorkspaceLabel.Length > 0, "工作区标签非空");
                Check(vm.PageTitle == "开始工作", "欢迎页标题正确");

                // 未运行时：无取消中状态。
                Check(!vm.IsBusy, "空闲时 IsBusy=false");

                // 跑一次 Fake 标准演示。
                await vm.RunDemoAsync();
                Check(!vm.IsRunning, "任务结束后 IsRunning=false");
                Check(vm.HasConversation, "有会话内容");
                Check(vm.PageTitle == "当前任务", "任务页标题正确");
                Check(vm.NewTaskCommand.CanExecute(null), "任务结束后可以新建任务");
                var items = vm.Conversation;
                Check(items.Count == 3, "对话条目数=3（用户/步骤组/助手），实际 " + items.Count);
                Check(items[0].Kind == Presentation.ConversationItemKind.UserMessage, "首条为用户消息");
                Check(items[1].Kind == Presentation.ConversationItemKind.ExecutionGroup, "第二条为执行步骤组");
                Check(items[1].GroupTitle.Contains("已完成"), "步骤组折叠标题：" + items[1].GroupTitle);
                Check(!items[1].IsExpanded, "完成后步骤组默认折叠");
                Check(items[1].Steps.Count >= 4, "步骤数 >= 4，实际 " + items[1].Steps.Count);
                Check(items[1].Steps.All(s => !s.IsRunning), "所有步骤已结束");
                Check(items[2].Kind == Presentation.ConversationItemKind.AssistantMessage, "末条为助手消息");
                Check(items[2].Artifacts.Count == 2, "助手消息挂 2 个成果卡，实际 " + items[2].Artifacts.Count);
                Check(items[2].Artifacts.All(a => a.FileExists), "成果文件存在");
                Check(!items[2].IsStreaming, "助手消息流式已固化");
                Check(vm.StatusText.Length == 0, "完成后状态行清空");
                Check(vm.HistoryGroups.Sum(g => g.Items.Count) >= 1, "侧栏历史出现条目");

                // 连续对话：追问筛选（同会话第 2 轮，新输出目录）。
                await vm.RunDemoTurnAsync("只保留销售一部，重新汇总");
                Check(vm.Conversation.Count == 6, "第 2 轮后对话条目=6，实际 " + vm.Conversation.Count);
                var turn2 = vm.Conversation[5];
                Check(turn2.Kind == Presentation.ConversationItemKind.AssistantMessage, "第 2 轮末条为助手消息");
                Check(turn2.Artifacts.Count == 2, "第 2 轮也交付 2 个成果（新版本不覆盖）");
                Check(turn2.Text.Contains("150.40"), "第 2 轮筛选后合计 150.40：" + turn2.Text);

                // 连续对话：纯问答轮（不生成文件也判成功）。
                await vm.RunDemoTurnAsync("这两个文件分别是什么格式？");
                Check(vm.Conversation.Count == 9, "第 3 轮后对话条目=9，实际 " + vm.Conversation.Count);
                var turn3 = vm.Conversation[8];
                Check(turn3.Kind == Presentation.ConversationItemKind.AssistantMessage, "问答轮末条为助手消息");
                Check(turn3.Artifacts.Count == 0, "问答轮无文件成果（问答不强制生成文件）");
                Check(turn3.Text.Length > 0, "问答轮有文本回复");

                // XLSX 轮：读 Excel 汇总并导出 xlsx。
                await vm.RunDemoTurnAsync("汇总 inputs 中的销售 Excel（xlsx），按部门统计销售额，导出 XLSX 汇总表。");
                var turn4 = vm.Conversation[vm.Conversation.Count - 1];
                Check(turn4.Kind == Presentation.ConversationItemKind.AssistantMessage, "XLSX 轮末条为助手消息");
                Check(turn4.Artifacts.Count == 1 && turn4.Artifacts[0].FileName.EndsWith(".xlsx"),
                    "XLSX 轮交付 1 个 xlsx 成果，实际 " + turn4.Artifacts.Count);

                // 文件整理轮：计划 + 确认（演示模式自动通过）+ 执行。
                var organizeTask = vm.RunDemoTurnAsync("把 inputs 里的演示文件整理归档到子文件夹。");
                var approvalDeadline = DateTime.UtcNow.AddSeconds(30);
                bool approvedInConversation = false;
                while (!organizeTask.IsCompleted && DateTime.UtcNow < approvalDeadline)
                {
                    var card = vm.Conversation.Select(c => c.QuestionCard).FirstOrDefault(c => c != null && c.IsPending && c.Options.Contains("确认执行"));
                    if (card != null) { card.ChooseCommand.Execute("确认执行"); approvedInConversation = true; }
                    await XpTask.Delay(20);
                }
                if (!organizeTask.IsCompleted) { vm.CancelCurrentTask(); throw new TimeoutException("整理演示未在限定时间内完成。"); }
                await organizeTask;
                Check(approvedInConversation, "整理审批通过对话卡片确认");
                var turn5 = vm.Conversation[vm.Conversation.Count - 1];
                Check(turn5.Kind == Presentation.ConversationItemKind.AssistantMessage, "整理轮末条为助手消息");
                Check(turn5.Artifacts.Count >= 1, "整理轮交付计划成果");
                Check(turn5.Text.Contains("移动"), "整理轮报告移动结果：" + turn5.Text);
                Check(Directory.GetDirectories(Path.Combine(temp, "inputs"), "已归档").Length == 1,
                    "整理后存在 inputs/已归档 目录");

                // 历史回看（会话维度：全部轮次）。
                var historyItem = vm.HistoryGroups.SelectMany(g => g.Items).First();
                vm.LoadHistoryTaskCommand.Execute(historyItem);
                var historyDeadline = DateTime.UtcNow.AddSeconds(15);
                while (vm.IsLoadingConversation && DateTime.UtcNow < historyDeadline) await XpTask.Delay(20);
                Check(!vm.IsLoadingConversation, "历史加载在限定时间内结束");
                Check(!vm.IsViewingHistory, "历史会话加载后支持继续对话");
                Check(!vm.InputLocked, "已完成会话加载后输入可用");
                Check(vm.SendLockedReason.Length == 0, "可继续对话时不显示旧版历史锁定提示");
                Check(vm.Conversation.Count >= 15, "回看重建全部轮次条目，实际 " + vm.Conversation.Count);

                // 新建任务回到欢迎态。
                vm.NewTaskCommand.Execute(null);
                Check(!vm.IsViewingHistory && vm.Conversation.Count == 0 && !vm.HasConversation,
                    "新建任务清空会话回到欢迎页");
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("[FAIL] 异常：" + ex.Message);
            }

            File.WriteAllText(resultPath,
                (failures == 0 ? "UI SELFTEST PASS\n" : $"UI SELFTEST FAIL ({failures})\n") + log);
            Current.Shutdown(failures == 0 ? 0 : 1);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            MainVm?.Dispose();
            base.OnExit(e);
        }

        /// <summary>供测试与调试：强制使用干净的临时配置目录。</summary>
        public static void UseIsolatedStores(string baseDirectory)
        {
            SettingsStore = new SettingsStore(Path.Combine(baseDirectory, "config"));
            Settings = SettingsStore.Load();
            TaskStore = new LocalTaskStore(Path.Combine(baseDirectory, "tasks"));
        }
    }

    /// <summary>隔离 App 与 Tools 的直接依赖的小工具（避免在 App 中引用 Tools 命名空间）。</summary>
    internal static class WorkspaceTools
    {
        public static void EnsureLayout(string root) => Tools.WorkspacePolicy.EnsureLayout(root);
    }
}
