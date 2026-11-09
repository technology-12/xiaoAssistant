using System;
using System.IO;
using System.Text;

namespace WorkAssistant.Core
{
    /// <summary>Same-volume replacement without a delete-before-move gap.</summary>
    public static class DurableFiles
    {
        public static void WriteText(string path, string text)
            => WriteBytes(path, new UTF8Encoding(false).GetBytes(text));
        public static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // Keep the full uniqueness token, but do not append it to an already
            // long target name (legacy Windows/.NET paths are limited to MAX_PATH).
            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".xiao-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
