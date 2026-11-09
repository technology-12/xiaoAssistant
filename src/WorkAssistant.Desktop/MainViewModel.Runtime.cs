using WorkAssistant.Core;

namespace WorkAssistant.Desktop
{
    public sealed partial class MainViewModel
    {
        private bool _isReadOnlyAnalysis;
        /// <summary>UI-owned policy, snapshotted with each submitted/queued task.</summary>
        public bool IsReadOnlyAnalysis
        {
            get => _isReadOnlyAnalysis;
            set
            {
                if (IsBusy || _isReadOnlyAnalysis == value) return;
                _isReadOnlyAnalysis = value;
                OnPropertyChanged(nameof(IsReadOnlyAnalysis));
                StatusText = value ? "只读分析：可以读取和计算，不会生成或修改文件。" : "执行模式：当前权限为「" + PermissionDisplay + "」，对新任务生效。";
            }
        }
        private AgentExecutionMode SelectedExecutionMode => IsReadOnlyAnalysis ? AgentExecutionMode.ReadOnly : AgentExecutionMode.Execute;
        private void RestoreExecutionMode(AgentExecutionMode mode)
        {
            _isReadOnlyAnalysis = mode == AgentExecutionMode.ReadOnly;
            OnPropertyChanged(nameof(IsReadOnlyAnalysis));
        }
    }
}
