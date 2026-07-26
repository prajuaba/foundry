using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using Foundry.Mongo.Repositories;
using Foundry.E2E.Showcase.Entities;
using Foundry.E2E.Showcase.Rules;

namespace Foundry.E2E.Showcase.Commands
{
    public record SubmitOrderCommand : IRequest<SubmitOrderResult>
    {
        public string CustomerId { get; init; } = string.Empty;
        public decimal TotalAmount { get; init; }
        public string OrderNumber { get; init; } = string.Empty;
    }

    public record SubmitOrderResult(string OrderId, string OrderNumber, decimal TotalAmount, string Status);

    public class SubmitOrderCommandHandler : IRequestHandler<SubmitOrderCommand, SubmitOrderResult>
    {
        private readonly IRepository<Order> _orderRepository;
        private readonly IRepository<Customer> _customerRepository;
        private readonly OrderBusinessRulesService _rulesService;

        public SubmitOrderCommandHandler(
            IRepository<Order> orderRepository,
            IRepository<Customer> customerRepository,
            OrderBusinessRulesService rulesService)
        {
            _orderRepository = orderRepository;
            _customerRepository = customerRepository;
            _rulesService = rulesService;
        }

        public async Task<SubmitOrderResult> Handle(SubmitOrderCommand request, CancellationToken cancellationToken)
        {
            // Execute business rules
            await _rulesService.ValidateOrderCommandAsync(request, cancellationToken);

            var customerId = new ObjectId(request.CustomerId);
            var customer = await _customerRepository.GetByIdAsync(customerId, null, cancellationToken);
            if (customer == null)
            {
                throw new InvalidOperationException($"Customer {request.CustomerId} not found.");
            }

            var order = new Order
            {
                Id = ObjectId.GenerateNewId(),
                CustomerId = customerId,
                OrderNumber = string.IsNullOrWhiteSpace(request.OrderNumber) ? $"ORD-{Guid.NewGuid().ToString("N")[..8].ToUpper()}" : request.OrderNumber,
                TotalAmount = request.TotalAmount,
                Status = OrderStatus.Approved,
                OrderDate = DateTime.UtcNow
            };

            await _orderRepository.InsertAsync(order, null, cancellationToken);

            return new SubmitOrderResult(
                order.Id.ToString(),
                order.OrderNumber,
                order.TotalAmount,
                order.Status.ToString()
            );
        }
    }
}
