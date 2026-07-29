namespace Foundry.E2E.Showcase;

/// <summary>
/// The request fields for <c>POST /api/v1/orders/submit</c>.
/// </summary>
/// <remarks>
/// <para>
/// The compiler emits <c>SubmitOrderCommand</c> as a <c>partial record</c> with no properties: a
/// custom endpoint declares its route, its roles and its rules, and the shape of its request is
/// application detail the schema does not try to state. The generated file says so and points here.
/// </para>
/// <para>
/// This is the extension contract the framework promises — <c>*.Custom.cs</c> is never overwritten
/// by a regeneration — and the showcase exercises it rather than describing it: regenerate the
/// project and this file survives, while <c>Generated/Commands/SubmitOrderCommand.cs</c> is
/// rewritten from the schema.
/// </para>
/// </remarks>
public partial record SubmitOrderCommand
{
    public string CustomerId { get; init; } = string.Empty;

    public string OrderNumber { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; }

    /// <summary>Masked at rest and in responses, under the <c>financial</c> category.</summary>
    public string PaymentCardNumber { get; init; } = string.Empty;
}
