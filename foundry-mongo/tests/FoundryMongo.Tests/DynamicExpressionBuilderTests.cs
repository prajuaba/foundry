using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Mongo.Infrastructure.Search;
using MongoDB.Bson;
using Xunit;

namespace Foundry.Mongo.Tests;

public class DynamicExpressionBuilderTests
{
    public record TestEntity : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public double Score { get; set; }
        public ObjectId CategoryId { get; set; }
        public DateTime JoinedOn { get; set; }
    }

    private readonly List<TestEntity> _data = new()
    {
        new() { Id = ObjectId.GenerateNewId(), Name = "Alice", Age = 30, IsActive = true, Score = 95.5, CategoryId = ObjectId.Parse("507f1f77bcf86cd799439011") },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Age = 25, IsActive = false, Score = 88.0, CategoryId = ObjectId.Parse("507f1f77bcf86cd799439012") },
        new() { Id = ObjectId.GenerateNewId(), Name = "Charlie", Age = 35, IsActive = true, Score = 92.3, CategoryId = ObjectId.Parse("507f1f77bcf86cd799439011") },
        new() { Id = ObjectId.GenerateNewId(), Name = "David", Age = 40, IsActive = false, Score = 75.0, CategoryId = ObjectId.Parse("507f1f77bcf86cd799439013") }
    };

    [Fact]
    public void BuildExpression_EqualsOperator_FiltersCorrectly()
    {
        var criteria = new[] { SearchCriterion.Equals("Name", "Alice") };
        var expr = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);
        
        var results = _data.AsQueryable().Where(expr).ToList();
        
        Assert.Single(results);
        Assert.Equal("Alice", results[0].Name);
    }

    [Fact]
    public void BuildExpression_GreaterThanOperator_FiltersCorrectly()
    {
        var criteria = new[] { SearchCriterion.GreaterThan("Age", 30) };
        var expr = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);
        
        var results = _data.AsQueryable().Where(expr).ToList();
        
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Name == "Charlie");
        Assert.Contains(results, x => x.Name == "David");
    }

    [Fact]
    public void BuildExpression_ContainsOperator_FiltersCorrectly()
    {
        var criteria = new[] { SearchCriterion.Contains("Name", "a") };
        var expr = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);
        
        var results = _data.AsQueryable().Where(expr).ToList();
        
        // Alice, Charlie, David have 'a' or 'A' (Contains is case-sensitive by default in .NET reflection contains, but let's check)
        // Wait, Charlie and David have 'a'. Alice has 'A' but StartsWith/Contains is case-sensitive unless overridden.
        // Let's assert based on exact matches.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Name == "Charlie");
        Assert.Contains(results, x => x.Name == "David");
    }

    [Fact]
    public void BuildExpression_InOperator_FiltersCorrectly()
    {
        var criteria = new[] { SearchCriterion.In("Age", new object[] { 25, 35 }) };
        var expr = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);
        
        var results = _data.AsQueryable().Where(expr).ToList();
        
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Name == "Bob");
        Assert.Contains(results, x => x.Name == "Charlie");
    }

    [Fact]
    public void BuildExpression_InOperatorWithObjectIdString_ConvertsAndFiltersCorrectly()
    {
        var targetOidStr = "507f1f77bcf86cd799439011";
        var criteria = new[] { SearchCriterion.In("CategoryId", new object[] { targetOidStr }) };
        var expr = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);
        
        var results = _data.AsQueryable().Where(expr).ToList();
        
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Name == "Alice");
        Assert.Contains(results, x => x.Name == "Charlie");
    }

    // ── Criteria as they arrive over HTTP ───────────────────────────────────
    //
    // The static factories above pass real CLR values, and every one of those tests passed while
    // the same filters answered 500 over HTTP. SearchCriterion.Value is `object?`, so the route's
    // deserializer boxes it as a JsonElement, which is not IConvertible. These deserialize with the
    // options the generated route uses, so the value arrives exactly as it does in production.

    private static SearchCriterion[] FromQueryString(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var criteria = JsonSerializer.Deserialize<SearchCriterion[]>(json, options)!;

        Assert.IsType<JsonElement>(criteria[0].Value);
        return criteria;
    }

    private List<TestEntity> Run(string json)
        => _data.AsQueryable().Where(DynamicExpressionBuilder.BuildExpression<TestEntity>(FromQueryString(json))).ToList();

    [Fact]
    public void AStringFromJsonFilters()
    {
        var results = Run("""[{"field":"Name","operator":"Equals","value":"Alice"}]""");

        Assert.Equal("Alice", Assert.Single(results).Name);
    }

    [Fact]
    public void ANumberFromJsonFiltersAnIntAndADouble()
    {
        Assert.Equal(2, Run("""[{"field":"Age","operator":"GreaterThan","value":30}]""").Count);
        Assert.Equal(2, Run("""[{"field":"Score","operator":"GreaterThan","value":90.5}]""").Count);
    }

    [Fact]
    public void ABooleanFromJsonFilters()
    {
        Assert.Equal(2, Run("""[{"field":"IsActive","operator":"Equals","value":true}]""").Count);
    }

    [Fact]
    public void AnObjectIdFromJsonFilters()
    {
        Assert.Equal(2, Run("""[{"field":"CategoryId","operator":"Equals","value":"507f1f77bcf86cd799439011"}]""").Count);
    }

    [Fact]
    public void AnInListFromJsonMatchesItsMembers()
    {
        // A JsonElement array is not IEnumerable, so this built `false` and matched nothing --
        // not a 500, but an empty 200 that reads as "no such rows".
        var results = Run("""[{"field":"Name","operator":"In","value":["Alice","Bob"]}]""");

        Assert.Equal(new[] { "Alice", "Bob" }, results.Select(r => r.Name).OrderBy(n => n));
    }

    [Fact]
    public void ADateFromJsonIsTheInstantItNames()
    {
        // Convert.ChangeType parses "…Z" into the host's local time. Expression.Equal compares
        // ticks and ignores Kind, so on a UTC+7 host the filter matched rows seven hours off --
        // the P32 timezone defect on the read path. Asserted on the constant itself, so the test
        // fails on a UTC host too, where the shifted and correct values have the same ticks.
        var criteria = FromQueryString("""[{"field":"JoinedOn","operator":"Equals","value":"2026-09-01T00:00:00Z"}]""");
        var expression = DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria);

        var constant = (DateTime)((ConstantExpression)((BinaryExpression)expression.Body).Right).Value!;

        Assert.Equal(DateTimeKind.Utc, constant.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), constant);
    }

    [Fact]
    public void AValueOfTheWrongShapeIsRefusedRatherThanWidened()
    {
        // An object where a number belongs must not become "no filter". It throws, which the route
        // turns into an error response, the same as any other unconvertible value.
        var criteria = FromQueryString("""[{"field":"Age","operator":"Equals","value":{"nested":1}}]""");

        Assert.ThrowsAny<Exception>(() => DynamicExpressionBuilder.BuildExpression<TestEntity>(criteria));
    }
}
