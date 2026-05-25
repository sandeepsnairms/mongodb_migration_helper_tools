using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MongoTestTools.Services
{
    public class ChangeStreamGeneratorService
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly List<Task> _workerTasks = new();
        private int _totalInserted = 0;
        private int _totalUpdated = 0;
        private int _totalDeleted = 0;
        private DateTime _startTime;

        public bool IsRunning { get; private set; }
        public int TotalInserted => _totalInserted;
        public int TotalUpdated => _totalUpdated;
        public int TotalDeleted => _totalDeleted;
        public double OperationsPerSecond { get; private set; }
        public string? LastError { get; private set; }

        public event Action? OnStateChanged;

        public void Start(string connectionString, string database, string collection, string prefix, 
                          int threadCount, int batchSize, int loopCount, bool continuousMode, bool randomDatabase, bool randomCollection,
                          int dbRangeStart, int dbRangeEnd, int collRangeStart, int collRangeEnd,
                          bool enableInserts, bool enableUpdates, bool enableDeletes,
                          bool enableShardKey, string shardKeyField, string shardKeyPrefix, int shardKeyRangeStart, int shardKeyRangeEnd,
                          string[] namespaces)
        {
            if (IsRunning)
            {
                LastError = "Generator is already running";
                return;
            }

            try
            {
                LastError = null;
                _cancellationTokenSource = new CancellationTokenSource();
                _totalInserted = 0;
                _totalUpdated = 0;
                _totalDeleted = 0;
                _startTime = DateTime.UtcNow;
                IsRunning = true;
                _workerTasks.Clear();

                // Start statistics update task
                var statsTask = Task.Run(async () =>
                {
                    while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                        CalculateOperationsPerSecond();
                        NotifyStateChanged();
                    }
                }, _cancellationTokenSource.Token);

                // Start worker threads
                for (int i = 0; i < threadCount; i++)
                {
                    int threadId = i; // Capture loop variable
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await GenerateChangeStreamAsync(connectionString, database, collection, 
                                prefix, threadId, batchSize, loopCount, continuousMode, randomDatabase, randomCollection,
                                dbRangeStart, dbRangeEnd, collRangeStart, collRangeEnd, 
                                enableInserts, enableUpdates, enableDeletes,
                                enableShardKey, shardKeyField, shardKeyPrefix, shardKeyRangeStart, shardKeyRangeEnd,
                                namespaces,
                                _cancellationTokenSource.Token);
                        }
                        catch (Exception ex)
                        {
                            LastError = $"Thread {threadId} error: {ex.Message}";
                            NotifyStateChanged();
                        }
                    }, _cancellationTokenSource.Token);

                    _workerTasks.Add(task);
                }

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
                await Task.WhenAll(_workerTasks);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _workerTasks.Clear();
                IsRunning = false;
                NotifyStateChanged();
            }
        }
        private async Task GenerateChangeStreamAsync(string connectionString, string databaseName, 
            string collectionName, string prefix, int threadId, int batchSize, int loopCount, bool continuousMode, 
            bool randomDatabase, bool randomCollection, int dbRangeStart, int dbRangeEnd, 
            int collRangeStart, int collRangeEnd, bool enableInserts, bool enableUpdates, bool enableDeletes,
            bool enableShardKey, string shardKeyField, string shardKeyPrefix, int shardKeyRangeStart, int shardKeyRangeEnd,
            string[] namespaces,
            CancellationToken cancellationToken)
        {
            dbRangeStart = Math.Clamp(dbRangeStart, 1, 10);
            dbRangeEnd = Math.Clamp(dbRangeEnd, 1, 10);
            if (dbRangeEnd < dbRangeStart)
            {
                (dbRangeStart, dbRangeEnd) = (dbRangeEnd, dbRangeStart);
            }

            var client = new MongoClient(connectionString);
            var database = client.GetDatabase(databaseName);
            var collection = database.GetCollection<BsonDocument>(collectionName);

            int counter = 0;
            var random = new Random(threadId); // Seed with threadId for different sequences per thread

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Handle namespace list: pick a random namespace
                    if (namespaces.Length > 0)
                    {
                        var ns = namespaces[random.Next(namespaces.Length)];
                        var dotIndex = ns.IndexOf('.');
                        if (dotIndex > 0 && dotIndex < ns.Length - 1)
                        {
                            var nsDb = ns.Substring(0, dotIndex);
                            var nsColl = ns.Substring(dotIndex + 1);
                            database = client.GetDatabase(nsDb);
                            collection = database.GetCollection<BsonDocument>(nsColl);
                        }
                    }
                    else
                    {
                        // Handle random database selection
                        if (randomDatabase)
                        {
                            int randNum = random.Next(dbRangeStart, dbRangeEnd + 1);
                            string formatted = randNum.ToString("D2");
                            var randomDbName = $"{databaseName}{formatted}";
                            database = client.GetDatabase(randomDbName);
                        }

                        // Handle random collection selection
                        if (randomCollection)
                        {
                            int randNum = random.Next(collRangeStart, collRangeEnd + 1);
                            string formatted = randNum.ToString("D3");
                            var randomCollName = $"{collectionName}{formatted}";
                            collection = database.GetCollection<BsonDocument>(randomCollName);
                        }
                    }

                    var ids = new List<ObjectId>();
                    string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

                    // Insert documents
                    if (enableInserts)
                    {
                        var docs = new List<BsonDocument>();
                        for (int i = 0; i < batchSize; i++)
                        {
                            string customId = $"{prefix}-T{threadId}-{timestamp}_{i}";
                            ObjectId id = ObjectId.GenerateNewId();

                            var names = new[] { "Alice", "Bob", "Charlie", "Diana", "Ethan", "Fiona", "George", "Hannah", "Ian", "Julia" };
                            int index = random.Next(names.Length);
                            string randomName = names[index];

                            // Mark 50% for updates if updates are enabled
                            if (enableUpdates && i % 2 == 0)
                                ids.Add(id);

                            var doc = new BsonDocument
                            {
                                { "_id", id },
                                { "customId", customId },
                                { "tenantId", $"{prefix}-tenant-{random.Next(1, 101)}" },
                                { "name", $"{collection.CollectionNamespace}-doc-{i}" },
                                { "value", i },
                                { "createdAt", DateTime.UtcNow }
                            };

                            // Add shard key field if enabled
                            if (enableShardKey)
                            {
                                int randomShardKeyNumber = random.Next(shardKeyRangeStart, shardKeyRangeEnd + 1);
                                int digits = shardKeyRangeEnd.ToString().Length;
                                string shardKeyValue = $"{shardKeyPrefix}{randomShardKeyNumber.ToString($"D{digits}")}";
                                doc.Add(shardKeyField, shardKeyValue);
                            }

                            docs.Add(doc);
                        }

                        await collection.InsertManyAsync(docs, cancellationToken: cancellationToken);
                        Interlocked.Add(ref _totalInserted, docs.Count);
                    }

                    // Update documents
                    if (enableUpdates)
                    {
                        foreach (var id in ids)
                        {
                            var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
                            var update = Builders<BsonDocument>.Update
                                .Set("updatedAt", DateTime.UtcNow)
                                .Inc("value", 1);
                            await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
                            Interlocked.Increment(ref _totalUpdated);
                        }
                    }

                    // Delete first N documents
                    if (enableDeletes)
                    {
                        await DeleteFirstNDocumentsAsync(collection, batchSize, cancellationToken);
                    }

                    counter++;

                    if (!continuousMode && counter >= loopCount)
                    {
                        break;
                    }

                    await Task.Delay(1000, cancellationToken); // 1 second delay between batches
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = $"Thread {threadId} iteration error: {ex.Message}";
                    NotifyStateChanged();
                    await Task.Delay(5000, cancellationToken); // Wait before retry
                }
            }
        }

        private async Task DeleteFirstNDocumentsAsync(IMongoCollection<BsonDocument> collection, int count, CancellationToken cancellationToken)
        {
            var filter = Builders<BsonDocument>.Filter.Empty;
            var sort = Builders<BsonDocument>.Sort.Ascending("_id");

            var firstDocs = await collection.Find(filter)
                                           .Sort(sort)
                                           .Limit(count)
                                           .ToListAsync(cancellationToken);

            if (firstDocs.Count == 0)
                return;

            var idsToDelete = firstDocs.Select(doc => doc["_id"]).ToList();
            var deleteFilter = Builders<BsonDocument>.Filter.In("_id", idsToDelete);
            var result = await collection.DeleteManyAsync(deleteFilter, cancellationToken);

            Interlocked.Add(ref _totalDeleted, (int)result.DeletedCount);
        }

        private void CalculateOperationsPerSecond()
        {
            var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            if (elapsed > 0)
            {
                var totalOps = _totalInserted + _totalUpdated + _totalDeleted;
                OperationsPerSecond = totalOps / elapsed;
            }
        }

        private void NotifyStateChanged() => OnStateChanged?.Invoke();
    }
}
