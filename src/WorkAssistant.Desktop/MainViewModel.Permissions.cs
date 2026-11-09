using WorkAssistant.Core;

namespace WorkAssistant.Desktop
{
    public sealed partial class MainViewModel
    {
        public TaskPermissionMode SelectedPermissionMode => _settings.PermissionMode;
        private TaskPermissionMode? _activePermissionMode;
        private TaskPermissionMode DisplayedPermissionMode => IsRunning && _activePermissionMode.HasValue ? _activePermissionMode.Value : SelectedPermissionMode;
        public string PermissionDisplay => TaskPermissions.Label(DisplayedPermissionMode);
        public string PermissionDescription => TaskPermissions.Description(DisplayedPermissionMode) + " 只读分析、工具白名单和工作区边界优先生效；任务提交后权限固定。";
        public bool SelectPermissionMode(TaskPermissionMode mode)
        {
            if (IsBusy) return false;
            TaskPermissions.Validate(mode);
            var previous = _settings.PermissionMode;
            if (previous == mode) return true;
            _settings.PermissionMode = mode;
            try { _settingsStore.Save(_settings); }
            catch { _settings.PermissionMode = previous; StatusText = "权限选择未能保存，已保留原设置。"; return false; }
            OnPropertyChanged(nameof(SelectedPermissionMode)); OnPropertyChanged(nameof(PermissionDisplay)); OnPropertyChanged(nameof(PermissionDescription));
            StatusText = "权限已设为「" + PermissionDisplay + "」；新任务生效。";
            return true;
        }
        private bool _isCompactingContext;
        private int _contextCompactions, _priorContextCompactions;
        public string ContextAttentionText => _isCompactingContext ? "正在整理上下文…" : _contextCompactions > 0 ? "上下文已整理 " + _contextCompactions + " 次" : "上下文自动整理";
        public string ContextAttentionDescription => "对话较长时，自动收起旧工具结果并压缩历史，保留当前目标、近期步骤、文档状态和待办。原始记录仍在本地，可按需回查；摘要不能代替原文或事实核验。";
        private void UpdateContextAttention(int? count = null, bool compacting = false)
        {
            if (count.HasValue) _contextCompactions = count.Value;
            _isCompactingContext = compacting;
            OnPropertyChanged(nameof(ContextAttentionText));
        }
    }
}
