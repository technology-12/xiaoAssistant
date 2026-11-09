using System.Collections.Generic;

namespace WorkAssistant.Core
{
    public sealed class TaskPlanItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "pending";
    }
    public sealed class TaskPlan
    {
        public int Revision { get; set; }
        public List<TaskPlanItem> Items { get; set; } = new();
    }
    public interface ITaskPlanStore
    {
        TaskPlan Read();
        void Replace(TaskPlan plan, int expectedRevision);
    }
    public interface ITaskHistoryReader
    {
        string Read(string taskId, string section);
    }
}
