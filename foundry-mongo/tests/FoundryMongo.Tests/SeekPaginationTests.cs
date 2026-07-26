using System;
using System.Collections.Generic;
using System.Linq;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using MongoDB.Bson;
using Xunit;

namespace Foundry.Mongo.Tests;

public class SeekPaginationTests
{
    public record Product : BaseEntity<ObjectId>
    {
        public string Sku { get; set; } = string.Empty;
        public int Price { get; set; }
        public DateTime ReleaseDate { get; set; }
    }

    private readonly List<Product> _products = new()
    {
        new() { Id = ObjectId.Parse("507f1f77bcf86cd799439011"), Sku = "PROD-A", Price = 100, ReleaseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Id = ObjectId.Parse("507f1f77bcf86cd799439012"), Sku = "PROD-B", Price = 150, ReleaseDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Id = ObjectId.Parse("507f1f77bcf86cd799439013"), Sku = "PROD-C", Price = 200, ReleaseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Id = ObjectId.Parse("507f1f77bcf86cd799439014"), Sku = "PROD-D", Price = 250, ReleaseDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) }
    };

    [Fact]
    public void BuildSeekFilter_Ascending_FiltersCorrectly()
    {
        // Find products after PROD-B (price > 150)
        var expr = SeekPaginationHelper.BuildSeekFilter<Product>("Price", 150, ascending: true);
        var results = _products.AsQueryable().Where(expr).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Sku == "PROD-C");
        Assert.Contains(results, p => p.Sku == "PROD-D");
    }

    [Fact]
    public void BuildSeekFilter_Descending_FiltersCorrectly()
    {
        // Find products before PROD-C (price < 200)
        var expr = SeekPaginationHelper.BuildSeekFilter<Product>("Price", 200, ascending: false);
        var results = _products.AsQueryable().Where(expr).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Sku == "PROD-A");
        Assert.Contains(results, p => p.Sku == "PROD-B");
    }

    [Fact]
    public void BuildSeekFilter_WithObjectIdString_ConvertsAndFiltersCorrectly()
    {
        // Seek on Id using string representation (standard case for JSON deserialized cursor)
        var cursorValue = "507f1f77bcf86cd799439012";
        var expr = SeekPaginationHelper.BuildSeekFilter<Product>("Id", cursorValue, ascending: true);
        var results = _products.AsQueryable().Where(expr).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Sku == "PROD-C");
        Assert.Contains(results, p => p.Sku == "PROD-D");
    }

    [Fact]
    public void BuildCompoundSeekFilter_FiltersCorrectly()
    {
        // Test seek pagination with multiple fields (Price, Id)
        // Seek condition: (Price > 150) OR (Price == 150 AND Id > 507f1f77bcf86cd799439012)
        var criteria = new[]
        {
            SearchCriterion.Equals("Price", 150),
            SearchCriterion.Equals("Id", ObjectId.Parse("507f1f77bcf86cd799439012"))
        };
        var values = new object?[] { 150, ObjectId.Parse("507f1f77bcf86cd799439012") };

        var expr = SeekPaginationHelper.BuildCompoundSeekFilter<Product>(criteria, values, ascending: true);
        var results = _products.AsQueryable().Where(expr).ToList();

        // Should return PROD-C (200) and PROD-D (250). PROD-B (150, 507f1f77bcf86cd799439012) is excluded since we want strictly greater.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Sku == "PROD-C");
        Assert.Contains(results, p => p.Sku == "PROD-D");
    }
}
