using System;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;

namespace WorkAssistant.Desktop
{
    internal sealed class UserInputProvider : IUserInputProvider
    {
        public Task<string?> RequestAsync(UserQuestion question, CancellationToken token)
        {
            var vm = App.MainVm ?? throw new InvalidOperationException("Conversation is not available.");
            return vm.RequestInlineAnswerAsync(question.Question, question.Options, token);
        }
    }
}
