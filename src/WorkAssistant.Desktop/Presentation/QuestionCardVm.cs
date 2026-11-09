using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WorkAssistant.Desktop.Presentation
{
    public sealed class QuestionCardVm : ObservableObject
    {
        private readonly TaskCompletionSource<string?> _completion = XpTask.CompletionSource<string?>();
        private string _answer = "";
        private bool _pending = true;
        public string Question { get; }
        public string Details { get; }
        public IList<string> Options { get; }
        public bool AllowFreeText { get; }
        public bool NeedsText => AllowFreeText && Options.Count == 0;
        public bool ShowDetails => !AllowFreeText && Details.Length > 0;
        public bool IsPending { get => _pending; private set { SetProperty(ref _pending, value, nameof(IsPending)); OnPropertyChanged(nameof(ResultText)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
        public string Answer { get => _answer; set => SetProperty(ref _answer, value, nameof(Answer)); }
        public string ResultText => IsPending ? "等待你的选择" : Answer.Length > 0 ? "已回答：" + Answer : "已取消，未授权继续此操作";
        public Task<string?> Completion => _completion.Task;
        public RelayCommand ChooseCommand { get; }
        public RelayCommand SubmitCommand { get; }
        public RelayCommand CancelCommand { get; }
        public QuestionCardVm(string question, IList<string> options, bool allowFreeText = true, string details = "")
        {
            Question = question; Options = options; AllowFreeText = allowFreeText; Details = details;
            ChooseCommand = new RelayCommand(value => Resolve(value as string), _ => IsPending);
            SubmitCommand = new RelayCommand(_ => Resolve(Answer), _ => IsPending && AllowFreeText && !string.IsNullOrWhiteSpace(Answer));
            CancelCommand = new RelayCommand(_ => Resolve(null), _ => IsPending);
        }
        public void Resolve(string? answer)
        {
            if (!IsPending) return;
            if (answer != null && (string.IsNullOrWhiteSpace(answer) || answer.Length > 4000 || !AllowFreeText && !Options.Contains(answer))) return;
            Answer = answer?.Trim() ?? ""; IsPending = false;
            _completion.TrySetResult(answer == null ? null : Answer);
        }
    }
}
