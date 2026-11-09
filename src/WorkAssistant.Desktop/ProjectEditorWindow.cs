using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WorkAssistant.Core;

namespace WorkAssistant.Desktop
{
    internal sealed class ProjectEditorWindow : Window
    {
        public ProjectRecord? Result { get; private set; }
        public ProjectEditorWindow(ProjectRecord? existing, string currentDirectory)
        {
            Title = existing == null ? "新建项目 · 晓助手" : "编辑项目 · 晓助手";
            Width = 620; Height = 570; MinWidth = 500; MinHeight = 490; ProductionUi.Style(this);
            var layout = new DockPanel { Margin = new Thickness(28) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
            var form = new StackPanel(); layout.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            form.Children.Add(new TextBlock { Text = existing == null ? "给这项工作一个独立空间" : "编辑项目", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
            TextBox Field(string label, string value, int limit)
            {
                form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 7) });
                var field = new TextBox { Text = value, MaxLength = limit, MinHeight = 40, Padding = new Thickness(10, 8, 10, 8) };
                field.SetResourceReference(StyleProperty, "InputBox"); form.Children.Add(field); return field;
            }
            var name = Field("项目名称", existing?.Name ?? "", 80);
            var description = Field("项目说明", existing?.Description ?? "", 1500);
            description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap; description.Height = 80;
            var directory = Field("工作目录", existing?.WorkspaceRoot ?? currentDirectory, 240); directory.IsReadOnly = true;
            form.Children.Add(ProductionUi.Button("选择目录…", () =>
            {
                using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "选择项目工作目录，原有文件不会被搬动或删除", ShowNewFolderButton = true };
                if (Directory.Exists(directory.Text)) picker.SelectedPath = directory.Text;
                if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) directory.Text = picker.SelectedPath;
            }));
            form.Children.Add(new TextBlock { Text = "项目用于整理对话并关联目录，不会搬动原文件。修改目录只影响项目中的新对话，既有对话保留原目录。删除项目也不会删除任何工作文件。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), LineHeight = 22 });
            actions.Children.Add(ProductionUi.Button("取消", Close));
            var save = ProductionUi.Button(existing == null ? "创建项目" : "保存修改", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); throw new InvalidOperationException("请填写项目名称。"); }
                if (!Directory.Exists(directory.Text)) throw new InvalidOperationException("请选择存在的工作目录。");
                Result = new ProjectRecord { ProjectId = existing?.ProjectId ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim().Replace("\r", " ").Replace("\n", " "),
                    Description = description.Text.Trim(), WorkspaceRoot = Path.GetFullPath(directory.Text), CreatedUtc = existing?.CreatedUtc ?? DateTime.UtcNow };
                DialogResult = true;
            });
            save.SetResourceReference(StyleProperty, "StudioPrimary"); actions.Children.Add(save);
            Content = layout; Loaded += (_, __) => name.Focus();
        }
    }
    internal sealed class ConversationNameWindow : Window
    {
        public string Result { get; private set; } = "";
        public ConversationNameWindow(string title)
        {
            Title = "重命名对话"; Width = 480; Height = 220; MinWidth = 400; ResizeMode = ResizeMode.NoResize; ProductionUi.Style(this);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "对话名称", FontSize = 20, FontWeight = FontWeights.SemiBold });
            var input = new TextBox { Text = title, MaxLength = 80, MinHeight = 40, Margin = new Thickness(0, 16, 0, 16) }; input.SetResourceReference(StyleProperty, "InputBox"); panel.Children.Add(input);
            panel.Children.Add(ProductionUi.Button("保存名称", () => { if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; } Result = input.Text.Trim(); DialogResult = true; }));
            Content = panel; Loaded += (_, __) => { input.Focus(); input.SelectAll(); };
        }
    }
}
