using System;
using Foundry.Mongo.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// AddFoundryMongo ran the caller's lambda at registration time, so a lambda reading
/// IConfiguration captured whatever configuration existed at that moment. In an application that is
/// before the host is built, and therefore before ConfigureAppConfiguration sources are applied --
/// which made the standard mechanism for pointing a test host at its own database silently
/// ineffective. An integration suite spent its whole life writing to the developer's real database
/// while its cleanup dropped an empty one it had never used.
/// </summary>
public class DeferredOptionsTests
{
    // A stand-in for configuration that gains a source after AddFoundryMongo has been called. The
    // real case is ConfigureAppConfiguration in a test host, which runs while the host is built --
    // after Program.cs has already registered everything.
    private sealed class LateConfig
    {
        public string DatabaseName { get; set; } = "original";
    }

    [Fact]
    public void ConfigurationChangedAfterRegistrationIsSeen()
    {
        var services = new ServiceCollection();
        var config = new LateConfig();

        services.AddFoundryMongo(o =>
        {
            o.ConnectionString = "mongodb://localhost:27017";
            o.DatabaseName = config.DatabaseName;
        });

        config.DatabaseName = "the_test_database";

        var options = services.BuildServiceProvider().GetRequiredService<FoundryMongoOptions>();

        Assert.Equal("the_test_database", options.DatabaseName);
    }

    [Fact]
    public void TheDatabaseIsOpenedOnTheLateName()
    {
        // The value reaching FoundryMongoOptions is not the point on its own; the point is which
        // database the application actually talks to.
        var services = new ServiceCollection();
        var config = new LateConfig();

        services.AddFoundryMongo(o =>
        {
            o.ConnectionString = "mongodb://localhost:27017";
            o.DatabaseName = config.DatabaseName;
        });

        config.DatabaseName = "the_test_database";

        var database = services.BuildServiceProvider().GetRequiredService<IMongoDatabase>();

        Assert.Equal("the_test_database", database.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public void AMissingConnectionStringStillThrowsAtRegistration()
    {
        // Deferring the values must not cost the fail-fast behaviour. It does not: the structural
        // read still runs the lambda while the container is being built, so a missing setting stops
        // the application at exactly the point it always did rather than at the first request.
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddFoundryMongo(o => { o.DatabaseName = "db"; }));
    }

    [Fact]
    public void AMissingDatabaseNameStillThrowsAtRegistration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddFoundryMongo(o => { o.ConnectionString = "mongodb://localhost:27017"; }));
    }

    [Fact]
    public void TheLambdaRunsOnceHoweverManyConsumersResolve()
    {
        var runs = 0;
        var services = new ServiceCollection();
        services.AddFoundryMongo(o =>
        {
            runs++;
            o.ConnectionString = "mongodb://localhost:27017";
            o.DatabaseName = "db";
        });

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<FoundryMongoOptions>();
        _ = provider.GetRequiredService<IMongoDatabase>();
        _ = provider.GetRequiredService<IMongoClient>();

        // Once for the structural read at registration, once for the singleton. Not once per
        // consumer -- a lambda with a side effect would otherwise fire unpredictably often.
        Assert.Equal(2, runs);
    }
}
