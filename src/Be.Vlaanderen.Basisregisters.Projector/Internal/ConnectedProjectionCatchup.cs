namespace Be.Vlaanderen.Basisregisters.Projector.Internal
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Autofac.Features.OwnedInstances;
    using ConnectedProjections;
    using Exceptions;
    using Extensions;
    using Microsoft.Extensions.Logging;
    using ProjectionHandling.Runner;
    using SqlStreamStore;
    using SqlStreamStore.Streams;

    internal class ConnectedProjectionCatchUp<TContext> where TContext : RunnerDbContext<TContext>
    {
        private readonly ConnectedProjectionMessageHandler<TContext> _messageHandler;
        private readonly IConnectedProjectionEventBus _eventBus;
        private readonly ConnectedProjectionName _runnerName;
        private readonly ILogger _logger;

        public int CatchupPageSize { get; set; } = 1000;

        public ConnectedProjectionCatchUp(
            ConnectedProjectionName name,
            ILogger logger,
            ConnectedProjectionMessageHandler<TContext> messageHandler,
            IConnectedProjectionEventBus eventBus)
        {
            _runnerName = name ?? throw new ArgumentNullException(nameof(name));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        // Used with reflection, be careful when refactoring/changing
        public async Task CatchUpAsync(
            IReadonlyStreamStore streamStore,
            Func<Owned<TContext>> contextFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => { CatchUpStopped(CatchUpStopReason.Aborted); });
            _eventBus.Send(new CatchUpStarted(_runnerName));
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                _logger.LogDebug(
                    "Started catch up with paging (CatchupPageSize: {CatchupPageSize})",
                    CatchupPageSize);

                long? position;
                using (var context = contextFactory())
                    position = await context.Value.GetRunnerPositionAsync(_runnerName, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                _logger.LogInformation(
                    "Start {RunnerName} CatchUp at position: {Position}",
                    _runnerName,
                    position);

                var page = await ReadPages(streamStore, position, cancellationToken);

                var continueProcessing = false == cancellationToken.IsCancellationRequested;
                while (continueProcessing)
                {
                    _logger.LogDebug(
                        "Processing page of {PageSize} starting at POS {FromPosition}",
                        page.Messages.Length,
                        page.FromPosition);

                    await _messageHandler.HandleAsync(page.Messages, contextFactory, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                        return;

                    if (page.IsEnd)
                        continueProcessing = false;
                    else
                        page = await page.ReadNext(cancellationToken);
                }

                CatchUpStopped(CatchUpStopReason.Finished);
            }
            catch (TaskCanceledException){ }
            catch (ConnectedProjectionMessageHandlingException exception)
            {
                CatchUpStopped(CatchUpStopReason.Error);
                _logger.LogError(
                    exception.InnerException,
                    "{RunnerName} catching up failed because an exception was thrown when handling the message at {Position}.",
                    exception.RunnerName,
                    exception.RunnerPosition);
            }
            catch (Exception exception)
            {
                CatchUpStopped(CatchUpStopReason.Error);
                _logger.LogError(
                    exception,
                    "{RunnerName} catching up failed because an exception was thrown",
                    _runnerName);
            }
        }

        private void CatchUpStopped(CatchUpStopReason reason)
        {
            _logger.LogInformation(
                "Stopping {RunnerName} CatchUp: {Reason}",
                _runnerName,
                reason);

            _eventBus.Send(new CatchUpStopped(_runnerName));
            if (CatchUpStopReason.Finished == reason)
                _eventBus.Send(new CatchUpFinished(_runnerName));
        }

        private async Task<ReadAllPage> ReadPages(
            IReadonlyStreamStore streamStore,
            long? position,
            CancellationToken cancellationToken)
        {
            return await streamStore.ReadAllForwards(
                position + 1 ?? Position.Start,
                CatchupPageSize,
                prefetchJsonData: true,
                cancellationToken);
        }
    }

    internal abstract class ConnectedProjectCatchUpAbstract : ConnectedProjectionCatchUp<ConnectedProjectCatchUpAbstract.AbstractContext>
    {
        private ConnectedProjectCatchUpAbstract()
            : base(null, null, null, null)
        { }

        public static string CatchUpAsyncName = nameof(CatchUpAsync);
        
        public abstract class AbstractContext : RunnerDbContext<AbstractContext>
        {
            public override string ProjectionStateSchema => throw new NotSupportedException();
        }
    }
}
