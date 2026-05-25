using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Concurrent;

namespace MongoTestTools.Services
{
    public enum ChangeStreamLevel { Collection, Database, Cluster }

    public record CollectionStats(int TotalChanges, double Tps, double MaxTps);

    public class ChangeStreamMonitorService
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _monitoringTask;
        private readonly ConcurrentQueue<DateTime> _changeTimestamps = new();
        private int _totalChanges = 0;
        private DateTime _startTime;
        private readonly ConcurrentDictionary<string, int> _changesPerCollection = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _timestampsPerCollection = new();
        private readonly ConcurrentDictionary<string, double> _maxTpsPerCollection = new();
        private readonly ConcurrentDictionary<string, double> _currentTpsPerCollection = new();

        public bool IsRunning { get; private set; }
        public int TotalChanges => _totalChanges;
        public double ChangesPerMinute { get; private set; }
        public double ChangesPerSecond { get; private set; }
        public string? LastError { get; private set; }
        public ChangeStreamLevel CurrentLevel { get; private set; }
        public string MonitoringScope { get; private set; } = "";
        public IReadOnlyDictionary<string, int> ChangesPerCollection => _changesPerCollection;

        public IReadOnlyDictionary<string, CollectionStats> CollectionStatsMap
        {
            get
            {
                var map = new Dictionary<string, CollectionStats>();
                foreach (var kvp in _changesPerCollection)
                {
                    _currentTpsPerCollection.TryGetValue(kvp.Key, out var tps);
                    _maxTpsPerCollection.TryGetValue(kvp.Key, out var maxTps);
                    map[kvp.Key] = new CollectionStats(kvp.Value, tps, maxTps);
                }
                return map;
            }
        }

        public event Action? OnStateChanged;

        public void Start(string connectionString, string database, string collection, string? resumeToken, ChangeStreamLevel level = ChangeStreamLevel.Collection, string[]? namespaceFilter = null)
        {
            if (IsRunning)
            {
                LastError = "Monitor is already running";
                return;
            }

            try
            {
                LastError = null;
                _cancellationTokenSource = new CancellationTokenSource();
                _totalChanges = 0;
                ChangesPerMinute = 0;
                ChangesPerSecond = 0;
                _changeTimestamps.Clear();
                _changesPerCollection.Clear();
                _timestampsPerCollection.Clear();
                _maxTpsPerCollection.Clear();
                _currentTpsPerCollection.Clear();
                _startTime = DateTime.UtcNow;
                CurrentLevel = level;

                var filterSet = namespaceFilter != null && namespaceFilter.Length > 0
                    ? new HashSet<string>(namespaceFilter, StringComparer.OrdinalIgnoreCase)
                    : null;

                MonitoringScope = level switch
                {
                    ChangeStreamLevel.Cluster => filterSet != null ? $"(filtered: {filterSet.Count} namespaces)" : "(all databases)",
                    ChangeStreamLevel.Database => filterSet != null ? $"{database} (filtered: {filterSet.Count} namespaces)" : database,
                    _ => filterSet != null ? $"{database}.{collection} (filtered: {filterSet.Count} namespaces)" : $"{database}.{collection}"
                };
                IsRunning = true;

                _monitoringTask = Task.Run(async () =>
                {
                    try
                    {
                        await MonitorChangeStreamAsync(connectionString, database, collection, resumeToken, level, filterSet, _cancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        LastError = $"Error: {ex.Message}";
                    }
                    finally
                    {
                        IsRunning = false;
                        NotifyStateChanged();
                    }
                });

                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                LastError = $"Failed to start: {ex.Message}";
                IsRunning = false;
                NotifyStateChanged();
            }
        }

        public async Task StopAsync()
        {
            if (!IsRunning || _cancellationTokenSource == null)
                return;

            try
            {
                _cancellationTokenSource.Cancel();
                if (_monitoringTask != null)
                {
                    await _monitoringTask;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                IsRunning = false;
                NotifyStateChanged();
            }
        }

        private async Task MonitorChangeStreamAsync(string connectionString, string database, string collection, string? resumeToken, ChangeStreamLevel level, HashSet<string>? namespaceFilter, CancellationToken cancellationToken)
        {
            var client = new MongoClient(connectionString);

            var options = new ChangeStreamOptions
            {
                FullDocument = ChangeStreamFullDocumentOption.UpdateLookup
            };

            // If resume token is provided, use it
            if (!string.IsNullOrWhiteSpace(resumeToken))
            {
                try
                {
                    options.ResumeAfter = BsonDocument.Parse(resumeToken);
                }
                catch (Exception ex)
                {
                    LastError = $"Invalid resume token: {ex.Message}";
                    NotifyStateChanged();
                    return;
                }
            }

            var emptyPipeline = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>();
            IChangeStreamCursor<ChangeStreamDocument<BsonDocument>> cursor = level switch
            {
                ChangeStreamLevel.Cluster => await client.WatchAsync(emptyPipeline, options, cancellationToken),
                ChangeStreamLevel.Database => await client.GetDatabase(database).WatchAsync(emptyPipeline, options, cancellationToken),
                _ => await client.GetDatabase(database).GetCollection<BsonDocument>(collection).WatchAsync(options, cancellationToken)
            };

            using (cursor)
            {

                // Start a background task to calculate changes per minute and notify UI
                var calculationTask = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000, cancellationToken); // Update every 5 seconds
                        CalculateChangesPerMinute();
                        NotifyStateChanged();
                    }
                }, cancellationToken);

                await cursor.ForEachAsync(change =>
                {
                    var ns = change.CollectionNamespace?.FullName ?? "unknown";

                    // Skip if namespace filter is active and this namespace isn't in the list
                    if (namespaceFilter != null && !namespaceFilter.Contains(ns))
                        return;

                    Interlocked.Increment(ref _totalChanges);
                    _changeTimestamps.Enqueue(DateTime.UtcNow);

                    // Track per-collection counts
                    _changesPerCollection.AddOrUpdate(ns, 1, (_, count) => count + 1);

                    // Track per-collection timestamps for TPS calculation
                    var collQueue = _timestampsPerCollection.GetOrAdd(ns, _ => new ConcurrentQueue<DateTime>());
                    collQueue.Enqueue(DateTime.UtcNow);

                    // Keep only last 2 minutes of timestamps for accurate calculation
                    while (_changeTimestamps.Count > 0 &&
                           _changeTimestamps.TryPeek(out var oldest) &&
                           (DateTime.UtcNow - oldest).TotalMinutes > 2)
                    {
                        _changeTimestamps.TryDequeue(out _);
                    }
                }, cancellationToken);

                await calculationTask;
            }
        }

        private void CalculateChangesPerMinute()
        {
            var now = DateTime.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);

            // Count changes in the last minute
            var recentChanges = _changeTimestamps.Count(ts => ts >= oneMinuteAgo);
            
            // Calculate based on elapsed time if less than 1 minute has passed
            var elapsedMinutes = (now - _startTime).TotalMinutes;
            var elapsedSeconds = (now - _startTime).TotalSeconds;
            if (elapsedMinutes < 1)
            {
                ChangesPerMinute = elapsedMinutes > 0 ? _totalChanges / elapsedMinutes : 0;
                ChangesPerSecond = elapsedSeconds > 0 ? _totalChanges / elapsedSeconds : 0;
            }
            else
            {
                ChangesPerMinute = recentChanges;
                ChangesPerSecond = recentChanges / 60.0;
            }

            // Calculate per-collection TPS
            foreach (var kvp in _timestampsPerCollection)
            {
                var queue = kvp.Value;

                // Prune old timestamps (> 2 minutes)
                while (queue.TryPeek(out var oldest) && (now - oldest).TotalMinutes > 2)
                    queue.TryDequeue(out _);

                var recentCount = queue.Count(ts => ts >= oneMinuteAgo);
                double tps;
                if (elapsedMinutes < 1)
                {
                    _changesPerCollection.TryGetValue(kvp.Key, out var total);
                    tps = elapsedSeconds > 0 ? total / elapsedSeconds : 0;
                }
                else
                {
                    tps = recentCount / 60.0;
                }

                _currentTpsPerCollection[kvp.Key] = tps;
                _maxTpsPerCollection.AddOrUpdate(kvp.Key, tps, (_, prev) => Math.Max(prev, tps));
            }
        }

        private void NotifyStateChanged() => OnStateChanged?.Invoke();
    }
}
