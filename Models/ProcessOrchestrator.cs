using Microsoft.Extensions.Logging;
using Orchestrator.Interfaces;

namespace Orchestrator.Models
{
    public class ProcessOrchestrator(
        IProcessOrchestratorTransaction transactionScope,
        ILogger<ProcessOrchestrator> logger) : IProcessOrchestrator
    {
        public ITransactionalFlow BeginWithTransaction() => new PipelineManager(transactionScope, logger);
    }
}
