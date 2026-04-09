namespace Orchestrator.Interfaces
{
    public interface ITransactionalFlow
    {
        IStepBuilder Do(Func<CancellationToken, Task> action);
        Task RunWithTransactionAsync(CancellationToken ct = default);
    }
}
