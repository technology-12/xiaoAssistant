using System;
using System.Collections.ObjectModel;

namespace WorkAssistant.Desktop.Presentation
{
    /// <summary>对话条目类型：从事件 Kind / 工具名 / 任务状态构建，不解析展示字符串。</summary>
    public enum ConversationItemKind
    {
        /// <summary>用户消息：浅底右对齐。</summary>
        UserMessage,
        /// <summary>助手回复：无外框正文，可挂文件卡；流式期间文本增量更新。</summary>
        AssistantMessage,
        /// <summary>执行步骤组：运行中展开、完成后折叠为“已完成 N 个步骤”。</summary>
        ExecutionGroup,
        /// <summary>失败信息：可读原因 + 下一动作，可展开错误码。</summary>
        ErrorMessage,
        /// <summary>系统提示（如“已加入队列”“正在查看历史任务”）。</summary>
        SystemNotice,
        UserQuestion
    }

    /// <summary>单个执行步骤（自然语言 + 时间 + 工具名 + 状态）。</summary>
    public sealed class TaskStepVm
    {
        public string Text { get; }
        public string TimeText { get; }
        public string? ToolName { get; }
        public bool IsFailed { get; }
        public bool IsRunning { get; }
        public string StateText => IsFailed ? "失败" : IsRunning ? "进行中" : "完成";

        public TaskStepVm(string text, string timeText, string? toolName, bool isFailed = false, bool isRunning = false)
        {
            Text = text;
            TimeText = timeText;
            ToolName = toolName;
            IsFailed = isFailed;
            IsRunning = isRunning;
        }
    }

    /// <summary>助手回复下的成果文件卡。</summary>
    public sealed class ArtifactCardVm
    {
        public string FileName { get; }
        public string Path { get; }
        public string SourcePath { get; }
        public string WorkspaceRoot { get; }
        public string TypeText { get; }
        public string SizeText { get; }
        public string Sha256 { get; }
        /// <summary>打开文件用的物理路径。</summary>
        public string FullPath { get; }
        public bool FileExists { get; }
        /// <summary>文件卡小图标几何（Icon.Table / Icon.Document）。</summary>
        public string IconKey { get; }

        public ArtifactCardVm(string fileName, string path, string typeText, string sizeText,
            string sha256, string fullPath, bool fileExists, string sourcePath = "", string workspaceRoot = "")
        {
            FileName = fileName;
            Path = path;
            SourcePath = sourcePath.Length == 0 ? path : sourcePath;
            WorkspaceRoot = workspaceRoot;
            TypeText = typeText;
            SizeText = sizeText;
            Sha256 = sha256;
            FullPath = fullPath;
            FileExists = fileExists;
            IconKey = IsTableType(fileName, typeText) ? "Icon.Table" : "Icon.Document";
        }

        private static bool IsTableType(string fileName, string typeText)
        {
            return fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   typeText.IndexOf("csv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeText.IndexOf("表格", StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>对话条目统一展示模型（助手消息支持流式文本更新）。</summary>
    public sealed class ConversationItem : ObservableObject
    {
        public QuestionCardVm? QuestionCard { get; private set; }
        public static ConversationItem Prompt(QuestionCardVm card) => new(ConversationItemKind.UserQuestion, card.Question, "") { QuestionCard = card };
        public ConversationItemKind Kind { get; }
        public string Text { get; private set; }
        public string TimeText { get; }
        /// <summary>ExecutionGroup：步骤列表。</summary>
        public ObservableCollection<TaskStepVm> Steps { get; } = new();
        /// <summary>组折叠态标题（如“已完成 4 个步骤”）。</summary>
        public string GroupTitle { get; private set; }
        /// <summary>步骤组是否展开。</summary>
        public bool IsExpanded { get; private set; }
        /// <summary>ErrorMessage：稳定错误码（可展开）。</summary>
        public string? ErrorCode { get; }
        /// <summary>ErrorMessage：建议的下一动作。</summary>
        public string? NextAction { get; }
        /// <summary>AssistantMessage：成果文件卡。</summary>
        public ObservableCollection<ArtifactCardVm> Artifacts { get; } = new();
        /// <summary>是否带成功强调。</summary>
        public bool IsSuccess { get; }
        /// <summary>助手消息是否仍在流式接收中。</summary>
        public bool IsStreaming { get; private set; }

        private ConversationItem(ConversationItemKind kind, string text, string timeText,
            string? errorCode = null, string? nextAction = null, bool isSuccess = false)
        {
            Kind = kind;
            Text = text;
            TimeText = timeText;
            GroupTitle = text;
            IsExpanded = true;
            ErrorCode = errorCode;
            NextAction = nextAction;
            IsSuccess = isSuccess;
        }

        public static ConversationItem User(string text, DateTime localTime) =>
            new(ConversationItemKind.UserMessage, text, localTime.ToString("HH:mm:ss"));

        public static ConversationItem Assistant(string text, DateTime localTime, bool success = true) =>
            new(ConversationItemKind.AssistantMessage, text, localTime.ToString("HH:mm:ss"), isSuccess: success)
            {
                IsStreaming = false
            };

        /// <summary>新建流式助手条目（文本随后增量更新）。</summary>
        public static ConversationItem AssistantStreaming() =>
            new(ConversationItemKind.AssistantMessage, "", DateTime.Now.ToString("HH:mm:ss"))
            {
                IsStreaming = true
            };

        public static ConversationItem Notice(string text) =>
            new(ConversationItemKind.SystemNotice, text, "");

        public static ConversationItem Error(string message, string? errorCode, string? nextAction, DateTime localTime) =>
            new(ConversationItemKind.ErrorMessage, message, localTime.ToString("HH:mm:ss"), errorCode, nextAction);

        /// <summary>新建执行步骤组（运行中展开）。</summary>
        public static ConversationItem Execution(DateTime localTime)
        {
            return new ConversationItem(ConversationItemKind.ExecutionGroup, "正在准备", localTime.ToString("HH:mm"))
            {
                IsRunningGroup = true
            };
        }

        public bool IsRunningGroup { get; private set; }

        /// <summary>流式：追加文本增量（UI 线程调用）。</summary>
        public void AppendStreamingText(string delta)
        {
            Text += delta;
            OnPropertyChanged(nameof(Text));
        }

        /// <summary>流式结束：固化最终文本与成果。</summary>
        public void FinishStreaming(string finalText)
        {
            IsStreaming = false;
            Text = finalText;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsStreaming));
        }

        /// <summary>流式中断（失败/取消）：标注不完整，不伪装完整回复。</summary>
        public void AbortStreaming(string note)
        {
            IsStreaming = false;
            if (Text.Length > 0) Text += "\n（" + note + "）";
            else Text = "（" + note + "）";
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsStreaming));
        }

        public void AddStep(TaskStepVm step)
        {
            Steps.Add(step);
            if (IsRunningGroup)
            {
                GroupTitle = Steps.Count == 0 ? "正在准备" : $"正在执行（{Steps.Count} 个步骤）";
            }
        }

        public void FinishStep(string timeText)
        {
            // 结束最后一个“进行中”步骤。
            for (var i = Steps.Count - 1; i >= 0; i--)
            {
                if (Steps[i].IsRunning)
                {
                    Steps[i] = new TaskStepVm(Steps[i].Text, timeText, Steps[i].ToolName,
                        Steps[i].IsFailed, false);
                    break;
                }
            }
        }

        public void MarkLastStepFailed(string timeText)
        {
            for (var i = Steps.Count - 1; i >= 0; i--)
            {
                if (Steps[i].IsRunning)
                {
                    Steps[i] = new TaskStepVm(Steps[i].Text, timeText, Steps[i].ToolName,
                        true, false);
                    break;
                }
            }
        }

        /// <summary>任务结束：折叠标题 + 完成折叠/失败展开。</summary>
        public void Complete(bool ok, bool cancelled)
        {
            IsRunningGroup = false;
            GroupTitle = cancelled
                ? $"任务已停止（{Steps.Count} 个步骤）"
                : ok ? $"已完成 {Steps.Count} 个步骤" : $"任务失败（{Steps.Count} 个步骤）";
            IsExpanded = !ok || cancelled;
            OnPropertyChanged(nameof(GroupTitle));
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    /// <summary>侧栏历史项（会话）。</summary>
    public sealed class HistoryItemVm : ObservableObject
    {
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value, nameof(IsSelected)); }
        public string ProjectId { get; set; } = "";
        public string SessionId { get; }
        public string TaskId { get; }
        public string Title { get; }
        public string StateText { get; }
        public string TimeText { get; }
        public DateTime CreatedLocal { get; }
        /// <summary>完成/失败/取消/中断着色。</summary>
        public bool IsFailed { get; }
        public bool IsRunning { get; }

        public HistoryItemVm(string sessionId, string taskId, string title, string stateText,
            DateTime createdLocal, bool isFailed, bool isRunning)
        {
            SessionId = sessionId;
            TaskId = taskId;
            Title = title.Length > 40 ? title.Substring(0, 40) + "…" : title;
            StateText = stateText;
            TimeText = createdLocal.ToString("HH:mm");
            CreatedLocal = createdLocal;
            IsFailed = isFailed;
            IsRunning = isRunning;
        }
    }

    /// <summary>历史日期分组。</summary>
    public sealed class HistoryGroupVm
    {
        public string Header { get; }
        public ObservableCollection<HistoryItemVm> Items { get; } = new();

        public HistoryGroupVm(string header)
        {
            Header = header;
        }
    }
}
