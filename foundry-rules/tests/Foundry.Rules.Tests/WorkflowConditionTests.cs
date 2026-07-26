using Foundry.Rules;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Guard-condition evaluation in <see cref="WorkflowEngine"/>.
/// </summary>
/// <remarks>
/// These are the highest-consequence pure functions in the module. A guard condition that
/// evaluates <c>false</c> when it should be <c>true</c> does not error — it blocks a legitimate
/// transition with "Guard condition failed", and a workflow simply never advances. There is no
/// stack trace and nothing in a log to distinguish it from a correctly-rejected transition.
/// </remarks>
public class WorkflowConditionTests
{
    public enum OrderStatus
    {
        Draft,
        Submitted,
        Approved
    }

    private sealed class Payload
    {
        public string Status { get; init; } = "Pending";
        public decimal TotalAmount { get; init; }
        public int Quantity { get; init; }
        public OrderStatus State { get; init; }
        public DateTime DueDate { get; init; }
        public bool IsUrgent { get; init; }
        public string? Notes { get; init; }
    }

    private static WorkflowEngine Engine() => new(new EmptyServiceProvider());

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // ---- string comparisons ----

    [Theory]
    [InlineData("Status", "equal", "Pending", true)]
    [InlineData("Status", "equal", "pending", true)]      // case-insensitive by design
    [InlineData("Status", "equal", "Approved", false)]
    [InlineData("Status", "notequal", "Approved", true)]
    [InlineData("Status", "notequal", "Pending", false)]
    public void StringConditions_CompareAsExpected(string property, string op, string expected, bool result)
    {
        Assert.Equal(result, Engine().EvaluateCondition(property, op, expected, new Payload()));
    }

    // ---- numeric comparisons ----

    [Theory]
    [InlineData("greaterthan", "100", true)]
    [InlineData("greaterthan", "200", false)]
    [InlineData("lessthan", "200", true)]
    [InlineData("greaterthanorequal", "150", true)]
    [InlineData("lessthanorequal", "150", true)]
    [InlineData("equal", "150", true)]
    [InlineData("notequal", "150", false)]
    public void NumericConditions_CompareAsExpected(string op, string expected, bool result)
    {
        var payload = new Payload { TotalAmount = 150m };
        Assert.Equal(result, Engine().EvaluateCondition("TotalAmount", op, expected, payload));
    }

    [Fact]
    public void DecimalCondition_IsParsedCultureInvariantly()
    {
        // The condition value comes from a stored workflow definition, not from the current user's
        // locale. Parsing it with the ambient culture makes a workflow behave differently depending
        // on the server's regional settings: under de-DE, "100.50" parses as 10050.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            var payload = new Payload { TotalAmount = 100.50m };

            Assert.True(Engine().EvaluateCondition("TotalAmount", "equal", "100.50", payload));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ---- enum comparisons ----

    [Fact]
    public void EnumCondition_MatchesByName()
    {
        // An enum's TypeCode is that of its underlying integer, so an enum property took the
        // numeric path, decimal.TryParse("Approved") failed, and the condition returned false.
        // Every enum-valued guard condition silently blocked its transition.
        var payload = new Payload { State = OrderStatus.Approved };

        Assert.True(Engine().EvaluateCondition("State", "equal", "Approved", payload));
        Assert.True(Engine().EvaluateCondition("State", "equal", "approved", payload));
        Assert.False(Engine().EvaluateCondition("State", "equal", "Draft", payload));
        Assert.True(Engine().EvaluateCondition("State", "notequal", "Draft", payload));
    }

    [Fact]
    public void EnumCondition_MatchesByUnderlyingNumber()
    {
        // Studio may serialise an enum condition as its ordinal, so both forms must work.
        var payload = new Payload { State = OrderStatus.Approved };
        Assert.True(Engine().EvaluateCondition("State", "equal", "2", payload));
    }

    [Fact]
    public void EnumCondition_WithUnknownName_DoesNotMatch()
    {
        var payload = new Payload { State = OrderStatus.Approved };
        Assert.False(Engine().EvaluateCondition("State", "equal", "NoSuchValue", payload));
    }

    // ---- date comparisons ----

    [Fact]
    public void DateCondition_SupportsOrdering()
    {
        // DateTime is neither numeric nor meaningfully comparable as a string, so it fell through
        // to the string branch where "lessthan" is unsupported and returned false. Any
        // deadline-based or escalation workflow silently refused to advance.
        var payload = new Payload { DueDate = new DateTime(2026, 07, 01, 0, 0, 0, DateTimeKind.Utc) };

        Assert.True(Engine().EvaluateCondition("DueDate", "lessthan", "2026-08-01", payload));
        Assert.False(Engine().EvaluateCondition("DueDate", "greaterthan", "2026-08-01", payload));
        Assert.True(Engine().EvaluateCondition("DueDate", "greaterthan", "2026-06-01", payload));
        Assert.True(Engine().EvaluateCondition("DueDate", "equal", "2026-07-01", payload));
    }

    // ---- boolean comparisons ----

    [Theory]
    [InlineData("equal", "true", true)]
    [InlineData("equal", "True", true)]
    [InlineData("notequal", "false", true)]
    [InlineData("equal", "false", false)]
    public void BooleanConditions_CompareAsExpected(string op, string expected, bool result)
    {
        var payload = new Payload { IsUrgent = true };
        Assert.Equal(result, Engine().EvaluateCondition("IsUrgent", op, expected, payload));
    }

    // ---- null handling ----

    [Fact]
    public void NullValue_RespectsTheOperator()
    {
        // A null property returned "does the expected value look like null?" without consulting
        // the operator at all, so `Notes notequal null` reported true for a null Notes -- the
        // opposite of what it asserts.
        var payload = new Payload { Notes = null };

        Assert.True(Engine().EvaluateCondition("Notes", "equal", "null", payload));
        Assert.False(Engine().EvaluateCondition("Notes", "notequal", "null", payload));
        Assert.True(Engine().EvaluateCondition("Notes", "notequal", "something", payload));
        Assert.False(Engine().EvaluateCondition("Notes", "equal", "something", payload));
    }

    // ---- misconfiguration ----

    [Fact]
    public void UnknownProperty_DoesNotSilentlyPass()
    {
        // Returning false is the safe direction (the transition is blocked rather than wrongly
        // allowed), and is asserted here so it cannot drift to a silent pass.
        Assert.False(Engine().EvaluateCondition("NoSuchProperty", "equal", "x", new Payload()));
    }

    [Fact]
    public void UnknownOperator_DoesNotSilentlyPass()
    {
        Assert.False(Engine().EvaluateCondition("Status", "sortOf", "Pending", new Payload()));
    }

    [Fact]
    public void NullPayload_DoesNotSilentlyPass()
    {
        Assert.False(Engine().EvaluateCondition("Status", "equal", "Pending", null!));
    }

    [Fact]
    public void OperatorCasing_DoesNotAffectTheResult()
    {
        var payload = new Payload { TotalAmount = 150m };
        Assert.True(Engine().EvaluateCondition("TotalAmount", "GreaterThan", "100", payload));
        Assert.True(Engine().EvaluateCondition("TotalAmount", "GREATERTHAN", "100", payload));
    }
}
