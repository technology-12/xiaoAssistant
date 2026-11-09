using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace WorkAssistant.Tests.Console
{
    internal static class TestEvidence
    {
        private static readonly List<object> Results = new List<object>();
        internal static void Record(string name, bool passed, long milliseconds)
            => Results.Add(new { name, status = passed ? "passed" : "failed", milliseconds });

        internal static void Save()
        {
            var path = Environment.GetEnvironmentVariable("WORKASSISTANT_TEST_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            var binaries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var paths = Directory.GetFiles(root).Concat(new[] { "x86", "x64" }.Where(a => Directory.Exists(Path.Combine(root, a))).SelectMany(a => Directory.GetFiles(Path.Combine(root, a))));
            foreach (var file in paths.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                using var sha = SHA256.Create(); using var stream = File.OpenRead(file);
                binaries[file.Substring(root.Length).Replace('\\', '/')] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            var report = new { schema_version = 1, build_id = Environment.GetEnvironmentVariable("WORKASSISTANT_BUILD_ID"),
                total = Test.Passed + Test.Failed, passed = Test.Passed, failed = Test.Failed, skipped = 0,
                cases = Results, binaries, runtime = Environment.Version.ToString(), actual_xp_acceptance = false };
            var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonConvert.SerializeObject(report, Formatting.Indented));
        }
    }
}
