﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AzureStorage;
using Common.Log;
using JetBrains.Annotations;

namespace Lykke.Logging
{
    [UsedImplicitly]
    internal sealed class FixLogEntityRepository : IFixLogEntityRepository
    {
        private readonly INoSQLTableStorage<FixLogEntity> _tableStorage;
        private readonly ILog _log;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private const int MaxNoElementsInCache = 1000000;
        private readonly BlockingCollection<FixLogEntity> _logItems = new BlockingCollection<FixLogEntity>(MaxNoElementsInCache);
        private readonly ManualResetEventSlim _wholeLogSaved = new ManualResetEventSlim();
        private readonly Task _saveTask;

        public FixLogEntityRepository(INoSQLTableStorage<FixLogEntity> tableStorage, ILog log)
        {
            _tableStorage = tableStorage;
            _log = log.CreateComponentScope(nameof(FixLogEntityRepository));
            _cancellationTokenSource = new CancellationTokenSource();
            _saveTask = Task.Run(SaveLogItems, _cancellationTokenSource.Token);
        }

        public void WriteLogItem(DateTime time, string senderCompId, string targetCompId, string message, FixMessageDirection direction)
        {
            var item = new FixLogEntity(time, senderCompId, targetCompId, message, direction);
            _logItems.Add(item);
            const int thresholdValue = MaxNoElementsInCache - 10000;
            if (_logItems.Count > thresholdValue && (_logItems.Count - thresholdValue) % 100 == 0)
            {
                _log.WriteWarning(nameof(WriteLogItem), null, "The log cache is almost full. Azure table performance is to low");
            }
        }

        private async Task SaveLogItems()
        {
            try
            {
                _wholeLogSaved.Reset();
                var entities = _logItems.GetConsumingEnumerable(_cancellationTokenSource.Token);

                foreach (var batch in entities)
                {
                    await _tableStorage.InsertAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await _tableStorage.InsertAsync(_logItems.ToArray()); //Save the rest if any
                }
                catch (Exception)
                {
                    // It is too late to do something
                }
            }
            catch (Exception e)
            {
                _log.WriteWarning(nameof(SaveLogItems), null, "Unable to save a log of Fix messages", e);
            }
            finally
            {
                _wholeLogSaved.Set();
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource.Dispose();
            _logItems.Dispose();
            _wholeLogSaved.Dispose();
            _saveTask.Dispose();
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            _wholeLogSaved.Wait(TimeSpan.FromSeconds(6000));
        }
    }
}
