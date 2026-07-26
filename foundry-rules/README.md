# 📏 Foundry.Rules

`Foundry.Rules` is a decoupled, lightweight business rules validation library built on C# (.NET 10). It provides standard interfaces and an orchestrator engine to evaluate domain and validation rules independently of request execution pipelines, making them reusable across API handlers, background workers, queues, and command-line interfaces.

---

## 🗺️ Key Features

*   **🧩 Framework Independence**: Core rules have **zero coupling** to ASP.NET Core, FluentValidation, or MediatR, facilitating reuse in non-API contexts.
*   **🎙️ Orchestrator Engine (`IBusinessRuleEngine`)**: Runs lists of registered rules programmatically for a specific command payload and aggregates failures.
*   **⚠️ Domain Exception Model**: Emits custom `BusinessRuleException` detailing policy validation failures.

---

## 🛠️ Usage & Integration

### 1. Writing a Rule
Implement **`IBusinessRule<T>`** and query domain repositories or check states:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;

namespace MyProject.Domain.Rules;

public class OrderLimitRule : IBusinessRule<SubmitOrderCommand>
{
    public Task<RuleResult> ValidateAsync(SubmitOrderCommand request, CancellationToken ct)
    {
        if (request.Amount > 10000)
        {
            return Task.FromResult(RuleResult.Failure("Order amount exceeds single transaction limit.", "LIMIT_EXCEEDED"));
        }
        return Task.FromResult(RuleResult.Success());
    }
}
```

### 2. Dependency Injection Registration (`Program.cs`)
Register the rules and core engine services:

```csharp
using Microsoft.Extensions.DependencyInjection;

// 1. Add rules engine
builder.Services.AddFoundryRules();

// 2. Add custom rules
builder.Services.AddTransient<IBusinessRule<SubmitOrderCommand>, OrderLimitRule>();
```

### 3. Executing Rules Programmatically
Inject and invoke `IBusinessRuleEngine` anywhere (e.g. background worker or console app):

```csharp
public class QueueProcessor
{
    private readonly IBusinessRuleEngine _rulesEngine;

    public QueueProcessor(IBusinessRuleEngine rulesEngine)
    {
        _rulesEngine = rulesEngine;
    }

    public async Task ProcessJobAsync(SubmitOrderCommand job, CancellationToken ct)
    {
        // Throws BusinessRuleException if any validation fails
        await _rulesEngine.EnsurePassedAsync(job, ct);
        
        // OR evaluate manually
        var failures = await _rulesEngine.EvaluateAsync(job, ct);
    }
}
```
