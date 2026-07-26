using System;
using System.Collections.Generic;
using System.Linq;
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
}
