using Microsoft.Extensions.Logging;
using Moq;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Tests
{
    public class ProcessOrchestratorTests
    {
        private readonly Mock<IProcessOrchestratorTransaction> _mockTx;
        private readonly Mock<ILogger<ProcessOrchestrator>> _mockTxLogger;
        private readonly Mock<ILogger<ProcessOrchestrator>> _mockLogger;
        private readonly IProcessOrchestrator _orchestrator;

        public ProcessOrchestratorTests()
        {
            _mockTx = new Mock<IProcessOrchestratorTransaction>();
            _mockTxLogger = new Mock<ILogger<ProcessOrchestrator>>();
            _mockLogger = new Mock<ILogger<ProcessOrchestrator>>();

            _orchestrator = new ProcessOrchestrator(
                _mockTx.Object,
                _mockTxLogger.Object);
        }

        #region Helper: Factory для создания делегатов с контролируемым поведением
        private static Func<CancellationToken, Task> MockAction(
            int failCount = 0,
            bool throwOnFail = true,
            string? failureMessage = null)
        {
            int attempt = 0;
            return async ct =>
            {
                attempt++;
                if (attempt <= failCount)
                {
                    if (throwOnFail)
                        throw new InvalidOperationException(failureMessage ?? $"Simulated failure #{attempt}");
                    await Task.CompletedTask;
                    return;
                }
                await Task.CompletedTask;
            };
        }

        private static Func<CancellationToken, Task> MockActionWithResult(
            bool shouldSucceed,
            string? failureMessage = null)
        {
            return async ct =>
            {
                if (!shouldSucceed)
                    throw new InvalidOperationException(failureMessage ?? "Simulated failure");
                await Task.CompletedTask;
            };
        }
        #endregion

        #region Успешный сценарий: все шаги проходят с первой попытки
        [Fact]
        public async Task RunAsync_AllStepsSucceed_CompletesTransaction()
        {
            // Arrange
            var ct = CancellationToken.None;

            // Act
            await _orchestrator.BeginWithTransaction()
                .Do(MockAction(failureMessage: "Step 1")) // Step 1: REST Request
                .WithRetries(3, TimeSpan.FromMilliseconds(10))
                .OnFail(MockAction(failureMessage: "Fail Step 1"))

                .AndThen()
                    .Do(MockAction(failureMessage: "BL")) // Step 2: Business Logic
                    .OnFail(MockAction(failureMessage: "Compensate BL")).WithRetries(3, TimeSpan.FromMilliseconds(10))
                    .Break(MockAction(failureMessage: "Break"))

                .AndThen()
                    .Do(MockAction(failureMessage: "Kafka commit")) // Step 3: Kafka Commit
                    .WithRetries(3, TimeSpan.FromMilliseconds(10))
                    .OnSuccess(MockAction(failureMessage: "Commit BL transaction"))
                    .OnFail(MockAction(failureMessage: "Fail kafka commit"))
                .AndThen()
                .RunWithTransactionAsync(ct);

            // Assert
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Once);
            _mockTx.Verify(tx => tx.RollbackAsync(CancellationToken.None), Times.Never);
        }
        #endregion

        #region Шаг 1 упал после всех ретраев → вызван Handle → откат транзакции
        [Fact]
        public async Task RunAsync_Step1FailsAfterRetries_HandleCalled_AbortsTransaction()
        {
            // Arrange: Step 1 всегда падает
            var step1Action = MockAction(failCount: 10, failureMessage: "REST Request 1 failed");
            var abortHandlerCalled = false;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(step1Action)
                    .WithRetries(3, TimeSpan.FromMilliseconds(10))
                    .OnFail(ct =>
                    {
                        abortHandlerCalled = true;
                        return Task.FromException(new OperationCanceledException("Abort: Step 1 failed"));
                    })
                    .AndThen()
                    .RunWithTransactionAsync(CancellationToken.None);
            });

            // Assert
            Assert.Contains("Abort: Step 1 failed", ex.Message);
            Assert.True(abortHandlerCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Never);
            _mockTx.Verify(tx => tx.RollbackAsync(CancellationToken.None), Times.Once);
        }
        #endregion

        #region Шаг 1 успешен со 2-й попытки ретрая
        [Fact]
        public async Task RunAsync_Step1SucceedsOnRetry_ContinuesPipeline()
        {
            // Arrange: Step 1 падает 1 раз, затем успешен
            var step1Action = MockAction(failCount: 1);

            // Act
            await _orchestrator.BeginWithTransaction()
                .Do(step1Action).WithRetries(3, TimeSpan.FromMilliseconds(10))
                    .OnFail(MockAction())
                .AndThen().Do(MockAction()) // Step 2
                    .OnFail(MockAction())
                    .Break(MockAction())
                .AndThen().Do(MockAction()) // Step 3
                    .OnSuccess(MockAction())
                    .OnFail(MockAction())
                .AndThen()
                .RunWithTransactionAsync(CancellationToken.None);

            // Assert
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Once);
        }
        #endregion

        #region Шаг 2 упал → компенсация успешна → вызван Break → откат транзакции
        [Fact]
        public async Task RunAsync_Step2Fails_CompensationSucceeds_BreakCalled_Aborts()
        {
            // Arrange
            var step2Action = MockActionWithResult(false, "Business logic failed");
            var compensationAction = MockActionWithResult(true); // Компенсация успешна
            var breakHandlerCalled = false;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(MockAction()) // Step 1: success
                        .OnFail(MockAction())
                    .AndThen().Do(step2Action) // Step 2: fail
                        .OnFail(compensationAction)
                        .WithRetries(2, TimeSpan.FromMilliseconds(10))
                        .Break(ct =>
                        {
                            breakHandlerCalled = true;
                            return Task.FromException(new OperationCanceledException("Abort: Compensation succeeded"));
                        })
                    .AndThen()
                    .RunWithTransactionAsync(CancellationToken.None);
            });

            // Assert
            Assert.Contains("Abort: Compensation succeeded", ex.Message);
            Assert.True(breakHandlerCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Never);
        }
        #endregion

        #region Шаг 2 упал → компенсация упала после ретраев → откат транзакции
        [Fact]
        public async Task RunAsync_Step2Fails_CompensationFailsAfterRetries_NotifyEmail_Aborts()
        {
            // Arrange
            var step2Action = MockActionWithResult(false);
            var compensationAction = MockAction(failCount: 10, failureMessage: "Compensation failed");
            var notifyCalled = false;
            var breakCalled = false;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(MockAction()) // Step 1: success
                        .OnFail(MockAction())
                    .AndThen()
                        .Do(step2Action).WithRetries(3, TimeSpan.FromMilliseconds(10)) // Step 2: fail
                            .OnFail(compensationAction).WithRetries(2, TimeSpan.FromMilliseconds(10))
                            .OnFailError(async (_, _) => { notifyCalled = true; await Task.CompletedTask; })

                        .Break(ct =>
                        {
                            breakCalled = true;
                            return Task.FromException(new OperationCanceledException("Abort: Compensation failed"));
                        })
                    .AndThen()
                    .RunWithTransactionAsync(CancellationToken.None);
            });

            // Assert
            Assert.Contains("Abort: Compensation failed", ex.Message);
            Assert.True(notifyCalled);
            Assert.True(breakCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Never);
            _mockTx.Verify(tx => tx.RollbackAsync(CancellationToken.None), Times.Once);
        }
        #endregion

        #region Шаг 2 упал → компенсация успешна со 2-й попытки → Break → откат транзакции
        [Fact]
        public async Task RunAsync_Step2Fails_CompensationSucceedsOnRetry_BreakCalled()
        {
            // Arrange
            var step2Action = MockActionWithResult(false);
            var compensationAction = MockAction(failCount: 1); // Успех со 2-й попытки
            var breakCalled = false;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(MockAction())
                    .OnFail(MockAction())
                    .AndThen().Do(step2Action)
                    .OnFail(compensationAction).WithRetries(3, TimeSpan.FromMilliseconds(10))
                    .OnFailError(async (_, _) => { await Task.CompletedTask; })
                    .Break(ct =>
                    {
                        breakCalled = true;
                        return Task.FromException(new OperationCanceledException("Abort: Compensation succeeded"));
                    })
                    .AndThen()
                    .RunWithTransactionAsync(CancellationToken.None);
            });

            Assert.True(breakCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Never);
        }
        #endregion

        #region Шаг 3 (Kafka Commit) упал после всех ретраев → Handle → уведомление + откат транзакции
        [Fact]
        public async Task RunAsync_Step3FailsAfterRetries_HandleNotifiesAndAborts()
        {
            // Arrange
            var kafkaAction = MockAction(failCount: 10, failureMessage: "Kafka commit failed");
            var handleCalled = false;
            var notifyCalled = false;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(MockAction()) // Step 1
                    .OnFail(MockAction())
                    .AndThen().Do(MockAction()) // Step 2: success
                    .OnFail(MockAction())
                    .Break(MockAction())
                    .AndThen().Do(kafkaAction) // Step 3: fail
                    .WithRetries(2, TimeSpan.FromMilliseconds(10))
                    .OnSuccess(MockAction())
                    .OnFail(ct =>
                    {
                        handleCalled = true;
                        notifyCalled = true;

                        throw new OperationCanceledException("Abort: Kafka failed");
                    })
                    .AndThen()
                    .RunWithTransactionAsync(CancellationToken.None);
            });

            // Assert
            Assert.Contains("Abort: Kafka failed", ex.Message);
            Assert.True(handleCalled);
            Assert.True(notifyCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Never);
        }
        #endregion

        #region Шаг 3 успешен со 2-й попытки → вызван OnSuccess → коммит
        [Fact]
        public async Task RunAsync_Step3SucceedsOnRetry_OnSuccessCalled_CommitsTransaction()
        {
            // Arrange
            var kafkaAction = MockAction(failCount: 1); // Успех со 2-й попытки
            var onSuccessCalled = false;

            // Act
            await _orchestrator.BeginWithTransaction()
                .Do(MockAction())
                .OnFail(MockAction())
                .AndThen().Do(MockAction())
                .OnFail(MockAction())
                .Break(MockAction())
                .AndThen().Do(kafkaAction) // Step 3
                .WithRetries(3, TimeSpan.FromMilliseconds(10))
                .OnSuccess(ct =>
                {
                    onSuccessCalled = true;
                    return Task.CompletedTask;
                })
                .OnFail(MockAction())
                .AndThen()
                .RunWithTransactionAsync(CancellationToken.None);

            // Assert
            Assert.True(onSuccessCalled);
            _mockTx.Verify(tx => tx.CommitAsync(CancellationToken.None), Times.Once);
        }
        #endregion

        #region Проверка иерархической обработки ошибок (Обработчик переопредлеляется)
        [Fact]
        public async Task RunAsync_WithoutState_CompensationIsOverwritten()
        {
            // Arrange
            var compensationCalled = false;
            var compensationErrorHandled = false;

            // Act: Пытаемся настроить цепочку БЕЗ отслеживания состояния
            // (Предполагаем, что просто присваиваем step.Handler = action)
            await _orchestrator.BeginWithTransaction()
                .Do(ct => throw new Exception("Step Failed"))
                // Вызов 1: Пытаемся задать компенсацию
                .OnFail(ct => { compensationCalled = true; return Task.CompletedTask; })
                // Вызов 2: Пытаемся задать обработчик ошибки компенсации ТАК НЕ РАБОТАЕТ, обработчик будет переопределен
                .OnFail(ct => { compensationErrorHandled = true; return Task.CompletedTask; })
                .AndThen()
                .RunWithTransactionAsync(CancellationToken.None);

            Assert.False(compensationCalled, "Компенсация1 должна была выполниться");
            Assert.True(compensationErrorHandled, "Компенсация компенсации1 должна была выполниться");
        }
        #endregion

        #region Проверка, что CancellationToken отменяет выполнение
        [Fact]
        public async Task RunAsync_CancellationTokenCancelled_ThrowsOperationCanceled()
        {
            // Arrange
            var cts = new CancellationTokenSource();


            // Act & Assert
            var actTask = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await _orchestrator.BeginWithTransaction()
                    .Do(ct => Task.Delay(TimeSpan.FromSeconds(30), ct)) // Долгий запрос
                    .AndThen()
                    .RunWithTransactionAsync(cts.Token);
            });

            await Task.Delay(TimeSpan.FromSeconds(1));
            cts.Cancel();

            await actTask;
        }
        #endregion
    }
}
