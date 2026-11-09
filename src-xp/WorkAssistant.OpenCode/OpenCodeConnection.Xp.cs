// XP 变体（替换 src/WorkAssistant.OpenCode/OpenCodeConnection.cs）：
// 基线 network.remote_kernel_allowed=false；XP 线强制本地内核，不读取开关。
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.OpenCode
{
    public sealed class OpenCodeConnection
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "http://127.0.0.1:4388";
        public string ProtectedToken { get; set; } = "";
        [JsonIgnore] public string Token { get; set; } = "";
        public void Validate()
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                throw new ToolException(ErrorCodes.InvalidArguments, "远程内核必须使用 HTTPS；本机回环地址允许 HTTP。");
            if (Token.Length < 32) throw new ToolException(ErrorCodes.InvalidArguments, "请在设置中填写至少 32 字符的远程内核访问令牌。");
        }
        public string Fingerprint()
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(BaseUrl.TrimEnd('/') + "\n" + Token))).Replace("-", "");
        }
    }
    public sealed class OpenCodeConnectionStore
    {
        public static bool IsLocalOnly
        {
            get { return true; } // XP 基线：远程内核不在产品边界内，强制本地内核。
        }
        private readonly string _file;
        public OpenCodeConnectionStore(string? file = null)
        {
            var local = Path.Combine(WorkAssistant.Infrastructure.ApplicationDataPaths.Root, "opencode.connection.json");
            _file = file ?? local;
        }
        public OpenCodeConnection Load()
        {
            if (IsLocalOnly) return new OpenCodeConnection { Enabled = false };
            string path = File.Exists(_file) ? _file : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opencode.connection.json");
            if (!File.Exists(path)) return new OpenCodeConnection();
            var settings = JsonConvert.DeserializeObject<OpenCodeConnection>(File.ReadAllText(path, Encoding.UTF8)) ?? new OpenCodeConnection();
            if (IsLocalOnly) settings.Enabled = false;
            try { settings.Token = settings.ProtectedToken.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedToken), null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { settings.Token = ""; }
            catch (FormatException) { settings.Token = ""; }
            return settings;
        }
        public void Save(OpenCodeConnection settings)
        {
            if (settings.Enabled) settings.Validate();
            settings.ProtectedToken = settings.Token.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.Token), null, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            DurableFiles.WriteText(_file, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
    }
}
