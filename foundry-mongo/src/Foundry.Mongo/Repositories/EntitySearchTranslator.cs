using System.Linq.Expressions;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using Foundry.Mongo.Infrastructure.Search;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// Turns a caller's search request into the query language MongoDB actually reads — the translation
/// half of <see cref="Repository{T}"/>, as a collaborator rather than a region of a file.
/// </summary>
/// <remarks>
/// <para>
/// This cluster has produced two defects, both the same root cause and both silent. A
/// <see cref="FilterDefinition{T}"/> resolves property names through the class map; a hand-built
/// <see cref="BsonDocument"/> does not. So a stage naming <c>Price</c> against documents that store
/// <c>price</c> does not error — it matches nothing. The soft-delete predicate in cross-collection
/// search never excluded a row, and the criteria never matched a field, and for a <em>search</em>
/// "no results" is a plausible answer every time.
/// </para>
/// <para>
/// The members are unchanged from when they lived on <c>Repository&lt;T&gt;</c>. This is a move, not
/// a redesign. What changes is that the two languages this class speaks — the typed builder and the
/// raw aggregation document — are now side by side, where the difference between them is visible
/// instead of being spread over eight hundred lines.
/// </para>
/// <para>
/// Its one dependency is <see cref="EntityAccessPolicy{T}"/>: the pipeline has to be isolated as it
/// is assembled, and criteria have to be entitled before they are compiled. There is no collection,
/// no session and no database here — the repository runs what this class builds.
/// </para>
/// </remarks>
internal sealed class EntitySearchTranslator<T> where T : class, IEntity<ObjectId>
{
    private readonly EntityAccessPolicy<T> _accessPolicy;

    public EntitySearchTranslator(EntityAccessPolicy<T> accessPolicy)
    {
        _accessPolicy = accessPolicy;
    }

    public Expression<Func<T, bool>> BuildExpression(SearchCriterion[] criteria)
    {
        _accessPolicy.EnsureCriteriaAreFilterable(criteria);
        return DynamicExpressionBuilder.BuildExpression<T>(criteria);
    }

    public static SortDefinition<T>? BuildSortDefinition(PagedRequest request)
    {
        if (request.CursorInfo != null)
        {
            return request.CursorInfo.Order == SortOrder.Ascending
                ? Builders<T>.Sort.Ascending(request.CursorInfo.FieldName)
                : Builders<T>.Sort.Descending(request.CursorInfo.FieldName);
        }

        if (request.SortBy != null)
        {
            return request.SortBy.Order == SortOrder.Ascending
                ? Builders<T>.Sort.Ascending(request.SortBy.FieldName)
                : Builders<T>.Sort.Descending(request.SortBy.FieldName);
        }

        return Builders<T>.Sort.Descending(e => e.Id);
    }

    /// <summary>
    /// Assembles the whole cross-collection aggregation: a match and projection over the first
    /// collection, a <c>$unionWith</c> for each of the others, and a <c>$facet</c> that pages and
    /// counts in one pass.
    /// </summary>
    /// <remarks>
    /// Every collection is matched and isolated on the way in rather than filtered afterwards. The
    /// projection is <c>$$ROOT</c> — the whole document — so a stage that fails to constrain is a
    /// stage that hands back everything in the collection, which is how this path came to read every
    /// tenant's rows out of every collection it was given.
    /// </remarks>
    public List<BsonDocument> BuildCrossCollectionPipeline(
        CrossCollectionSearchRequest request,
        IReadOnlyList<(Type EntityType, string CollectionName)> collections,
        int pageNumber,
        int pageSize)
    {
        var first = collections[0];
        var firstMatchDoc = BuildBsonFilter(request.Criteria, first.EntityType);
        _accessPolicy.ApplyIsolationTo(firstMatchDoc, first.EntityType);

        var firstProjectDoc = new BsonDocument
        {
            { "_id", 0 },
            { "EntityId", new BsonDocument("$toString", "$_id") },
            { "CollectionsName", first.CollectionName },
            { "EntityType", first.EntityType.FullName ?? first.EntityType.Name },
            { "Properties", "$$ROOT" }
        };

        var unionStages = new List<BsonDocument>();
        for (int i = 1; i < collections.Count; i++)
        {
            var other = collections[i];
            var otherMatchDoc = BuildBsonFilter(request.Criteria, other.EntityType);
            _accessPolicy.ApplyIsolationTo(otherMatchDoc, other.EntityType);

            var otherProjectDoc = new BsonDocument
            {
                { "_id", 0 },
                { "EntityId", new BsonDocument("$toString", "$_id") },
                { "CollectionsName", other.CollectionName },
                { "EntityType", other.EntityType.FullName ?? other.EntityType.Name },
                { "Properties", "$$ROOT" }
            };

            var unionStage = new BsonDocument("$unionWith", new BsonDocument
            {
                { "coll", other.CollectionName },
                { "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", otherMatchDoc),
                        new BsonDocument("$project", otherProjectDoc)
                    }
                }
            });

            unionStages.Add(unionStage);
        }

        BsonDocument sortStage;
        if (request.Pagination?.SortBy != null)
        {
            var sortField = request.Pagination.SortBy.FieldName;
            if (!sortField.Equals("EntityId", StringComparison.OrdinalIgnoreCase) &&
                !sortField.Equals("CollectionsName", StringComparison.OrdinalIgnoreCase) &&
                !sortField.Equals("EntityType", StringComparison.OrdinalIgnoreCase))
            {
                sortField = "Properties." + sortField;
            }
            var sortOrder = request.Pagination.SortBy.Order == SortOrder.Ascending ? 1 : -1;
            sortStage = new BsonDocument("$sort", new BsonDocument(sortField, sortOrder));
        }
        else
        {
            sortStage = new BsonDocument("$sort", new BsonDocument("EntityId", -1));
        }

        var skip = Math.Max(0, (pageNumber - 1) * pageSize);

        var facetStage = new BsonDocument("$facet", new BsonDocument
        {
            { "metadata", new BsonArray { new BsonDocument("$count", "total") } },
            { "data", new BsonArray
                {
                    sortStage,
                    new BsonDocument("$skip", skip),
                    new BsonDocument("$limit", pageSize)
                }
            }
        });

        var mainPipeline = new List<BsonDocument>
        {
            new BsonDocument("$match", firstMatchDoc),
            new BsonDocument("$project", firstProjectDoc)
        };

        foreach (var unionStage in unionStages)
        {
            mainPipeline.Add(unionStage);
        }

        mainPipeline.Add(facetStage);

        return mainPipeline;
    }

    /// <summary>
    /// Builds an aggregation <c>$match</c> from search criteria, for <paramref name="entityType"/>.
    /// </summary>
    /// <remarks>
    /// The type is a parameter because the field names are not. A criterion names a property —
    /// <c>Price</c> — and the document stores an element — <c>price</c>, because
    /// <c>MongoDbConventions</c> registers <c>CamelCaseElementNameConvention</c>. This built the match
    /// from the property name, so every criterion matched no field and the search returned nothing,
    /// silently and without error. It is the same defect as the soft-delete predicate beside it, found
    /// the same way and left in place a cycle longer because it costs results rather than isolation.
    /// </remarks>
    public static BsonDocument BuildBsonFilter(SearchCriterion[] criteria, Type entityType)
    {
        var filterDoc = new BsonDocument();
        foreach (var criterion in criteria)
        {
            var field = EntityAccessPolicy<T>.ElementName(entityType, criterion.Field);
            var valueDoc = ConvertToBsonValue(criterion.Value);
            BsonValue operatorValue = criterion.Operator switch
            {
                SearchOperator.Equals => new BsonDocument("$eq", valueDoc),
                SearchOperator.NotEquals => new BsonDocument("$ne", valueDoc),
                SearchOperator.GreaterThan => new BsonDocument("$gt", valueDoc),
                SearchOperator.LessThan => new BsonDocument("$lt", valueDoc),
                SearchOperator.GreaterThanOrEqual => new BsonDocument("$gte", valueDoc),
                SearchOperator.LessThanOrEqual => new BsonDocument("$lte", valueDoc),
                SearchOperator.Contains => new BsonRegularExpression(EscapeRegex(criterion.Value?.ToString()), "i"),
                SearchOperator.StartsWith => new BsonRegularExpression("^" + EscapeRegex(criterion.Value?.ToString()), "i"),
                SearchOperator.EndsWith => new BsonRegularExpression(EscapeRegex(criterion.Value?.ToString()) + "$", "i"),
                SearchOperator.In => new BsonDocument("$in", new BsonArray(BuildBsonArray(criterion.Value))),
                _ => throw new NotSupportedException($"Operator '{criterion.Operator}' is not supported in Bson filters.")
            };

            if (operatorValue is BsonRegularExpression)
            {
                filterDoc[field] = operatorValue;
            }
            else
            {
                if (filterDoc.Contains(field) && filterDoc[field].IsBsonDocument)
                {
                    filterDoc[field].AsBsonDocument.Merge(operatorValue.AsBsonDocument);
                }
                else
                {
                    filterDoc[field] = operatorValue;
                }
            }
        }
        return filterDoc;
    }

    private static BsonValue ConvertToBsonValue(object? value) => value switch
    {
        null => BsonNull.Value,
        ObjectId oid => oid,
        DateTime dt => dt,
        string s => s,
        int i => i,
        long l => l,
        double d => d,
        bool b => b,
        _ => value.ToString() ?? string.Empty
    };

    private static IEnumerable<BsonValue> BuildBsonArray(object? value)
    {
        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return ConvertToBsonValue(item);
            }
        }
        else if (value != null)
        {
            yield return ConvertToBsonValue(value);
        }
    }

    private static string EscapeRegex(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Escape(pattern);
    }
}
