using System;
using System.Reflection;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Mongo.DependencyInjection;
using Foundry.Mongo.Diagnostics;
using Foundry.Core.Security;
using Foundry.Mongo.Infrastructure.Conventions;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.UnitOfWork;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Foundry.Core.Outbox;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Contains DI registration extensions for registering the Foundry.Mongo repository layer in ASP.NET Core applications.
/// </summary>
public static class FoundryMongoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MongoClient, MongoDatabase, IRepository generic abstraction, global Conventions, and symmetric Encryption Provider.
    /// Optionally wraps all resolved repositories in a transparent CachedRepository decorator.
    /// </summary>
    public static IServiceCollection AddFoundryMongo(
        this IServiceCollection services,
        Action<FoundryMongoOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new FoundryMongoOptions();
        configureOptions(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("ConnectionString is required to initialize MongoDB client.", nameof(configureOptions));
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            throw new ArgumentException("DatabaseName is required to bind MongoDB database context.", nameof(configureOptions));
        }

        // Initialize global casing and serialization conventions
        MongoDbConventions.Register();

        // Configure connection client and target database context
        var mongoSettings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        mongoSettings.MinConnectionPoolSize = options.MinConnectionPoolSize;
        mongoSettings.MaxConnectionPoolSize = options.MaxConnectionPoolSize;
        var mongoClient = new MongoClient(mongoSettings);
        var database = mongoClient.GetDatabase(options.DatabaseName);

        services.TryAddSingleton<IMongoClient>(mongoClient);
        services.TryAddSingleton<IMongoDatabase>(database);
        services.TryAddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        services.TryAddSingleton<Foundry.Core.Tenant.ITenantContext, Foundry.Core.Tenant.TenantContext>();

        // Register mock KMS client by default if not already registered (for local development/testing)
        services.TryAddSingleton<IKmsClient, LocalMockKmsClient>();

        // Bind AES or KMS Envelope encryption provider if keys are present
        if (!string.IsNullOrWhiteSpace(options.EncryptedEncryptionKey))
        {
            services.TryAddSingleton<IEncryptionProvider>(sp =>
            {
                var kmsClient = sp.GetRequiredService<IKmsClient>();
                return new KmsEnvelopeEncryptionProvider(kmsClient, options.EncryptedEncryptionKey);
            });
        }
        else if (!string.IsNullOrWhiteSpace(options.EncryptionKey))
        {
            services.TryAddSingleton<IEncryptionProvider>(_ => new AesEncryptionProvider(options.EncryptionKey));
        }

        // Bind Caching decorator options if enabled
        if (options.EnableCaching)
        {
            services.AddMemoryCache();
            services.TryAddSingleton(new CachedRepositoryOptions
            {
                DefaultTtl = options.DefaultCacheTtl ?? TimeSpan.FromMinutes(5)
            });
            services.TryAddTransient(typeof(Repository<>));
        }

        // Scan assemblies for IEntity<ObjectId> types to register correct concrete repository types
        var entityTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .Where(t => typeof(IEntity<ObjectId>).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

        foreach (var entityType in entityTypes)
        {
            var interfaceType = typeof(IRepository<>).MakeGenericType(entityType);
            var isPartitioned = entityType.GetCustomAttribute<PartitionedAttribute>() != null;

            if (isPartitioned)
            {
                var implementationType = typeof(PartitionedRepository<>).MakeGenericType(entityType);
                services.TryAddTransient(interfaceType, implementationType);
            }
            else
            {
                if (options.EnableCaching)
                {
                    var implementationType = typeof(CachedRepository<>).MakeGenericType(entityType);
                    services.TryAddTransient(interfaceType, implementationType);
                }
                else
                {
                    var implementationType = typeof(Repository<>).MakeGenericType(entityType);
                    services.TryAddTransient(interfaceType, implementationType);
                }
            }
        }

        // Generic fallback registrations
        services.TryAddTransient(typeof(Repository<>));
        services.TryAddTransient(typeof(PartitionedRepository<>));
        services.TryAddTransient(typeof(IRepository<>), typeof(Repository<>));

        services.TryAddTransient<MongoDbHealthCheck>();

        // Register transactional outbox services
        services.TryAddTransient<IOutboxQueue, Foundry.Mongo.Services.MongoOutboxQueue>();
        services.AddHostedService<Foundry.Mongo.Services.OutboxPublisherWorker>();

        // Register background archival worker
        services.AddHostedService<Foundry.Mongo.Services.DataArchivalWorker>();

        return services;
    }
}
