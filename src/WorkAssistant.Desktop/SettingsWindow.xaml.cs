using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.OpenCode;

namespace WorkAssistant.Desktop
{
    /// <summary>
    /// 模型设置（正式版 §7.1：多 Profile、测试与切换）：
    /// - Profile 列表新增/删除/切换；凭据按 Profile 独立 DPAPI 保存。
    /// - 测试连接只针对当前编辑的 Profile，用合成数据探测，成功状态不自动套用到其他 Profile。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly SettingsStore _store;
        private bool _testing;
        private bool _loadingProfile;

        /// <summary>编辑中的 Profile 副本（保存时整体写回）。</summary>
        private readonly List<ModelProfile> _profiles;
        private readonly Dictionary<string, string> _apiKeys = new(StringComparer.Ordinal);
        private string _activeName;
        private string? _displayedProfileName;
        private readonly HashSet<string> _deletedProfiles = new(StringComparer.Ordinal);

        public SettingsWindow(AppSettings settings, SettingsStore store)
        {
            InitializeComponent();
            void ClearConnectionDisplay() { if (!_testing) TestResultText.Visibility = Visibility.Collapsed; }
            BaseUrlBox.TextChanged += (_, __) => ClearConnectionDisplay();
            ProtocolBox.SelectionChanged += (_, __) =>
            {
                ClearConnectionDisplay(); bool messages = (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string == "anthropic-messages";
                AnthropicVersionPanel.Visibility = messages ? Visibility.Visible : Visibility.Collapsed;
                BaseUrlHintText.Text = messages ? "填写服务商根地址或 /v1 基础地址，将使用 Messages 接口。" : "填写服务商提供的基础地址，通常以 /v1 结尾。";
            };
            ContextTokensBox.TextChanged += (_, __) => ClearConnectionDisplay();
            OutputTokensBox.TextChanged += (_, __) => ClearConnectionDisplay();
            ModelBox.TextChanged += (_, __) => ClearConnectionDisplay();
            TimeoutBox.TextChanged += (_, __) => ClearConnectionDisplay();
            ApiKeyBox.PasswordChanged += (_, __) => ClearConnectionDisplay();
            ProxyCheck.Checked += (_, __) => ClearConnectionDisplay();
            ProxyCheck.Unchecked += (_, __) => ClearConnectionDisplay();
            _settings = settings;
            _store = store;
            var connection = new OpenCodeConnectionStore().Load();
            RemoteKernelCheck.IsChecked = connection.Enabled;
            KernelUrlBox.Text = connection.BaseUrl;
            KernelTokenBox.Password = connection.Token;
            RemoteKernelPanel.IsEnabled = !OpenCodeConnectionStore.IsLocalOnly;
            RemoteKernelExpander.Visibility = OpenCodeConnectionStore.IsLocalOnly ? Visibility.Collapsed : Visibility.Visible;
            if (App.MainVm is { IsBusy: true })
                RemoteKernelCheck.IsEnabled = KernelUrlBox.IsEnabled = KernelTokenBox.IsEnabled = false;

            _profiles = settings.Profiles
                .Select(p => new ModelProfile(p.Name)
                {
                    BaseUrl = p.BaseUrl, ModelName = p.ModelName,
                    TimeoutSeconds = p.TimeoutSeconds, UseSystemProxy = p.UseSystemProxy,
                    VerifiedFingerprint = p.VerifiedFingerprint, LastVerifiedUtc = p.LastVerifiedUtc,
                    ToolCallingVerified = p.ToolCallingVerified, StreamingSupported = p.StreamingSupported,
                    Protocol = p.Protocol, AnthropicVersion = p.AnthropicVersion, ContextWindowTokens = p.ContextWindowTokens, MaxOutputTokens = p.MaxOutputTokens
                })
                .ToList();
            if (_profiles.Count == 0)
            {
                _profiles.Add(new ModelProfile(AppSettings.DefaultProfileName));
            }
            foreach (var profile in _profiles)
            {
                _apiKeys[profile.Name] = store.LoadEffectiveApiKey(profile);
            }
            // 当前活动 Profile 的密钥已在 AppSettings.ApiKey（加载时解密）。
            _activeName = settings.ActiveProfileName;
            if (_profiles.All(p => p.Name != _activeName)) _activeName = _profiles[0].Name;
            _apiKeys[_activeName] = settings.ApiKey;

            DemoModeCheck.IsChecked = settings.FakeMode;
            EnableSubAgentsCheck.IsChecked = settings.EnableSubAgents;
            if (App.MainVm is { IsBusy: true }) EnableSubAgentsCheck.IsEnabled = SubAgentProfileBox.IsEnabled = false;
            TestResultText.Text = "";
            SaveResultText.Text = "";

            ReloadProfileBox(selected: _activeName);

            // 任务运行中：模式切换锁定。
            if (App.MainVm is { IsBusy: true })
            {
                DemoModeCheck.IsEnabled = false;
                ModeLockHint.Visibility = Visibility.Visible;
            }
        }

        private void ReloadProfileBox(string selected)
        {
            _loadingProfile = true;
            ProfileBox.Items.Clear();
            foreach (var profile in _profiles)
            {
                ProfileBox.Items.Add(profile.Name);
            }
            var index = _profiles.FindIndex(p => p.Name == selected);
            ProfileBox.SelectedIndex = index >= 0 ? index : 0;
            _loadingProfile = false;
            LoadProfileFields(SelectedProfile);
            DeleteProfileButton.IsEnabled = _profiles.Count > 1;
            var workerName = (SubAgentProfileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.SubAgentProfileName;
            SubAgentProfileBox.Items.Clear();
            SubAgentProfileBox.Items.Add(new ComboBoxItem { Content = "跟随主模型", Tag = "" });
            foreach (var profile in _profiles) SubAgentProfileBox.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Name });
            SubAgentProfileBox.SelectedItem = SubAgentProfileBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == workerName) ?? SubAgentProfileBox.Items[0];
        }

        private ModelProfile SelectedProfile =>
            ProfileBox.SelectedIndex >= 0 ? _profiles[ProfileBox.SelectedIndex] : _profiles[0];

        /// <summary>切换 Profile：先把当前表单写回副本，再装载新 Profile。</summary>
        private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingProfile || ProfileBox.SelectedIndex < 0) return;
            var previous = _profiles.FirstOrDefault(p => p.Name == _displayedProfileName);
            if (previous != null) WriteFormTo(previous);
            LoadProfileFields(SelectedProfile);
        }

        private void LoadProfileFields(ModelProfile profile)
        {
            _displayedProfileName = profile.Name;
            BaseUrlBox.Text = profile.BaseUrl.Length > 0
                ? profile.BaseUrl : "https://gateway.example.com/v1";
            ProtocolBox.SelectedItem = ProtocolBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == profile.Protocol);
            ContextTokensBox.Text = profile.ContextWindowTokens.ToString(); OutputTokensBox.Text = profile.MaxOutputTokens.ToString();
            AnthropicVersionBox.Text = profile.AnthropicVersion; ContextErrorText.Visibility = Visibility.Collapsed;
            ModelBox.Text = profile.ModelName;
            _apiKeys.TryGetValue(profile.Name, out var key);
            ApiKeyBox.Password = key ?? "";
            TimeoutBox.Text = profile.TimeoutSeconds.ToString();
            ProxyCheck.IsChecked = profile.UseSystemProxy;
            SaveResultText.Text = "";
            TestResultText.Text = "";
            TestResultText.Visibility = Visibility.Collapsed;
        }

        private void WriteFormTo(ModelProfile profile)
        {
            profile.BaseUrl = BaseUrlBox.Text.Trim();
            profile.ModelName = ModelBox.Text.Trim();
            _apiKeys[profile.Name] = ApiKeyBox.Password;
            profile.TimeoutSeconds = int.TryParse(TimeoutBox.Text.Trim(), out var t) && t > 0 ? t : 90;
            profile.UseSystemProxy = ProxyCheck.IsChecked == true;
            profile.Protocol = (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            profile.AnthropicVersion = AnthropicVersionBox.Text.Trim();
            profile.ContextWindowTokens = int.TryParse(ContextTokensBox.Text.Trim(), out var context) ? context : -1;
            profile.MaxOutputTokens = int.TryParse(OutputTokensBox.Text.Trim(), out var output) ? output : -1;
        }

        private void OnAddProfile(object sender, RoutedEventArgs e)
        {
            var previous = _profiles.FirstOrDefault(p => p.Name == _displayedProfileName);
            if (previous != null) WriteFormTo(previous);
            var name = "配置" + (_profiles.Count + 1);
            var counter = _profiles.Count + 1;
            while (_profiles.Any(p => p.Name == name))
            {
                counter++;
                name = "配置" + counter;
            }
            var profile = new ModelProfile(name)
            {
                BaseUrl = "", ModelName = "", TimeoutSeconds = 90, UseSystemProxy = true
            };
            _profiles.Add(profile);
            _apiKeys[name] = "";
            ReloadProfileBox(name);
        }

        private void OnDeleteProfile(object sender, RoutedEventArgs e)
        {
            if (_profiles.Count <= 1) return;
            var victim = SelectedProfile;
            var message = $"删除配置“{victim.Name}”？（其密钥文件会一并删除，不影响其他配置）";
            if (MessageBox.Show(this, message, "删除模型配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _profiles.Remove(victim);
            _apiKeys.Remove(victim.Name);
            _deletedProfiles.Add(victim.Name); // Cancel must not delete persisted credentials.
            if (_activeName == victim.Name) _activeName = _profiles[0].Name;
            ReloadProfileBox(_profiles[0].Name);
        }

        private ModelSettings ReadForm()
        {
            var profile = SelectedProfile;
            WriteFormTo(profile);
            return ModelSettings.FromProfile(profile, _apiKeys.TryGetValue(profile.Name, out var key) ? key : "");
        }

        private void OnDemoModeChanged(object sender, RoutedEventArgs e)
        {
            // 实时预览模式徽标由保存后刷新；此处仅提示。
        }

        private async void OnTestConnection(object sender, RoutedEventArgs e)
        {
            if (_testing) return; // 等待状态，避免重复提交
            if (!ValidateLimits()) return;
            var modelSettings = ReadForm();
            if (modelSettings.BaseUrl.Length == 0 || modelSettings.ModelName.Length == 0)
            {
                ShowTestResult(false, "请先填写接口地址与模型名称。");
                return;
            }
            if (!modelSettings.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ShowTestResult(false,
                    "提醒：当前使用 HTTP 明文连接，密钥与内容会以明文传输，仅建议用于可信内网。");
            }

            _testing = true;
            var testedProfile = SelectedProfile;
            TestButton.IsEnabled = false;
            TestButton.Content = "测试中…";
            TestResultText.Visibility = Visibility.Visible;
            TestResultText.Foreground = (Brush)FindResource("BrushTextSecondary");
            TestResultText.Text = "正在连接并探测工具调用能力…";
            try
            {
                var result = await XpTask.Run(async () =>
                {
                    using var cts = XpTask.Cts(TimeSpan.FromSeconds(100));
                    return await ModelConnectionProbe.TestAsync(modelSettings, cts.Token);
                });
                var ok = result.AuthOk && result.ModelOk && result.ToolCallingOk;
                ModelCapabilityCache.Apply(testedProfile, modelSettings, result, DateTime.UtcNow);
                ShowTestResult(ok, (!result.AuthChecked ? "认证未验证" : result.AuthOk ? "认证通过" : "认证失败") +
                    "；" + (result.ModelOk ? "模型可用" : "模型不可用") +
                    "；" + (result.ToolCallingOk ? "已检测工具调用支持" : "未确认工具调用支持") +
                    "。" + result.Message);
                bool stillMatches = ((ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string) == modelSettings.Protocol && AnthropicVersionBox.Text.Trim() == modelSettings.AnthropicVersion && _displayedProfileName == testedProfile.Name && BaseUrlBox.Text.Trim() == modelSettings.BaseUrl &&
                    ModelBox.Text.Trim() == modelSettings.ModelName && ApiKeyBox.Password == modelSettings.ApiKey &&
                    (int.TryParse(TimeoutBox.Text.Trim(), out int currentTimeout) && currentTimeout > 0 ? currentTimeout : 90) == modelSettings.TimeoutSeconds &&
                    (ProxyCheck.IsChecked == true) == modelSettings.UseSystemProxy;
                if (!stillMatches) ShowTestResult(false, "检测结果对应配置“" + testedProfile.Name + "”的旧表单；当前配置已变化，请重新检测。");
                App.MainVm?.ReportConnectionResult(ok, ok ? "连接成功" : "连接失败", modelSettings);
            }
            catch (Exception ex)
            {
                ShowTestResult(false, "测试失败：" + ex.Message);
                App.MainVm?.ReportConnectionResult(false, "连接失败", modelSettings);
            }
            finally
            {
                _testing = false;
                TestButton.IsEnabled = true;
                TestButton.Content = "测试连接";
            }
        }

        private void ShowTestResult(bool ok, string text)
        {
            TestResultText.Visibility = Visibility.Visible;
            TestResultText.Foreground = ok
                ? (Brush)FindResource("BrushSuccess")
                : (Brush)FindResource("BrushError");
            TestResultText.Text = text;
        }

        private void OnValidateLimits(object sender, RoutedEventArgs e) => ValidateLimits();
        private bool ValidateLimits()
        {
            try
            {
                if (!int.TryParse(ContextTokensBox.Text.Trim(), out var context) || !int.TryParse(OutputTokensBox.Text.Trim(), out var output))
                    throw new ToolException(ErrorCodes.InvalidArguments, "请填写整数，未指定时填写 0。");
                _ = new ModelContextBudget(context, output);
                if (ProtocolBox.SelectedItem == null) throw new ToolException(ErrorCodes.InvalidArguments, "请选择接口协议。");
                if (((ComboBoxItem)ProtocolBox.SelectedItem).Tag as string == "anthropic-messages" &&
                    !DateTime.TryParseExact(AnthropicVersionBox.Text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    throw new ToolException(ErrorCodes.InvalidArguments, "Messages API 版本请填写 YYYY-MM-DD。");
                ContextErrorText.Visibility = Visibility.Collapsed; return true;
            }
            catch (ToolException ex)
            {
                ContextErrorText.Text = ex.Message; ContextErrorText.Visibility = Visibility.Visible;
                for (DependencyObject? parent = ContextErrorText; parent != null; parent = LogicalTreeHelper.GetParent(parent))
                    if (parent is Expander advanced) advanced.IsExpanded = true;
                ContextErrorText.BringIntoView(); return false;
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_testing) { SaveResultText.Text = "检测尚未结束，请等待或取消此窗口。"; return; }
            if (!ValidateLimits()) return;
            _activeName = SelectedProfile.Name;
            // 先把当前表单写回副本。
            WriteFormTo(SelectedProfile);

            try { foreach (var profile in _profiles) _ = new ModelContextBudget(profile.ContextWindowTokens, profile.MaxOutputTokens); }
            catch (ToolException ex) { SaveResultText.Text = ex.Message; return; }
            var demoMode = DemoModeCheck.IsEnabled ? DemoModeCheck.IsChecked == true : _settings.FakeMode;
            if (!demoMode)
            {
                var active = _profiles.FirstOrDefault(p => p.Name == _activeName) ?? SelectedProfile;
                if (active.BaseUrl.Length == 0 || active.ModelName.Length == 0)
                {
                    SaveResultText.Foreground = (Brush)FindResource("BrushError");
                    SaveResultText.Text = "保存失败：在线模式的活动配置缺少接口地址或模型名称。";
                    if (active.BaseUrl.Length == 0) BaseUrlBox.Focus();
                    else ModelBox.Focus();
                    return;
                }
            }

            try
            {
                if (RemoteKernelCheck.IsEnabled && !OpenCodeConnectionStore.IsLocalOnly)
                    new OpenCodeConnectionStore().Save(new OpenCodeConnection { Enabled = RemoteKernelCheck.IsChecked == true,
                        BaseUrl = KernelUrlBox.Text.Trim(), Token = KernelTokenBox.Password });
                _settings.Profiles = _profiles;
                if (EnableSubAgentsCheck.IsEnabled)
                {
                    _settings.EnableSubAgents = EnableSubAgentsCheck.IsChecked == true;
                    _settings.SubAgentProfileName = (SubAgentProfileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                }
                _settings.ActiveProfileName = _activeName;
                if (DemoModeCheck.IsEnabled)
                {
                    _settings.FakeMode = DemoModeCheck.IsChecked == true;
                }
                foreach (var profile in _profiles)
                {
                    _store.SaveApiKey(profile.Name,
                        _apiKeys.TryGetValue(profile.Name, out var key) ? key : "");
                }
                _store.Save(_settings);
                foreach (var name in _deletedProfiles) _store.DeleteApiKey(name);
                // 回填活动密钥到运行时对象（切换 Profile 后立即生效）。
                _settings.ApiKey = _apiKeys.TryGetValue(_activeName, out var activeKey) ? activeKey : "";
            }
            catch (Exception ex)
            {
                SaveResultText.Foreground = (Brush)FindResource("BrushError");
                SaveResultText.Text = "保存失败：" + ex.Message;
                return;
            }
            DialogResult = true;
            Close();
        }

        private async void OnTestKernel(object sender, RoutedEventArgs e)
        {
            if (_testing) return;
            _testing = true; SaveResultText.Text = "正在检查内核连接…";
            try
            {
                using var client = new OpenCodeGatewayClient(new OpenCodeConnection { BaseUrl = KernelUrlBox.Text.Trim(), Token = KernelTokenBox.Password });
                using var timeout = XpTask.Cts(TimeSpan.FromSeconds(10));
                var result = await client.SendAsync("health", null, timeout.Token);
                SaveResultText.Text = (int?)result["version"] == 1 && (string?)result["upstreamVersion"] == "1.1.20"
                    ? "内核连接正常 · OpenCode 1.1.20" : "内核协议或版本不匹配。";
            }
            catch { SaveResultText.Text = "无法连接内核，请检查地址、访问令牌及服务运行状态。"; }
            finally { _testing = false; }
        }
    }
}
