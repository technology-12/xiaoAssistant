using System;
using System.IO;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class PersistentFilePlanStore : IPersistentPlanStore
    {
        private readonly string _directory;
        public PersistentFilePlanStore(string directory) { _directory = directory; Directory.CreateDirectory(directory); }
        public string Add(FilePlanRecord plan)
        {
            var id = "plan_" + Guid.NewGuid().ToString("N");
            Save(new FilePlanRecord(id, plan.Operations) { TextEdit = plan.TextEdit }); return id;
        }
        public FilePlanRecord? Find(string planId)
        {
            var path = Resolve(planId); if (!File.Exists(path)) return null;
            using var stream = VerifiedFiles.OpenRead(path, _directory);
            if (stream.Length > 2 * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "计划记录超过 2 MiB 限制。");
            using var reader = new JsonTextReader(new StreamReader(stream)) { MaxDepth = 16 };
            return Newtonsoft.Json.Linq.JObject.Load(reader, new Newtonsoft.Json.Linq.JsonLoadSettings {
                DuplicatePropertyNameHandling = Newtonsoft.Json.Linq.DuplicatePropertyNameHandling.Error }).ToObject<FilePlanRecord>();
        }
        public void Save(FilePlanRecord plan) => DurableFiles.WriteText(Resolve(plan.PlanId), JsonConvert.SerializeObject(plan));
        private string Resolve(string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^plan_[a-f0-9]{32}$")) throw new ToolException(ErrorCodes.InvalidArguments, "计划标识非法。 ");
            return Path.Combine(_directory, id + ".json");
        }
    }
}
