using System;
using System.Windows;
using System.Windows.Controls;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Desktop
{
    /// <summary>执行详情：只读展示任务原始事件（诊断用途，等宽字体）。</summary>
    public partial class DiagnosticsWindow : Window
    {
        public DiagnosticsWindow(LocalTaskStore store, string taskId)
        {
            InitializeComponent();
            Title = "执行详情 · " + taskId.Substring(0, Math.Min(8, taskId.Length));
            EventsList.Items.Clear();
            foreach (var e in store.LoadEvents(taskId))
            {
                var line = $"[{e.UtcTime.ToLocalTime():HH:mm:ss.fff}] {e.Kind}" +
                           (e.ToolName != null ? " · " + e.ToolName : "") +
                           (e.ToolOk != null ? (e.ToolOk == true ? " · 成功" : " · 失败") : "") +
                           "  {e.Summary}";
                EventsList.Items.Add(line);
            }
            if (EventsList.Items.Count == 0)
            {
                EventsList.Items.Add("（该任务没有事件记录）");
            }
            string? failure = store.LoadDocument("host-failure", taskId);
            if (!string.IsNullOrEmpty(failure))
            {
                EventsList.Items.Add("宿主诊断（仅阶段、异常类型与方法名，不含密钥或输入内容）：");
                EventsList.Items.Add(failure);
            }
        }
    }
}
