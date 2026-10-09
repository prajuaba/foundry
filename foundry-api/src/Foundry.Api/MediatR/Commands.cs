global using MediatR;
global using MongoDB.Bson;
global using Foundry.Core.Entities;

namespace Foundry.Api.MediatR;

/// <summary>
/// A command that writes an entity as a client sent it: the generic POST and PUT, over REST and GraphQL.
/// </summary>
/// <remarks>
/// Lets a pipeline behaviour see the entity without knowing its type. The workflow engine's own
/// writes do not go through these commands, which is what lets a behaviour treat everything
/// arriving here as untrusted.
/// </remarks>
public interface IClientEntityWrite
{
    object Entity { get; }
    bool IsInsert { get; }
}

public record InsertCommand<TEntity>(TEntity Entity) : IRequest<TEntity>, IClientEntityWrite
    where TEntity : class, IEntity<ObjectId>
{
    object IClientEntityWrite.Entity => Entity;
    bool IClientEntityWrite.IsInsert => true;
}

public record UpdateCommand<TEntity>(TEntity Entity) : IRequest<TEntity>, IClientEntityWrite
    where TEntity : class, IEntity<ObjectId>
{
    object IClientEntityWrite.Entity => Entity;
    bool IClientEntityWrite.IsInsert => false;
}

public record DeleteCommand<TEntity>(ObjectId Id, string OperatorId) : IRequest<bool>
    where TEntity : class, IEntity<ObjectId>;
