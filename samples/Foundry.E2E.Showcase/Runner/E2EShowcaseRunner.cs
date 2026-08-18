using System.Text;
using MediatR;
using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;
using Foundry.Core.Paging;
using Foundry.Mongo.Repositories;
using Foundry.Rules;
using Foundry.E2E.Showcase.Services;
using Foundry.Api.MediatR;
using Foundry.Core.Outbox;

namespace Foundry.E2E.Showcase.Runner;

/// <summary>
/// Drives the showcase domain from inside the process, with <c>--run-e2e</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every type this touches — the entities, the commands, the file services, the workflow
/// definitions — was written by the schema compiler from <c>e2e-schema.ir.json</c>. That is the
/// claim being demonstrated: not that these features exist, but that a schema produces them.
/// </para>
/// <para>
/// This runs the layers a HTTP request cannot reach on its own (the rules engine directly, the
/// repository's paging and soft delete). The REST surface, GraphQL, real-time and the workflow
/// endpoints are exercised by serving, which is what the application does by default.
/// </para>
/// </remarks>
public class E2EShowcaseRunner
{
    /// <summary>
    /// A standard test card number, used wherever the showcase needs a value that must never
    /// survive to a place it does not belong. Named rather than repeated so the value asserted
    /// on is the same one written -- the two drifting apart would make the assertion vacuous.
    /// </summary>
    private const string TestCardNumber = "4111111111111111";

    private readonly IServiceProvider _serviceProvider;

    public E2EShowcaseRunner(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public async Task RunFullScenarioAsync()
    {
        Console.WriteLine();
        Console.WriteLine("FOUNDRY E2E SHOWCASE — every type below was generated from e2e-schema.ir.json");
        Console.WriteLine(new string('-', 79));

        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var customers = sp.GetRequiredService<IRepository<Customer>>();
        var products = sp.GetRequiredService<IRepository<Product>>();
        var orders = sp.GetRequiredService<IRepository<Order>>();
        var notes = sp.GetRequiredService<IRepository<CustomerNote>>();
        var mediator = sp.GetRequiredService<IMediator>();
        var productFiles = sp.GetRequiredService<ProductFileService>();
        var rules = sp.GetRequiredService<IBusinessRuleEngine>();

        // Customer and LedgerEntry are multi-tenant, and the data layer refuses to write a row that
        // would belong to no tenant. Over HTTP the tenant comes from the caller's token or the
        // X-Tenant-ID header via TenantContextMiddleware; there is no request here, so it is set
        // explicitly. The refusal is the framework working, not an obstacle to route around.
        sp.GetRequiredService<Foundry.Core.Tenant.ITenantContext>().SetTenantId("showcase-tenant");

        // 1. Encryption and masking, both declared per property in the schema.
        var customer = new Customer
        {
            Id = ObjectId.GenerateNewId(),
            Email = "john.doe@enterprise-foundry.io",
            FullName = "John Doe",
            PhoneNumber = "+44 7700 900123",
            CreditLimit = 50_000m,
            Tier = CustomerTier.Gold
        };
        await customers.InsertAsync(customer);
        Step("Customer stored", $"{customer.FullName} — Email encrypted (AES-256 envelope), "
            + "PhoneNumber masked under the 'contact' category");

        // 2. FileIO, from `enableFileIO` plus its allowed extensions.
        var csv = "Sku,Name,Description,UnitPrice,StockQuantity\n"
                + "PROD-001,Foundry Enterprise Suite,Everything included,1499.99,100\n"
                + "PROD-002,Foundry Analytics Engine,Reporting and dashboards,899.50,50\n"
                + "PROD-003,Foundry Cloud Gateway,Ingress and routing,499.00,0\n";

        using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var imported = await productFiles.ImportAllAsync(csvStream, "catalog.csv");
        foreach (var product in imported)
        {
            await products.InsertAsync(product with { Id = ObjectId.GenerateNewId() });
        }
        Step("Catalog imported", $"{imported.Count} products through the generated ProductFileService");

        // 3. The rules engine, on the request the custom endpoint is typed against.
        var submit = new SubmitOrderCommand
        {
            CustomerId = customer.Id.ToString(),
            OrderNumber = $"ORD-{DateTime.UtcNow:yyyyMMddHHmmss}",
            TotalAmount = 2_399.49m,
            PaymentCardNumber = TestCardNumber
        };

        await mediator.Send(submit);
        Step("Order submitted", $"{submit.OrderNumber} via the generated handler and SubmitOrderRule");

        var results = await rules.EvaluateAsync(submit with { TotalAmount = 0m }, CancellationToken.None);
        var refusal = results.FirstOrDefault(r => !r.IsPassed);
        Step("Rule refused a bad order", refusal is null
            ? "NOTHING — the rule passed an order with a zero total, which it must not"
            : refusal.ErrorMessage ?? "(no message)");

        // 4. Owner-scoped data. There is no authenticated caller in this process, and the data
        //    layer refuses to write a row that would belong to nobody and be unreachable. That
        //    refusal is the feature, so the showcase demonstrates it rather than working around it.
        try
        {
            await notes.InsertAsync(new CustomerNote
            {
                Id = ObjectId.GenerateNewId(),
                OwnerId = "sales-agent-1",
                SharedWith = ["sales-agent-2"],
                Body = "Prefers phone contact after 4pm."
            });
            Step("Owner-scoped write", "ACCEPTED — which it must not be without a caller");
        }
        catch (InvalidOperationException ex)
        {
            Step("Owner-scoped write refused", ex.Message.Split('.')[0] + ".");
        }

        // 5. Paging and soft delete, from `softDelete` on the entity.
        var page = await orders.GetPagedAsync(new PagedRequest { PageNumber = 1, PageSize = 10 });
        Step("Paged orders", $"{page.Items.Count} of {page.TotalRecords}");

        var first = page.Items.FirstOrDefault();
        if (first is not null)
        {
            await orders.DeleteAsync(first.Id);
            var remaining = await orders.FindManyAsync(x => x.Id == first.Id);
            Step("Soft delete", $"{remaining.Count} row(s) still visible for {first.OrderNumber}");
        }

        // 6. Outbox redaction behavior.
        var order = new Order
        {
            Id = ObjectId.GenerateNewId(),
            CustomerId = customer.Id,
            OrderNumber = $"ORD-{DateTime.UtcNow:yyyyMMddHHmmss}",
            TotalAmount = 1234.56m,
            OrderDate = DateTime.UtcNow,
            Status = OrderStatus.Pending,
            Shipment = ShipmentMethod.Standard,

            // The whole point of the step below. Without a card number actually set, the
            // assertion that the payload does not contain one passes because nothing was
            // ever there -- a green check for a redaction that never ran.
            PaymentCardNumber = TestCardNumber
        };

        await mediator.Send(new InsertCommand<Order>(order));

        var outboxMessages = sp.GetRequiredService<IRepository<OutboxMessage>>();
        var messages = await outboxMessages.FindManyAsync(_ => true);
        var message = messages.FirstOrDefault(m => m.Payload.Contains(order.Id.ToString()));

        if (message is null)
        {
            Step("Outbox redaction", "NOTHING — no outbox row was published");
        }
        else
        {
            var payload = message.Payload;
            var hasCardNumber = payload.Contains(TestCardNumber);

            if (!hasCardNumber)
            {
                Step("Outbox redaction", $"Card number absent from payload for topic {message.Topic}");
            }
            else
            {
                Step("Outbox redaction", "LEAKED — the raw card number reached the outbox payload");
            }
        }

        Console.WriteLine(new string('-', 79));
        Console.WriteLine("Showcase complete. Run without --run-e2e to serve REST, GraphQL and real-time.");
        Console.WriteLine();
    }

    private static void Step(string what, string detail)
        => Console.WriteLine($"  {what,-26} {detail}");
}
