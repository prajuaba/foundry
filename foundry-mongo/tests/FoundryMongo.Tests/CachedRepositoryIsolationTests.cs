using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// A cached record belongs to the caller it was fetched for, not to the collection.
/// </summary>
/// <remarks>
/// <para>
/// The key was <c>collection:id</c>. <c>Repository&lt;T&gt;.GetByIdAsync</c> hands back a row that
/// has already passed the tenant filter, the owner filter and per-category masking, so the entry
/// held one caller's view and the next caller asking for that id was served it — a direct read that
/// walks past the tenant filter entirely.
/// </para>
/// <para>
/// Found by turning caching on in a real application after the API-layer cache had been fixed:
/// tenant <c>globex</c> requested a project belonging to tenant <c>acme</c> by id and got 200. The
/// unit tests over the pipeline cache all passed at the time, because this is a different cache.
/// </para>
/// </remarks>
public class CachedRepositoryIsolationTests
{
    public record Widget : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Ctx : ICurrentUserContext
    {
        public string OperatorId { get; init; } = "op";
        public string? OperatorName => null;
        public ClaimsPrincipal? User { get; init; }
    }

    private static ICurrentUserContext Caller(string subject, params string[] scopes)
    {
        var claims = new List<Claim> { new("sub", subject) };
        foreach (var s in scopes) claims.Add(new Claim("scope", s));
        return new Ctx
        {
            OperatorId = subject,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };
    }

    private static ITenantContext Tenant(string? id)
    {
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns(id);
        ctx.HasTenant.Returns(id is not null);
        return ctx;
    }

    /// <summary>
    /// Builds a repository over a collection that always returns <paramref name="record"/>, and
    /// reports how many times the database was actually asked.
    /// </summary>
    private static (CachedRepository<Widget> Repo, Func<int> DbReads) Build(
        IMemoryCache cache, Widget record, ICurrentUserContext user, ITenantContext tenant)
    {
        var db = Substitute.For<IMongoDatabase>();
        var collection = Substitute.For<IMongoCollection<Widget>>();
        collection.CollectionNamespace.Returns(
            new CollectionNamespace(new DatabaseNamespace("TestDb"), "Widgets"));
        db.GetCollection<Widget>(Arg.Any<string>()).Returns(collection);

        var reads = 0;
        collection.FindAsync(
            Arg.Any<FilterDefinition<Widget>>(),
            Arg.Any<FindOptions<Widget, Widget>>(),
            Arg.Any<CancellationToken>()
        ).Returns(_ =>
        {
            reads++;
            return Task.FromResult<IAsyncCursor<Widget>>(new TestAsyncCursor<Widget>(record));
        });

        return (new CachedRepository<Widget>(new Repository<Widget>(db), cache, null, user, tenant), () => reads);
    }

    [Fact]
    public async Task TwoTenantsAskingForOneIdDoNotShareAnEntry()
    {
        // The defect: acme fetches, globex is served acme's row without the database being asked
        // -- and therefore without the tenant filter running.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var id = ObjectId.GenerateNewId();
        var record = new Widget { Id = id, Name = "acme-owned", Version = 1 };

        var (acme, acmeReads) = Build(cache, record, Caller("u1"), Tenant("acme"));
        var (globex, globexReads) = Build(cache, record, Caller("u1"), Tenant("globex"));

        await acme.GetByIdAsync(id);
        await globex.GetByIdAsync(id);

        Assert.Equal(1, acmeReads());
        Assert.Equal(1, globexReads());
    }

    [Fact]
    public async Task TwoSubjectsInOneTenantDoNotShareAnEntry()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var id = ObjectId.GenerateNewId();
        var record = new Widget { Id = id, Name = "owned", Version = 1 };

        var (one, oneReads) = Build(cache, record, Caller("member-one"), Tenant("acme"));
        var (two, twoReads) = Build(cache, record, Caller("member-two"), Tenant("acme"));

        await one.GetByIdAsync(id);
        await two.GetByIdAsync(id);

        Assert.Equal(1, oneReads());
        Assert.Equal(1, twoReads());
    }

    [Fact]
    public async Task AnUnmaskingScopeDoesNotShareAnEntry()
    {
        // What the cached value contains, not which rows come back: an entry populated by a caller
        // with no scopes must not be replayed to one holding view:contact, nor the reverse.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var id = ObjectId.GenerateNewId();
        var record = new Widget { Id = id, Name = "masked-or-not", Version = 1 };

        var (plain, plainReads) = Build(cache, record, Caller("u1"), Tenant("acme"));
        var (entitled, entitledReads) = Build(cache, record, Caller("u1", "view:contact"), Tenant("acme"));

        await plain.GetByIdAsync(id);
        await entitled.GetByIdAsync(id);

        Assert.Equal(1, plainReads());
        Assert.Equal(1, entitledReads());
    }

    [Fact]
    public async Task TheSameCallerAskingTwiceIsServedFromCache()
    {
        // Keying per caller is only correct if it still caches; without this the suite would pass
        // for a key that never hits.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var id = ObjectId.GenerateNewId();
        var record = new Widget { Id = id, Name = "same", Version = 1 };

        var (repo, reads) = Build(cache, record, Caller("u1", "view:pii"), Tenant("acme"));

        await repo.GetByIdAsync(id);
        await repo.GetByIdAsync(id);

        Assert.Equal(1, reads());
    }

    [Fact]
    public async Task AWriteEvictsEveryCallersCopy()
    {
        // Per-caller keys mean a write cannot evict by recomputing one key. Whoever updates a
        // record is rarely the only one holding it, and the rest would serve the stale value for
        // the remainder of the TTL -- so eviction goes through a change token per record.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var id = ObjectId.GenerateNewId();
        var record = new Widget { Id = id, Name = "before", Version = 1 };

        var (reader, readerReads) = Build(cache, record, Caller("reader"), Tenant("acme"));
        var (writer, _) = Build(cache, record, Caller("writer"), Tenant("acme"));

        await reader.GetByIdAsync(id);
        Assert.Equal(1, readerReads());
        await reader.GetByIdAsync(id);
        Assert.Equal(1, readerReads()); // cached

        await writer.DeleteAsync(id);

        await reader.GetByIdAsync(id);
        Assert.Equal(2, readerReads()); // the writer's eviction reached the reader's entry
    }
}
