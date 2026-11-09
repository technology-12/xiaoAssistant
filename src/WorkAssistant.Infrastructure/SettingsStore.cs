using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Infrastructure
{
    /// <summary>模型连接 Profile（正式版 §7.1：多端点、测试与切换）。</summary>
    public sealed class ModelProfile
    {
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string ModelName { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 90;
        public bool UseSystemProxy { get; set; } = true;
        public string VerifiedFingerprint { get; set; } = "";
        public DateTime? LastVerifiedUtc { get; set; }
        public bool ToolCallingVerified { get; set; }
        public bool? StreamingSupported { get; set; }
        public string Protocol { get; set; } = "openai-chat";
        public string AnthropicVersion { get; set; } = "2023-06-01";
        public int ContextWindowTokens { get; set; }
        public int MaxOutputTokens { get; set; }

        public ModelProfile() { }

        public ModelProfile(string name)
        {
            Name = name;
        }
    }

    /// <summary>模型与界面配置（非敏感部分持久化为 settings.json）。</summary>
    public sealed class AppSettings
    {
        public WorkAssistant.Core.TaskPermissionMode PermissionMode { get; set; } = WorkAssistant.Core.TaskPermissionMode.Assisted;
        public List<ModelProfile> Profiles { get; set; } = new();
        public string ActiveProfileName { get; set; } = "";
        public string WorkspacePath { get; set; } = "";
        public string SelectedProjectId { get; set; } = "";
        public bool FakeMode { get; set; } = true;
        public bool EnableSubAgents { get; set; } = true;
        public string SubAgentProfileName { get; set; } = "";
        public string UpdateManifestUrl { get; set; } = "";
        public string UpdateChannel { get; set; } = "stable";
        public int RetentionDays { get; set; } = 90;
        public int LocalQuotaMiB { get; set; } = 1024;
        public string DataConsentFingerprint { get; set; } = "";
        public int BundledDefaultsVersion { get; set; }

        /// <summary>运行时填充（来自 DPAPI 密文），不写入 settings.json。</summary>
        [JsonIgnore]
        public string ApiKey { get; set; } = "";

        public const string DefaultProfileName = "默认";

        // ---- 活动 Profile 门面（老代码按单配置读写）----------------------------

        public ModelProfile ActiveProfile
        {
            get
            {
                var profile = Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, ActiveProfileName, StringComparison.Ordinal));
                if (profile == null) profile = Profiles.FirstOrDefault();
                if (profile == null)
                {
                    profile = new ModelProfile(AppSettings.DefaultProfileName);
                    Profiles.Add(profile);
                    ActiveProfileName = profile.Name;
                }
                return profile;
            }
        }

        public string BaseUrl
        {
            get => ActiveProfile.BaseUrl;
            set => ActiveProfile.BaseUrl = value;
        }

        public string ModelName
        {
            get => ActiveProfile.ModelName;
            set => ActiveProfile.ModelName = value;
        }

        public int TimeoutSeconds
        {
            get => ActiveProfile.TimeoutSeconds;
            set => ActiveProfile.TimeoutSeconds = value;
        }

        public bool UseSystemProxy
        {
            get => ActiveProfile.UseSystemProxy;
            set => ActiveProfile.UseSystemProxy = value;
        }
    }

    /// <summary>
    /// 配置存储：settings.json 放非敏感配置，credentials/&lt;profile&gt;.dat 为 DPAPI
    /// （当前用户）加密的 API Key（每个 Profile 独立一份）。
    /// 目录：%LOCALAPPDATA%/Win7WorkAssistant/。
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Win7WorkAssistant.v1");

        private readonly string _directory;

        public SettingsStore(string? directory = null)
        {
            _directory = directory ?? ApplicationDataPaths.Root;
            Directory.CreateDirectory(_directory);
        }

        public string Root => _directory;

        private string SettingsPath => Path.Combine(_directory, "settings.json");
        private string CredentialsDirectory => Path.Combine(_directory, "credentials");

        public AppSettings Load()
        {
            var settings = new AppSettings();
            var migrated = false;
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    var obj = JObject.Parse(json);

                    settings.WorkspacePath = (string?)obj["workspace_path"] ?? "";
                    settings.SelectedProjectId = (string?)obj["selected_project_id"] ?? "";
                    settings.FakeMode = (bool?)obj["fake_mode"] ?? true;
                    settings.EnableSubAgents = (bool?)obj["enable_subagents"] ?? true;
                    settings.SubAgentProfileName = (string?)obj["subagent_profile"] ?? "";
                    settings.UpdateManifestUrl = (string?)obj["update_manifest_url"] ?? "";
                    settings.UpdateChannel = (string?)obj["update_channel"] == "preview" ? "preview" : "stable";
                    settings.RetentionDays = Math.Max(1, (int?)obj["retention_days"] ?? 90);
                    settings.LocalQuotaMiB = Math.Max(100, (int?)obj["local_quota_mib"] ?? 1024);
                    settings.DataConsentFingerprint = (string?)obj["data_consent_fingerprint"] ?? "";
                    settings.BundledDefaultsVersion = (int?)obj["bundled_defaults_version"] ?? 0;
                    settings.PermissionMode = WorkAssistant.Core.TaskPermissions.ParseStored(obj["permission_mode"]);

                    foreach (var p in (JArray?)obj["profiles"] ?? new JArray())
                    {
                        settings.Profiles.Add(new ModelProfile
                        {
                            Name = (string?)p["name"] ?? AppSettings.DefaultProfileName,
                            BaseUrl = (string?)p["base_url"] ?? "",
                            ModelName = (string?)p["model"] ?? "",
                            TimeoutSeconds = (int?)p["timeout_seconds"] ?? 90,
                            UseSystemProxy = (bool?)p["use_system_proxy"] ?? true,
                            VerifiedFingerprint = (string?)p["verified_fingerprint"] ?? "",
                            LastVerifiedUtc = (DateTime?)p["last_verified_utc"], ToolCallingVerified = (bool?)p["tool_calling_verified"] ?? false,
                            StreamingSupported = (bool?)p["streaming_supported"],
                            Protocol = (string?)p["protocol"] ?? "openai-chat", AnthropicVersion = (string?)p["anthropic_version"] ?? "2023-06-01",
                            ContextWindowTokens = (int?)p["context_tokens"] ?? 0, MaxOutputTokens = (int?)p["max_output_tokens"] ?? 0
                        });
                    }
                    settings.ActiveProfileName = (string?)obj["active_profile"] ?? "";
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is FormatException)
                {
                    // 配置损坏时回退默认值，不阻塞启动。
                    settings = new AppSettings();
                }

                // 旧版单配置迁移：flat base_url/model → “默认” Profile（只迁移一次）。
                if (settings.Profiles.Count == 0)
                {
                    try
                    {
                        var obj = JObject.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8));
                        var legacy = new ModelProfile(AppSettings.DefaultProfileName)
                        {
                            BaseUrl = (string?)obj["base_url"] ?? "",
                            ModelName = (string?)obj["model"] ?? "",
                            TimeoutSeconds = (int?)obj["timeout_seconds"] ?? 90,
                            UseSystemProxy = (bool?)obj["use_system_proxy"] ?? true
                        };
                        if (legacy.BaseUrl.Length > 0 || legacy.ModelName.Length > 0)
                        {
                            settings.Profiles.Add(legacy);
                            settings.ActiveProfileName = legacy.Name;
                            // 迁移旧凭据文件。
                            var legacyCredential = Path.Combine(_directory, "credentials.dat");
                            if (File.Exists(legacyCredential))
                            {
                                var target = CredentialPath(legacy.Name);
                                if (!File.Exists(target))
                                {
                                    File.Copy(legacyCredential, target);
                                }
                            }
                            migrated = true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException)
                    {
                    }
                }
            }

            var bundled = LoadBundledDefaults();
            if (bundled != null && settings.BundledDefaultsVersion < bundled.Version)
            {
                bool configured = settings.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.ModelName) &&
                    p.BaseUrl.IndexOf("gateway.example.com", StringComparison.OrdinalIgnoreCase) < 0);
                if (!configured) settings.Profiles.RemoveAll(p => string.IsNullOrWhiteSpace(p.ModelName) &&
                    (string.IsNullOrWhiteSpace(p.BaseUrl) || p.BaseUrl.IndexOf("gateway.example.com", StringComparison.OrdinalIgnoreCase) >= 0));
                foreach (var profile in bundled.Profiles)
                    if (!settings.Profiles.Any(p => p.Name == profile.Name)) settings.Profiles.Add(profile);
                if (!configured)
                {
                    settings.ActiveProfileName = bundled.DefaultProfile;
                    settings.FakeMode = false;
                }
                settings.BundledDefaultsVersion = bundled.Version;
                migrated = true;
            }

            if (settings.Profiles.Count == 0)
            {
                settings.Profiles.Add(new ModelProfile(AppSettings.DefaultProfileName));
                settings.ActiveProfileName = AppSettings.DefaultProfileName;
            }
            if (settings.Profiles.All(p =>
                    !string.Equals(p.Name, settings.ActiveProfileName, StringComparison.Ordinal)))
            {
                settings.ActiveProfileName = settings.Profiles[0].Name;
            }

            settings.ApiKey = LoadEffectiveApiKey(settings.ActiveProfile);
            if (migrated)
            {
                Save(settings); // 落盘迁移结果
            }
            return settings;
        }

        public void Save(AppSettings settings)
        {
            var profiles = new JArray();
            foreach (var p in settings.Profiles)
            {
                profiles.Add(new JObject
                {
                    ["name"] = p.Name,
                    ["base_url"] = p.BaseUrl,
                    ["model"] = p.ModelName,
                    ["timeout_seconds"] = p.TimeoutSeconds,
                    ["use_system_proxy"] = p.UseSystemProxy,
                    ["verified_fingerprint"] = p.VerifiedFingerprint, ["last_verified_utc"] = p.LastVerifiedUtc,
                    ["tool_calling_verified"] = p.ToolCallingVerified, ["streaming_supported"] = p.StreamingSupported,
                    ["protocol"] = p.Protocol, ["anthropic_version"] = p.AnthropicVersion,
                    ["context_tokens"] = p.ContextWindowTokens, ["max_output_tokens"] = p.MaxOutputTokens
                });
            }
            var obj = new JObject
            {
                ["profiles"] = profiles,
                ["active_profile"] = settings.ActiveProfileName,
                ["workspace_path"] = settings.WorkspacePath,
                ["selected_project_id"] = settings.SelectedProjectId,
                ["fake_mode"] = settings.FakeMode,
                ["permission_mode"] = settings.PermissionMode.ToString(),
                ["enable_subagents"] = settings.EnableSubAgents,
                ["subagent_profile"] = settings.SubAgentProfileName,
                ["update_manifest_url"] = settings.UpdateManifestUrl, ["update_channel"] = settings.UpdateChannel,
                ["retention_days"] = settings.RetentionDays, ["local_quota_mib"] = settings.LocalQuotaMiB,
                ["data_consent_fingerprint"] = settings.DataConsentFingerprint,
                ["bundled_defaults_version"] = settings.BundledDefaultsVersion
            };
            WorkAssistant.Core.DurableFiles.WriteText(SettingsPath, obj.ToString(Formatting.Indented));
        }

        // ---- 凭据（每 Profile 独立 DPAPI 密文）--------------------------------

        public string LoadApiKey(string profileName)
        {
            var path = CredentialPath(profileName);
            if (File.Exists(path + ".disabled")) return "";
            if (!File.Exists(path))
            {
                // 迁移期兜底：旧版单凭据文件。
                var legacy = Path.Combine(_directory, "credentials.dat");
                if (profileName == AppSettings.DefaultProfileName && File.Exists(legacy)) path = legacy;
                else return "";
            }
            try
            {
                var cipher = File.ReadAllBytes(path);
                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                return "";
            }
            catch (IOException)
            {
                return "";
            }
        }

        public void SaveApiKey(string profileName, string apiKey)
        {
            Directory.CreateDirectory(CredentialsDirectory);
            var path = CredentialPath(profileName);
            if (string.IsNullOrEmpty(apiKey))
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
                WorkAssistant.Core.DurableFiles.WriteText(path + ".disabled", "disabled");
                return;
            }
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
            WorkAssistant.Core.DurableFiles.WriteBytes(path, cipher);
            if (File.Exists(path + ".disabled")) File.Delete(path + ".disabled");
        }

        public void DeleteApiKey(string profileName)
        {
            var path = CredentialPath(profileName);
            foreach (string suffix in new[] { "", ".bak", ".disabled", ".disabled.bak" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }

        public string LoadEffectiveApiKey(ModelProfile profile)
        {
            string path = CredentialPath(profile.Name);
            if (File.Exists(path + ".disabled")) return "";
            if (File.Exists(path)) return LoadApiKey(profile.Name);
            var defaults = LoadBundledDefaults();
            if (defaults?.Profiles.Any(p => p.Name == profile.Name && p.BaseUrl.TrimEnd('/') == profile.BaseUrl.TrimEnd('/') && p.ModelName == profile.ModelName) == true)
                return defaults.ApiKey;
            return profile.Name == AppSettings.DefaultProfileName ? LoadApiKey(profile.Name) : "";
        }

        private sealed class BundledDefaults
        {
            public int Version { get; set; }
            public string DefaultProfile { get; set; } = "";
            public string ApiKey { get; set; } = "";
            public List<ModelProfile> Profiles { get; } = new();
        }
        private static BundledDefaults? LoadBundledDefaults()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.defaults.json");
            if (!File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > 32768) throw new IOException();
                var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var value = new BundledDefaults { Version = (int?)json["version"] ?? 1, DefaultProfile = (string?)json["default_profile"] ?? "", ApiKey = (string?)json["api_key"] ?? "" };
                foreach (var item in (JArray?)json["profiles"] ?? new JArray())
                {
                    string url = (string?)item["base_url"] ?? "";
                    var profile = new ModelProfile((string?)item["name"] ?? "") { BaseUrl = url, ModelName = (string?)item["model"] ?? "", TimeoutSeconds = 90, UseSystemProxy = true };
                    if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.ModelName) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || value.Profiles.Any(p => p.Name == profile.Name)) throw new IOException();
                    value.Profiles.Add(profile);
                }
                if (!value.Profiles.Any(p => p.Name == value.DefaultProfile)) throw new IOException();
                return value;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is FormatException || ex is InvalidCastException)
            { throw new IOException("默认模型配置文件无效，请检查完整分发包；配置正文不会写入日志。"); }
        }

        /// <summary>Profile 名 → 安全文件名（非法字符替换 + 短哈希防碰撞）。</summary>
        private string CredentialPath(string profileName)
        {
            var safe = new string(profileName.Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            if (safe.Length == 0) safe = "profile";
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(profileName)))
                .Replace('/', '_').Replace('+', '-').TrimEnd('=');
            return Path.Combine(CredentialsDirectory, safe + "." + hash.Substring(0, 8) + ".dat");
        }
    }

    /// <summary>供构造 HttpModelClient 使用的模型设置。</summary>
    public sealed class ModelSettings
    {
        public string Protocol { get; set; } = "openai-chat";
        public string AnthropicVersion { get; set; } = "2023-06-01";
        public int ContextWindowTokens { get; set; }
        public int MaxOutputTokens { get; set; }
        public int EffectiveOutputTokens => MaxOutputTokens > 0 ? MaxOutputTokens : ModelCapabilities.DefaultOutput(this);
        public string Fingerprint()
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(
                BaseUrl.TrimEnd('/') + "\n" + ModelName + "\n" + ApiKey + "\n" + UseSystemProxy + "\n" + TimeoutSeconds +
                (Protocol == "openai-chat" ? "" : "\n" + Protocol + "\n" + AnthropicVersion))));
        }
        public string BaseUrl { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 90;
        public bool UseSystemProxy { get; set; } = true;
        public bool? StreamingSupported { get; set; }

        public static ModelSettings From(AppSettings app) => FromProfile(app.ActiveProfile, app.ApiKey);

        public static ModelSettings FromProfile(ModelProfile profile, string apiKey)
        {
            var settings = new ModelSettings {
                BaseUrl = profile.BaseUrl, ModelName = profile.ModelName, ApiKey = apiKey,
                TimeoutSeconds = profile.TimeoutSeconds, UseSystemProxy = profile.UseSystemProxy,
                Protocol = profile.Protocol, AnthropicVersion = profile.AnthropicVersion,
                ContextWindowTokens = profile.ContextWindowTokens, MaxOutputTokens = profile.MaxOutputTokens
            };
            settings.StreamingSupported = ModelCapabilityCache.IsFresh(profile, settings, DateTime.UtcNow) ? profile.StreamingSupported : null;
            return settings;
        }

    }
}
