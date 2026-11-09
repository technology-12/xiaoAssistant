using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Desktop
{
    internal sealed class SkillLibraryWindow : Window
    {
        private readonly SkillCatalog _catalog;
        private readonly ListBox _list = new() { DisplayMemberPath = "Name", BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        private readonly TextBox _id = Field(), _name = Field(), _description = Field(), _instructions = Field();
        private readonly Dictionary<string, CheckBox> _tools = new();
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        public SkillLibraryWindow(Action<SkillDefinition?> use) : this(use, "") { }
        public SkillLibraryWindow(Action<SkillDefinition?> use, string workspace)
        {
            _catalog = string.IsNullOrWhiteSpace(workspace) ? new SkillCatalog() : new SkillCatalog(workspace);
            Title = "技能工作室 · 晓助手"; Width = 1080; Height = 780; MinWidth = 780; MinHeight = 560; ProductionUi.Style(this);
            var layout = new Grid { Margin = new Thickness(28) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
            heading.Children.Add(new TextBlock { Text = "把你的方法，变成可复用的技能", FontSize = 25, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "选择一个工作流，或让技能创建器协助起草。草稿经你审阅保存后才加入技能库。", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
            layout.Children.Add(heading);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) }); body.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(body, 1); layout.Children.Add(body);
            var navigation = new DockPanel { Margin = new Thickness(0, 0, 24, 0) };
            var navActions = new StackPanel();
            navActions.Children.Add(ProductionUi.Button("用技能创建器起草", () => { var creator = _catalog.Find("skill-creator"); if (creator == null) throw new InvalidOperationException("内置技能创建器文件缺失，请使用完整候选目录。"); use(creator); Close(); }));
            navActions.Children.Add(ProductionUi.Button("新建空白技能", () => { _list.SelectedItem = null; Populate(new SkillDefinition { AllowedTools = new List<string> { "ask_user" } }); }));
            navActions.Children.Add(ProductionUi.Button("导入技能草稿", () =>
            {
                var picker = new OpenFileDialog { Filter = "技能草稿|*.skill.json", Title = "选择草稿元数据（同目录需有配套 SKILL.md）" };
                if (picker.ShowDialog(this) == true) { _list.SelectedItem = null; Populate(_catalog.ImportDraft(picker.FileName)); _status.Text = "草稿已载入。请审阅说明与工具列表，再保存为新技能。"; }
            }));
            navActions.Children.Add(ProductionUi.Button("使用通用助手", () => { use(null); Close(); }));
            DockPanel.SetDock(navActions, Dock.Bottom); navigation.Children.Add(navActions); navigation.Children.Add(_list); body.Children.Add(navigation);
            var editor = new StackPanel();
            Add(editor, "技能 ID · 小写字母、数字和连字符", _id); Add(editor, "显示名称", _name); Add(editor, "适用场景", _description);
            _description.TextWrapping = TextWrapping.Wrap; _description.MaxLength = 500;
            _instructions.AcceptsReturn = true; _instructions.TextWrapping = TextWrapping.Wrap; _instructions.Height = 210; _instructions.MaxLength = 30000; _instructions.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Add(editor, "工作方法", _instructions);
            editor.Children.Add(new TextBlock { Text = "这个技能可以使用的工具", Margin = new Thickness(0, 16, 0, 10), FontWeight = FontWeights.SemiBold });
            var toolPanel = new WrapPanel();
            string[] labels = { "列出输入文件", "预览 CSV", "预览 Excel", "计算 CSV", "筛选与汇总", "导出表格与报告", "导出 Excel", "生成 Word", "填充 Word 模板", "查看模板字段", "提取 PDF 文字", "生成归档计划", "执行已批准归档", "询问业务口径", "创建技能草稿" };
            for (int i = 0; i < SkillRules.ToolNames.Length; i++)
            {
                string tool = SkillRules.ToolNames[i]; var check = new CheckBox { Content = i < labels.Length ? labels[i] : ToolLabel(tool), ToolTip = tool, Margin = new Thickness(0, 0, 18, 12), MinHeight = 24 };
                _tools[tool] = check; toolPanel.Children.Add(check);
            }
            editor.Children.Add(toolPanel);
            editor.Children.Add(new TextBlock { Text = "技能仅组合内置能力；文件修改仍需逐次确认。导入不会安装脚本、程序或访问新的网络服务。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
            var actions = new WrapPanel();
            actions.Children.Add(ProductionUi.Button("保存为新技能", () =>
            {
                var skill = ReadForm(); _catalog.SaveNew(skill); Refresh(); _list.SelectedItem = ((List<SkillDefinition>)_list.ItemsSource).First(s => s.Id == skill.Id); _status.Text = "技能已保存，现在可以使用。";
            }));
            actions.Children.Add(ProductionUi.Button("使用已保存技能", () => { if (_list.SelectedItem is SkillDefinition skill) { use(skill); Close(); } else _status.Text = "请先保存草稿，或在左侧选择已有技能。"; }));
            editor.Children.Add(actions); editor.Children.Add(_status);
            var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            _list.SelectionChanged += (_, __) => { if (_list.SelectedItem is SkillDefinition skill) { Populate(skill); _status.Text = (skill.ProjectLocal ? "当前项目 · .claude/skills" : skill.BuiltIn ? "内置技能" : "我的技能") + " · v" + skill.Version + " · 修改后请使用新的 ID 另存到个人技能库。"; } };
            Content = layout; Refresh(); if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }
        private static TextBox Field()
        {
            var field = new TextBox { MinHeight = 38, Padding = new Thickness(10, 8, 10, 8) };
            field.SetResourceReference(FrameworkElement.StyleProperty, "InputBox"); return field;
        }
        private static string ToolLabel(string name) => name switch {
            "read_document_text" => "阅读文档", "search_input_text" => "检索输入", "update_task_plan" => "维护任务计划",
            "read_task_history" => "回查任务", "list_skills" => "浏览技能", "read_skill" => "阅读技能",
            "delegate_tasks" => "派发子任务", "import_task_artifacts" => "接收子任务成果", "list_project_commands" => "浏览项目命令",
            "inspect_office" => "检查 Word 与 PPT", "write_word" => "编写 Word", "edit_word" => "修改 Word 副本",
            "write_presentation" => "编写 PPT", "edit_presentation" => "修改 PPT 副本", "plan_text_edit" => "生成文本提案",
            "validate_text_edit" => "核验文本版本", "apply_text_edit" => "审批提交文本", "rollback_text_edit" => "审批回滚文本", _ => name };
        private static void Add(Panel panel, string label, TextBox field) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 6) }); panel.Children.Add(field); }
        private void Refresh() { _list.ItemsSource = _catalog.Load(); if (_catalog.LoadErrors.Count > 0) _status.Text = string.Join("\n", _catalog.LoadErrors); }
        private void Populate(SkillDefinition skill) { _id.Text = skill.Id; _name.Text = skill.Name; _description.Text = skill.Description; _instructions.Text = skill.Instructions; foreach (var pair in _tools) pair.Value.IsChecked = skill.AllowedTools.Contains(pair.Key); }
        private SkillDefinition ReadForm() => new() { Id = _id.Text.Trim(), Name = _name.Text.Trim(), Description = _description.Text.Trim(), Instructions = _instructions.Text.Trim(), AllowedTools = _tools.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList() };
    }
}
