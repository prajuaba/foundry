using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Mongo.Repositories;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;

namespace Foundry.Mongo.Benchmark;

public static class Program
{
    public record BenchmarkEntity : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string EncryptedField { get; set; } = string.Empty;
    }

    private static readonly string EncryptionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("benchmark-key-123456789012345678"));

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("======================================================================");
        Console.WriteLine("                FoundryMongo Performance Benchmark Suite              ");
        Console.WriteLine("======================================================================");
        Console.ResetColor();
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine($"Processor Count: {Environment.ProcessorCount}");
        Console.WriteLine($".NET Runtime: {Environment.Version}");
        Console.WriteLine("----------------------------------------------------------------------\n");

        var objectId = ObjectId.GenerateNewId();
        var encryptionProvider = new AesEncryptionProvider(EncryptionKey);
        
        // Setup raw plaintext entity and encrypted db-side representation
        var rawEntity = new BenchmarkEntity
        {
            Id = objectId,
            Name = "Plaintext Standard Document Title",
            EncryptedField = "This is a highly sensitive supplier code note."
        };
        var encryptedEntity = new BenchmarkEntity
        {
            Id = objectId,
            Name = rawEntity.Name,
            EncryptedField = encryptionProvider.Encrypt(rawEntity.EncryptedField),
            Version = 1
        };

        // Create Mocks using NSubstitute
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<BenchmarkEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("BenchmarkDb"), "BenchmarkEntities"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<BenchmarkEntity>(Arg.Any<string>()).Returns(mockCollection);

        // Configure FindAsync return stubs
        var plainCursor = new TestAsyncCursor<BenchmarkEntity>(rawEntity);
        var encryptedCursor = new TestAsyncCursor<BenchmarkEntity>(encryptedEntity);

        // Use lambda factories to prevent cursor exhaustion across calls
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<BenchmarkEntity>>(),
            Arg.Any<FindOptions<BenchmarkEntity, BenchmarkEntity>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => Task.FromResult<IAsyncCursor<BenchmarkEntity>>(new TestAsyncCursor<BenchmarkEntity>(rawEntity)));

        // Repositories
        var rawRepo = new Repository<BenchmarkEntity>(mockDb);

        // Create another database mock for the encryption collection to isolate calls
        var encryptDb = Substitute.For<IMongoDatabase>();
        var encryptCollection = Substitute.For<IMongoCollection<BenchmarkEntity>>();
        encryptCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("BenchmarkDb"), "BenchmarkEntities"));
        encryptCollection.Database.Returns(encryptDb);
        encryptDb.GetCollection<BenchmarkEntity>(Arg.Any<string>()).Returns(encryptCollection);

        encryptCollection.FindAsync(
            Arg.Any<FilterDefinition<BenchmarkEntity>>(),
            Arg.Any<FindOptions<BenchmarkEntity, BenchmarkEntity>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => Task.FromResult<IAsyncCursor<BenchmarkEntity>>(new TestAsyncCursor<BenchmarkEntity>(encryptedEntity)));

        var encryptRepo = new Repository<BenchmarkEntity>(encryptDb, null, null, encryptionProvider);
        
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cachedRepo = new CachedRepository<BenchmarkEntity>(encryptRepo, memoryCache);

        // Pre-fill cache for read hit testing
        await cachedRepo.GetByIdAsync(objectId);

        // --- Run Benchmarks ---

        // 1. Direct reads (no decryption)
        var bench1 = await ProfileAsync("Direct Read (Plaintext)", 2000, () => rawRepo.GetByIdAsync(objectId));

        // 2. Direct reads + decryption
        var bench2 = await ProfileAsync("Direct Read + AES Decryption", 2000, () => encryptRepo.GetByIdAsync(objectId));

        // 3. Cache Hit reads (transparent cached repo)
        var bench3 = await ProfileAsync("Cache Hit Read (In-Memory)", 20000, () => cachedRepo.GetByIdAsync(objectId));

        // --- Print Comparative Report ---
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n======================================================================");
        Console.WriteLine("                        PERFORMANCE SUMMARY                           ");
        Console.WriteLine("======================================================================");
        Console.ResetColor();
        Console.WriteLine(string.Format("| {0,-30} | {1,-12} | {2,-18} |", "Benchmark Scenario", "Avg Latency", "Throughput (ops/s)"));
        Console.WriteLine(new string('-', 70));

        PrintSummaryRow(bench1);
        PrintSummaryRow(bench2);
        PrintSummaryRow(bench3);

        Console.WriteLine(new string('-', 70));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\nBenchmark completed successfully.");
        Console.ResetColor();
    }

    private static async Task<BenchmarkResult> ProfileAsync(string name, int iterations, Func<Task> action)
    {
        Console.Write($"Running '{name}' ({iterations} iterations)... ");

        // Force GC to clean up allocations before start
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var initialMemory = GC.GetTotalMemory(true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            await action();
        }

        stopwatch.Stop();

        var finalMemory = GC.GetTotalMemory(false);
        var memoryAllocatedBytes = Math.Max(0, finalMemory - initialMemory);
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        var avgLatencyUs = (elapsedMs * 1000.0) / iterations;
        var opsPerSec = (iterations / elapsedMs) * 1000.0;

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("Done.");
        Console.ResetColor();

        return new BenchmarkResult(name, iterations, elapsedMs, avgLatencyUs, opsPerSec, memoryAllocatedBytes, gen0, gen1, gen2);
    }

    private static void PrintSummaryRow(BenchmarkResult res)
    {
        Console.WriteLine(string.Format(
            "| {0,-30} | {1,-12:F3} μs | {2,-18:N0} |",
            res.Name,
            res.AvgLatencyUs,
            res.OpsPerSec));
    }

    private record BenchmarkResult(
        string Name,
        int Iterations,
        double ElapsedMs,
        double AvgLatencyUs,
        double OpsPerSec,
        long MemoryAllocatedBytes,
        int Gen0,
        int Gen1,
        int Gen2);
}

// Reusable low-overhead async cursor wrapper
internal class TestAsyncCursor<T> : IAsyncCursor<T>
{
    private readonly T _data;
    private bool _called;

    public TestAsyncCursor(T data)
    {
        _data = data;
    }

    public IEnumerable<T> Current => new[] { _data };

    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        if (_called) return false;
        _called = true;
        return true;
    }

    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MoveNext(cancellationToken));
    }

    public void Dispose() { }
}
