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

/// <summary>
/// Turns a submitted order request into an <see cref="Order"/>.
/// </summary>
/// <remarks>
/// A scaffold the showcase has filled in, which is what scaffolds are for: the compiler wrote the
/// class, its constructor and its repository dependency from the schema, and stopped where the
/// decisions start. Regenerating the project leaves this file exactly as it is.
/// </remarks>
public class SubmitOrderCommandHandler : IRequestHandler<SubmitOrderCommand, bool>
{
    private readonly IRepository<Order> _repository;

    public SubmitOrderCommandHandler(IRepository<Order> repository)
    {
        _repository = repository;
    }

    public async Task<bool> Handle(SubmitOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = new Order
        {
            Id = ObjectId.GenerateNewId(),
            CustomerId = ObjectId.TryParse(request.CustomerId, out var customerId) ? customerId : ObjectId.Empty,
            OrderNumber = request.OrderNumber,
            TotalAmount = request.TotalAmount,
            PaymentCardNumber = request.PaymentCardNumber,
            Status = OrderStatus.Pending,
            Shipment = ShipmentMethod.Standard,
            OrderDate = DateTime.UtcNow
        };

        await _repository.InsertAsync(entity, ct: cancellationToken);
        return true;
    }
}