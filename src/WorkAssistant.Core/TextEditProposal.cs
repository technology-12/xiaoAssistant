using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WorkAssistant.Core
{
    /// <summary>Immutable-by-convention proposal, never authorization to write the source file.</summary>
    public sealed class TextEditProposal
    {
        public string SourcePath { get; set; } = "";
        public string BeforeSha256 { get; set; } = "";
        public string AfterSha256 { get; set; } = "";
        public string BeforeBase64 { get; set; } = "";
        public string AfterBase64 { get; set; } = "";
        public string Diff { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public int MatchCount { get; set; }
    }

    public static class TextEditEngine
    {
        public const int MaxBytes = 256 * 1024;
        public const int MaxReplacementChars = 16000;
        public static TextEditProposal Propose(string path, byte[] original, string oldText, string newText, bool replaceAll, string reason)
        {
            if (oldText.Length == 0 || oldText == newText || oldText.Length > MaxReplacementChars || newText.Length > MaxReplacementChars || reason.Length > 500)
                throw new ToolException(ErrorCodes.InvalidArguments, "替换文本不能为空、相同或超过限制；本轮不支持新建/删除文件。");
            string text = Decode(original, out var encoding, out var preamble);
            int count = 0, offset = 0, found;
            while ((found = text.IndexOf(oldText, offset, StringComparison.Ordinal)) >= 0)
            {
                if (++count > 1000) throw new ToolException(ErrorCodes.LimitExceeded, "匹配数超过 1000。");
                offset = found + oldText.Length;
            }
            if (count == 0 || !replaceAll && (count > 1 || text.IndexOf(oldText, text.IndexOf(oldText, StringComparison.Ordinal) + 1, StringComparison.Ordinal) >= 0))
                throw new ToolException(ErrorCodes.PlanInvalid, "原文没有唯一匹配；请提供更多上下文，或明确 replace_all。");
            string updated = text.Replace(oldText, newText);
            if (text.Replace("\r\n", "\n").Replace('\r', '\n') == updated.Replace("\r\n", "\n").Replace('\r', '\n'))
                throw new ToolException(ErrorCodes.InvalidArguments, "本轮不支持仅改变换行格式的提案。");
            var body = encoding.GetBytes(updated);
            if (body.Length + preamble.Length > MaxBytes) throw new ToolException(ErrorCodes.LimitExceeded, "编辑后的文件超过 256 KiB。");
            var bytes = preamble.Concat(body).ToArray();
            Decode(bytes, out _, out _); // Reject control characters and invalid surrogate sequences before saving.
            var proposal = new TextEditProposal { SourcePath = path, BeforeSha256 = Hash(original), AfterSha256 = Hash(bytes),
                BeforeBase64 = Convert.ToBase64String(original), AfterBase64 = Convert.ToBase64String(bytes), MatchCount = count,
                Reason = reason, Diff = UnifiedDiff(path, text, updated) };
            proposal.Fingerprint = Fingerprint(proposal); return proposal;
        }
        public static void Validate(TextEditProposal proposal)
        {
            try
            {
                var before = Convert.FromBase64String(proposal.BeforeBase64); var after = Convert.FromBase64String(proposal.AfterBase64);
                if (proposal.MatchCount < 1 || proposal.MatchCount > 1000 || proposal.Reason.Length > 500 || proposal.SourcePath.Length > 200 ||
                    Hash(before) != proposal.BeforeSha256 || Hash(after) != proposal.AfterSha256 || proposal.BeforeSha256 == proposal.AfterSha256 ||
                    Fingerprint(proposal) != proposal.Fingerprint || UnifiedDiff(proposal.SourcePath, Decode(before, out _, out _), Decode(after, out _, out _)) != proposal.Diff)
                    throw new ToolException(ErrorCodes.PlanInvalid, "文本提案摘要或内容不一致，禁止使用。");
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            { throw new ToolException(ErrorCodes.PlanInvalid, "文本提案编码或结构无效。"); }
        }
        public static string Fingerprint(TextEditProposal p) => Hash(Encoding.UTF8.GetBytes(string.Join("\0", p.SourcePath, p.BeforeSha256, p.AfterSha256, p.Reason, p.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Diff)));
        public static string Hash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static string Decode(byte[] bytes, out Encoding encoding, out byte[] preamble)
        {
            if (bytes.Length > MaxBytes) throw new ToolException(ErrorCodes.LimitExceeded, "文本文件超过 256 KiB。");
            int skip = 0; encoding = new UTF8Encoding(false, true);
            if (bytes.Length >= 4 && (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 254 && bytes[3] == 255 || bytes[0] == 255 && bytes[1] == 254 && bytes[2] == 0 && bytes[3] == 0))
                throw new ToolException(ErrorCodes.UnsupportedFormat, "暂不支持 UTF-32。");
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) skip = 3;
            else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            preamble = bytes.Take(skip).ToArray();
            string text;
            try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) { throw new ToolException(ErrorCodes.UnsupportedFormat, "仅支持有效 UTF-8 或带 BOM 的 UTF-16 文本。"); }
            if (text.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) throw new ToolException(ErrorCodes.UnsupportedFormat, "拒绝二进制或包含控制字符的文本。");
            return text;
        }
        private static string UnifiedDiff(string path, string before, string after)
        {
            string[] Lines(string text) { var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); return text.Length == 0 ? XpArray.Empty<string>() : text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal) ? lines.Take(lines.Length - 1).ToArray() : lines; }
            var a = Lines(before); var b = Lines(after); int prefix = 0, suffix = 0;
            // Final-newline differences are represented as a changed last line.
            bool endA = before.EndsWith("\n", StringComparison.Ordinal) || before.EndsWith("\r", StringComparison.Ordinal);
            bool endB = after.EndsWith("\n", StringComparison.Ordinal) || after.EndsWith("\r", StringComparison.Ordinal);
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix] && !(endA != endB && (prefix == a.Length - 1 || prefix == b.Length - 1))) prefix++;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - suffix - 1] == b[b.Length - suffix - 1] && !(suffix == 0 && endA != endB)) suffix++;
            int start = Math.Max(0, prefix - 3), tail = Math.Min(3, suffix), aEnd = a.Length - suffix + tail, bEnd = b.Length - suffix + tail;
            var output = new StringBuilder("--- a/" + path + "\n+++ b/" + path + "\n");
            output.Append("@@ -").Append(a.Length == 0 ? 0 : start + 1).Append(',').Append(aEnd - start).Append(" +").Append(b.Length == 0 ? 0 : start + 1).Append(',').Append(bEnd - start).Append(" @@\n");
            void Line(char marker, string value, bool lastWithoutNewline) { output.Append(marker).Append(value).Append('\n'); if (lastWithoutNewline) output.Append("\\ No newline at end of file\n"); }
            for (int i = start; i < prefix; i++) Line(' ', a[i], i == a.Length - 1 && !endA);
            for (int i = prefix; i < a.Length - suffix; i++) Line('-', a[i], i == a.Length - 1 && !endA);
            for (int i = prefix; i < b.Length - suffix; i++) Line('+', b[i], i == b.Length - 1 && !endB);
            for (int i = 0; i < tail; i++) { int ai = a.Length - suffix + i, bi = b.Length - suffix + i; Line(' ', a[ai], ai == a.Length - 1 && !endA && bi == b.Length - 1 && !endB); }
            return output.ToString();
        }
    }
}
