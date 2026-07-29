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

public class InStockProductsQueryHandler : IRequestHandler<InStockProductsQuery, System.Collections.Generic.IReadOnlyList<Product>>
{
    private readonly IRepository<Product> _repository;

    public InStockProductsQueryHandler(IRepository<Product> repository)
    {
        _repository = repository;
    }

    public async Task<System.Collections.Generic.IReadOnlyList<Product>> Handle(InStockProductsQuery request, CancellationToken cancellationToken)
    {
        var items = await _repository.FindManyAsync(
            x => x.StockQuantity > request.MinimumStock,
            ct: cancellationToken);

        // Returning the entities directly. Project them into a DTO here if the API should not
        // expose the full entity shape — declare that DTO in the schema's "dtos" section.
        return items;
    }
}