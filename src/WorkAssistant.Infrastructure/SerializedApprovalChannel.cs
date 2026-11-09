using System;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    /// <summary>Serialize prompts and correlate replies. Late decisions cannot authorize the next operation.</summary>
    public sealed class SerializedApprovalChannel : IApprovalProvider
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _sync = new();
        private readonly Func<ApprovalRequest, CancellationToken, Task> _send;
        private string? _pendingId;
        private TaskCompletionSource<bool>? _pending;
        public SerializedApprovalChannel(Func<ApprovalRequest, CancellationToken, Task> send) { _send = send; }
        public bool TryDecide(string? requestId, bool approved)
        {
            lock (_sync) return requestId != null && requestId == _pendingId && _pending != null && _pending.TrySetResult(approved);
        }
        public async Task<bool> RequestAsync(ApprovalRequest request, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            // Assign a new correlation ID even if a caller reuses the same request object.
            var outgoing = new ApprovalRequest(request.ToolName, request.Title, request.Operations, request.Reason);
            var completion = XpTask.CompletionSource<bool>();
            try
            {
                lock (_sync) { _pendingId = outgoing.RequestId; _pending = completion; }
                using var registration = token.Register(() => completion.TrySetCanceled());
                await _send(outgoing, token).ConfigureAwait(false);
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) { _pendingId = null; _pending = null; }
                _gate.Release();
            }
        }
    }
}
