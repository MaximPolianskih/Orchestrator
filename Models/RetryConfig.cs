namespace Orchestrator.Models
{
    public record RetryConfig(int MaxAttempts, TimeSpan InitialDelay);
}
