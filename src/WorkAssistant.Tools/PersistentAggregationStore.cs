using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class PersistentAggregationStore : IAggregationStore
    {
        private readonly string _root;
        public PersistentAggregationStore(string root) { _root = root; Directory.CreateDirectory(root); }
        public string Add(AggregationResult result)
        {
            var id = "agg_" + Guid.NewGuid().ToString("N");
            var stored = new AggregationResult(id, result.GroupByColumn, result.SumColumn, result.SourcePaths,
                result.RecordCount, result.Groups, result.Total, result.FilterDescription, result.MappingDescription, result.FilteredOutCount);
            DurableFiles.WriteText(Path.Combine(_root, id + ".json"), JsonConvert.SerializeObject(stored));
            return id;
        }
        public AggregationResult? Find(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^agg_[a-f0-9]{32}$")) return null;
            string path = Path.Combine(_root, id + ".json");
            return File.Exists(path) ? JsonConvert.DeserializeObject<AggregationResult>(File.ReadAllText(path)) : null;
        }
    }
}
