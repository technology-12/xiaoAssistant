using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WorkAssistant.Infrastructure
{
    public sealed class UpdateManifest
    {
        public string Purpose { get; set; } = "update";
        public string ExecutableSha256 { get; set; } = "";
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public int MinimumSchema { get; set; } = 1;
        public int MaximumSchema { get; set; } = 1;
        public string InstallerUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Bytes { get; set; }
        public DateTime PublishedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
    public sealed class PreparedUpdate
    {
        public UpdateManifest Manifest { get; set; } = null!;
        public string InstallerPath { get; set; } = "";
    }
    public static class TrustedUpdates
    {
        // Embedded by the release build. Unsigned developer builds deliberately cannot install updates.
        public static string PublisherCertificateSha256 => typeof(TrustedUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PublisherCertificateSha256")?.Value ?? "";
        private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static UpdateManifest VerifyManifest(byte[] signedContent, string channel, bool allowOlder = false)
        {
            if (PublisherCertificateSha256.Length != 64) throw new InvalidOperationException("此构建尚未配置正式发布证书，更新安装已禁用。");
            if (signedContent.Length > 1024 * 1024) throw new IOException("更新清单过大。");
            var cms = new SignedCms(); cms.Decode(signedContent); cms.CheckSignature(false);
            if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate == null ||
                !string.Equals(Hash(cms.SignerInfos[0].Certificate!.RawData), PublisherCertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("更新清单发布者不匹配。");
            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(Encoding.UTF8.GetString(cms.ContentInfo.Content)) ?? throw new IOException("更新清单无效。");
            if (!System.Version.TryParse(manifest.Version, out var version) || manifest.Channel != channel ||
                (channel != "stable" && channel != "preview") || manifest.MinimumSchema > 1 || manifest.MaximumSchema < 1)
                throw new IOException("更新版本、渠道或状态库版本不兼容。");
            if (!allowOlder && version <= typeof(TrustedUpdates).Assembly.GetName().Version) throw new IOException("当前已是此渠道的最新版本。");
            if (!allowOlder && (manifest.PublishedUtc.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5) || manifest.ExpiresUtc.ToUniversalTime() <= DateTime.UtcNow))
                throw new IOException("更新清单未生效或已过期，请检查系统时间。");
            if (allowOlder)
            {
                if (manifest.Purpose != "release" || !System.Text.RegularExpressions.Regex.IsMatch(manifest.ExecutableSha256, "^[a-fA-F0-9]{64}$")) throw new IOException("旧版本身份清单无效。");
                return manifest;
            }
            if (manifest.Purpose != "update") throw new IOException("清单类型不是更新包。");
            if (manifest.Bytes < 1 || manifest.Bytes > 300L * 1024 * 1024 || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256, "^[a-fA-F0-9]{64}$"))
                throw new IOException("更新大小或摘要无效。");
            RequireHttps(manifest.InstallerUrl);
            return manifest;
        }
        private static Uri RequireHttps(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new IOException("更新地址必须是有效 HTTPS 地址。");
            return uri;
        }
        public static async Task<PreparedUpdate> DownloadAsync(string manifestUrl, string channel, string root, CancellationToken token)
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            byte[] signed;
            using (var response = await client.GetAsync(RequireHttps(manifestUrl), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var stream = new LimitedReadStream(await response.Content.ReadAsStreamAsync(), 1024 * 1024);
                using var memory = new MemoryStream(); await stream.CopyToAsync(memory, 32768, token).ConfigureAwait(false); signed = memory.ToArray();
            }
            var manifest = VerifyManifest(signed, channel);
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "setup.exe");
            try
            {
                using (var response = await client.GetAsync(RequireHttps(manifest.InstallerUrl), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using var stream = new LimitedReadStream(await response.Content.ReadAsStreamAsync(), manifest.Bytes);
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await stream.CopyToAsync(output, 32768, token).ConfigureAwait(false); output.Flush(true);
                }
                VerifyInstaller(path, manifest);
                WorkAssistant.Core.DurableFiles.WriteText(Path.Combine(directory, "manifest.p7m.base64"), Convert.ToBase64String(signed));
                return new PreparedUpdate { Manifest = manifest, InstallerPath = path };
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }
        public static PreparedUpdate Import(string signedManifest, string installer, string channel)
        {
            var manifest = VerifyManifest(File.ReadAllBytes(signedManifest), channel);
            VerifyInstaller(installer, manifest);
            return new PreparedUpdate { Manifest = manifest, InstallerPath = installer };
        }
        public static void VerifyInstaller(string path, UpdateManifest manifest)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                if (stream.Length != manifest.Bytes || !string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("安装包大小或摘要不一致。");
            VerifyExecutable(path);
        }
        public static void VerifyExecutable(string path)
        {
            if (PublisherCertificateSha256.Length != 64) throw new InvalidOperationException("发布证书尚未配置。");
            var file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = path };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            try
            {
                Marshal.StructureToPtr(file, memory, false);
                var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Revocation = 1, Choice = 1, File = memory, ProviderFlags = 0x80 };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new CryptographicException("程序签名或证书链验证失败。");
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (!string.Equals(Hash(certificate.RawData), PublisherCertificateSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("程序发布者不匹配。");
            }
            finally { Marshal.DestroyStructure<TrustFile>(memory); Marshal.FreeHGlobal(memory); }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustData
        { public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File; public uint StateAction; public IntPtr State, Url; public uint ProviderFlags, Context; }
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    }
}
