using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;
using WorkAssistant.Desktop.Presentation;

namespace WorkAssistant.Desktop
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<ProjectItemVm> Projects { get; } = new();
        private List<ProjectRecord> _sidebarProjects = new();
        private List<SessionRecord> _sidebarSessions = new();
        private Dictionary<string, (string Text, bool Failed, bool Running)> _sidebarStates = new();
        private ProjectRecord? _selectedProject;
        private string _sidebarSearch = "", _currentSessionTitle = "";
        private bool _projectSelectionInitialized;
        public RelayCommand SelectProjectCommand { get; private set; } = null!;
        private bool _isLoadingConversation, _conversationNeedsReview;
        public bool IsLoadingConversation
        {
            get => _isLoadingConversation;
            private set
            {
                if (!SetProperty(ref _isLoadingConversation, value, nameof(IsLoadingConversation))) return;
                OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(InputLocked)); OnPropertyChanged(nameof(ModeLocked)); OnPropertyChanged(nameof(SendLockedReason));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
        public string SidebarSearch
        {
            get => _sidebarSearch;
            set { if (SetProperty(ref _sidebarSearch, value, nameof(SidebarSearch))) ApplySidebar(); }
        }
        public string CurrentProjectId => _selectedProject?.ProjectId ?? "";
        public string ContextTitle => _selectedProject?.Name ?? "个人空间";
        public string HomeSubtitle => _selectedProject == null ? "写下今天想完成的事，或者从一个技能开始。" :
            string.IsNullOrWhiteSpace(_selectedProject.Description) ? "这个项目的对话与工作目录，集中在这里。" : _selectedProject.Description;
        public ProjectRecord? CurrentProject => _selectedProject;
        public bool IsProjectListEmpty => Projects.Count == 0;
        public string ConversationHeading => string.IsNullOrWhiteSpace(_currentSessionTitle) ? "新对话" : _currentSessionTitle;

        private void InitializeProjectNavigation()
        {
            SelectProjectCommand = new RelayCommand(p =>
            {
                if (p is not ProjectItemVm item) return;
                try { OpenProject(item.Project); }
                catch (Exception ex) { StatusText = "无法打开项目：" + ex.Message; }
            }, _ => !IsBusy);
        }
        private void NotifyProjectContext()
        {
            OnPropertyChanged(nameof(CurrentProjectId)); OnPropertyChanged(nameof(ContextTitle));
            OnPropertyChanged(nameof(HomeSubtitle)); OnPropertyChanged(nameof(WelcomeTitle)); OnPropertyChanged(nameof(CurrentProject));
            OnPropertyChanged(nameof(ConversationTitle));
        }
        private void PersistProjectSelection()
        {
            _settings.SelectedProjectId = CurrentProjectId;
            _settings.WorkspacePath = WorkspacePath;
            try { _settingsStore.Save(_settings); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { StatusText = "项目选择已更新，但默认目录设置未能保存。"; }
        }
        public void StartGeneralChat()
        {
            if (IsBusy) return;
            _projectSelectionInitialized = true; _selectedProject = null;
            NewTask(); PersistProjectSelection(); NotifyProjectContext(); ApplySidebar();
        }
        public void OpenProject(ProjectRecord project)
        {
            if (IsBusy) return;
            project = _taskStore.LoadProjects().FirstOrDefault(p => p.ProjectId == project.ProjectId)
                ?? throw new InvalidOperationException("该项目已被删除，请重新选择。");
            _projectSelectionInitialized = true; _selectedProject = project;
            NewTask(); WorkspacePath = project.WorkspaceRoot; IsAuthorized = Directory.Exists(WorkspacePath);
            PersistProjectSelection(); NotifyProjectContext(); ApplySidebar();
        }
        public void SaveProject(ProjectRecord project, bool newlyCreated)
        {
            if (IsBusy) return;
            WorkspacePolicy.EnsureLayout(project.WorkspaceRoot);
            _taskStore.SaveProject(project);
            if (newlyCreated) OpenProject(project);
            else if (CurrentProjectId == project.ProjectId)
            {
                _selectedProject = project;
                if (_currentSessionId == null && !HasConversation)
                { WorkspacePath = project.WorkspaceRoot; IsAuthorized = Directory.Exists(WorkspacePath); }
                PersistProjectSelection(); NotifyProjectContext();
            }
            ReloadHistory(); StatusText = newlyCreated ? "项目已创建；可以开始项目内的新对话。" : "项目已更新；既有对话的工作目录保持不变。";
        }
        public void RemoveProject(ProjectRecord project)
        {
            if (IsBusy) return;
            _taskStore.DeleteProject(project);
            if (CurrentProjectId == project.ProjectId)
            { _selectedProject = null; PersistProjectSelection(); NotifyProjectContext(); }
            ReloadHistory(); StatusText = "项目分类已删除，对话、任务记录和所有文件均已保留。";
        }
        public void RenameConversation(string sessionId, string title)
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(title) || title.Length > 80) throw new InvalidOperationException("对话名称需为 1～80 个字符。");
            var session = _taskStore.LoadSession(sessionId) ?? throw new InvalidOperationException("对话已不存在。");
            session.Title = title.Trim(); _taskStore.SaveSession(session);
            if (_currentSessionId == sessionId) { _currentSessionTitle = session.Title; OnPropertyChanged(nameof(ConversationTitle)); }
            ReloadHistory();
        }
        public void MoveConversation(string sessionId, ProjectRecord? project)
        {
            if (IsBusy) return;
            var session = _taskStore.LoadSession(sessionId) ?? throw new InvalidOperationException("对话已不存在。");
            if (project != null && !_taskStore.LoadProjects().Any(p => p.ProjectId == project.ProjectId)) throw new InvalidOperationException("目标项目已不存在。");
            session.ProjectId = project?.ProjectId ?? ""; _taskStore.SaveSession(session);
            if (_currentSessionId == sessionId) { _selectedProject = project; PersistProjectSelection(); NotifyProjectContext(); }
            if (project != null)
            {
                var target = Projects.FirstOrDefault(p => p.Project.ProjectId == project.ProjectId);
                if (target != null) target.IsExpanded = true;
            }
            ReloadHistory(); StatusText = project == null ? "对话已移出项目，文件位置未改变。" : "对话已归入“" + project.Name + "”，文件位置未改变。";
        }
        public void RemoveConversation(string sessionId)
        {
            if (IsBusy) return;
            _taskStore.DeleteConversation(sessionId);
            if (_currentSessionId == sessionId) NewTask();
            ReloadHistory(); StatusText = "已从对话列表移除；任务记录和文件仍保留在任务中心。";
        }
        private void ApplySidebar()
        {
            var expanded = new HashSet<string>(Projects.Where(p => p.IsExpanded).Select(p => p.Project.ProjectId));
            string query = SidebarSearch.Trim();
            bool Matches(string text) => query.Length == 0 || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            HistoryItemVm Row(SessionRecord session)
            {
                var state = _sidebarStates.TryGetValue(session.SessionId, out var value) ? value : (Text: "对话", Failed: false, Running: false);
                return new HistoryItemVm(session.SessionId, session.Turns.LastOrDefault()?.TaskId ?? "", session.Title, state.Text,
                    session.LastActiveUtc.ToLocalTime(), state.Failed, state.Running) { ProjectId = session.ProjectId, IsSelected = session.SessionId == _currentSessionId };
            }
            Projects.Clear();
            foreach (var project in _sidebarProjects)
            {
                var sessions = _sidebarSessions.Where(s => s.ProjectId == project.ProjectId).ToList();
                if (!Matches(project.Name) && !Matches(project.Description) && !sessions.Any(s => Matches(s.Title))) continue;
                var item = new ProjectItemVm(project) { IsSelected = project.ProjectId == CurrentProjectId,
                    IsExpanded = query.Length > 0 || expanded.Contains(project.ProjectId) || project.ProjectId == CurrentProjectId };
                foreach (var session in sessions.Where(s => Matches(project.Name) || Matches(s.Title)).Take(100)) item.Conversations.Add(Row(session));
                Projects.Add(item);
            }
            HistoryGroups.Clear();
            var ids = new HashSet<string>(_sidebarProjects.Select(p => p.ProjectId));
            var today = DateTime.Today;
            foreach (var group in _sidebarSessions.Where(s => !ids.Contains(s.ProjectId) && Matches(s.Title)).Take(100)
                .GroupBy(s => GroupHeader(s.LastActiveUtc.ToLocalTime(), today, today.AddDays(-1))))
            {
                var view = new HistoryGroupVm(group.Key);
                foreach (var session in group) view.Items.Add(Row(session));
                HistoryGroups.Add(view);
            }
            OnPropertyChanged(nameof(IsHistoryEmpty)); OnPropertyChanged(nameof(IsProjectListEmpty));
        }
    }
}
