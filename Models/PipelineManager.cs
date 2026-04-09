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

        public IStepBuilder Do(Func<CancellationToken, Task> action)
        {
            var step = new PipelineStep { ConfigurationLog = ".Do", Action = action };
            _currentStep = step;
            _steps.Add(step);
            return this;
        }

        public ITransactionalFlow AndThen()
        {
            _currentStep!.ConfigurationLog += $".{nameof(AndThen)}";
            return this;
        }

        public IStepBuilder WithRetries(int maxAttempts = 3, TimeSpan? initialDelay = null)
        {
            _currentStep!.ConfigurationLog += $".{nameof(WithRetries)}";
            _currentStep!.RetryConfig = new RetryConfig(maxAttempts, initialDelay ?? TimeSpan.FromSeconds(1));
            return this;
        }

        public IStepBuilder OnSuccess(Func<CancellationToken, Task> action)
        {
            _currentStep!.ConfigurationLog += $".{nameof(OnSuccess)}";
            _currentStep!.OnSuccessHandler = action;
            return this;
        }

        public IStepBuilder OnFail(Func<CancellationToken, Task> action)
        {
            _currentStep!.ConfigurationLog += $".{nameof(OnFail)}";
            _currentStep!.FailHandler = action;
            return this;
        }

        public IStepBuilder OnFailError(Func<Exception, CancellationToken, Task> action)
        {
            _currentStep!.ConfigurationLog += $".{nameof(OnFailError)}";
            _currentStep!.FailErrorHandler = action;
            return this;
        }

        public IStepBuilder Break(Func<CancellationToken, Task> handler)
        {
            _currentStep!.ConfigurationLog += $".{nameof(Break)}";
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
                    logger.LogInformation($"Выполняем цепочку: {_currentStep!.ConfigurationLog}");
                    var stepSuccess = await ExecuteActionAsync(step, _currentStep!.Action, _currentStep!.RetryConfig, cancellationToken);

                    if (_currentStep!.BreakHandler != null)
                    {
                        _currentStep!.ExecutionLog += "Выполянем .Break->";
                        await _currentStep!.BreakHandler(cancellationToken);
                        //Заверщаем выполнение пайплайна
                        return;
                    }

                    if (stepSuccess)
                    {
                        _currentStep!.ExecutionLog += "Выполянем .OnSuccess->";
                        if (_currentStep!.OnSuccessHandler != null)
                        {
                            await _currentStep!.OnSuccessHandler(cancellationToken);
                        }
                    }
                    else
                    {
                        if (_currentStep!.FailHandler != null)
                        {
                            _currentStep!.ExecutionLog += "Выполянем .OnFail->";
                            try
                            {
                                await _currentStep!.FailHandler(cancellationToken);
                            }
                            catch (Exception failEx)
                            {
                                // Если FailHandler упал с ошибкой, вызываем FailErrorHandler
                                if (_currentStep!.FailErrorHandler != null)
                                {
                                    _currentStep!.ExecutionLog += "Выполянем .OnFailError->";
                                    await _currentStep!.FailErrorHandler(failEx, cancellationToken);
                                }
                                else
                                {
                                    // Если нет обработчика ошибки FailHandler, пробрасываем ошибку
                                    throw;
                                }
                            }
                        }
                        // Если нет Handle/Compensate → пробрасываем ошибку
                        else
                        {
                            throw new InvalidOperationException($"Пайплайн {_currentStep!.ConfigurationLog} не корректный и не может быть обработан");
                        }
                    }
                }

                await tx.CommitAsync(CancellationToken.None);
                logger.LogInformation($"Оркестратор успешно завершил работу. Транзакция закомичена.");
            }
            //Если операция отменена откатываем транзакцию (что будет если отмена произойдет на компенсации?)
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

        private async Task<bool> ExecuteActionAsync(
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
                    _currentStep!.ExecutionLog += maxAttempts > 1
                        ? $"Успех на {attempt} ретрае->"
                        : "Выполнено->";
                    return true;
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

                    logger.LogWarning(ex, "{StepName} ошибка выполнени {Attempt}/{Max}. Повтор через {Delay:F1}s...", _currentStep!.ConfigurationLog, attempt, maxAttempts, initialDelay);
                    await Task.Delay(initialDelay, cancellationToken);
                }
            }

            _currentStep!.ExecutionLog += $"Неудача после {maxAttempts} ретраев->";
            logger.LogError(lastEx, "{StepName} исчерпал все ретраи.", _currentStep!.ConfigurationLog);
            return false;
        }
    }
}
