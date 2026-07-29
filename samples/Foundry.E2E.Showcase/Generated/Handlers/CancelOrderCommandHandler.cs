// <foundry-scaffold/>
//
// Foundry generated this file once as a starting point and will never overwrite it.
// This is your file: put your business logic here and commit it.
//
#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Foundry.E2E.Showcase;

namespace Foundry.E2E.Showcase.Handlers;

public class CancelOrderCommandHandler : IRequestHandler<CancelOrderCommand, bool>
{
    private readonly IRepository<Order> _repository;

    public CancelOrderCommandHandler(IRepository<Order> repository)
    {
        _repository = repository;
    }

    public async Task<bool> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = await _repository.GetByIdAsync(request.Id);
        if (entity == null)
        {
            return false;
        }

        // Apply visual assignments
        entity = entity with
        {
            Status = request.NewStatus,
        };

        await _repository.UpdateAsync(entity);
        return true;
    }
}