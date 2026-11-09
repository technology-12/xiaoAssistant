using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;

namespace WorkAssistant.Desktop
{
    internal static class ProductionUi
    {
        public static Button Button(string label, Action click)
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), MinHeight = 44 };
            button.SetResourceReference(FrameworkElement.StyleProperty, "StudioButton");
            button.Click += (_, __) => { try { click(); } catch (Exception ex) { MessageBox.Show(ex.Message, label); } };
            return button;
        }
        public static void Style(Window window)
        {
            window.SetResourceReference(Control.BackgroundProperty, "BrushSurfaceMain");
            window.SetResourceReference(Control.ForegroundProperty, "BrushTextPrimary");
            window.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
            window.FontSize = 14;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    internal sealed class ArtifactPreviewWindow : Window
    {
        private readonly CancellationTokenSource _cancel = new();
        public ArtifactPreviewWindow(TaskRecord task, ArtifactInfo artifact)
        {
            Title = "成果预览 · " + Path.GetFileName(artifact.Path); Width = 940; Height = 680; MinWidth = 640; MinHeight = 440;
            ProductionUi.Style(this);
            var panel = new DockPanel { Margin = new Thickness(24) };
            var caption = new TextBlock { Text = "正在独立进程中校验并读取成果…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption);
            var content = new ContentControl(); panel.Children.Add(content); Content = panel;
            Closed += (_, __) => _cancel.Cancel();
            Loaded += async (_, __) =>
            {
                try
                {
                    var result = await new TaskHostClient().RunAsync(new HostRequest { Task = task, StoreRoot = App.TaskStore.Root,
                        PreviewPath = artifact.PublishedPath.Length > 0 && File.Exists(Path.Combine(task.WorkspaceRoot, artifact.PublishedPath)) ? artifact.PublishedPath : artifact.Path, PreviewSha256 = artifact.Sha256 }, new DenyAllProvider(), _ => { }, _cancel.Token);
                    var preview = JsonConvert.DeserializeObject<PreviewDocument>(result.FinalText) ?? throw new IOException("预览响应无效。");
                    caption.Text = "工具：" + artifact.ToolName + " v" + artifact.ToolVersion + " · 输入快照 SHA256 " + artifact.SourceSnapshotSha256 + "\n" + preview.Notice + "\n来源：" + artifact.Path + " · SHA256 " + artifact.Sha256;
                    if (preview.Columns.Length > 0)
                    {
                        var table = new DataTable();
                        for (int i = 0; i < preview.Columns.Length; i++) table.Columns.Add("C" + i);
                        foreach (var row in preview.Rows) table.Rows.Add(row.Select(v => (object)v).ToArray());
                        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = table.DefaultView, CanUserAddRows = false };
                        for (int i = 0; i < preview.Columns.Length; i++) grid.Columns.Add(new DataGridTextColumn { Header = preview.Columns[i], Binding = new System.Windows.Data.Binding("C" + i) });
                        content.Content = grid;
                    }
                    else content.Content = new TextBox { Text = preview.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
                }
                catch (Exception ex) { caption.Text = "无法预览：" + ex.Message; }
            };
        }
    }

    internal sealed class TaskCenterWindow : Window
    {
        private readonly DataGrid _tasks = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false };
        private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
        public TaskCenterWindow(Action<TaskRecord> rerun)
        {
            Title = "任务中心"; Width = 1080; Height = 760; MinWidth = 760; MinHeight = 520; ProductionUi.Style(this);
            var panel = new DockPanel { Margin = new Thickness(24) };
            var title = new TextBlock { Text = "任务中心", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            actions.Children.Add(ProductionUi.Button("刷新", Refresh));
            actions.Children.Add(ProductionUi.Button("取消所选任务", () =>
            {
                if (_tasks.SelectedItem is not TaskRecord task) return;
                if (task.State == TaskState.Queued) { task.State = TaskState.Cancelled; task.FinishedUtc = DateTime.UtcNow; App.TaskStore.SaveTask(task); }
                else if (App.MainVm?.IsBusy == true && App.MainVm.ActiveTaskId == task.TaskId) App.MainVm.CancelCurrentTask();
                else MessageBox.Show("只能取消排队中或当前正在执行的任务。");
                Refresh();
            }));
            actions.Children.Add(ProductionUi.Button("作为新任务重新执行", () =>
            {
                if (_tasks.SelectedItem is not TaskRecord task) return;
                if (App.MainVm?.IsBusy == true) { MessageBox.Show("请先等待当前任务结束。"); return; }
                if (task.State == TaskState.NeedsReview) { MessageBox.Show("请先复核变更清单中的源文件和目标文件。为避免重复修改，此任务不能直接重跑；请在对话中创建新的明确请求。"); return; }
                if (task.State == TaskState.Queued) { task.State = TaskState.Cancelled; App.TaskStore.SaveTask(task); }
                rerun(task); Close();
            }));
            actions.Children.Add(ProductionUi.Button("备份状态库", () =>
            {
                var save = new SaveFileDialog { FileName = "workassistant-state-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sqlite3", Filter = "SQLite 备份|*.sqlite3" };
                if (save.ShowDialog(this) == true) { App.TaskStore.Backup(save.FileName); MessageBox.Show("备份已保存；输入快照及成果文件需另行保留。"); }
            }));
            actions.Children.Add(ProductionUi.Button("导出所选会话", () =>
            {
                if (_tasks.SelectedItem is not TaskRecord task) return;
                var save = new SaveFileDialog { FileName = "session-" + task.SessionId + ".json", Filter = "JSON|*.json" };
                if (save.ShowDialog(this) != true) return;
                DurableFiles.WriteText(save.FileName, JsonConvert.SerializeObject(App.TaskStore.LoadTasks().Where(t => t.SessionId == task.SessionId), Formatting.Indented));
            }));
            actions.Children.Add(ProductionUi.Button("删除任务记录", () =>
            {
                if (_tasks.SelectedItem is not TaskRecord task) return;
                if (task.State == TaskState.Running || task.State == TaskState.WaitingForUser || App.MainVm?.ActiveTaskId == task.TaskId) throw new IOException("请先停止任务。");
                if (MessageBox.Show(this, "删除任务记录、事件、快照和本地回执？工作区中的输入与成果文件将保留。", "删除任务", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                App.TaskStore.DeleteTask(task.TaskId); Refresh();
            }));
            DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
            var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            foreach (var pair in new[] { ("请求", "UserRequest"), ("状态", "State"), ("类型", "Kind"), ("创建时间 UTC", "CreatedUtc"), ("任务 ID", "TaskId") })
                _tasks.Columns.Add(new DataGridTextColumn { Header = pair.Item1, Binding = new System.Windows.Data.Binding(pair.Item2), Width = pair.Item2 == "UserRequest" ? new DataGridLength(2, DataGridLengthUnitType.Star) : DataGridLength.Auto });
            _tasks.SelectionChanged += (_, __) => ShowDetails();
            grid.Children.Add(_tasks); Grid.SetRow(_details, 1); grid.Children.Add(_details); panel.Children.Add(grid); Content = panel; Refresh();
        }
        private void Refresh() { _tasks.ItemsSource = App.TaskStore.LoadTasks(); }
        private void ShowDetails()
        {
            if (_tasks.SelectedItem is not TaskRecord task) return;
            _details.Text = "任务 " + task.TaskId + "\n" + task.ErrorMessage + "\n\n" + task.FinalText + "\n\n输入快照：" + task.SnapshotRoot + "\n" +
                string.Join("\n", task.Steps.Select(s => s.Tool + " v" + s.ToolVersion + " · " + s.State + " · " + s.CallId)) + "\n\n成果：\n" +
                string.Join("\n", task.Artifacts.Select(a => a.Path + " · " + a.Sha256));
            string folder = new WorkspacePolicy(task.WorkspaceRoot).GetTaskOutputDirectory(task.TaskId);
            if (Directory.Exists(folder)) foreach (string journal in Directory.GetFiles(folder, "mutation-*.json"))
                if (new FileInfo(journal).Length < 1024 * 1024) _details.Text += "\n\n变更记录：\n" + File.ReadAllText(journal);
        }
    }
}
