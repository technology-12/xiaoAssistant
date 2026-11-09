using System;
using System.Linq;

namespace WorkAssistant.Core
{
    /// <summary>One conservative classification for process loss, startup recovery and child failure.</summary>
    public static class TaskInterruption
    {
        private static readonly string[] Producers = { "export_summary", "export_xlsx", "generate_docx", "fill_docx_template", "extract_pdf_text", "draft_skill", "apply_file_plan", "apply_text_edit", "rollback_text_edit" };
        public static bool IsActive(TaskRecord task) => task.State == TaskState.Created || task.State == TaskState.Queued ||
            task.State == TaskState.Running || task.State == TaskState.WaitingForUser;
        public static bool HasUncertainWrites(TaskRecord task) => task.Steps.Any(step =>
            (step.WritesFiles || Producers.Contains(step.Tool)) &&
            (step.State == "Started" || step.State == "Prepared" || step.State == "Committed" ||
             (step.Tool == "apply_file_plan" || ApprovedToolOperation.IsTextMutation(step.Tool)) && step.State == "Verified" || step.State == "Failed" && !string.IsNullOrEmpty(step.CommitManifestPath)));
        public static void Mark(TaskRecord task, bool cancelled, string? failureCode = null, bool childNeedsReview = false)
        {
            bool review = childNeedsReview || HasUncertainWrites(task);
            task.State = review ? TaskState.NeedsReview : cancelled ? TaskState.Cancelled : TaskState.Interrupted;
            task.ErrorCode = review ? ErrorCodes.NeedsReview : cancelled ? ErrorCodes.Cancelled : failureCode ?? ErrorCodes.WorkerUnavailable;
            task.ErrorMessage = review ? "任务执行已停止，存在未确认的文件写入或子任务结果，需根据本地提交记录复核；不会自动重放。" :
                cancelled ? "任务已取消，未自动重放任何操作。" : "任务执行中断，已保留本地记录；不会自动重放。";
            task.SubState = TaskSubState.None; task.FinishedUtc = DateTime.UtcNow;
        }
    }
}
