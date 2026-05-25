using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MongoTestTools.Services
{
    public class CollectionComparerService
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isRunning;
        private int _processedCollections;
        private int _totalMismatches;
        private int _activeCollections;

        public bool IsRunning => _isRunning;
        public bool IsRechecking { get; private set; }
        public int TotalCollections { get; private set; }
        public int ProcessedCollections => _processedCollections;
        public int TotalMismatches => _totalMismatches;
        public int ActiveCollections => _activeCollections;
        public TimeSpan MaxLag { get; private set; }
        public string CurrentCollection { get; private set; } = string.Empty;
        public ConcurrentBag<ComparisonResult> Results { get; private set; } = new();

        public event Action? OnStateChanged;

        public async Task StartComparison(
            string sourceConnectionString,
            string targetConnectionString,
            int sampleSize,
            List<string> namespaces,
            int collectionParallelism = 4,
            List<string>? timestampFields = null,
            CancellationToken cancellationToken = default)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            TotalCollections = namespaces.Count;
            _processedCollections = 0;
            _totalMismatches = 0;
            _activeCollections = 0;
            MaxLag = TimeSpan.Zero;
            Results = new ConcurrentBag<ComparisonResult>();
            
            NotifyStateChanged();

            try
            {
                var sourceClient = new MongoClient(sourceConnectionString);
                var targetClient = new MongoClient(targetConnectionString);

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = collectionParallelism,
                    CancellationToken = _cancellationTokenSource.Token
                };

                await Parallel.ForEachAsync(namespaces, parallelOptions, async (ns, ct) =>
                {
                    var parts = ns.Split('.');
                    if (parts.Length < 2)
                    {
                        Results.Add(new ComparisonResult
                        {
                            Namespace = ns,
                            Status = "Invalid namespace format",
                            IsSuccess = false
                        });
                        Interlocked.Increment(ref _processedCollections);
                        NotifyStateChanged();
                        return;
                    }

                    var databaseName = parts[0];
                    var collectionName = string.Join(".", parts.Skip(1));

                    Interlocked.Increment(ref _activeCollections);
                    NotifyStateChanged();

                    // Scale batch size inversely with parallelism to cap total memory
                    var batchSize = Math.Max(10, 400 / collectionParallelism);

                    var result = await CompareCollection(
                        sourceClient,
                        targetClient,
                        databaseName,
                        collectionName,
                        sampleSize,
                        batchSize,
                        timestampFields,
                        ct);

                    Results.Add(result);
                    Interlocked.Increment(ref _processedCollections);
                    Interlocked.Decrement(ref _activeCollections);

                    if (result.MismatchCount > 0)
                        Interlocked.Add(ref _totalMismatches, result.MismatchCount);

                    // Update global max lag
                    if (result.MaxLag > MaxLag)
                        MaxLag = result.MaxLag;

                    NotifyStateChanged();
                });
            }
            catch (OperationCanceledException)
            {
                // Comparison was cancelled
            }
            finally
            {
                _isRunning = false;
                _activeCollections = 0;
                CurrentCollection = string.Empty;
                NotifyStateChanged();
            }
        }

        private async Task<ComparisonResult> CompareCollection(
            MongoClient sourceClient,
            MongoClient targetClient,
            string databaseName,
            string collectionName,
            int sampleSize,
            int batchSize,
            List<string>? timestampFields,
            CancellationToken cancellationToken)
        {
            var result = new ComparisonResult
            {
                Namespace = $"{databaseName}.{collectionName}",
                ComparedAt = DateTime.UtcNow
            };

            try
            {
                var sourceDb = sourceClient.GetDatabase(databaseName);
                var targetDb = targetClient.GetDatabase(databaseName);

                var sourceCollection = sourceDb.GetCollection<BsonDocument>(collectionName);
                var targetCollection = targetDb.GetCollection<BsonDocument>(collectionName);

                // Get document counts in parallel
                var sourceCountTask = sourceCollection.CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken);
                var targetCountTask = targetCollection.CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken);
                await Task.WhenAll(sourceCountTask, targetCountTask);
                result.SourceCount = sourceCountTask.Result;
                result.TargetCount = targetCountTask.Result;

                // Select documents from source — either sorted descending or random sample
                // Stream the cursor in small batches to avoid loading all into memory
                IAsyncCursor<BsonDocument> sampleCursor;
                if (timestampFields != null && timestampFields.Count > 0)
                {
                    var sortDef = new BsonDocument();
                    foreach (var field in timestampFields)
                        sortDef.Add(field, -1);

                    var findOptions = new FindOptions<BsonDocument>
                    {
                        Sort = sortDef,
                        Limit = sampleSize,
                        BatchSize = batchSize
                    };
                    sampleCursor = await sourceCollection.FindAsync(new BsonDocument(), findOptions, cancellationToken);
                }
                else
                {
                    sampleCursor = await sourceCollection.Aggregate(new AggregateOptions { BatchSize = batchSize })
                        .Sample(sampleSize)
                        .ToCursorAsync(cancellationToken);
                }

                int totalSampled = 0;
                int mismatched = 0;
                var errors = new ConcurrentBag<string>();
                var mismatchedDocs = new ConcurrentBag<MismatchedDoc>();
                TimeSpan maxLag = TimeSpan.Zero;

                while (await sampleCursor.MoveNextAsync(cancellationToken))
                {
                    var currentBatch = sampleCursor.Current.ToList();
                    totalSampled += currentBatch.Count;

                    // Process this cursor batch in sub-batches
                    for (int offset = 0; offset < currentBatch.Count; offset += batchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var chunk = currentBatch.Skip(offset).Take(batchSize).ToList();

                        // Hash source docs, keep _id + hash + doc reference for timestamp extraction
                        var sourceInfoById = new Dictionary<BsonValue, (string Hash, BsonDocument Doc)>(chunk.Count);
                        var chunkIds = new BsonArray();
                        foreach (var sourceDoc in chunk)
                        {
                            var id = sourceDoc.GetValue("_id");
                            sourceInfoById[id] = (ComputeHash(sourceDoc), sourceDoc);
                            chunkIds.Add(id);
                        }

                        // Free the chunk list reference
                        chunk = null;

                        // Fetch matching target docs via $in and stream-hash them
                        var filter = new BsonDocument("_id", new BsonDocument("$in", chunkIds));
                        var targetHashById = new Dictionary<BsonValue, string>(sourceInfoById.Count);

                        var findOptions = new FindOptions<BsonDocument> { BatchSize = batchSize };
                        using var targetCursor = await targetCollection.FindAsync(filter, findOptions, cancellationToken);
                        while (await targetCursor.MoveNextAsync(cancellationToken))
                        {
                            foreach (var targetDoc in targetCursor.Current)
                            {
                                targetHashById[targetDoc.GetValue("_id")] = ComputeHash(targetDoc);
                            }
                        }

                        // Compare
                        foreach (var kvp in sourceInfoById)
                        {
                            var (lagSuffix, lagValue) = GetLagInfo(kvp.Value.Doc, timestampFields);

                            if (!targetHashById.TryGetValue(kvp.Key, out var targetHash))
                            {
                                errors.Add($"Document with _id {kvp.Key} missing in target{lagSuffix}");
                                mismatchedDocs.Add(new MismatchedDoc { Id = kvp.Key, Lag = lagValue, Reason = "missing", SourceHash = kvp.Value.Hash });
                                Interlocked.Increment(ref mismatched);
                                if (lagValue > maxLag) maxLag = lagValue;
                            }
                            else if (kvp.Value.Hash != targetHash)
                            {
                                errors.Add($"Hash mismatch for _id {kvp.Key}{lagSuffix}");
                                mismatchedDocs.Add(new MismatchedDoc { Id = kvp.Key, Lag = lagValue, Reason = "hash mismatch", SourceHash = kvp.Value.Hash });
                                Interlocked.Increment(ref mismatched);
                                if (lagValue > maxLag) maxLag = lagValue;
                            }
                        }
                    }
                }

                result.SampleSize = totalSampled;

                result.MismatchCount = mismatched;
                result.MaxLag = maxLag;
                result.MismatchedDocs = mismatchedDocs.ToList();
                result.Errors = errors.ToList();
                result.IsSuccess = true;
                if (mismatched == 0)
                {
                    result.Status = "Match";
                }
                else
                {
                    var maxLagStr = maxLag > TimeSpan.Zero ? $", max lag={FormatLag(maxLag)}" : "";
                    result.Status = $"{mismatched} mismatch(es) found{maxLagStr}";
                }
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Status = $"Error: {ex.Message}";
                result.Errors = new List<string> { ex.ToString() };
            }

            return result;
        }

        private static string ComputeHash(BsonDocument document)
        {
            var bsonBytes = document.ToBson();
            var hash = SHA256.HashData(bsonBytes);
            return Convert.ToBase64String(hash);
        }

        private static (string Suffix, TimeSpan Lag) GetLagInfo(BsonDocument sourceDoc, List<string>? timestampFields)
        {
            if (timestampFields == null || timestampFields.Count == 0)
                return (string.Empty, TimeSpan.Zero);

            foreach (var field in timestampFields)
            {
                if (sourceDoc.Contains(field))
                {
                    var value = sourceDoc[field];
                    DateTime? ts = null;

                    if (value.IsValidDateTime)
                        ts = value.ToUniversalTime();
                    else if (value.BsonType == BsonType.String && DateTime.TryParse(value.AsString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                        ts = parsed;

                    if (ts.HasValue)
                    {
                        var lag = DateTime.UtcNow - ts.Value;
                        if (lag < TimeSpan.Zero) lag = TimeSpan.Zero;
                        return ($" | {field}={ts.Value:yyyy-MM-dd HH:mm:ss} UTC, lag={FormatLag(lag)}", lag);
                    }
                }
            }

            return (string.Empty, TimeSpan.Zero);
        }

        private static string FormatLag(TimeSpan lag)
        {
            if (lag.TotalDays >= 1)
                return $"{lag.Days}d {lag.Hours}h {lag.Minutes}m";
            if (lag.TotalHours >= 1)
                return $"{lag.Hours}h {lag.Minutes}m {lag.Seconds}s";
            return $"{lag.Minutes}m {lag.Seconds}s";
        }

        public async Task RecheckMismatches(
            string sourceConnectionString,
            string targetConnectionString,
            int lagThresholdSeconds,
            List<string>? timestampFields = null,
            CancellationToken cancellationToken = default)
        {
            if (_isRunning)
                return;

            // Collect all mismatched docs above the lag threshold, grouped by namespace
            var previousResults = Results.ToList();
            var threshold = TimeSpan.FromSeconds(lagThresholdSeconds);

            var recheckGroups = previousResults
                .Where(r => r.MismatchedDocs.Any(d => d.Lag >= threshold))
                .Select(r => new
                {
                    r.Namespace,
                    r.SampleSize,
                    r.ComparedAt,
                    Docs = r.MismatchedDocs.Where(d => d.Lag >= threshold).ToList()
                })
                .ToList();

            if (!recheckGroups.Any())
                return;

            _isRunning = true;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var recheckNamespaces = new HashSet<string>(recheckGroups.Select(g => g.Namespace));

            // Keep results for collections not being rechecked
            var keptResults = previousResults.Where(r => !recheckNamespaces.Contains(r.Namespace)).ToList();

            TotalCollections = recheckGroups.Count;
            _processedCollections = 0;
            _totalMismatches = 0;
            _activeCollections = 0;
            MaxLag = TimeSpan.Zero;
            IsRechecking = true;
            Results = new ConcurrentBag<ComparisonResult>(keptResults);

            // Include kept results in totals
            foreach (var kept in keptResults)
            {
                _totalMismatches += kept.MismatchCount;
                if (kept.MaxLag > MaxLag) MaxLag = kept.MaxLag;
            }

            NotifyStateChanged();

            try
            {
                var sourceClient = new MongoClient(sourceConnectionString);
                var targetClient = new MongoClient(targetConnectionString);

                var batchSize = Math.Max(10, 400 / Math.Max(1, recheckGroups.Count));

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(recheckGroups.Count, 10),
                    CancellationToken = _cancellationTokenSource.Token
                };

                await Parallel.ForEachAsync(recheckGroups, parallelOptions, async (group, ct) =>
                {
                    Interlocked.Increment(ref _activeCollections);
                    NotifyStateChanged();

                    var parts = group.Namespace.Split('.');
                    var databaseName = parts[0];
                    var collectionName = string.Join(".", parts.Skip(1));

                    var result = await RecheckIds(
                        sourceClient, targetClient,
                        databaseName, collectionName,
                        group.Docs, group.SampleSize, group.ComparedAt, batchSize, timestampFields, ct);

                    Results.Add(result);
                    Interlocked.Increment(ref _processedCollections);
                    Interlocked.Decrement(ref _activeCollections);

                    if (result.MismatchCount > 0)
                        Interlocked.Add(ref _totalMismatches, result.MismatchCount);

                    if (result.MaxLag > MaxLag)
                        MaxLag = result.MaxLag;

                    NotifyStateChanged();
                });
            }
            catch (OperationCanceledException) { }
            finally
            {
                _isRunning = false;
                IsRechecking = false;
                _activeCollections = 0;
                CurrentCollection = string.Empty;
                NotifyStateChanged();
            }
        }

        private async Task<ComparisonResult> RecheckIds(
            MongoClient sourceClient, MongoClient targetClient,
            string databaseName, string collectionName,
            List<MismatchedDoc> originalMismatches, int originalSampleSize, DateTime originalComparedAt, int batchSize,
            List<string>? timestampFields,
            CancellationToken cancellationToken)
        {
            var result = new ComparisonResult
            {
                Namespace = $"{databaseName}.{collectionName}",
                ComparedAt = originalComparedAt,
                RecheckedAt = DateTime.UtcNow,
                SampleSize = originalSampleSize
            };

            try
            {
                var sourceCollection = sourceClient.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
                var targetCollection = targetClient.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);

                var sourceCountTask = sourceCollection.CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken);
                var targetCountTask = targetCollection.CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken);
                await Task.WhenAll(sourceCountTask, targetCountTask);
                result.SourceCount = sourceCountTask.Result;
                result.TargetCount = targetCountTask.Result;

                // Build lookup of original source hashes
                var originalHashById = originalMismatches.ToDictionary(d => d.Id, d => d.SourceHash);
                var ids = originalMismatches.Select(d => d.Id).ToList();

                int mismatched = 0;
                int sourceChanged = 0;
                var errors = new ConcurrentBag<string>();
                var mismatchedDocs = new ConcurrentBag<MismatchedDoc>();
                TimeSpan maxLag = TimeSpan.Zero;

                // Process in batches
                for (int offset = 0; offset < ids.Count; offset += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batchIds = new BsonArray(ids.Skip(offset).Take(batchSize));
                    var filter = new BsonDocument("_id", new BsonDocument("$in", batchIds));

                    // Fetch source docs for this batch
                    var sourceById = new Dictionary<BsonValue, (string Hash, BsonDocument Doc)>();
                    var findOptions = new FindOptions<BsonDocument> { BatchSize = batchSize };
                    using (var srcCursor = await sourceCollection.FindAsync(filter, findOptions, cancellationToken))
                    {
                        while (await srcCursor.MoveNextAsync(cancellationToken))
                        {
                            foreach (var doc in srcCursor.Current)
                                sourceById[doc.GetValue("_id")] = (ComputeHash(doc), doc);
                        }
                    }

                    // Fetch target docs
                    var targetHashById = new Dictionary<BsonValue, string>();
                    using (var tgtCursor = await targetCollection.FindAsync(filter, findOptions, cancellationToken))
                    {
                        while (await tgtCursor.MoveNextAsync(cancellationToken))
                        {
                            foreach (var doc in tgtCursor.Current)
                                targetHashById[doc.GetValue("_id")] = ComputeHash(doc);
                        }
                    }

                    // Compare
                    foreach (var id in batchIds)
                    {
                        if (!sourceById.TryGetValue(id, out var srcInfo))
                        {
                            errors.Add($"Document with _id {id} missing in source (deleted?)");
                            continue;
                        }

                        // Skip if source doc changed since original comparison
                        if (originalHashById.TryGetValue(id, out var origHash) && !string.IsNullOrEmpty(origHash) && srcInfo.Hash != origHash)
                        {
                            sourceChanged++;
                            continue;
                        }

                        var (lagSuffix, lagValue) = GetLagInfo(srcInfo.Doc, timestampFields);

                        if (!targetHashById.TryGetValue(id, out var tgtHash))
                        {
                            errors.Add($"Document with _id {id} missing in target{lagSuffix}");
                            mismatchedDocs.Add(new MismatchedDoc { Id = id, Lag = lagValue, Reason = "missing", SourceHash = srcInfo.Hash });
                            Interlocked.Increment(ref mismatched);
                            if (lagValue > maxLag) maxLag = lagValue;
                        }
                        else if (srcInfo.Hash != tgtHash)
                        {
                            errors.Add($"Hash mismatch for _id {id}{lagSuffix}");
                            mismatchedDocs.Add(new MismatchedDoc { Id = id, Lag = lagValue, Reason = "hash mismatch", SourceHash = srcInfo.Hash });
                            Interlocked.Increment(ref mismatched);
                            if (lagValue > maxLag) maxLag = lagValue;
                        }
                    }
                }

                result.MismatchCount = mismatched;
                result.MaxLag = maxLag;
                result.MismatchedDocs = mismatchedDocs.ToList();
                result.Errors = errors.ToList();
                result.IsSuccess = true;
                if (mismatched == 0)
                {
                    var suffix = sourceChanged > 0 ? $" ({sourceChanged} skipped, source changed)" : "";
                    result.Status = $"Match{suffix}";
                }
                else
                {
                    var maxLagStr = maxLag > TimeSpan.Zero ? $", max lag={FormatLag(maxLag)}" : "";
                    var suffix = sourceChanged > 0 ? $", {sourceChanged} skipped (source changed)" : "";
                    result.Status = $"{mismatched} mismatch(es) found{maxLagStr}{suffix}";
                }
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Status = $"Error: {ex.Message}";
                result.Errors = new List<string> { ex.ToString() };
            }

            return result;
        }

        public async Task Stop()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                await Task.Delay(100); // Give time for cancellation to complete
            }
        }

        private void NotifyStateChanged()
        {
            OnStateChanged?.Invoke();
        }
    }

    public class ComparisonResult
    {
        public string Namespace { get; set; } = string.Empty;
        public long SourceCount { get; set; }
        public long TargetCount { get; set; }
        public int SampleSize { get; set; }
        public int MismatchCount { get; set; }
        public TimeSpan MaxLag { get; set; }
        public bool IsSuccess { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime ComparedAt { get; set; }
        public DateTime? RecheckedAt { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<MismatchedDoc> MismatchedDocs { get; set; } = new();
    }

    public class MismatchedDoc
    {
        public BsonValue Id { get; set; } = BsonNull.Value;
        public TimeSpan Lag { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
    }
}
