using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Desktop
{
    internal sealed class MaintenanceWindow : Window
    {
        private PreparedUpdate? _prepared;
        private readonly TextBox _url = new();
        private readonly ComboBox _channel = new() { ItemsSource = new[] { "stable", "preview" } };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) };
        private readonly CancellationTokenSource _cancel = new();
        public MaintenanceWindow()
        {
            Title = "版本、诊断与更新"; Width = 860; Height = 760; MinWidth = 650; MinHeight = 550; ProductionUi.Style(this);
            var panel = new StackPanel { Margin = new Thickness(28) };
            panel.Children.Add(new TextBlock { Text = "版本与维护", FontSize = 26, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "当前版本 " + typeof(App).Assembly.GetName().Version + " · 状态库 v1\n" +
                (TrustedUpdates.PublisherCertificateSha256.Length == 64 ? "已配置发布者证书，更新将验证签名、摘要与渠道。" : "开发候选构建：未配置正式发布证书，更新安装不可用。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 24) });
            panel.Children.Add(new TextBlock { Text = "更新清单地址（HTTPS，仅连接你指定的服务）", Margin = new Thickness(0, 0, 0, 6) });
            _url.Text = App.Settings.UpdateManifestUrl; _url.MinHeight = 38; panel.Children.Add(_url);
            panel.Children.Add(new TextBlock { Text = "发布渠道", Margin = new Thickness(0, 12, 0, 6) });
            _channel.SelectedItem = App.Settings.UpdateChannel; _channel.MinHeight = 38; panel.Children.Add(_channel);
            var updates = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            var download = ProductionUi.Button("检查并下载更新", () => { });
            download.Click += async (_, __) =>
            {
                download.IsEnabled = false; _status.Text = "正在验证清单并下载安装包…";
                try
                {
                    App.Settings.UpdateManifestUrl = _url.Text.Trim(); App.Settings.UpdateChannel = (string)_channel.SelectedItem; App.SettingsStore.Save(App.Settings);
                    _prepared = await TrustedUpdates.DownloadAsync(App.Settings.UpdateManifestUrl, App.Settings.UpdateChannel,
                        Path.Combine(App.SettingsStore.Root, "updates"), _cancel.Token);
                    _status.Text = "已验证版本 " + _prepared.Manifest.Version + "，渠道 " + _prepared.Manifest.Channel + "。点击安装前会备份状态库。";
                }
                catch (Exception ex) { _status.Text = "更新未准备好：" + ex.Message; }
                finally { download.IsEnabled = true; }
            };
            updates.Children.Add(download);
            updates.Children.Add(ProductionUi.Button("导入已签名安装包", () =>
            {
                var manifest = new OpenFileDialog { Filter = "签名更新清单|*.p7m" }; if (manifest.ShowDialog(this) != true) return;
                var installer = new OpenFileDialog { Filter = "安装程序|*.exe" }; if (installer.ShowDialog(this) != true) return;
                _prepared = TrustedUpdates.Import(manifest.FileName, installer.FileName, (string)_channel.SelectedItem);
                _status.Text = "已验证版本 " + _prepared.Manifest.Version + "，可安装。";
            }));
            updates.Children.Add(ProductionUi.Button("安装已验证更新", () =>
            {
                if (_prepared == null) throw new IOException("请先下载或导入并验证更新。");
                if (App.MainVm?.IsBusy == true) throw new IOException("请等待任务结束后再更新。");
                using var locked = new FileStream(_prepared.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                TrustedUpdates.VerifyInstaller(_prepared.InstallerPath, _prepared.Manifest);
                App.TaskStore.Backup(Path.Combine(App.SettingsStore.Root, "backups", "pre-update-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".sqlite3"));
                Process.Start(new ProcessStartInfo(_prepared.InstallerPath) { UseShellExecute = true });
                Application.Current.Shutdown();
            }));
            panel.Children.Add(updates); panel.Children.Add(_status);
            var retention = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var days = new TextBox { Text = App.Settings.RetentionDays.ToString(), Width = 72, MinHeight = 38, Margin = new Thickness(8, 0, 16, 0) };
            var quota = new TextBox { Text = App.Settings.LocalQuotaMiB.ToString(), Width = 90, MinHeight = 38, Margin = new Thickness(8, 0, 16, 0) };
            retention.Children.Add(new TextBlock { Text = "保留天数", VerticalAlignment = VerticalAlignment.Center }); retention.Children.Add(days);
            retention.Children.Add(new TextBlock { Text = "本地配额 MiB", VerticalAlignment = VerticalAlignment.Center }); retention.Children.Add(quota);
            retention.Children.Add(ProductionUi.Button("保存保留策略", () =>
            {
                if (!int.TryParse(days.Text, out int dayValue) || dayValue < 1 || dayValue > 3650 || !int.TryParse(quota.Text, out int quotaValue) || quotaValue < 100 || quotaValue > 102400) throw new IOException("保留天数需为 1～3650，配额需为 100～102400 MiB。");
                App.Settings.RetentionDays = dayValue; App.Settings.LocalQuotaMiB = quotaValue; App.SettingsStore.Save(App.Settings); _status.Text = "保留策略已保存；点击清理后生效。";
            }));
            panel.Children.Add(retention);
            var actions = new WrapPanel();
            actions.Children.Add(ProductionUi.Button("导出脱敏诊断", () =>
            {
                var file = new SaveFileDialog { Filter = "JSON|*.json", FileName = "workassistant-diagnostics.json" };
                if (file.ShowDialog(this) == true) DurableFiles.WriteText(file.FileName, EnvironmentReport.Capture(App.TaskStore).ToString());
            }));
            actions.Children.Add(ProductionUi.Button("按保留期与配额清理", () =>
            {
                if (App.MainVm?.IsBusy == true) throw new IOException("请先等待当前任务结束。");
                if (MessageBox.Show(this, "将删除超过保留期的已结束任务；如仍超出配额，将继续从最旧的终态任务开始清理。待复核、排队和运行中任务以及工作区文件会保留。继续吗？", "清理本地任务", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                int count = App.TaskStore.Cleanup(App.Settings.RetentionDays, App.Settings.LocalQuotaMiB);
                _status.Text = "已清理 " + count + " 项过期或超配额的终态任务；工作区原件与成果文件保留。";
            }));
            actions.Children.Add(ProductionUi.Button("恢复状态库备份", () =>
            {
                if (App.MainVm?.IsBusy == true) throw new IOException("请先等待当前任务结束。");
                var file = new OpenFileDialog { Filter = "SQLite 备份|*.sqlite3" }; if (file.ShowDialog(this) != true) return;
                if (MessageBox.Show(this, "将用备份替换当前任务状态并退出程序。当前状态会另存备份；请重新启动程序。", "恢复状态库", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                App.TaskStore.Backup(Path.Combine(App.SettingsStore.Root, "backups", "pre-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".sqlite3"));
                App.TaskStore.Restore(file.FileName); Application.Current.Shutdown();
            }));
            panel.Children.Add(actions);
            panel.Children.Add(new TextBlock { Text = "旧版本", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 12) });
            string current = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(current);
            bool found = false;
            if (parent?.Name == "versions") foreach (var directory in parent.GetDirectories().Where(d => !string.Equals(d.FullName, current, StringComparison.OrdinalIgnoreCase)))
            {
                string executable = Path.Combine(directory.FullName, "WorkAssistant.Desktop.exe"), manifest = Path.Combine(directory.FullName, "release.p7m");
                if (!File.Exists(executable) || !File.Exists(manifest)) continue;
                found = true;
                panel.Children.Add(ProductionUi.Button("切换到 " + directory.Name, () =>
                {
                    if (App.MainVm?.IsBusy == true) throw new IOException("请先结束当前任务。");
                    var release = TrustedUpdates.VerifyManifest(File.ReadAllBytes(manifest), (string)_channel.SelectedItem, true);
                    TrustedUpdates.VerifyExecutable(executable);
                    if (!string.Equals(WorkAssistant.Tools.TaskOutputs.Sha256File(executable), release.ExecutableSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("旧版本摘要与签名清单不一致。");
                    if (System.Version.Parse(FileVersionInfo.GetVersionInfo(executable).FileVersion!) != System.Version.Parse(release.Version)) throw new IOException("旧版本文件与签名清单不一致。");
                    App.TaskStore.Backup(Path.Combine(App.SettingsStore.Root, "backups", "pre-rollback-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".sqlite3"));
                    Process.Start(new ProcessStartInfo(executable, "--wait-for-exit " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                    Application.Current.Shutdown();
                }));
            }
            if (!found) panel.Children.Add(new TextBlock { Text = "尚无已签名的旧版本。安装器会保留版本目录；只允许切换到兼容当前状态库的版本。", TextWrapping = TextWrapping.Wrap });
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Closed += (_, __) => _cancel.Cancel();
        }
    }
}
