using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class CrossCollectionSearchTests
{
    public record Product : BaseEntity<ObjectId>
    {
        public string Sku { get; set; } = string.Empty;
        public int Price { get; set; }
    }

    public record Customer : BaseEntity<ObjectId>, ISoftDelete
    {
        public string FullName { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    [Fact]
    public async Task CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<Product>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "Products"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<Product>(Arg.Any<string>()).Returns(mockCollection);

        var mockBsonCollection = Substitute.For<IMongoCollection<BsonDocument>>();
        mockBsonCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "Products"));
        mockDb.GetCollection<BsonDocument>("Products").Returns(mockBsonCollection);

        // Mock the AggregateAsync response with a single facet result document
        var facetResult = new BsonDocument
        {
            { "metadata", new BsonArray { new BsonDocument("total", 1) } },
            { "data", new BsonArray
                {
                    new BsonDocument
                    {
                        { "EntityId", "507f1f77bcf86cd799439011" },
                        { "CollectionsName", "Products" },
                        { "EntityType", typeof(Product).FullName },
                        { "Properties", new BsonDocument { { "Sku", "PROD-X" }, { "Price", 99 } } }
                    }
                }
            }
        };

        var mockCursor = new TestAsyncCursor<BsonDocument>(facetResult);
        PipelineDefinition<BsonDocument, BsonDocument>? capturedPipeline = null;

        mockBsonCollection.AggregateAsync(
            Arg.Any<PipelineDefinition<BsonDocument, BsonDocument>>(),
            Arg.Any<AggregateOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo => {
            capturedPipeline = callInfo.Arg<PipelineDefinition<BsonDocument, BsonDocument>>();
            return Task.FromResult<IAsyncCursor<BsonDocument>>(mockCursor);
        });

        var repository = new Repository<Product>(mockDb);
        
        var request = new CrossCollectionSearchRequest
        {
            EntityTypes = new[] { typeof(Product), typeof(Customer) },
            Criteria = new[] { SearchCriterion.Equals("Price", 99) },
            Pagination = new PagedRequest
            {
                PageNumber = 1,
                PageSize = 10,
                SortBy = new SortRequest { FieldName = "Price", Order = SortOrder.Descending }
            }
        };

        // Act
        var result = await repository.CrossCollectionSearchAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalRecords);
        Assert.Equal("PROD-X", result.Items[0].Properties["Sku"]);

        // Verify captured pipeline
        Assert.NotNull(capturedPipeline);
        var renderedStages = capturedPipeline.Render(new RenderArgs<BsonDocument>(
            BsonDocumentSerializer.Instance,
            BsonSerializer.SerializerRegistry
        )).Documents.ToList();

        // 1. Initial Match (for first collection - Product)
        // 2. Initial Project
        // 3. UnionWith (for Customer)
        // 4. Facet (Pagination & Total Count)
        Assert.Equal(4, renderedStages.Count);

        // Assert initial match stage filters by price
        var matchStage = renderedStages[0];
        Assert.True(matchStage.Contains("$match"));
        Assert.Equal(99, matchStage["$match"]["Price"]["$eq"].AsInt32);

        // Assert union stage has union with Customers collection and includes soft-delete match filter since Customer is ISoftDelete
        var unionStage = renderedStages[2];
        Assert.True(unionStage.Contains("$unionWith"));
        Assert.Equal("Customers", unionStage["$unionWith"]["coll"].AsString);
        
        var unionPipeline = unionStage["$unionWith"]["pipeline"].AsBsonArray;
        var unionMatch = unionPipeline[0].AsBsonDocument;
        Assert.True(unionMatch.Contains("$match"));
        // Matches soft delete: { "IsDeleted": { "$ne": true } }
        Assert.True(unionMatch["$match"].AsBsonDocument.Contains("IsDeleted"));
        Assert.True(unionMatch["$match"]["IsDeleted"]["$ne"].AsBoolean);

        // Assert facet contains sort on Properties.Price descending, skip 0, limit 10
        var facetStage = renderedStages[3];
        Assert.True(facetStage.Contains("$facet"));
        var dataPipeline = facetStage["$facet"]["data"].AsBsonArray;
        
        var sortOp = dataPipeline[0].AsBsonDocument;
        Assert.True(sortOp.Contains("$sort"));
        Assert.Equal(-1, sortOp["$sort"]["Properties.Price"].AsInt32);

        var skipOp = dataPipeline[1].AsBsonDocument;
        Assert.True(skipOp.Contains("$skip"));
        Assert.Equal(0, skipOp["$skip"].AsInt32);

        var limitOp = dataPipeline[2].AsBsonDocument;
        Assert.True(limitOp.Contains("$limit"));
        Assert.Equal(10, limitOp["$limit"].AsInt32);
    }
}
