using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WorkAssistant.Core;
using WorkAssistant.Desktop.Presentation;

namespace WorkAssistant.Desktop
{
    public partial class MainWindow
    {
        private void RunSidebarAction(Action action)
        {
            if (App.MainVm?.IsBusy != false) return;
            try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "项目与对话"); }
        }
        private void OnCreateProject(object sender, RoutedEventArgs e) => RunSidebarAction(() => EditProject(null));
        private void EditProject(ProjectRecord? project)
        {
            var editor = new ProjectEditorWindow(project, App.MainVm!.WorkspacePath) { Owner = this };
            if (editor.ShowDialog() == true && editor.Result != null) App.MainVm.SaveProject(editor.Result, project == null);
        }
        private static MenuItem SidebarMenuItem(string title, Action action)
        {
            var item = new MenuItem { Header = title, Padding = new Thickness(14, 9, 14, 9) };
            item.Click += (_, __) => action(); return item;
        }
        private static void ShowSidebarMenu(Button anchor, ContextMenu menu)
        {
            menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.MinWidth = 190; anchor.ContextMenu = menu; menu.IsOpen = true;
        }
        private void OnToggleProject(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ProjectItemVm project }) project.IsExpanded = !project.IsExpanded;
            e.Handled = true;
        }
        private void OnProjectMenu(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ProjectItemVm project } button) return;
            var menu = new ContextMenu();
            menu.Items.Add(SidebarMenuItem("新建项目对话", () => RunSidebarAction(() => App.MainVm!.OpenProject(project.Project))));
            menu.Items.Add(SidebarMenuItem("编辑项目…", () => RunSidebarAction(() => EditProject(project.Project))));
            menu.Items.Add(SidebarMenuItem("打开工作目录", () => RunSidebarAction(() =>
            {
                if (!Directory.Exists(project.WorkspaceRoot)) throw new DirectoryNotFoundException("目录已不存在，请编辑项目重新关联。");
                Process.Start(new ProcessStartInfo(project.WorkspaceRoot) { UseShellExecute = true });
            })));
            menu.Items.Add(new Separator());
            menu.Items.Add(SidebarMenuItem("删除项目…", () => RunSidebarAction(() =>
            {
                if (MessageBox.Show(this, "删除项目“" + project.Name + "”？\n\n仅移除项目分类，其中的对话将移至普通对话列表。\n工作目录、原文件、成果和任务记录全部保留。", "删除项目（保留文件）", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    App.MainVm!.RemoveProject(project.Project);
            })));
            ShowSidebarMenu(button, menu); e.Handled = true;
        }
        private void OnConversationMenu(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: HistoryItemVm conversation } button) return;
            var menu = new ContextMenu();
            menu.Items.Add(SidebarMenuItem("重命名…", () => RunSidebarAction(() =>
            {
                var session = App.TaskStore.LoadSession(conversation.SessionId); if (session == null) return;
                var editor = new ConversationNameWindow(session.Title) { Owner = this };
                if (editor.ShowDialog() == true) App.MainVm!.RenameConversation(session.SessionId, editor.Result);
            })));
            var move = new MenuItem { Header = "移入项目", Padding = new Thickness(14, 9, 14, 9), ToolTip = "只改变归类，不搬动文件；既有对话保留原工作目录。" };
            foreach (var project in App.TaskStore.LoadProjects())
            {
                var target = project;
                var item = SidebarMenuItem(project.Name, () => RunSidebarAction(() => App.MainVm!.MoveConversation(conversation.SessionId, target)));
                item.IsCheckable = true; item.IsChecked = project.ProjectId == conversation.ProjectId; move.Items.Add(item);
            }
            if (move.Items.Count == 0) move.Items.Add(new MenuItem { Header = "请先新建项目", IsEnabled = false });
            menu.Items.Add(move);
            var ungroup = SidebarMenuItem("移出项目", () => RunSidebarAction(() => App.MainVm!.MoveConversation(conversation.SessionId, null)));
            ungroup.IsEnabled = !string.IsNullOrEmpty(conversation.ProjectId); menu.Items.Add(ungroup);
            menu.Items.Add(new Separator());
            menu.Items.Add(SidebarMenuItem("从对话列表删除…", () => RunSidebarAction(() =>
            {
                if (MessageBox.Show(this, "从左侧列表删除这段对话？\n任务记录和所有文件仍保留，可在任务中心查看。", "移除对话", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    App.MainVm!.RemoveConversation(conversation.SessionId);
            })));
            ShowSidebarMenu(button, menu); e.Handled = true;
        }
        private void OnSidebarMore(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            var menu = new ContextMenu();
            menu.Items.Add(SidebarMenuItem("版本、诊断与更新…", () => OnMaintenance(sender, e)));
            ShowSidebarMenu(button, menu);
        }
    }
}
