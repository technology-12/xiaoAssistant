using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using WorkAssistant.Infrastructure;
using System.IO;
using System.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;

namespace WorkAssistant.Desktop
{
    public partial class MainWindow : Window
    {
        private IInputElement? _drawerReturnFocus;
        private readonly System.Windows.Forms.NotifyIcon _tray = new();
        private bool _composerIsComposing;
        private bool _followConversation = true;
        public MainWindow()
        {
            InitializeComponent();
            TextCompositionManager.AddPreviewTextInputStartHandler(this, OnComposerCompositionStarted);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnComposerCompositionStarted);
            TextCompositionManager.AddPreviewTextInputHandler(this, OnComposerCompositionFinished);
            var vm = new MainViewModel(
                App.Settings, App.SettingsStore, App.TaskStore, Dispatcher,
                PickWorkspace, OpenSettingsDialog, OpenDiagnostics);
            App.MainVm = vm;
            DataContext = vm;
            _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!) ?? System.Drawing.SystemIcons.Application;
            _tray.Text = "晓助手";
            var trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMenu.Items.Add("打开晓助手", null, (_, __) => Dispatcher.BeginInvoke(new Action(() => { Show(); WindowState = WindowState.Normal; Activate(); _tray.Visible = false; })));
            trayMenu.Items.Add("停止当前任务", null, (_, __) => Dispatcher.BeginInvoke(new Action(vm.CancelCurrentTask)));
            trayMenu.Items.Add("退出", null, (_, __) => Dispatcher.BeginInvoke(new Action(Close)));
            _tray.ContextMenuStrip = trayMenu;
            _tray.DoubleClick += (_, __) => Dispatcher.BeginInvoke(new Action(() => { Show(); WindowState = WindowState.Normal; Activate(); _tray.Visible = false; }));
            Closing += (_, args) =>
            {
                if (App.IsHeadlessQa) { vm.CancelCurrentTask(); return; }
                if (!vm.IsBusy) return;
                var decision = MessageBox.Show(this, "当前任务正在运行。\n是：停止任务并退出；否：最小化到托盘并继续任务；取消：返回。\n正在提交的文件变更中止后可能需要复核。", "退出晓助手", MessageBoxButton.YesNoCancel);
                if (decision == MessageBoxResult.Cancel) { args.Cancel = true; return; }
                if (decision == MessageBoxResult.No) { args.Cancel = true; _tray.Visible = true; Hide(); return; }
                vm.CancelCurrentTask();
            };
            Closed += (_, __) => { _tray.Dispose(); vm.Dispose(); };
            Loaded += (_, __) =>
            {
                var area = SystemParameters.WorkArea;
                MaxHeight = Math.Max(MinHeight, area.Height);
                Width = Math.Min(Width, area.Width);
                Height = Math.Min(Height, area.Height);
                if (ActualWidth < 1000 && !vm.SidebarCollapsed) vm.ToggleSidebarCommand.Execute(null);
            };
            SizeChanged += (_, e) =>
            {
                if (e.PreviousSize.Width >= 1000 && e.NewSize.Width < 1000 && !vm.SidebarCollapsed)
                    vm.ToggleSidebarCommand.Execute(null);
            };
            vm.Conversation.CollectionChanged += (_, change) =>
            {
                var question = change.NewItems?.OfType<Presentation.ConversationItem>().FirstOrDefault(item => item.Kind == Presentation.ConversationItemKind.UserQuestion);
                if (question != null)
                {
                    _followConversation = false;
                    question.QuestionCard!.PropertyChanged += (_, changed) =>
                    {
                        if (changed.PropertyName == nameof(Presentation.QuestionCardVm.IsPending) && !question.QuestionCard.IsPending) _followConversation = true;
                    };
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ConversationList.ScrollIntoView(question); ConversationList.UpdateLayout();
                        if (ConversationList.ItemContainerGenerator.ContainerFromItem(question) is FrameworkElement element)
                            element.BringIntoView(new Rect(0, 0, Math.Max(1, element.ActualWidth), 1));
                    }), DispatcherPriority.Background);
                    return;
                }
                if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ||
                    change.NewItems?.OfType<Presentation.ConversationItem>().Any(item => item.Kind == Presentation.ConversationItemKind.UserMessage || item.Kind == Presentation.ConversationItemKind.UserQuestion) == true)
                    _followConversation = true;
                if (vm.Conversation.Count > 0 && _followConversation)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_followConversation) ConversationScrollViewer()?.ScrollToEnd();
                    }), DispatcherPriority.Background);
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(vm.IsDrawerOpen)) return;
                if (vm.IsDrawerOpen)
                {
                    _drawerReturnFocus = Keyboard.FocusedElement;
                    Dispatcher.BeginInvoke(new Action(() => DrawerClose.Focus()), DispatcherPriority.Input);
                }
                else if (_drawerReturnFocus != null)
                {
                    Keyboard.Focus(_drawerReturnFocus);
                    _drawerReturnFocus = null;
                }
            };
        }

        private ScrollViewer? ConversationScrollViewer()
        {
            ScrollViewer? Find(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is ScrollViewer scroll) return scroll;
                    var found = Find(child); if (found != null) return found;
                }
                return null;
            }
            return Find(ConversationList);
        }

        private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var scroll = ConversationScrollViewer();
            if (scroll == null || !ReferenceEquals(e.OriginalSource, scroll)) return;
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            {
                if (_followConversation) Dispatcher.BeginInvoke(new Action(() => { if (_followConversation) scroll.ScrollToEnd(); }), DispatcherPriority.Background);
            }
            else if (e.VerticalChange != 0)
                _followConversation = scroll.ScrollableHeight - scroll.VerticalOffset < 3;
        }

        private void OnComposerCompositionStarted(object sender, TextCompositionEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox { Name: "TaskInput" }) _composerIsComposing = true;
        }

        private void OnComposerCompositionFinished(object sender, TextCompositionEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox { Name: "TaskInput" }) _composerIsComposing = false;
        }

        private void OnTaskInputLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            => _composerIsComposing = false;

        private void OnTaskInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Let the IME consume Enter while choosing or committing composition text.
            if (e.Key == Key.ImeProcessed || _composerIsComposing || e.Key != Key.Return) return;
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Shift) != 0) return;
            if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            if (sender is not TextBox input || string.IsNullOrWhiteSpace(input.Text) || DataContext is not MainViewModel vm) return;
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            // The keyboard send path queues during execution; it must never invoke Stop on empty input.
            if (vm.SendCommand.CanExecute("send")) vm.SendCommand.Execute("send");
        }

        private string? PickWorkspace()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择你熟悉的工作目录：助手会自动查找其中的常见文档，交付文档保存在 output。",
                ShowNewFolderButton = true
            };
            if (App.MainVm is { IsBusy: false } &&
                dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return dialog.SelectedPath;
            }
            return null;
        }

        private void OpenSettingsDialog()
        {
            var dialog = new SettingsWindow(App.Settings, App.SettingsStore) { Owner = this };
            dialog.ShowDialog();
            App.MainVm?.RefreshModelState();
        }

        private void OnOpenModelMenu(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || App.MainVm == null) return;
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                MinWidth = 260,
                Padding = new Thickness(6),
                FontFamily = FontFamily,
                FontSize = 13
            };
            menu.SetResourceReference(Control.BackgroundProperty, "BrushSurfaceElevated");
            menu.SetResourceReference(Control.ForegroundProperty, "BrushTextPrimary");
            menu.Items.Add(new MenuItem { Header = App.MainVm.IsBusy ? "选择模型 · 当前任务保持原模型" : "选择模型 · 自动保存", IsEnabled = false });
            foreach (var profile in App.Settings.Profiles)
            {
                string name = profile.Name;
                bool duplicateModel = App.Settings.Profiles.Count(p => string.Equals(p.ModelName, profile.ModelName, StringComparison.OrdinalIgnoreCase)) > 1;
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(profile.ModelName) ? name + "（未配置）" : profile.ModelName + (duplicateModel ? " · " + name : ""),
                    ToolTip = "配置：" + name + "；切换对下一任务生效",
                    IsCheckable = true,
                    IsChecked = !App.Settings.FakeMode && App.Settings.ActiveProfileName == name,
                    IsEnabled = !string.IsNullOrWhiteSpace(profile.BaseUrl) && !string.IsNullOrWhiteSpace(profile.ModelName),
                    Padding = new Thickness(12, 9, 12, 9)
                };
                item.Click += (_, __) =>
                {
                    try { App.MainVm.SelectModelProfile(name); }
                    catch (Exception) { MessageBox.Show(this, "模型切换未能保存，已保留原配置。请检查本地配置文件的访问权限。", "切换模型"); }
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "管理模型与连接设置…", Padding = new Thickness(12, 9, 12, 9) };
            settings.Click += (_, __) => OpenSettingsDialog(); menu.Items.Add(settings);
            button.ContextMenu = menu; menu.IsOpen = true;
        }

        private void OnOpenPermissionMenu(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsBusy || sender is not Button button) return;
            var menu = CreatePermissionMenu(button);
            button.ContextMenu = menu; menu.IsOpen = true;
        }
        private ContextMenu CreatePermissionMenu(Button button)
        {
            var vm = (MainViewModel)DataContext;
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = 300 };
            menu.SetResourceReference(Control.BackgroundProperty, "BrushSurfaceElevated");
            menu.SetResourceReference(Control.ForegroundProperty, "BrushTextPrimary");
            menu.Items.Add(new MenuItem { Header = "任务权限 · 选择后自动保存", IsEnabled = false });
            foreach (var mode in new[] { WorkAssistant.Core.TaskPermissionMode.Assisted, WorkAssistant.Core.TaskPermissionMode.FullAccess, WorkAssistant.Core.TaskPermissionMode.RequestApproval })
            {
                var content = new StackPanel { MaxWidth = 300 };
                content.Children.Add(new TextBlock { Text = WorkAssistant.Core.TaskPermissions.Label(mode), FontSize = 13, FontWeight = FontWeights.Medium });
                var detail = new TextBlock { Text = WorkAssistant.Core.TaskPermissions.Description(mode), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary"); content.Children.Add(detail);
                var item = new MenuItem { Header = content, IsCheckable = true, IsChecked = vm.SelectedPermissionMode == mode, Padding = new Thickness(12, 10, 12, 10) };
                item.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, WorkAssistant.Core.TaskPermissions.Label(mode));
                item.Click += (_, __) => vm.SelectPermissionMode(mode);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "范围：当前授权工作区；只读分析优先生效", IsEnabled = false, FontSize = 12 });
            return menu;
        }

        private void OpenDiagnostics(string taskId)
        {
            var dialog = new DiagnosticsWindow(App.TaskStore, taskId) { Owner = this };
            dialog.Show();
        }

        private void OnSkills(object sender, RoutedEventArgs e)
        {
            if (App.MainVm == null || App.MainVm.IsBusy) return;
            new SkillLibraryWindow(skill => App.MainVm.UseSkill(skill), App.MainVm.WorkspacePath) { Owner = this }.ShowDialog();
        }

        private void OnTaskCenter(object sender, RoutedEventArgs e)
        {
            var window = new TaskCenterWindow(task => App.MainVm?.RetryTask(task)) { Owner = this };
            window.Show();
        }

        private void OnAttachFiles(object sender, RoutedEventArgs e)
        {
            var vm = App.MainVm;
            if (vm == null || vm.IsBusy) return;
            if (!vm.IsAuthorized) { vm.ChooseWorkspaceCommand.Execute(null); if (!vm.IsAuthorized) return; }
            var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true,
                Filter = "支持的文件|*.csv;*.xlsx;*.docx;*.pptx;*.pdf;*.txt;*.md;*.json;*.log;*.xml", Title = "将文件副本附加到当前工作区" };
            if (picker.ShowDialog(this) != true) return;
            var imported = new System.Collections.Generic.List<string>();
            try
            {
                long total = 0;
                foreach (string path in picker.FileNames)
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (!WorkAssistant.Tools.ListFilesTool.SupportedExtensions.Contains(extension)) throw new IOException("文件类型不支持。");
                    using var input = VerifiedFiles.OpenRead(path, Path.GetDirectoryName(path)!);
                    total += input.Length;
                    if (input.Length > 10L * 1024 * 1024 || total > 100L * 1024 * 1024) throw new IOException("附件超限：单文件 10 MiB，本次合计 100 MiB。");
                    string filename = Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + Path.GetFileName(path);
                    var policy = new WorkspacePolicy(vm.WorkspacePath);
                    string relative = "inputs/" + filename;
                    string target = policy.ResolveOriginalReadablePath(relative);
                    try
                    {
                        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output); output.Flush(true);
                    }
                    catch { if (File.Exists(target)) File.Delete(target); throw; }
                    imported.Add(relative);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "附件导入未全部完成：" + ex.Message, "附加文件"); }
            if (imported.Count > 0)
            {
                vm.UserInput = (vm.UserInput + "\n已附加文件：" + string.Join("、", imported)).Trim();
                MessageBox.Show(this, "已导入 " + imported.Count + " 个文件副本。发送任务后将创建独立输入快照。", "附加文件");
            }
        }

        private void OnPreviewArtifact(object sender, RoutedEventArgs e)
        {
            var card = (sender as Button)?.CommandParameter as Presentation.ArtifactCardVm ?? App.MainVm?.SelectedArtifact;
            if (card == null) return;
            var task = App.TaskStore.LoadTasks().FirstOrDefault(t => t.Artifacts.Any(a =>
                a.Path == card.SourcePath && (card.WorkspaceRoot.Length == 0 || string.Equals(t.WorkspaceRoot, card.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))));
            var artifact = task?.Artifacts.FirstOrDefault(a => a.Path == card.SourcePath);
            if (task != null && artifact != null) new ArtifactPreviewWindow(task, artifact) { Owner = this }.Show();
        }

        private void OnMaintenance(object sender, RoutedEventArgs e)
        {
            new MaintenanceWindow { Owner = this }.ShowDialog();
        }
    }
}
