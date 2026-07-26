using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using Foundry.Mongo.Repositories;
using Foundry.Core.Entities;
using Foundry.Core.Paging;

namespace Foundry.Api.MediatR;

public class InsertCommandHandler<TEntity> : IRequestHandler<InsertCommand<TEntity>, TEntity>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public InsertCommandHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<TEntity> Handle(InsertCommand<TEntity> request, CancellationToken cancellationToken)
    {
        await _repository.InsertAsync(request.Entity, ct: cancellationToken);
        return request.Entity;
    }
}

public class UpdateCommandHandler<TEntity> : IRequestHandler<UpdateCommand<TEntity>, TEntity>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public UpdateCommandHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<TEntity> Handle(UpdateCommand<TEntity> request, CancellationToken cancellationToken)
    {
        await _repository.UpdateAsync(request.Entity, ct: cancellationToken);
        return request.Entity;
    }
}

public class DeleteCommandHandler<TEntity> : IRequestHandler<DeleteCommand<TEntity>, bool>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public DeleteCommandHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<bool> Handle(DeleteCommand<TEntity> request, CancellationToken cancellationToken)
    {
        await _repository.DeleteByObjectIdAsync(request.Id, request.OperatorId, ct: cancellationToken);
        return true;
    }
}

public class GetByIdQueryHandler<TEntity> : IRequestHandler<GetByIdQuery<TEntity>, TEntity?>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public GetByIdQueryHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<TEntity?> Handle(GetByIdQuery<TEntity> request, CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(request.Id, ct: cancellationToken);
    }
}

public class FindManyQueryHandler<TEntity> : IRequestHandler<FindManyQuery<TEntity>, IReadOnlyList<TEntity>>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public FindManyQueryHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<TEntity>> Handle(FindManyQuery<TEntity> request, CancellationToken cancellationToken)
    {
        if (request.Criteria != null && request.Criteria.Length > 0)
        {
            return await _repository.FindByCriteriaAsync(request.Criteria, ct: cancellationToken);
        }
        return await _repository.FindManyAsync(request.Filter, request.SortBy, request.SortOrder, request.Limit, ct: cancellationToken);
    }
}

public class SearchPagedQueryHandler<TEntity> : IRequestHandler<SearchPagedQuery<TEntity>, PagedResult<TEntity>>
    where TEntity : class, IEntity<ObjectId>
{
    private readonly IRepository<TEntity> _repository;

    public SearchPagedQueryHandler(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public async Task<PagedResult<TEntity>> Handle(SearchPagedQuery<TEntity> request, CancellationToken cancellationToken)
    {
        return await _repository.SearchPagedAsync(request.Criteria, request.PageRequest, ct: cancellationToken);
    }
}
