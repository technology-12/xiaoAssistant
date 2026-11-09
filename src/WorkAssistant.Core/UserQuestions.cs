using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace WorkAssistant.Core
{
    public sealed class UserQuestion
    {
        public string Question { get; set; } = "";
        public List<string> Options { get; set; } = new();
    }
    public interface IUserInputProvider
    {
        Task<string?> RequestAsync(UserQuestion question, CancellationToken token);
    }
}
