using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WorkAssistant.Core;
using WorkAssistant.Desktop.Presentation;
using WorkAssistant.Infrastructure;
using WorkAssistant.OpenCode;
using WorkAssistant.Tools;

namespace WorkAssistant.Desktop
{
    /// <summary>快捷任务（仅展示已实现能力，点击只填充输入不执行）。</summary>
    public sealed class SuggestionVm
    {
        public string Title { get; }
        public string Description { get; }
        public string RequestText { get; }
        public string IconKey { get; }

        public SuggestionVm(string title, string description, string requestText, string iconKey)
        {
            Title = title;
            Description = description;
            RequestText = requestText;
            IconKey = iconKey;
        }
    }

    /// <summary>
    /// 主窗口 ViewModel：会话连续对话 + 任务生命周期 + 队列 + 流式渲染 + 历史回看。
    /// 运行中/取消中锁定：模式、工作区、任务切换；新请求进入队列串行执行（本地写工具不并发）。
    /// </summary>
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        public const string StandardDemoRequest =
            "汇总 inputs 文件夹中的销售 CSV，按部门统计销售额，生成汇总表和报告。";

        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;
        private readonly LocalTaskStore _taskStore;
        private readonly Dispatcher _ui;
        private readonly Func<string?> _pickWorkspace;
        private readonly Action _openSettings;
        private readonly Action<string> _openDiagnostics;
        private CancellationTokenSource? _cts;
        private ConversationItem? _runningGroup;
        private ConversationItem? _streamingItem;

        /// <summary>当前会话 ID（连续对话共享；新建任务后为空）。</summary>
        private string? _currentSessionId;
        /// <summary>任务队列：本地写工具串行执行（正式版 §7.4）。</summary>
        private readonly Queue<TaskRecord> _pendingRequests = new();
        private bool _queuePaused;

        public ObservableCollection<ConversationItem> Conversation { get; } = new();
        public ObservableCollection<HistoryGroupVm> HistoryGroups { get; } = new();
        public IList<SuggestionVm> Suggestions { get; } = new List<SuggestionVm>
        {
            new("创建一个技能", "把常用的工作方法，保存为自己的技能",
                "请帮我创建一个办公技能，先了解我的工作目标和需要的交付。", "Icon.BrandStar"),
            new("整理 Excel 表格", "查看数据结构，明确口径，再计算和导出",
                "查看 inputs 中的 Excel 表头，帮助我确定字段、筛选口径及需要导出的结果。", "Icon.Table"),
            new("提取 PDF 文字", "提取文本型 PDF 的内容并生成带页码的 Markdown",
                "提取 inputs 中 PDF 文件的文字内容，生成带页码引用的 Markdown 成果。", "Icon.Document"),
            new("整理输入文件", "生成分类归档计划，确认后执行（不删除文件）",
                "把 inputs 中的演示文件整理归档到子文件夹，先给出计划，确认后执行。", "Icon.Folder")
        };

        public RelayCommand SendCommand { get; }
        public RelayCommand NewTaskCommand { get; }
        public RelayCommand RetryCommand { get; }
        public RelayCommand ShowArtifactCommand { get; }
        public RelayCommand ChooseWorkspaceCommand { get; }
        public RelayCommand OpenWorkspaceCommand { get; }
        public RelayCommand OpenOutputDirectoryCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand OpenArtifactCommand { get; }
        public RelayCommand RevealArtifactCommand { get; }
        public RelayCommand CopyPathCommand { get; }
        public RelayCommand FillSuggestionCommand { get; }
        public RelayCommand LoadHistoryTaskCommand { get; }
        public RelayCommand CloseDrawerCommand { get; }
        public RelayCommand ToggleSidebarCommand { get; }
        public RelayCommand ShowDiagnosticsCommand { get; }

        // ---- 状态属性 -------------------------------------------------------

        private string _workspacePath = "";
        public string WorkspacePath
        {
            get => _workspacePath;
            private set
            {
                if (SetProperty(ref _workspacePath, value, nameof(WorkspacePath)))
                {
                    OnPropertyChanged(nameof(WorkspaceName));
                    OnPropertyChanged(nameof(WorkspaceLocation));
                }
            }
        }

        public string WorkspaceName => WorkspacePath.Length == 0
            ? ""
            : new DirectoryInfo(WorkspacePath).Name;

        public string WorkspaceLabel => string.IsNullOrEmpty(WorkspaceName) ? "选择工作区" : WorkspaceName;
        public string WorkspaceLocation => WorkspacePath.Length == 0 ? "尚未选择工作目录" : "工作目录：" + WorkspacePath;
        public bool IsHistoryEmpty => HistoryGroups.Count == 0;
        public string WelcomeTitle => _selectedProject != null ? _selectedProject.Name : DateTime.Now.Hour < 11 ? "早上好，今天想完成什么？" : DateTime.Now.Hour < 18 ? "下午好，让工作轻一点。" : "晚上好，留一点时间给自己。";
        public string PageTitle => HasConversation ? "当前任务" : "开始工作";
        public string ConversationTitle => ConversationHeading;
        public string FooterText => IsDemoMode ? "演示模式 · 不连接模型服务" : KernelDisplay + " · 本地文件操作受控";
        public string KernelDisplay { get; private set; } = "OpenCode 远程内核";

        private bool _isAuthorized;
        public bool IsAuthorized
        {
            get => _isAuthorized;
            private set
            {
                if (SetProperty(ref _isAuthorized, value, nameof(IsAuthorized)))
                {
                    OnPropertyChanged(nameof(SendLockedReason));
                }
            }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value, nameof(IsRunning)))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(SendLockedReason));
                    OnPropertyChanged(nameof(InputLocked));
                    OnPropertyChanged(nameof(ModeLocked));
                }
            }
        }

        private bool _isCancelling;
        public bool IsCancelling
        {
            get => _isCancelling;
            private set
            {
                if (SetProperty(ref _isCancelling, value, nameof(IsCancelling)))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(InputLocked));
                    OnPropertyChanged(nameof(ModeLocked));
                }
            }
        }

        /// <summary>运行或取消中：输入与全部切换锁定。</summary>
        private bool _isAwaitingPreflight;
        public bool IsBusy => IsRunning || IsCancelling || IsLoadingConversation || _isAwaitingPreflight;
        public bool InputLocked => IsViewingHistory || !IsAuthorized || IsLoadingConversation || _conversationNeedsReview || _isAwaitingPreflight;
        public bool ModeLocked => IsBusy;

        /// <summary>发送不可用时给出原因（输入区提示）。</summary>
        public string SendLockedReason
        {
            get
            {
                if (IsLoadingConversation) return "正在打开对话…";
                if (_isAwaitingPreflight) return "请先回答对话中的确认卡片";
                if (_conversationNeedsReview) return "本对话有待复核的文件变更，请先查看任务中心；可新建对话开展其他工作。";
                if (!IsAuthorized) return "请先选择并授权工作目录";
                if (IsCancelling) return "正在取消当前任务…";
                if (IsRunning) return "任务运行中，新请求将加入队列";
                if (IsViewingHistory) return "正在查看历史任务，点击“新建任务”开始新任务";
                return "";
            }
        }

        private bool _isViewingHistory;
        public bool IsViewingHistory
        {
            get => _isViewingHistory;
            private set
            {
                if (SetProperty(ref _isViewingHistory, value, nameof(IsViewingHistory)))
                {
                    OnPropertyChanged(nameof(InputLocked));
                    OnPropertyChanged(nameof(SendLockedReason));
                    OnPropertyChanged(nameof(HasConversation));
                }
            }
        }

        public bool HasConversation => Conversation.Count > 0 || IsViewingHistory;

        private string _userInput = "";
        public string UserInput
        {
            get => _userInput;
            set => SetProperty(ref _userInput, value, nameof(UserInput));
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value, nameof(StatusText));
        }

        private ArtifactCardVm? _selectedArtifact;
        public ArtifactCardVm? SelectedArtifact
        {
            get => _selectedArtifact;
            private set
            {
                SetProperty(ref _selectedArtifact, value, nameof(SelectedArtifact));
                OnPropertyChanged(nameof(IsDrawerOpen));
            }
        }

        public bool IsDrawerOpen => SelectedArtifact != null;

        private bool _sidebarCollapsed;
        public bool SidebarCollapsed
        {
            get => _sidebarCollapsed;
            private set => SetProperty(ref _sidebarCollapsed, value, nameof(SidebarCollapsed));
        }

        // ---- 模型状态（结构化，不解析字符串） -------------------------------

        public bool IsDemoMode => _settings.FakeMode;
        public string ModeBadge => _settings.FakeMode ? "演示模式" : "在线模型";
        /// <summary>模型显示名（顶栏）。</summary>
        public string ModelDisplay => _settings.FakeMode
            ? "演示模型"
            : (_settings.ModelName.Length > 0 ? _settings.ModelName : "未配置模型");
        /// <summary>连接状态文字；仅在线且测试成功时显示可用点。</summary>
        public string ConnectionText { get; private set; } = "";
        public bool ShowConnectionDot { get; private set; }

        public void RefreshModelState()
        {
            try { KernelDisplay = new OpenCodeConnectionStore().Load().Enabled ? "OpenCode 远程内核" : "本机 C# 增强内核"; }
            catch { KernelDisplay = "内核连接待配置"; }
            OnPropertyChanged(nameof(KernelDisplay)); OnPropertyChanged(nameof(FooterText));
            if (_settings.FakeMode)
            {
                ConnectionText = "";
                ShowConnectionDot = false;
            }
            else if (_settings.BaseUrl.Trim().Length == 0 || _settings.ModelName.Trim().Length == 0)
            {
                ConnectionText = "未配置";
                ShowConnectionDot = false;
            }
            else
            {
                var matches = App.LastConnectionFingerprint == ModelSettings.From(_settings).Fingerprint();
                var profile = _settings.ActiveProfile;
                var persisted = ModelCapabilityCache.IsFresh(profile, ModelSettings.From(_settings), DateTime.UtcNow);
                ConnectionText = matches ? App.LastConnectionState : persisted ? "上次检测 " + profile.LastVerifiedUtc!.Value.ToLocalTime().ToString("MM-dd HH:mm") : "未测试";
                ShowConnectionDot = matches && App.LastConnectionOk;
            }
            OnPropertyChanged(nameof(IsDemoMode));
            OnPropertyChanged(nameof(ModeBadge));
            OnPropertyChanged(nameof(ModelDisplay));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(ShowConnectionDot));
        }

        public void SelectModelProfile(string profileName)
        {
            var profile = _settings.Profiles.FirstOrDefault(p => p.Name == profileName)
                ?? throw new InvalidOperationException("模型配置已不存在，请重新打开模型列表。");
            string key = _settingsStore.LoadEffectiveApiKey(profile);
            string previousName = _settings.ActiveProfileName, previousKey = _settings.ApiKey;
            bool previousMode = _settings.FakeMode;
            _settings.ActiveProfileName = profile.Name;
            _settings.ApiKey = key;
            _settings.FakeMode = false;
            try { _settingsStore.Save(_settings); }
            catch
            {
                _settings.ActiveProfileName = previousName; _settings.ApiKey = previousKey; _settings.FakeMode = previousMode;
                RefreshModelState(); throw;
            }
            RefreshModelState();
            if (!IsBusy) StatusText = "已切换到 " + profile.ModelName;
        }

        /// <summary>设置窗口测试连接后回写状态。</summary>
        public void ReportConnectionResult(bool ok, string stateText, ModelSettings? tested = null)
        {
            App.LastConnectionFingerprint = (tested ?? ModelSettings.From(_settings)).Fingerprint();
            App.LastConnectionState = stateText;
            App.LastConnectionOk = ok;
            RefreshModelState();
        }

        // ---- 构造 -----------------------------------------------------------

        public MainViewModel(AppSettings settings, SettingsStore settingsStore,
            LocalTaskStore taskStore, Dispatcher ui,
            Func<string?> pickWorkspace, Action openSettings, Action<string> openDiagnostics)
        {
            _settings = settings;
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkspaceName)) OnPropertyChanged(nameof(WorkspaceLabel));
                if (e.PropertyName == nameof(IsAuthorized)) OnPropertyChanged(nameof(InputLocked));
                if (e.PropertyName == nameof(HasConversation) || e.PropertyName == nameof(IsViewingHistory))
                {
                    OnPropertyChanged(nameof(PageTitle));
                    OnPropertyChanged(nameof(ConversationTitle));
                }
                if (e.PropertyName == nameof(IsBusy)) { OnPropertyChanged(nameof(PermissionDisplay)); OnPropertyChanged(nameof(PermissionDescription)); }
                if (e.PropertyName == nameof(ModelDisplay)) OnPropertyChanged(nameof(FooterText));
            };
            HistoryGroups.CollectionChanged += (_, __) => OnPropertyChanged(nameof(IsHistoryEmpty));
            _settingsStore = settingsStore;
            _taskStore = taskStore;
            _ui = ui;
            _pickWorkspace = pickWorkspace;
            _openSettings = openSettings;
            _openDiagnostics = openDiagnostics;

            SendCommand = new RelayCommand(OnSendOrStop,
                _ => IsRunning || (!InputLocked && UserInput.Trim().Length > 0));
            NewTaskCommand = new RelayCommand(_ => StartGeneralChat(), _ => !IsBusy);
            RetryCommand = new RelayCommand(_ => RetryLast(), _ => !IsBusy && !_conversationNeedsReview && _lastRequest != null);
            ShowArtifactCommand = new RelayCommand(p => OpenArtifactDetails(p as ArtifactCardVm));
            ChooseWorkspaceCommand = new RelayCommand(_ => ChooseWorkspace(), _ => !IsBusy);
            OpenWorkspaceCommand = new RelayCommand(_ => OpenWorkspaceFolder(false), _ => IsAuthorized);
            OpenOutputDirectoryCommand = new RelayCommand(_ => OpenWorkspaceFolder(true), _ => IsAuthorized);
            OpenSettingsCommand = new RelayCommand(_ => _openSettings());
            OpenArtifactCommand = new RelayCommand(p => OpenArtifact(p as ArtifactCardVm));
            RevealArtifactCommand = new RelayCommand(p => RevealArtifact(p as ArtifactCardVm));
            CopyPathCommand = new RelayCommand(p => CopyPath(p as ArtifactCardVm));
            FillSuggestionCommand = new RelayCommand(p =>
            {
                if (p is SuggestionVm s)
                {
                    if (s.Title == "创建一个技能") UseSkill(new SkillCatalog().Find("skill-creator"));
                    UserInput = s.RequestText;
                }
            });
            LoadHistoryTaskCommand = new RelayCommand(p => LoadHistorySession(p as HistoryItemVm), _ => !IsBusy);
            CloseDrawerCommand = new RelayCommand(_ => SelectedArtifact = null);
            ToggleSidebarCommand = new RelayCommand(_ => SidebarCollapsed = !SidebarCollapsed);
            ShowDiagnosticsCommand = new RelayCommand(_ =>
            {
                if (_lastTaskId != null) _openDiagnostics(_lastTaskId);
            }, _ => _lastTaskId != null);

            InitializeProjectNavigation();
            if (string.IsNullOrWhiteSpace(settings.WorkspacePath))
            {
                settings.WorkspacePath = ApplicationDataPaths.DefaultWorkspace(settingsStore.Root);
                WorkspacePolicy.EnsureLayout(settings.WorkspacePath);
                settingsStore.Save(settings);
            }
            WorkspacePath = settings.WorkspacePath;
            IsAuthorized = Directory.Exists(settings.WorkspacePath);
            ReloadHistory();
            RefreshModelState();
        }

        private string _selectedSkillId = "";
        public string SkillDisplay { get; private set; } = "通用助手";
        public void UseSkill(SkillDefinition? skill)
        {
            if (IsBusy) return;
            NewTask(); _selectedSkillId = skill?.Id ?? ""; SkillDisplay = skill?.Name ?? "通用助手";
            OnPropertyChanged(nameof(SkillDisplay));
            UserInput = skill?.Id == "skill-creator" ? "帮我创建一个技能，我希望完成的工作是：" : "";
        }
        private string? _lastTaskId;
        private string? _lastRequest;

        // ---- 命令 -----------------------------------------------------------

        private void ChooseWorkspace()
        {
            var path = _pickWorkspace();
            if (path == null) return;
            try
            {
                WorkspacePolicy.EnsureLayout(path);
                WorkspacePath = path;
                IsAuthorized = true;
                _settings.WorkspacePath = path;
                _settingsStore.Save(_settings);
                if (_selectedProject != null) { _selectedProject.WorkspaceRoot = path; _taskStore.SaveProject(_selectedProject); }
                NewTask(); ReloadHistory(); NotifyProjectContext();
                OnPropertyChanged(nameof(SendLockedReason));
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法初始化工作目录：" + ex.Message, "工作目录",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 发送或停止：空闲时发送；运行中按钮为“停止”，
        /// Ctrl+Enter（CommandParameter=send）且有输入时把新请求加入队列。
        /// </summary>
        private async void OnSendOrStop(object? parameter)
        {
            if (IsCancelling) return;
            if (IsRunning)
            {
                var fromKeyboard = parameter as string == "send";
                if (fromKeyboard && UserInput.Trim().Length > 0 && !IsViewingHistory)
                {
                    await RunTurnAsync(UserInput.Trim());
                }
                else
                {
                    Stop();
                }
                return;
            }
            if (IsViewingHistory || !IsAuthorized) return;
            await RunTurnAsync(UserInput.Trim());
        }

        public string? ActiveTaskId => IsBusy ? _lastTaskId : null;
        public void CancelCurrentTask() => Stop();
        public void RetryTask(TaskRecord task)
        {
            if (IsBusy) return;
            IsReadOnlyAnalysis = task.ExecutionMode == AgentExecutionMode.ReadOnly;
            var sourceSession = _taskStore.LoadSession(task.SessionId);
            _selectedProject = _taskStore.LoadProjects().FirstOrDefault(p => p.ProjectId == sourceSession?.ProjectId);
            AuthorizeWorkspace(task.WorkspaceRoot);
            NewTask(); PersistProjectSelection(); NotifyProjectContext();
            _ = RunTurnAsync(task.UserRequest);
        }

        private void Stop()
        {
            if (!IsRunning || IsCancelling) return;
            IsCancelling = true;
            StatusText = "正在取消…";
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void NewTask()
        {
            if (IsBusy) return;
            IsViewingHistory = false;
            _currentSessionId = null;
            _priorContextCompactions = 0; UpdateContextAttention(0);
            _currentSessionTitle = ""; _conversationNeedsReview = false;
            OnPropertyChanged(nameof(ConversationTitle)); OnPropertyChanged(nameof(InputLocked)); OnPropertyChanged(nameof(SendLockedReason));
            ApplySidebar();
            Conversation.Clear();
            UserInput = "";
            StatusText = "";
            SelectedArtifact = null;
            OnPropertyChanged(nameof(HasConversation));
        }

        /// <summary>失败后的重试：新开一轮（新输出目录，不覆盖原成果）。</summary>
        public void RetryLast()
        {
            if (IsBusy || _conversationNeedsReview || _lastRequest == null) return;
            var request = _lastRequest;
            _ = RunTurnAsync(request); // 事件驱动触发，异常在内部处理
        }

        // ---- 会话与任务 ------------------------------------------------------

        /// <summary>发送一轮：无会话则新建；返回是否实际开始执行（false=入队）。</summary>
        private async Task RunTurnAsync(string request)
        {
            if (request.Length == 0) return;
            if (IsBusy)
            {
                var queued = new TaskRecord(Guid.NewGuid().ToString("N"), request, WorkspacePath)
                { State = TaskState.Queued, SessionId = _currentSessionId ?? "", PermissionMode = SelectedPermissionMode, ExecutionMode = SelectedExecutionMode };
                _taskStore.SaveTask(queued);
                _pendingRequests.Enqueue(queued);
                UserInput = "";
                Conversation.Add(ConversationItem.Notice(
                    $"已加入队列（第 {_pendingRequests.Count} 位）：{Truncate(request, 40)}"));
                OnPropertyChanged(nameof(HasConversation));
                return;
            }
            await ExecuteTurnAsync(request);
            // 队列串行执行（本地写工具不并发）。
            while (_pendingRequests.Count > 0 && !IsBusy && !_queuePaused)
            {
                var previous = _lastTaskId == null ? null : _taskStore.LoadTask(_lastTaskId);
                if (previous?.State == TaskState.NeedsReview || previous?.State == TaskState.Interrupted || previous?.State == TaskState.Cancelled)
                { _queuePaused = true; Conversation.Add(ConversationItem.Notice("队列已暂停；请在任务中心复核状态后，逐项作为新任务执行。")); break; }
                var next = _pendingRequests.Peek();
                if (!string.Equals(next.WorkspaceRoot, WorkspacePath, StringComparison.OrdinalIgnoreCase))
                { _queuePaused = true; break; }
                _pendingRequests.Dequeue();
                var latest = _taskStore.LoadTask(next.TaskId);
                if (latest?.State == TaskState.Queued) await ExecuteTurnAsync(next.UserRequest, next);
            }
        }

        private async Task ExecuteTurnAsync(string request, TaskRecord? queued = null)
        {
            if (IsBusy || request.Length == 0) return;
            var remoteConnection = new OpenCodeConnectionStore().Load();
            ModelSettings? subAgentModel = null;
            if (_settings.EnableSubAgents && !_settings.FakeMode)
            {
                var workerProfile = string.IsNullOrEmpty(_settings.SubAgentProfileName) ? _settings.ActiveProfile :
                    _settings.Profiles.FirstOrDefault(p => p.Name == _settings.SubAgentProfileName);
                if (workerProfile == null) { StatusText = "子 Agent 的模型配置不存在，请在设置中重新选择。"; return; }
                subAgentModel = ModelSettings.FromProfile(workerProfile, workerProfile.Name == _settings.ActiveProfileName ? _settings.ApiKey : _settingsStore.LoadEffectiveApiKey(workerProfile));
            }
            if (remoteConnection.Enabled && !_settings.FakeMode)
            {
                try { remoteConnection.Validate(); }
                catch (ToolException ex) { StatusText = ex.Message; return; }
            }
            if (!_settings.FakeMode)
            {
                if (_settings.BaseUrl.Trim().Length == 0 || _settings.ModelName.Trim().Length == 0)
                {
                    StatusText = "请在右上角的模型设置中填写接口地址与模型名称。";
                    return;
                }
                if (_settings.ApiKey.Length == 0)
                {
                    if (!await ConfirmBeforeRunAsync("API Key 为空。你的网关允许匿名访问并继续执行吗？", "匿名访问不会绕过网关认证要求。")) return;
                }
            }

            if (!_settings.FakeMode)
            {
                string fingerprint = ModelSettings.From(_settings).Fingerprint();
                if (subAgentModel != null) fingerprint += ":subagent:" + subAgentModel.Fingerprint();
                if (remoteConnection.Enabled) fingerprint += ":opencode:" + remoteConnection.Fingerprint();
                if (_settings.DataConsentFingerprint != fingerprint)
                {
                    string transport = _settings.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "HTTPS 加密连接" : "HTTP 明文连接（密钥和内容将以明文传输）";
                    var details = (subAgentModel != null ? "本地子 Agent 使用：" + subAgentModel.BaseUrl + "\n" : "") +
                        (remoteConnection.Enabled ? "指定内核服务：" + remoteConnection.BaseUrl + "\n" : "") +
                        "任务描述、工作区文件名、必要内容及对话将发送到：\n" + _settings.BaseUrl + "\n" + transport + "。文档解析在本地完成。";
                    if (!await ConfirmBeforeRunAsync("是否授权这个模型配置用于后续任务？", details)) return;
                    _settings.DataConsentFingerprint = fingerprint; _settingsStore.Save(_settings);
                }
            }

            IsViewingHistory = false;
            UserInput = "";

            // 会话：新建或延续（正式版 §5.4）。
            var isNewSession = _currentSessionId == null;
            SessionRecord session;
            if (isNewSession)
            {
                var sessionId = Guid.NewGuid().ToString("N");
                session = new SessionRecord(sessionId, Truncate(request, 80), WorkspacePath) { ProjectId = CurrentProjectId };
                _taskStore.SaveSession(session);
                _currentSessionId = sessionId; _currentSessionTitle = session.Title;
                OnPropertyChanged(nameof(ConversationTitle)); ApplySidebar();
            }
            else
            {
                session = _taskStore.LoadSession(_currentSessionId!) ??
                    new SessionRecord(_currentSessionId!, Truncate(request, 40), WorkspacePath);
            }

            var taskId = queued?.TaskId ?? Guid.NewGuid().ToString("N");
            var turnIndex = session.Turns.Count + 1;
            _lastTaskId = taskId;
            _lastRequest = request;

            var record = new TaskRecord(taskId, request, WorkspacePath)
            {
                SessionId = session.SessionId,
                TurnIndex = turnIndex,
                PermissionMode = queued?.PermissionMode ?? SelectedPermissionMode,
                ExecutionMode = queued?.ExecutionMode ?? SelectedExecutionMode
            };
            _taskStore.SaveTask(record);
            _taskStore.AppendTurn(session, new SessionTurn
            {
                TaskId = taskId, CreatedUtc = record.CreatedUtc, Request = request
            });

            ReloadHistory();
            // 完整成功轮次上下文，包括工具调用及回执；超出预算时明确报错。
            var priorMessages = BuildPriorMessages(session, taskId);

            // 对话区：用户消息 + 执行步骤组。
            Conversation.Add(ConversationItem.User(request, DateTime.Now));
            var group = ConversationItem.Execution(DateTime.Now);
            _runningGroup = group;
            Conversation.Add(group);
            OnPropertyChanged(nameof(HasConversation));


            _activePermissionMode = record.PermissionMode;
            IsRunning = true;
            StatusText = "正在请求模型…";
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                var options = new AgentRunOptions
                {
                    ExecutionMode = record.ExecutionMode,
                    PriorMessages = priorMessages,
                    SkillId = _selectedSkillId,
                    IsFollowUpTurn = !isNewSession && turnIndex > 1
                };
                var hostRequest = new HostRequest
                {
                    Task = record, Options = options, Fake = _settings.FakeMode,
                    Model = ModelSettings.From(_settings), StoreRoot = _taskStore.Root,
                    EnableSubAgents = _settings.EnableSubAgents, SubAgentModel = subAgentModel
                };
                Action<TaskEvent> events = e => _ui.BeginInvoke(new Action(() => HandleTaskEvent(e, group)));
                var finished = remoteConnection.Enabled && !_settings.FakeMode
                    ? await new OpenCodeTaskRunner(remoteConnection).RunAsync(hostRequest, new ApprovalProvider(this), events, token, new UserInputProvider())
                    : await new TaskHostClient().RunAsync(hostRequest, new ApprovalProvider(this), events, token, new UserInputProvider());
                _taskStore.SaveTask(finished);
                RenderTaskFinished(finished, group);
            }
            catch (Exception ex)
            {
                var latest = record;
                try { latest = _taskStore.LoadTask(taskId) ?? record; } catch { }
                if (!latest.FinishedUtc.HasValue)
                {
                    bool uncertain = latest.Steps.Any(step => step.Tool == "apply_file_plan" && step.State != "Pending" && step.State != "Denied" && step.State != "Failed");
                    latest.State = uncertain ? TaskState.NeedsReview : TaskState.Failed;
                    latest.ErrorCode = uncertain ? ErrorCodes.NeedsReview : ErrorCodes.WorkerUnavailable;
                    latest.ErrorMessage = "任务异常终止：" + ex.GetType().Name;
                    latest.FinishedUtc = DateTime.UtcNow;
                    try { _taskStore.SaveTask(latest); } catch { latest.ErrorMessage += "；状态未能落盘，请保留已有文件并在下次启动后复核。"; }
                }
                RenderTaskFinished(latest, group);
            }
            finally
            {
                IsRunning = false;
                IsCancelling = false;
                _runningGroup = null;
                _cts?.Dispose();
                _cts = null;
                ReloadHistory();
                RefreshModelState();
            }
        }

        /// <summary>从会话历史构建此前轮次的对话上下文（§7.3：摘要压缩，口径保留在文本中）。</summary>
        private List<ChatMessage>? BuildPriorMessages(SessionRecord session, string excludeTaskId)
        {
            var messages = new List<ChatMessage>();
            var history = session.Turns.Where(t => t.TaskId != excludeTaskId).Select(t => new { Turn = t, Task = _taskStore.LoadTask(t.TaskId) }).ToList();
            _priorContextCompactions = history.Where(h => h.Task != null).Sum(h => h.Task!.ContextCompactionCount);
            UpdateContextAttention(_priorContextCompactions);
            int lastProjection = history.FindLastIndex(h => h.Task != null && h.Task.ContextMessages.Count > 0 &&
                (h.Task.State == TaskState.Completed || h.Task.State == TaskState.CompletedWithWarnings));
            if (lastProjection >= 0) messages.AddRange(history[lastProjection].Task!.ContextMessages);
            foreach (var entry in history.Skip(lastProjection + 1))
            {
                var turn = entry.Turn;
                var task = entry.Task;
                if (task != null && task.Messages.Count > 0 &&
                    (task.State == TaskState.Completed || task.State == TaskState.CompletedWithWarnings))
                    messages.AddRange(task.Messages);
                else if (task != null && task.FinalText.Length > 0)
                {
                    messages.Add(ChatMessage.User(turn.Request));
                    messages.Add(new ChatMessage("assistant", task.FinalText));
                }
                else if (task != null)
                {
                    messages.Add(ChatMessage.User(turn.Request));
                    messages.Add(new ChatMessage("assistant", "上一轮未完成（" + (task.ErrorCode ?? task.State.ToString()) + "），不能把未验证结果作为成功证据，也不自动重放旧文件操作。"));
                }
            }
            return messages.Count > 0 ? messages : null;
        }

        private static string Truncate(string text, int maxLength) =>
            text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";

        // ---- 事件 → 类型化条目 ----------------------------------------------

        private void HandleTaskEvent(TaskEvent e, ConversationItem group)
        {
            var local = e.UtcTime.ToLocalTime();
            var time = local.ToString("HH:mm:ss");
            switch (e.Kind)
            {
                case TaskEventKind.SubAgentStarted:
                case TaskEventKind.SubAgentFinished:
                    group.AddStep(new TaskStepVm(e.Summary, time, null)); StatusText = e.Summary; break;
                case TaskEventKind.SubAgentProgress:
                case TaskEventKind.ToolProgress:
                    StatusText = e.Summary; break;
                case TaskEventKind.ModelRetryScheduled:
                    StatusText = e.Summary; group.AddStep(new TaskStepVm(e.Summary, time, null)); break;
                case TaskEventKind.PermissionAutoApproved:
                    group.AddStep(new TaskStepVm("已按当前权限自动批准：" + NaturalStepText(e.ToolName), time, e.ToolName)); break;
                case TaskEventKind.ContextCompactionStarted:
                    UpdateContextAttention(compacting: true);
                    group.AddStep(new TaskStepVm("整理长对话上下文", time, null, isRunning: true));
                    StatusText = e.Summary; break;
                case TaskEventKind.ContextCompactionFinished:
                    UpdateContextAttention(_contextCompactions + (e.Summary.StartsWith("上下文已整理") ? 1 : 0));
                    group.FinishStep(time); StatusText = e.Summary; break;
                case TaskEventKind.ModelRequestStarted:
                    group.AddStep(new TaskStepVm("请求模型", time, null, isRunning: true));
                    StatusText = "正在请求模型…";
                    break;
                case TaskEventKind.ModelRequestFinished:
                    group.FinishStep(time);
                    break;
                case TaskEventKind.AssistantDelta:
                    // 流式文本：创建或延续流式助手条目。
                    if (_streamingItem == null)
                    {
                        _streamingItem = ConversationItem.AssistantStreaming();
                        Conversation.Add(_streamingItem);
                        OnPropertyChanged(nameof(HasConversation));
                    }
                    _streamingItem.AppendStreamingText(e.Summary);
                    break;
                case TaskEventKind.ToolCallStarted:
                    group.AddStep(new TaskStepVm(NaturalStepText(e.ToolName), time, e.ToolName, isRunning: true));
                    StatusText = "正在" + NaturalStepText(e.ToolName) + "…";
                    break;
                case TaskEventKind.ToolCallFinished:
                    if (e.ToolOk == true)
                    {
                        group.FinishStep(time);
                    }
                    else
                    {
                        group.MarkLastStepFailed(time);
                    }
                    break;
                case TaskEventKind.ApprovalRequested:
                    group.AddStep(new TaskStepVm("等待确认：" + e.Summary, time, e.ToolName, isRunning: true));
                    StatusText = "等待你确认操作…";
                    break;
                case TaskEventKind.ApprovalResolved:
                    if (e.ToolOk == true)
                    {
                        group.FinishStep(time);
                        StatusText = "已确认，继续执行…";
                    }
                    else
                    {
                        group.MarkLastStepFailed(time);
                    }
                    break;
                case TaskEventKind.TaskStarted:
                    StatusText = "任务开始";
                    break;
                case TaskEventKind.TaskCompleted:
                case TaskEventKind.TaskFailed:
                case TaskEventKind.TaskCancelled:
                case TaskEventKind.TaskNeedsReview:
                    break;
            }
        }

        /// <summary>工具名 → 自然语言步骤（新工具默认显示原始名，可读且不虚构）。</summary>
        private static string NaturalStepText(string? toolName) => toolName switch
        {
            "list_files" => "读取文件列表",
            "preview_csv" => "预览文件内容",
            "preview_table" => "预览表格",
            "aggregate_csv" => "汇总表格数据",
            "aggregate_table" => "汇总表格数据",
            "export_summary" => "导出汇总成果",
            "export_xlsx" => "导出 Excel 成果",
            "generate_docx" => "生成 Word 报告",
            "fill_docx_template" => "填充 Word 模板",
            "preview_docx_fields" => "读取模板字段",
            "extract_pdf_text" => "提取 PDF 文字",
            "plan_file_organization" => "生成整理计划",
            "apply_file_plan" => "执行文件整理",
            "read_document_text" => "分页阅读文档",
            "search_input_text" => "搜索文档内容",
            "update_task_plan" => "整理任务步骤",
            "read_task_history" => "回查原始记录",
            "list_skills" => "查找适用技能",
            "read_skill" => "读取技能方法",
            "delegate_tasks" => "并发分配子任务",
            "import_task_artifacts" => "校验并接收子任务成果",
            null => "请求模型",
            _ => toolName
        };

        private void RenderTaskFinished(TaskRecord finished, ConversationItem group)
        {
            UpdateContextAttention(_priorContextCompactions + finished.ContextCompactionCount);
            if (finished.State == TaskState.NeedsReview)
            {
                _conversationNeedsReview = true;
                OnPropertyChanged(nameof(InputLocked)); OnPropertyChanged(nameof(SendLockedReason));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
            group.Complete(ok: finished.State is TaskState.Completed or TaskState.CompletedWithWarnings,
                cancelled: finished.State == TaskState.Cancelled);

            switch (finished.State)
            {
                case TaskState.Completed:
                case TaskState.CompletedWithWarnings:
                    var finalText = finished.FinalText.Length > 0 ? finished.FinalText : "任务完成。";
                    if (_streamingItem != null)
                    {
                        _streamingItem.FinishStreaming(finalText);
                        foreach (var a in finished.Artifacts)
                        {
                            _streamingItem.Artifacts.Add(ToCard(a, finished.WorkspaceRoot));
                        }
                        _streamingItem = null;
                    }
                    else
                    {
                        var assistant = ConversationItem.Assistant(finalText, DateTime.Now);
                        foreach (var a in finished.Artifacts)
                        {
                            assistant.Artifacts.Add(ToCard(a, finished.WorkspaceRoot));
                        }
                        Conversation.Add(assistant);
                    }
                    StatusText = "";
                    break;
                case TaskState.Cancelled:
                    if (_streamingItem != null)
                    {
                        _streamingItem.AbortStreaming("任务已停止，以上内容可能不完整");
                        _streamingItem = null;
                    }
                    StatusText = "任务已停止";
                    break;
                case TaskState.NeedsReview:
                    if (_streamingItem != null)
                    {
                        _streamingItem.AbortStreaming("部分操作已执行，等待人工复核");
                        _streamingItem = null;
                    }
                    Conversation.Add(ConversationItem.Error(
                        ReadableError(finished), finished.ErrorCode,
                        "查看变更记录 file_mutations.md，手动处理剩余文件", DateTime.Now));
                    StatusText = "需要人工复核";
                    break;
                default:
                    if (_streamingItem != null)
                    {
                        _streamingItem.AbortStreaming("任务失败，以上内容可能不完整");
                        _streamingItem = null;
                    }
                    var nextAction = NextActionFor(finished.ErrorCode);
                    Conversation.Add(ConversationItem.Error(
                        ReadableError(finished), finished.ErrorCode, nextAction, DateTime.Now));
                    StatusText = "任务失败";
                    break;
            }
        }

        private static string ReadableError(TaskRecord finished)
        {
            var message = finished.ErrorMessage ?? "任务失败。";
            return finished.ErrorCode switch
            {
                ErrorCodes.ModelAuthFailed => "当前模型配置的密钥或访问权限未通过认证（401/403）。请在右上角选择其他配置，或在设置中更新此配置的 API Key。",
                ErrorCodes.ModelUnavailable => "无法连接模型服务，请检查接口地址与网络。",
                ErrorCodes.RemoteKernelUnavailable => message,
                ErrorCodes.NoArtifacts => "模型没有产出合格成果。可以换个说法描述任务后重试。",
                ErrorCodes.LimitExceeded => "任务超出预算限制：" + message,
                ErrorCodes.ModelResponseTooLarge => "模型返回内容过大：" + message,
                ErrorCodes.ModelOutputTruncated => "模型输出被截断：" + message,
                ErrorCodes.ToolLoopDetected => message,
                ErrorCodes.ToolPolicyDenied => message,
                ErrorCodes.UserApprovalDenied => "你取消了文件操作，本轮未做任何修改。",
                ErrorCodes.NeedsReview => "部分文件操作已执行、其余未完成：" + message,
                ErrorCodes.UnsupportedFormat => "文件格式不支持：" + message,
                _ => message
            };
        }

        private static string? NextActionFor(string? code) => code switch
        {
            ErrorCodes.ModelAuthFailed => "在右上角核对所选配置，或到模型设置更新该配置的密钥",
            ErrorCodes.ModelUnavailable => "打开模型设置核对接口地址后重试",
            ErrorCodes.NoArtifacts => "重试或换一种方式描述任务",
            ErrorCodes.LimitExceeded => "缩小数据范围后重试",
            ErrorCodes.ModelResponseTooLarge => "按章节或文件分批生成后重试",
            ErrorCodes.ModelOutputTruncated => "按章节分批生成，或核对模型的输出额度后重试",
            ErrorCodes.ToolLoopDetected => "补充要求后重新提交",
            ErrorCodes.ToolPolicyDenied => "关闭只读分析后重新提交",
            ErrorCodes.UserApprovalDenied => "重新发起并确认，或调整整理要求",
            _ => "查看执行详情了解原因"
        };

        private ArtifactCardVm ToCard(ArtifactInfo a, string workspaceRoot)
        {
            string relative = a.Path;
            if (Directory.Exists(workspaceRoot) && a.PublishedPath.StartsWith(WorkspacePolicy.DeliveryDir + "/", StringComparison.Ordinal))
            {
                var candidate = new WorkspacePolicy(workspaceRoot).ResolveDeliveryPath(Path.GetFileName(a.PublishedPath));
                if (File.Exists(candidate) && new WorkspacePolicy(workspaceRoot).ToWorkspaceRelative(candidate) == a.PublishedPath) relative = a.PublishedPath;
            }
            var full = Path.Combine(workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var exists = File.Exists(full);
            var sizeText = exists ? new FileInfo(full).Length + " 字节" : "文件不存在";
            var typeText = a.Type switch
            {
                "summary_csv" => "CSV 汇总表",
                "report_md" => "Markdown 报告",
                "summary_xlsx" => "Excel 汇总表",
                "report_docx" => "Word 报告",
                "filled_docx" => "Word 文档（模板填充）",
                "extracted_text_md" => "PDF 文本（Markdown）",
                "organize_plan_md" => "整理计划",
                "mutations_log" => "变更记录",
                "word_document" => "Word 文档",
                "presentation" => "PowerPoint 演示文稿",
                _ => a.Type
            };
            return new ArtifactCardVm(
                Path.GetFileName(relative), relative, typeText, sizeText, a.Sha256, full, exists, a.Path, workspaceRoot);
        }

        private void OpenWorkspaceFolder(bool output)
        {
            try
            {
                string path = output ? Path.Combine(WorkspacePath, WorkspacePolicy.DeliveryDir) : WorkspacePath;
                if (!Directory.Exists(path)) throw new IOException("目录尚不存在，请先选择工作目录或生成文件。");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            { StatusText = "无法打开目录：" + ex.Message; }
        }

        // ---- 审批提供者 -------------------------------------------------------

        /// <summary>
        /// 审批通道：演示模式自动通过（脚本演示；事件中仍可见审批步骤）；
        /// 在线模式弹窗征得用户明确同意；任务取消时按拒绝收尾。
        /// </summary>
        private sealed class ApprovalProvider : IApprovalProvider
        {
            private readonly MainViewModel _owner;

            public ApprovalProvider(MainViewModel owner) => _owner = owner;

            public async Task<bool> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
            {
                var answer = await _owner.RequestInlineAnswerAsync(request.Title,
                    new[] { "确认执行", "拒绝本次" }, cancellationToken, false,
                    request.Reason + "\n\n" + string.Join("\n\n", request.Operations)).ConfigureAwait(false);
                return answer == "确认执行";
            }
        }

        public async Task<string?> RequestInlineAnswerAsync(string question, IList<string> options, CancellationToken token, bool allowFreeText = true, string details = "")
        {
            token.ThrowIfCancellationRequested();
            Presentation.QuestionCardVm? card = null;
            await _ui.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                card = new Presentation.QuestionCardVm(question, options, allowFreeText, details);
                SelectedArtifact = null;
                Conversation.Add(ConversationItem.Prompt(card));
                OnPropertyChanged(nameof(HasConversation));
            });
            using var registration = token.Register(() =>
            {
                if (!_ui.HasShutdownStarted) _ = _ui.BeginInvoke(new Action(() => card?.Resolve(null)));
            });
            var result = await card!.Completion.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task<bool> ConfirmBeforeRunAsync(string question, string details)
        {
            _isAwaitingPreflight = true;
            void Notify() { foreach (var name in new[] { nameof(IsBusy), nameof(InputLocked), nameof(ModeLocked), nameof(SendLockedReason) }) OnPropertyChanged(name); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
            Notify();
            try { return await RequestInlineAnswerAsync(question, new[] { "同意并继续", "暂不执行" }, CancellationToken.None, false, details) == "同意并继续"; }
            finally { _isAwaitingPreflight = false; Notify(); }
        }

        // ---- 历史（按会话）----------------------------------------------------

        private int _historyRefreshVersion;
        private async void ReloadHistory()
        {
            int version = Interlocked.Increment(ref _historyRefreshVersion);
            try
            {
                var snapshot = await XpTask.Run(() =>
                {
                    var sessions = _taskStore.LoadSessions();
                    var states = sessions.Take(200).ToDictionary(session => session.SessionId, SessionState);
                    return new { Projects = _taskStore.LoadProjects(), Sessions = sessions, States = states };
                }).ConfigureAwait(false);
                if (_ui.HasShutdownStarted) return;
                await _ui.InvokeAsync(() =>
                {
                    if (version != _historyRefreshVersion) return;
                    _sidebarProjects = snapshot.Projects; _sidebarSessions = snapshot.Sessions; _sidebarStates = snapshot.States;
                    if (!_projectSelectionInitialized)
                    {
                        _projectSelectionInitialized = true;
                        if (!IsBusy && !HasConversation)
                        {
                            _selectedProject = _sidebarProjects.FirstOrDefault(p => p.ProjectId == _settings.SelectedProjectId);
                            if (_selectedProject != null) { WorkspacePath = _selectedProject.WorkspaceRoot; IsAuthorized = Directory.Exists(WorkspacePath); }
                            NotifyProjectContext();
                        }
                    }
                    ApplySidebar();
                });
            }
            catch (Exception)
            {
                if (!_ui.HasShutdownStarted)
                    _ = _ui.BeginInvoke(new Action(() => { if (version == _historyRefreshVersion && !IsBusy) StatusText = "侧栏记录暂时无法读取，可稍后重试。"; }));
            }
        }

        /// <summary>侧栏只读取最近一轮状态，完整步骤与历史保留在任务中心。</summary>
        private (string Text, bool Failed, bool Running) SessionState(SessionRecord session)
        {
            var last = session.Turns.LastOrDefault();
            if (last == null) return ("新对话", false, false);
            var state = _taskStore.LoadTaskState(last.TaskId) ?? TaskState.Interrupted;
            return state switch
            {
                TaskState.Running or TaskState.WaitingForUser => ("运行中", false, true),
                TaskState.Completed or TaskState.CompletedWithWarnings => ("完成", false, false),
                TaskState.NeedsReview => ("待复核", true, false),
                TaskState.Failed => ("失败", true, false),
                TaskState.Cancelled => ("已取消", false, false),
                TaskState.Queued => ("排队中", false, false),
                TaskState.Created => ("准备中", false, true),
                _ => ("已中断", false, false)
            };
        }

        private static string GroupHeader(DateTime local, DateTime today, DateTime yesterday)
        {
            if (local.Date == today) return "今天";
            if (local.Date == yesterday) return "昨天";
            return local.ToString("M月d日");
        }

        /// <summary>异步打开旧对话，保留历史来源；后续输入创建新任务，不重放旧步骤。</summary>
        private async void LoadHistorySession(HistoryItemVm? item)
        {
            if (item == null || IsBusy) return;
            IsLoadingConversation = true;
            try
            {
                var snapshot = await XpTask.Run(() =>
                {
                    var session = _taskStore.LoadSession(item.SessionId);
                    var tasks = session?.Turns.Select(turn => _taskStore.LoadTask(turn.TaskId)).Where(task => task != null).Cast<TaskRecord>().ToList() ?? new List<TaskRecord>();
                    var events = tasks.ToDictionary(task => task.TaskId, task => _taskStore.LoadEvents(task.TaskId));
                    return new { Session = session, Tasks = tasks, Events = events };
                });
                var session = snapshot.Session;
                if (session == null) return;
                var tasks = snapshot.Tasks;
            SelectedArtifact = null;
            Conversation.Clear();
            _streamingItem = null;

            foreach (var turn in session.Turns)
            {
                var record = tasks.FirstOrDefault(t => t.TaskId == turn.TaskId);
                if (record == null) continue;
                _lastTaskId = record.TaskId;
                _lastRequest = record.UserRequest;

                Conversation.Add(ConversationItem.User(record.UserRequest, record.CreatedUtc.ToLocalTime()));
                var group = ConversationItem.Execution(record.CreatedUtc.ToLocalTime());
                foreach (var e in snapshot.Events[record.TaskId])
                {
                    if (e.Kind == TaskEventKind.AssistantDelta) continue;
                    var time = e.UtcTime.ToLocalTime().ToString("HH:mm:ss");
                    switch (e.Kind)
                    {
                        case TaskEventKind.SubAgentStarted:
                        case TaskEventKind.SubAgentFinished:
                        case TaskEventKind.ModelRetryScheduled:
                            group.AddStep(new TaskStepVm(e.Summary, time, null));
                            break;
                        case TaskEventKind.ModelRequestStarted:
                            group.AddStep(new TaskStepVm("请求模型", time, null));
                            break;
                        case TaskEventKind.ToolCallStarted:
                            group.AddStep(new TaskStepVm(NaturalStepText(e.ToolName), time, e.ToolName));
                            break;
                        case TaskEventKind.ApprovalRequested:
                            group.AddStep(new TaskStepVm("等待确认：" + e.Summary, time, e.ToolName));
                            break;
                        case TaskEventKind.ToolCallFinished:
                            if (e.ToolOk != true)
                            {
                                group.MarkLastStepFailed(time);
                            }
                            break;
                    }
                }
                group.Complete(ok: record.State is TaskState.Completed or TaskState.CompletedWithWarnings,
                    cancelled: record.State == TaskState.Cancelled);
                Conversation.Add(group);

                if (record.State is TaskState.Completed or TaskState.CompletedWithWarnings)
                {
                    var assistant = ConversationItem.Assistant(
                        record.FinalText.Length > 0 ? record.FinalText : "任务完成。",
                        (record.FinishedUtc ?? record.CreatedUtc).ToLocalTime());
                    foreach (var a in record.Artifacts)
                    {
                        assistant.Artifacts.Add(ToCard(a, record.WorkspaceRoot));
                    }
                    Conversation.Add(assistant);
                }
                else if (record.State is TaskState.Failed or TaskState.NeedsReview)
                {
                    Conversation.Add(ConversationItem.Error(
                        ReadableError(record), record.ErrorCode, null,
                        (record.FinishedUtc ?? record.CreatedUtc).ToLocalTime()));
                }
            }

                _selectedProject = _sidebarProjects.FirstOrDefault(project => project.ProjectId == session.ProjectId);
                RestoreExecutionMode(snapshot.Tasks.LastOrDefault()?.ExecutionMode ?? AgentExecutionMode.Execute);
                _priorContextCompactions = snapshot.Tasks.Sum(t => t.ContextCompactionCount); UpdateContextAttention(_priorContextCompactions);
                _projectSelectionInitialized = true;
                WorkspacePath = session.WorkspaceRoot; IsAuthorized = Directory.Exists(WorkspacePath);
                _currentSessionId = session.SessionId; _currentSessionTitle = session.Title;
                _conversationNeedsReview = tasks.LastOrDefault()?.State == TaskState.NeedsReview;
                IsViewingHistory = false;
                StatusText = _conversationNeedsReview ? "此对话有待复核的文件变更，请先查看任务中心。" : "";
                PersistProjectSelection(); NotifyProjectContext(); ApplySidebar();
                OnPropertyChanged(nameof(HasConversation)); OnPropertyChanged(nameof(InputLocked)); OnPropertyChanged(nameof(SendLockedReason));
            }
            catch (Exception) { StatusText = "打开对话失败，请稍后重试。"; }
            finally { IsLoadingConversation = false; }
        }

        // ---- 成果动作 ---------------------------------------------------------

        private void OpenArtifact(ArtifactCardVm? card)
        {
            if (card == null) return;
            if (!card.FileExists)
            {
                MessageBox.Show("文件不存在（可能已被移动或删除）。", "打开成果",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(card.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败：" + ex.Message, "打开成果",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RevealArtifact(ArtifactCardVm? card)
        {
            if (card == null) return;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + card.FullPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "打开目录",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyPath(ArtifactCardVm? card)
        {
            if (card == null) return;
            try
            {
                Clipboard.SetText(card.FullPath);
                StatusText = "已复制路径：" + card.FullPath;
            }
            catch (Exception)
            {
                StatusText = "复制失败";
            }
        }

        /// <summary>文件卡点击 → 打开详情抽屉（null 关闭抽屉）。</summary>
        public void OpenArtifactDetails(ArtifactCardVm? card)
        {
            SelectedArtifact = card;
        }

        // ---- 演示参数（--demo-ui）与自动化支持 ------------------------------

        /// <summary>直接授权指定工作区（仅供 --demo-ui 与自动化验证使用）。</summary>
        internal void AuthorizeWorkspace(string path)
        {
            WorkspacePolicy.EnsureLayout(path);
            WorkspacePath = path;
            IsAuthorized = true;
            _settings.WorkspacePath = path;
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(SendLockedReason));
        }

        /// <summary>以标准演示请求跑一次任务（Fake 模式，自动路由演示脚本）。</summary>
        internal Task RunDemoAsync()
        {
            _settings.FakeMode = true;
            RefreshModelState();
            return RunTurnAsync(StandardDemoRequest);
        }

        /// <summary>测试/演示：在当前会话中追加一轮（Fake 模式自动路由）。</summary>
        internal Task RunDemoTurnAsync(string request)
        {
            _settings.FakeMode = true;
            RefreshModelState();
            return RunTurnAsync(request);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _historyRefreshVersion);
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
