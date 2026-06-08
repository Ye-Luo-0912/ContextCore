using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Embedding;

namespace ContextCore.Tests;

/// <summary>覆盖 P3 embedding 与向量检索公共契约。</summary>
[TestClass]
public sealed class ContextCoreEmbeddingAbstractionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void EmbeddingRequest_ShouldSerializeAndDeserialize()
    {
        var request = new EmbeddingRequest
        {
            OperationId = "embedding-operation",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ModelName = "mock-embedding",
            InputKind = EmbeddingInputKind.ContextItem,
            Normalize = true,
            Inputs =
            [
                new EmbeddingInput
                {
                    Id = "input-1",
                    Text = "The first text to embed.",
                    SourceRef = "context:item-1",
                    Tags = ["alpha"],
                    Metadata = new Dictionary<string, string>
                    {
                        ["type"] = "note"
                    }
                }
            ],
            Metadata = new Dictionary<string, string>
            {
                ["reason"] = "contract-test"
            }
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var actual = JsonSerializer.Deserialize<EmbeddingRequest>(json, JsonOptions);

        Assert.IsNotNull(actual);
        Assert.AreEqual("embedding-operation", actual!.OperationId);
        Assert.AreEqual("workspace-test", actual.WorkspaceId);
        Assert.AreEqual("collection-test", actual.CollectionId);
        Assert.AreEqual("mock-embedding", actual.ModelName);
        Assert.AreEqual(EmbeddingInputKind.ContextItem, actual.InputKind);
        Assert.IsTrue(actual.Normalize);
        Assert.AreEqual(1, actual.Inputs.Count);
        Assert.AreEqual("input-1", actual.Inputs[0].Id);
        Assert.AreEqual("context:item-1", actual.Inputs[0].SourceRef);
        Assert.AreEqual("alpha", actual.Inputs[0].Tags.Single());
        Assert.AreEqual("contract-test", actual.Metadata["reason"]);
    }

    [TestMethod]
    public async Task EmbeddingProviderContract_ShouldReturnVectorPerInput()
    {
        var provider = new DeterministicEmbeddingProvider();

        var result = await provider.EmbedAsync(new EmbeddingRequest
        {
            OperationId = "embedding-provider",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ModelName = "deterministic",
            Inputs =
            [
                new EmbeddingInput
                {
                    Id = "input-a",
                    Text = "alpha",
                    SourceRef = "item-a"
                },
                new EmbeddingInput
                {
                    Id = "input-b",
                    Text = "beta",
                    SourceRef = "item-b"
                }
            ]
        });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("embedding-provider", result.OperationId);
        Assert.AreEqual("deterministic", result.ModelName);
        Assert.AreEqual(3, result.Dimensions);
        Assert.AreEqual(2, result.Vectors.Count);
        Assert.AreEqual("input-a", result.Vectors[0].InputId);
        Assert.AreEqual("item-a", result.Vectors[0].SourceRef);
        Assert.AreEqual(3, result.Vectors[0].Values.Count);
        Assert.AreEqual(2, result.Usage.ModelCalls);
    }

    [TestMethod]
    public async Task VectorStoreContract_ShouldUpsertGetSearchAndDelete()
    {
        var store = new TestVectorStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new VectorRecord
        {
            Id = "vector-a",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "item-a",
            SourceKind = "context",
            ModelName = "deterministic",
            Dimensions = 3,
            Vector = [1f, 0f, 0f],
            ContentHash = "hash-a",
            Tags = ["alpha"],
            CreatedAt = now,
            UpdatedAt = now
        });
        await store.UpsertAsync(new VectorRecord
        {
            Id = "vector-b",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "item-b",
            SourceKind = "memory",
            ModelName = "deterministic",
            Dimensions = 3,
            Vector = [0.8f, 0.2f, 0f],
            ContentHash = "hash-b",
            Tags = ["alpha", "memory"],
            CreatedAt = now,
            UpdatedAt = now
        });

        var fetched = await store.GetAsync("workspace-test", "vector-a");
        var results = await store.SearchAsync(new VectorQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1f, 0f, 0f],
            TopK = 1,
            Tags = ["alpha"],
            IncludeVector = false
        });
        await store.DeleteAsync("workspace-test", "vector-a");
        var deleted = await store.GetAsync("workspace-test", "vector-a");

        Assert.IsNotNull(fetched);
        Assert.AreEqual("item-a", fetched!.SourceId);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("vector-a", results[0].Record.Id);
        Assert.AreEqual(1, results[0].Rank);
        Assert.IsTrue(results[0].Score > 0.99);
        Assert.AreEqual(0, results[0].Record.Vector.Count);
        Assert.IsNull(deleted);
    }

    [TestMethod]
    public async Task EmbeddingJobServiceContract_ShouldCreateAndProcessJob()
    {
        var service = new TestEmbeddingJobService(new DeterministicEmbeddingProvider(), new TestVectorStore());

        var job = await service.EnqueueAsync(new EmbeddingRequest
        {
            OperationId = "embedding-job",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ModelName = "deterministic",
            InputKind = EmbeddingInputKind.ContextItem,
            Inputs =
            [
                new EmbeddingInput
                {
                    Id = "input-job",
                    Text = "job input",
                    SourceRef = "item-job",
                    Tags = ["job"]
                }
            ]
        });
        var processed = await service.ProcessAsync(job);
        var recent = await service.QueryRecentAsync("workspace-test", "collection-test", 10);

        Assert.AreEqual(EmbeddingJobState.Queued, job.State);
        Assert.AreEqual(EmbeddingJobState.Succeeded, processed.State);
        Assert.IsNotNull(processed.Result);
        Assert.AreEqual(1, processed.Result!.Vectors.Count);
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual(processed.JobId, recent[0].JobId);
    }

    [TestMethod]
    public async Task MockEmbeddingProvider_ShouldBatchNormalizeAndUseContentHashCache()
    {
        var cache = new EmbeddingCacheService();
        var provider = new MockEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "mock-test",
            Dimensions = 8,
            MaxBatchSize = 1,
            EnableContentHashCache = true
        }, cache);

        var result = await provider.EmbedAsync(new EmbeddingRequest
        {
            OperationId = "mock-embedding",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ModelName = "mock-test",
            Inputs =
            [
                new EmbeddingInput { Id = "input-1", Text = "same text", SourceRef = "item-1" },
                new EmbeddingInput { Id = "input-2", Text = "same text", SourceRef = "item-2" }
            ]
        });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, result.Vectors.Count);
        Assert.AreEqual(8, result.Dimensions);
        Assert.AreEqual(1, provider.GeneratedVectorCount);
        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual("false", result.Vectors[0].Metadata["cacheHit"]);
        Assert.AreEqual("true", result.Vectors[1].Metadata["cacheHit"]);
        Assert.IsTrue(Math.Abs(1.0 - result.Vectors[0].Norm!.Value) < 0.0001);
    }

    [TestMethod]
    public async Task OnnxEmbeddingSessionManager_ShouldLoadOnDemandAndUnloadWhenIdle()
    {
        var factory = new FakeOnnxEmbeddingSessionFactory(dimensions: 3);
        var manager = new OnnxEmbeddingSessionManager(new EmbeddingOptions
        {
            ModelName = "onnx-test",
            IdleUnloadAfter = TimeSpan.FromSeconds(1)
        }, factory);

        var session = await manager.GetSessionAsync();
        var firstLastUsedAt = manager.LastUsedAt;
        var notUnloaded = await manager.UnloadIfIdleAsync(DateTimeOffset.UtcNow);
        var unloaded = await manager.UnloadIfIdleAsync(DateTimeOffset.UtcNow.AddSeconds(2));

        Assert.IsNotNull(session);
        Assert.AreEqual(1, manager.LoadCount);
        Assert.IsTrue(firstLastUsedAt is not null);
        Assert.IsFalse(notUnloaded);
        Assert.IsTrue(unloaded);
        Assert.IsFalse(manager.IsLoaded);
        Assert.IsTrue(factory.CreatedSessions.Single().Disposed);
    }

    [TestMethod]
    public async Task OnnxEmbeddingProvider_ShouldSupportBatchEmbeddingAndContentHashCache()
    {
        var cache = new EmbeddingCacheService();
        var factory = new FakeOnnxEmbeddingSessionFactory(dimensions: 3);
        var manager = new OnnxEmbeddingSessionManager(new EmbeddingOptions
        {
            ModelName = "onnx-test",
            Dimensions = 3,
            MaxBatchSize = 2,
            EnableContentHashCache = true
        }, factory);
        var provider = new OnnxEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "onnx-test",
            Dimensions = 3,
            MaxBatchSize = 2,
            EnableContentHashCache = true
        }, manager, cache);

        var request = new EmbeddingRequest
        {
            OperationId = "onnx-embedding",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ModelName = "onnx-test",
            Inputs =
            [
                new EmbeddingInput { Id = "input-a", Text = "alpha", SourceRef = "item-a" },
                new EmbeddingInput { Id = "input-b", Text = "beta", SourceRef = "item-b" },
                new EmbeddingInput { Id = "input-c", Text = "gamma", SourceRef = "item-c" }
            ]
        };

        var first = await provider.EmbedAsync(request);
        var second = await provider.EmbedAsync(new EmbeddingRequest
        {
            OperationId = "onnx-embedding-cache",
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ModelName = request.ModelName,
            InputKind = request.InputKind,
            Normalize = request.Normalize,
            Inputs = request.Inputs,
            Metadata = request.Metadata
        });
        var session = factory.CreatedSessions.Single();

        Assert.IsTrue(first.Succeeded);
        Assert.AreEqual(3, first.Vectors.Count);
        Assert.AreEqual(2, session.BatchCallCount);
        Assert.AreEqual(3, first.Usage.ModelCalls);
        Assert.AreEqual(0, second.Usage.ModelCalls);
        Assert.AreEqual(2, session.BatchCallCount);
        Assert.AreEqual("true", second.Vectors[0].Metadata["cacheHit"]);
    }

    [TestMethod]
    public async Task OnnxRuntimeEmbeddingProvider_ShouldUseProjectLocalModel()
    {
        var modelPath = EmbeddingModelPaths.ResolveModelPath();
        var vocabularyPath = EmbeddingModelPaths.ResolveVocabularyPath(null, modelPath);
        if (!File.Exists(modelPath) || !File.Exists(vocabularyPath))
        {
            Assert.Inconclusive("本地 ONNX embedding 模型文件不存在，跳过真实模型 smoke test。");
        }

        var options = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            Dimensions = 0,
            MaxBatchSize = 2,
            MaxSequenceLength = 64,
            EnableContentHashCache = true,
            Normalize = true,
            ModelPath = modelPath,
            VocabularyPath = vocabularyPath,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1
        };
        var manager = new OnnxEmbeddingSessionManager(options);
        var provider = new OnnxEmbeddingProvider(options, manager, new EmbeddingCacheService());

        try
        {
            var request = new EmbeddingRequest
            {
                OperationId = "onnx-runtime-embedding",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ModelName = EmbeddingModelPaths.DefaultModelName,
                Inputs =
                [
                    new EmbeddingInput
                    {
                        Id = "input-memory",
                        Text = "上下文记忆系统可以保存长期任务信息。",
                        SourceRef = "item-memory"
                    },
                    new EmbeddingInput
                    {
                        Id = "input-search",
                        Text = "向量检索可以找回相关的上下文记录。",
                        SourceRef = "item-search"
                    },
                    new EmbeddingInput
                    {
                        Id = "input-unrelated",
                        Text = "今天的天气很热，我想喝一杯冰水。",
                        SourceRef = "item-unrelated"
                    }
                ]
            };

            var first = await provider.EmbedAsync(request);
            var second = await provider.EmbedAsync(new EmbeddingRequest
            {
                OperationId = "onnx-runtime-embedding-cache",
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                ModelName = request.ModelName,
                Inputs =
                [
                    new EmbeddingInput
                    {
                        Id = "input-memory-repeat",
                        Text = request.Inputs[0].Text,
                        SourceRef = "item-memory-repeat"
                    }
                ]
            });

            Assert.IsTrue(first.Succeeded);
            Assert.AreEqual(3, first.Vectors.Count);
            Assert.AreEqual(512, first.Dimensions);
            Assert.AreEqual(512, first.Vectors[0].Values.Count);
            Assert.IsTrue(first.Vectors[0].Values.Any(value => Math.Abs(value) > 0.000001));
            Assert.IsTrue(Math.Abs(1.0 - first.Vectors[0].Norm!.Value) < 0.001);
            Assert.IsTrue(Cosine(first.Vectors[0].Values, first.Vectors[1].Values)
                > Cosine(first.Vectors[0].Values, first.Vectors[2].Values));
            Assert.AreEqual(0, second.Usage.ModelCalls);
            Assert.AreEqual("true", second.Vectors[0].Metadata["cacheHit"]);
        }
        finally
        {
            await manager.ForceUnloadAsync();
        }
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
        {
            return 0;
        }

        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private sealed class FakeOnnxEmbeddingSessionFactory : IOnnxEmbeddingSessionFactory
    {
        private readonly int _dimensions;

        public FakeOnnxEmbeddingSessionFactory(int dimensions)
        {
            _dimensions = dimensions;
        }

        public List<FakeOnnxEmbeddingSession> CreatedSessions { get; } = new();

        public Task<IOnnxEmbeddingSession> CreateAsync(
            EmbeddingOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = new FakeOnnxEmbeddingSession(options.ModelName, _dimensions);
            CreatedSessions.Add(session);
            return Task.FromResult<IOnnxEmbeddingSession>(session);
        }
    }

    private sealed class FakeOnnxEmbeddingSession : IOnnxEmbeddingSession
    {
        public FakeOnnxEmbeddingSession(string modelName, int dimensions)
        {
            ModelName = modelName;
            Dimensions = dimensions;
        }

        public string ModelName { get; }

        public int Dimensions { get; }

        public int BatchCallCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchCallCount++;

            var vectors = texts
                .Select(text =>
                {
                    var values = new float[Dimensions];
                    values[0] = Math.Max(1, text.Length);
                    if (Dimensions > 1)
                    {
                        values[1] = BatchCallCount;
                    }

                    if (Dimensions > 2)
                    {
                        values[2] = 1f;
                    }

                    return (IReadOnlyList<float>)values;
                })
                .ToArray();

            return Task.FromResult<IReadOnlyList<IReadOnlyList<float>>>(vectors);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vectors = request.Inputs.Select(input =>
            {
                var seed = Math.Max(1, input.Text.Length);
                return new EmbeddingVector
                {
                    InputId = input.Id,
                    SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                    Values = [seed, seed % 7, 1f],
                    Norm = Math.Sqrt((seed * seed) + ((seed % 7) * (seed % 7)) + 1)
                };
            }).ToArray();

            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = request.ModelName ?? "deterministic",
                Dimensions = 3,
                Succeeded = true,
                Vectors = vectors,
                Usage = new ContextOperationUsage
                {
                    InputTokens = request.Inputs.Sum(input => Math.Max(1, input.Text.Length / 4)),
                    OutputTokens = 0,
                    ModelCalls = request.Inputs.Count
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class TestVectorStore : IVectorStore
    {
        private readonly Dictionary<string, VectorRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records[Key(record.WorkspaceId, record.Id)] = Clone(record);
            return Task.CompletedTask;
        }

        public Task<VectorRecord?> GetAsync(
            string workspaceId,
            string vectorId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_records.TryGetValue(Key(workspaceId, vectorId), out var record)
                ? Clone(record)
                : null);
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            VectorQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var results = _records.Values
                .Where(record => string.Equals(record.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(record => string.IsNullOrWhiteSpace(query.CollectionId)
                    || string.Equals(record.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
                .Where(record => tags.Count == 0 || tags.All(record.Tags.Contains))
                .Where(record => sourceKinds.Count == 0 || sourceKinds.Contains(record.SourceKind))
                .Select(record => new
                {
                    Record = record,
                    Score = Cosine(query.Vector, record.Vector)
                })
                .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
                .OrderByDescending(item => item.Score)
                .Take(query.TopK > 0 ? query.TopK : 10)
                .Select((item, index) => new VectorSearchResult
                {
                    Record = query.IncludeVector ? Clone(item.Record) : Clone(item.Record, includeVector: false),
                    Score = item.Score,
                    Rank = index + 1
                })
                .ToArray();

            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
        }

        public Task DeleteAsync(
            string workspaceId,
            string vectorId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records.Remove(Key(workspaceId, vectorId));
            return Task.CompletedTask;
        }

        private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            var length = Math.Min(left.Count, right.Count);
            if (length == 0)
            {
                return 0;
            }

            var dot = 0.0;
            var leftNorm = 0.0;
            var rightNorm = 0.0;
            for (var i = 0; i < length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            return leftNorm <= 0 || rightNorm <= 0
                ? 0
                : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }

        private static VectorRecord Clone(VectorRecord record, bool includeVector = true)
        {
            return new VectorRecord
            {
                Id = record.Id,
                WorkspaceId = record.WorkspaceId,
                CollectionId = record.CollectionId,
                SourceId = record.SourceId,
                SourceKind = record.SourceKind,
                ModelName = record.ModelName,
                Dimensions = record.Dimensions,
                Vector = includeVector ? record.Vector.ToArray() : Array.Empty<float>(),
                ContentHash = record.ContentHash,
                Tags = record.Tags.ToArray(),
                Metadata = new Dictionary<string, string>(record.Metadata),
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt
            };
        }

        private static string Key(string workspaceId, string id)
        {
            return $"{workspaceId}\u001f{id}";
        }
    }

    private sealed class TestEmbeddingJobService : IEmbeddingJobService
    {
        private readonly IEmbeddingProvider _provider;
        private readonly IVectorStore _vectorStore;
        private readonly List<EmbeddingJob> _jobs = new();

        public TestEmbeddingJobService(IEmbeddingProvider provider, IVectorStore vectorStore)
        {
            _provider = provider;
            _vectorStore = vectorStore;
        }

        public Task<EmbeddingJob> EnqueueAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = new EmbeddingJob
            {
                JobId = string.IsNullOrWhiteSpace(request.OperationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.OperationId,
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                State = EmbeddingJobState.Queued,
                Request = request,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _jobs.Add(job);
            return Task.FromResult(job);
        }

        public async Task<EmbeddingJob> ProcessAsync(
            EmbeddingJob job,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = DateTimeOffset.UtcNow;
            var result = await _provider.EmbedAsync(job.Request, cancellationToken).ConfigureAwait(false);
            foreach (var vector in result.Vectors)
            {
                await _vectorStore.UpsertAsync(new VectorRecord
                {
                    Id = $"{job.JobId}-{vector.InputId}",
                    WorkspaceId = job.WorkspaceId,
                    CollectionId = job.CollectionId,
                    SourceId = vector.SourceRef,
                    SourceKind = job.Request.InputKind.ToString(),
                    ModelName = result.ModelName,
                    Dimensions = result.Dimensions,
                    Vector = vector.Values,
                    ContentHash = vector.Metadata.GetValueOrDefault("contentHash", string.Empty),
                    Tags = job.Request.Inputs.FirstOrDefault(input => input.Id == vector.InputId)?.Tags ?? Array.Empty<string>(),
                    CreatedAt = startedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
            }

            var processed = new EmbeddingJob
            {
                JobId = job.JobId,
                WorkspaceId = job.WorkspaceId,
                CollectionId = job.CollectionId,
                State = EmbeddingJobState.Succeeded,
                Request = job.Request,
                Result = result,
                CreatedAt = job.CreatedAt,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = job.Metadata
            };

            _jobs.RemoveAll(item => item.JobId == job.JobId);
            _jobs.Add(processed);
            return processed;
        }

        public Task<IReadOnlyList<EmbeddingJob>> QueryRecentAsync(
            string workspaceId,
            string? collectionId,
            int take,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobs = _jobs
                .Where(job => string.Equals(job.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(job.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(job => job.CompletedAt ?? job.CreatedAt)
                .Take(take > 0 ? take : 50)
                .ToArray();

            return Task.FromResult<IReadOnlyList<EmbeddingJob>>(jobs);
        }
    }
}
