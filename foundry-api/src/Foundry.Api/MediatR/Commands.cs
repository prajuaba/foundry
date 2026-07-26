global using MediatR;
global using MongoDB.Bson;
global using Foundry.Core.Entities;

namespace Foundry.Api.MediatR;

public record InsertCommand<TEntity>(TEntity Entity) : IRequest<TEntity>
    where TEntity : class, IEntity<ObjectId>;

public record UpdateCommand<TEntity>(TEntity Entity) : IRequest<TEntity>
    where TEntity : class, IEntity<ObjectId>;

public record DeleteCommand<TEntity>(ObjectId Id, string OperatorId) : IRequest<bool>
    where TEntity : class, IEntity<ObjectId>;
