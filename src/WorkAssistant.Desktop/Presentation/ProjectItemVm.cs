using System.Collections.ObjectModel;
using WorkAssistant.Core;

namespace WorkAssistant.Desktop.Presentation
{
    public sealed class ProjectItemVm : ObservableObject
    {
        public ProjectRecord Project { get; }
        public string Name => Project.Name;
        public string Description => Project.Description;
        public string WorkspaceRoot => Project.WorkspaceRoot;
        public ObservableCollection<HistoryItemVm> Conversations { get; } = new();
        public string ConversationCount => Conversations.Count + " 个对话";
        private bool _isExpanded, _isSelected;
        public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value, nameof(IsExpanded)); }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value, nameof(IsSelected)); }
        public ProjectItemVm(ProjectRecord project) { Project = project; }
    }
}
