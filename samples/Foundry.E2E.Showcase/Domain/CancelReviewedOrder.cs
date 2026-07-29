using MediatR;
using Microsoft.Extensions.Logging;
using Foundry.Rules;

namespace Foundry.E2E.Showcase.Commands;

/// <summary>
/// Cancels an order that is under review, carrying the reason with it.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written on purpose. The <c>cancel</c> transition sets <c>"useCustomCommand": true</c>, which
/// tells the compiler to emit neither the command nor its handler for that transition — everything
/// else in this workflow gets both generated. The engine matches a request to a transition by its
/// trigger name, so a hand-written command participates on exactly the same terms as a generated
/// one, and gets the same guard evaluation, role checks and state write from
/// <c>WorkflowTransitionBehavior</c>.
/// </para>
/// <para>
/// The reason to write one is a field the generated shape has no way to know about: a cancellation
/// needs a reason, and the schema has nowhere to say so.
/// </para>
/// </remarks>
public record CancelReviewedOrder : IRequest<Unit>, IWorkflowTransitionRequest
{
    /// <summary>The order being cancelled.</summary>
    public string EntityId { get; init; } = string.Empty;

    /// <summary>Why it was cancelled. The whole reason this command is hand-written.</summary>
    public string Reason { get; init; } = string.Empty;

    string IWorkflowTransitionRequest.EntityId => EntityId;

    /// <inheritdoc />
    public string EntityType => "Order";

    /// <inheritdoc />
    public string TransitionId => "cancel";

    /// <inheritdoc />
    public string FromState => "Review";

    /// <inheritdoc />
    public string ToState => "Cancelled";
}

/// <summary>
/// Handles <see cref="CancelReviewedOrder"/>.
/// </summary>
/// <remarks>
/// The state change itself is applied by <c>WorkflowTransitionBehavior</c> in the pipeline, so this
/// handler carries only what is specific to cancelling — here, recording the reason.
/// </remarks>
public class CancelReviewedOrderHandler : IRequestHandler<CancelReviewedOrder, Unit>
{
    private readonly ILogger<CancelReviewedOrderHandler> _logger;

    public CancelReviewedOrderHandler(ILogger<CancelReviewedOrderHandler> logger) => _logger = logger;

    public Task<Unit> Handle(CancelReviewedOrder request, CancellationToken ct)
    {
        _logger.LogInformation(
            "Order {EntityId} cancelled during review: {Reason}", request.EntityId, request.Reason);

        return Task.FromResult(Unit.Value);
    }
}
