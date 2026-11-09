using System;
using System.Threading;
using System.Windows;
using WorkAssistant.Core;

namespace WorkAssistant.Desktop
{
    /// <summary>
    /// 审批对话框（正式版 §5.3/§9.2）：展示具体文件与动作，用户明确同意或取消；
    /// 一次确认只覆盖本次列出的操作。任务被取消时窗口自动关闭并视为未同意。
    /// </summary>
    public partial class ApprovalWindow : Window
    {
        private bool _resolved;

        public bool Approved { get; private set; }

        public ApprovalWindow(ApprovalRequest request, CancellationToken cancellationToken)
        {
            InitializeComponent();
            TitleText.Text = request.Title;
            ReasonText.Text = request.Reason;
            OperationsList.ItemsSource = request.Operations;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Approved = false;
                        _resolved = true;
                        try { Close(); } catch (InvalidOperationException) { }
                    }));
                });
            }
        }

        private void OnAllow(object sender, RoutedEventArgs e)
        {
            Approved = true;
            _resolved = true;
            Close();
        }

        private void OnDeny(object sender, RoutedEventArgs e)
        {
            Approved = false;
            _resolved = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_resolved)
            {
                Approved = false; // 关闭窗口（含 Esc / 标题栏 X）视为拒绝
            }
            base.OnClosing(e);
        }
    }
}
