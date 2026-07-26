using System;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Foundry.Rules;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace Foundry.Api.Workflow;

/// <summary>
/// Loads, saves and records workflow entities through the MongoDB repository layer.
/// </summary>
/// <remarks>
/// <para>
/// The reflection this replaces lived in <c>WorkflowTransitionBehavior</c>, which is in
/// <c>Foundry.Rules</c> — a project that deliberately does not reference a data provider. That
/// constraint is real, so the generic dispatch has not disappeared; it has moved to the layer that
/// owns <c>IRepository&lt;T&gt;</c> and been reduced to one place, behind a typed interface, with the
/// entity type resolved from an explicit registry instead of an assembly scan.
/// </para>
/// <para>
/// The key type is no longer guessed. It used to be inferred from the id string's length — 24
/// characters meant <c>ObjectId</c>, anything else <c>string</c> — so a malformed id was quietly
/// treated as a different key type and failed inside the driver. <c>ObjectId</c> is the only key type
/// the data layer supports (see FDY1011), so a value that is not one is rejected here with a message
/// naming the id.
/// </para>
/// </remarks>
public sealed class MongoWorkflowStateStore : IWorkflowStateStore
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEntityTypeRegistry _registry;

    /// <summary>Initializes the store.</summary>
    public MongoWorkflowStateStore(IServiceProvider serviceProvider, WorkflowEntityTypeRegistry registry)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public async Task<IWorkflowStateful?> LoadAsync(
        string entityTypeName, string entityId, CancellationToken ct = default)
    {
        var entityType = _registry.Resolve(entityTypeName);
        var id = ParseId(entityId);

        var accessor = GetAccessor(entityType);
        var entity = await accessor.GetByIdAsync(_serviceProvider, id, ct);

        if (entity is null) return null;

        if (entity is not IWorkflowStateful stateful)
        {
            throw new WorkflowException(
                $"Entity type '{entityTypeName}' must implement IWorkflowStateful to take part in a workflow.");
        }

        return stateful;
    }

    /// <inheritdoc />
    public Task SaveAsync(string entityTypeName, IWorkflowStateful entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityType = _registry.Resolve(entityTypeName);
        return GetAccessor(entityType).UpdateAsync(_serviceProvider, entity, ct);
    }

    /// <inheritdoc />
    public Task AppendActivityLogAsync(WorkflowActivityLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        // Loudly, if the history collection is not wired up. The previous implementation resolved the
        // log repository reflectively and skipped the write when any step returned null, so a
        // transition succeeded while leaving no trace -- in the one place someone would look to find
        // out what happened.
        var repository = _serviceProvider.GetService<IRepository<WorkflowActivityLog>>()
            ?? throw new WorkflowException(
                "IRepository<WorkflowActivityLog> is not registered, so workflow history cannot be "
                + "recorded. Register it (AddFoundryMongo does) or remove the workflow behaviour.");

        return repository.InsertAsync(log, null, ct);
    }

    private static ObjectId ParseId(string entityId)
    {
        if (!ObjectId.TryParse(entityId, out var id))
        {
            throw new WorkflowException(
                $"Entity id '{entityId}' is not a valid ObjectId. The data layer only supports "
                + "ObjectId keys, so a transition cannot be applied to this id.");
        }

        return id;
    }

    /// <summary>
    /// Bridges from a runtime <see cref="Type"/> to the generic repository API.
    /// </summary>
    /// <remarks>
    /// The single remaining piece of generic dispatch, isolated here so nothing above this class has to
    /// know about it. Cached per entity type: constructing the closed generic on every transition was
    /// avoidable work on a hot path.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IRepositoryAccessor> Accessors = new();

    private static IRepositoryAccessor GetAccessor(Type entityType) =>
        Accessors.GetOrAdd(entityType, static type =>
        {
            if (!typeof(IEntity<ObjectId>).IsAssignableFrom(type))
            {
                throw new WorkflowException(
                    $"Entity type '{type.Name}' is not an IEntity<ObjectId>, so no repository exists for it.");
            }

            var accessorType = typeof(RepositoryAccessor<>).MakeGenericType(type);
            return (IRepositoryAccessor)Activator.CreateInstance(accessorType)!;
        });

    private interface IRepositoryAccessor
    {
        Task<object?> GetByIdAsync(IServiceProvider services, ObjectId id, CancellationToken ct);
        Task UpdateAsync(IServiceProvider services, object entity, CancellationToken ct);
    }

    private sealed class RepositoryAccessor<TEntity> : IRepositoryAccessor
        where TEntity : class, IEntity<ObjectId>
    {
        public async Task<object?> GetByIdAsync(IServiceProvider services, ObjectId id, CancellationToken ct)
        {
            var repository = Resolve(services);
            return await repository.GetByIdAsync(id, null, ct);
        }

        public Task UpdateAsync(IServiceProvider services, object entity, CancellationToken ct)
        {
            var repository = Resolve(services);
            return repository.UpdateAsync((TEntity)entity, null, ct);
        }

        private static IRepository<TEntity> Resolve(IServiceProvider services) =>
            services.GetService<IRepository<TEntity>>()
            ?? throw new WorkflowException(
                $"IRepository<{typeof(TEntity).Name}> is not registered, so the workflow entity cannot "
                + "be loaded or saved.");
    }
}
