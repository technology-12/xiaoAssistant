using System;

namespace WorkAssistant.Core
{
    // Virtual organization only; the directory belongs to the user, never to the project lifecycle.
    public sealed class ProjectRecord
    {
        public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string WorkspaceRoot { get; set; } = "";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
