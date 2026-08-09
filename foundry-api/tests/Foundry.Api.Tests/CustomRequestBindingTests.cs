using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Xunit;
using Foundry.Api.Endpoints;

namespace Foundry.Api.Tests;

/// <summary>
/// GET and DELETE custom endpoints bind their request from the route and query string.
/// </summary>
/// <remarks>
/// <para>
/// The generator emitted <c>new TRequest()</c> and sent it unpopulated, because those methods carry
/// no body. Every property held its type default, and the compiler types those properties from the
/// entity property each one filters — so a declared
/// <c>UtilizationPercent GreaterThan MinimumUtilizationPercent</c> ran as <c>&gt; 0</c> and returned
/// every row, and <c>AllocatedHours LessThan MaximumAllocatedHours</c> ran as <c>&lt; 0</c> and
/// returned none.
/// </para>
/// <para>
/// Neither failed. Both answered 200 with the wrong rows, which is the failure mode the compiler's
/// own <c>filterOperator</c> comment calls "the quietest way a generator can be wrong" — the same
/// mistake, one layer above where it had already been fixed once.
/// </para>
/// </remarks>
public class CustomRequestBindingTests
{
    private sealed record Query
    {
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
        public decimal Threshold { get; init; }
        public bool Flag { get; init; }
        public DateTime AsOf { get; init; }
        public Guid Correlation { get; init; }
        public ObjectId ResourceId { get; init; }
        public DayOfWeek Day { get; init; }
        public DateOnly On { get; init; }
        public TimeOnly At { get; init; }
        public long Big { get; init; }
        public double Ratio { get; init; }
    }

    private static Query Bind(params (string Key, string Value)[] query)
    {
        var context = new DefaultHttpContext();
        context.Request.Query = new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
                Array.ConvertAll(query, q => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(q.Key, q.Value))));
        return CustomRequestBinder.Bind<Query>(context);
    }

    [Fact]
    public void TheDeclaredFilterValueArrivesInsteadOfTheTypeDefault()
    {
        // The defect, at its smallest: this was 0, and every threshold query compared against it.
        Assert.Equal(100m, Bind(("threshold", "100")).Threshold);
        Assert.Equal(42, Bind(("count", "42")).Count);
    }

    [Theory]
    [InlineData("name", "hello")]
    [InlineData("count", "7")]
    [InlineData("big", "9000000000")]
    [InlineData("ratio", "1.5")]
    [InlineData("threshold", "12.75")]
    [InlineData("flag", "true")]
    [InlineData("day", "Friday")]
    [InlineData("on", "2026-09-07")]
    [InlineData("at", "13:45")]
    public void EveryScalarTheIrAllowsBinds(string key, string value)
    {
        // DateOnly, TimeOnly and Guid are the ones that used to fall through to Convert.ChangeType,
        // which does not know them -- so a schema declaring any of the three produced an endpoint
        // that rejected valid input.
        var bound = Bind((key, value));
        Assert.NotEqual(default, typeof(Query).GetProperty(key, System.Reflection.BindingFlags.IgnoreCase
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(bound));
    }

    [Fact]
    public void GuidAndObjectIdBind()
    {
        var guid = Guid.NewGuid();
        var oid = ObjectId.GenerateNewId();

        Assert.Equal(guid, Bind(("correlation", guid.ToString())).Correlation);
        Assert.Equal(oid, Bind(("resourceId", oid.ToString())).ResourceId);
    }

    [Fact]
    public void EnumsBindCaseInsensitively()
        => Assert.Equal(DayOfWeek.Friday, Bind(("day", "friday")).Day);

    [Fact]
    public void PropertyNamesMatchCaseInsensitively()
    {
        // Callers write camelCase query strings; the generated properties are PascalCase.
        Assert.Equal(5, Bind(("Count", "5")).Count);
        Assert.Equal(5, Bind(("count", "5")).Count);
    }

    [Fact]
    public void AnInstantIsNormalisedToUtc()
    {
        // The data layer stores UTC. An offset is converted rather than compared as written, and a
        // value with no zone is taken as UTC rather than as the server's local time -- otherwise
        // the same query means different things on two hosts.
        var withOffset = Bind(("asOf", "2026-10-01T02:00:00+02:00")).AsOf;
        Assert.Equal(DateTimeKind.Utc, withOffset.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), withOffset);

        var zoneless = Bind(("asOf", "2026-10-01T00:00:00")).AsOf;
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), zoneless);
    }

    [Fact]
    public void DecimalsAreParsedInvariantly()
    {
        // A query string is a wire format; the server's locale has no business deciding whether
        // 1.5 is one-and-a-half.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(1.5m, Bind(("threshold", "1.5")).Threshold);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void RouteValuesWinOverQueryString()
    {
        // A value matched by the route template is not the caller's to contradict with a duplicate
        // query parameter.
        var context = new DefaultHttpContext();
        context.Request.Query = new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["count"] = "1" });
        context.Request.RouteValues["count"] = "2";

        Assert.Equal(2, CustomRequestBinder.Bind<Query>(context).Count);
    }

    [Fact]
    public void AnUnparseableValueIsRejectedRatherThanIgnored()
    {
        // Skipping it would answer 200 with a result set wider than the one asked for, which is the
        // worst direction to fail. ValidationException maps to 400.
        var ex = Assert.Throws<ValidationException>(() => Bind(("count", "not-a-number")));
        Assert.Contains("count", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownParameterIsIgnored()
    {
        // Callers append tracing and cache-busting parameters, and the generated list endpoints
        // already ignore what they do not recognise.
        var bound = Bind(("count", "3"), ("utm_source", "email"));
        Assert.Equal(3, bound.Count);
    }

    [Fact]
    public void NoParametersLeavesTheDefaults()
        => Assert.Equal(0, Bind().Count);
}
