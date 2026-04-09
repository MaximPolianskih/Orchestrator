using Microsoft.Extensions.Logging;
using Orchestrator.Interfaces;

namespace Orchestrator.Models
{
    public sealed class PipelineManager(
        IProcessOrchestratorTransaction tx,
        ILogger logger) : ITransactionalFlow, IStepBuilder
    {
        private readonly List<PipelineStep> _steps = new();
        private PipelineStep? _currentStep;
        private string _pipelineConfigurationLog = "BeginWithTransaction";
        private RetryTarget _lastRetryTarget = RetryTarget.Action;

        private enum RetryTarget
        {
            Action,
            FailHandler
        }

        public IStepBuilder Do(Func<CancellationToken, Task> action)
        {
            _pipelineConfigurationLog += ".Execute";
            var step = new PipelineStep { ConfigurationLog = _pipelineConfigurationLog, Action = action };
            _currentStep = step;
            _steps.Add(step);
            _lastRetryTarget = RetryTarget.Action;
            return this;
        }

        public ITransactionalFlow AndThen()
        {
            _pipelineConfigurationLog += $".{nameof(AndThen)}";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            return this;
        }

        public IStepBuilder WithRetries(int maxAttempts = 3, TimeSpan? initialDelay = null)
        {
            _pipelineConfigurationLog += $".{nameof(WithRetries)}";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            var retryConfig = new RetryConfig(maxAttempts, initialDelay ?? TimeSpan.FromSeconds(1));
            if (_lastRetryTarget == RetryTarget.FailHandler)
            {
                _currentStep!.FailRetryConfig = retryConfig;
            }
            else
            {
                _currentStep!.RetryConfig = retryConfig;
            }
            return this;
        }

        public IStepBuilder OnSuccess(Func<CancellationToken, Task> action)
        {
            _pipelineConfigurationLog += $".{nameof(OnSuccess)}";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            _currentStep!.OnSuccessHandler = action;
            return this;
        }

        public IStepBuilder OnFail(Func<CancellationToken, Task> action)
        {
            _pipelineConfigurationLog += $".{nameof(OnFail)}.CompensateWith";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            _currentStep!.FailHandler = action;
            _lastRetryTarget = RetryTarget.FailHandler;
            return this;
        }

        public IStepBuilder OnFailError(Func<Exception, CancellationToken, Task> action)
        {
            _pipelineConfigurationLog += $".{nameof(OnFailError)}";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            _currentStep!.FailErrorHandler = action;
            return this;
        }

        public IStepBuilder Break(Func<CancellationToken, Task> handler)
        {
            _pipelineConfigurationLog += $".{nameof(Break)}";
            if (_currentStep != null)
            {
                _currentStep.ConfigurationLog = _pipelineConfigurationLog;
            }
            _currentStep!.BreakHandler = handler;
            return this;
        }

        public async Task RunWithTransactionAsync(CancellationToken cancellationToken = default)
        {
            logger.LogInformation($"Начало выполнения {nameof(RunWithTransactionAsync)}.");
            await tx.BeginAsync(cancellationToken);

            try
            {
                foreach (var step in _steps)
                {
                    _currentStep = step;
                    logger.LogInformation($"Выполняем цепочку: {step.ConfigurationLog}");
                    var (stepSuccess, _) = await ExecuteActionAsync(step, step.Action, step.RetryConfig, cancellationToken);

                    if (stepSuccess)
                    {
                        step.ExecutionLog += "Выполянем .OnSuccess->";
                        if (step.OnSuccessHandler != null)
                        {
                            await step.OnSuccessHandler(cancellationToken);
                        }
                    }
                    else
                    {
                        if (step.FailHandler != null)
                        {
                            step.ExecutionLog += "Выполянем .OnFail->";
                            var (compensationSuccess, compensationError) =
                                await ExecuteActionAsync(step, step.FailHandler, step.FailRetryConfig, cancellationToken);

                            if (!compensationSuccess)
                            {
                                // Если FailHandler упал с ошибкой, вызываем FailErrorHandler
                                if (step.FailErrorHandler != null)
                                {
                                    step.ExecutionLog += "Выполянем .OnFailError->";
                                    await step.FailErrorHandler(
                                        compensationError ?? new InvalidOperationException("Ошибка компенсации без исключения."),
                                        cancellationToken);
                                }
                                else
                                {
                                    // Если нет обработчика ошибки FailHandler, пробрасываем ошибку
                                    throw compensationError ?? new InvalidOperationException("Ошибка компенсации без исключения.");
                                }
                            }
                        }
                        // Если нет Handle/Compensate → пробрасываем ошибку
                        else
                        {
                            throw new InvalidOperationException($"Пайплайн {step.ConfigurationLog} не корректный и не может быть обработан");
                        }

                        if (step.BreakHandler != null)
                        {
                            step.ExecutionLog += "Выполянем .Break->";
                            await step.BreakHandler(cancellationToken);
                            //Заверщаем выполнение пайплайна
                            return;
                        }
                    }
                }

                await tx.CommitAsync(CancellationToken.None);
                logger.LogInformation($"Оркестратор успешно завершил работу. Транзакция закомичена.");
            }
            //Если операция отменена откатываем транзакцию
            catch (OperationCanceledException cancelationException)
            {
                logger.LogError(cancelationException, "В процессе выполнения произошол вызов ThrowIfCancellationRequested. Откат транзакции.");
                await tx.RollbackAsync(CancellationToken.None);
                logger.LogError(cancelationException, "В процессе выполнения произошол вызов ThrowIfCancellationRequested. Успешный откат.");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Оркестратор упал с ошибкой. Откат транзакции.");
                await tx.RollbackAsync(CancellationToken.None);
                logger.LogError(ex, "Оркестратор упал с ошибкой. Успешный откат.");
                throw;
            }
        }

        private async Task<(bool Success, Exception? LastException)> ExecuteActionAsync(
            PipelineStep step,
            Func<CancellationToken, Task> action,
            RetryConfig? retryConfig,
            CancellationToken cancellationToken)
        {
            var maxAttempts = retryConfig?.MaxAttempts ?? 1;
            var initialDelay = retryConfig?.InitialDelay ?? TimeSpan.Zero;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await action(cancellationToken);
                    // Формируем итоговое имя с результатом выполнения
                    step.ExecutionLog += maxAttempts > 1
                        ? $"Успех на {attempt} ретрае->"
                        : "Выполнено->";
                    return (true, null);
                }
                //Если операция отменена через токен не заходим на ретраи, если по таймауту операции то ретраим
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt == maxAttempts)
                    {
                        break;
                    }

                    logger.LogWarning(ex, "{StepName} ошибка выполнени {Attempt}/{Max}. Повтор через {Delay:F1}s...", step.ConfigurationLog, attempt, maxAttempts, initialDelay);
                    await Task.Delay(initialDelay, cancellationToken);
                }
            }

            step.ExecutionLog += $"Неудача после {maxAttempts} ретраев->";
            logger.LogError(lastEx, "{StepName} исчерпал все ретраи.", step.ConfigurationLog);
            return (false, lastEx);
        }
    }
}
