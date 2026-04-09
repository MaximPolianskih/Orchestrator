namespace Orchestrator.Models
{
    internal class PipelineStep
    {
        /// <summary>
        /// Лог конфигурации, заполняемое последовательно при конфигурации (цепочка вызовов)
        /// </summary>
        public string ConfigurationLog { get; set; } = string.Empty;
        /// <summary>
        /// Лог вызовов, заполняется при выполнении шагов
        /// </summary>
        public string ExecutionLog { get; set; } = string.Empty;
        public Func<CancellationToken, Task> Action { get; set; } = null!;
        public RetryConfig? RetryConfig { get; set; }
        public RetryConfig? FailRetryConfig { get; set; }
        public Func<CancellationToken, Task>? OnSuccessHandler { get; set; }
        public Func<CancellationToken, Task>? FailHandler { get; set; }
        public Func<Exception, CancellationToken, Task>? FailErrorHandler { get; set; }
        public Func<CancellationToken, Task>? BreakHandler { get; set; }
    }
}
