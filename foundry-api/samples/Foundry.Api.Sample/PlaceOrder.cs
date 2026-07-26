using MediatR;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Paperclip.OrderingSystem.Domain;

public record PlaceOrderCommand(string CustomerId, List<string> ItemIds) : IRequest<PlaceOrderResult>;

public record PlaceOrderResult(string OrderId, string Status);

public class PlaceOrderCommandHandler : IRequestHandler<PlaceOrderCommand, PlaceOrderResult>
{
    public Task<PlaceOrderResult> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new PlaceOrderResult("ORD-12345", "Processed"));
    }
}
