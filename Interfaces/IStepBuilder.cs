namespace Orchestrator.Interfaces
{
    public interface IStepBuilder
    {
        ITransactionalFlow AndThen();
        IStepBuilder WithRetries(int maxAttempts = 3, TimeSpan? initialDelay = null);
        IStepBuilder OnSuccess(Func<CancellationToken, Task> action);
        /// <summary>
        /// Обратотка ошибки операции, не поддерживается вложенность (нельзя обработать ошибку ошибки)
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        IStepBuilder OnFail(Func<CancellationToken, Task> action);
        /// <summary>Прерывание пайплайна</summary>
        IStepBuilder Break(Func<CancellationToken, Task> handler);
    }
}
