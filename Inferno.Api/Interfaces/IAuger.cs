using System;
using System.Threading;
using System.Threading.Tasks;

namespace Inferno.Api.Interfaces
{
    public interface IAuger
    {
        Task Run(TimeSpan RunTime, CancellationToken token);
        bool IsOn { get; }
    }
}