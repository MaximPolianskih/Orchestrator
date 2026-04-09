namespace Orchestrator.Interfaces
{
    public interface IProcessOrchestrator
    {
        /// <summary>Точка входа во флюент-пайплайн. Возвращает новый экземпляр на каждый вызов.</summary>
        ITransactionalFlow BeginWithTransaction();
    }
}
