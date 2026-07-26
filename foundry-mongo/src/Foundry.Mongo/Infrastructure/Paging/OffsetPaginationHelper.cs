using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Core.Paging;

/// <summary>
/// Helper for offset-based pagination using MongoDB's Skip/Take pattern with aggregation pipeline support.
/// Enforces MaxDepthCap to prevent deep-scanning performance degradation. Provides both LINQ-compatible and raw MongoDB query approaches.
/// </summary>
public static class OffsetPaginationHelper
{
    /// <summary>
    /// Builds a MongoDB.Aggregate PipelineStageDefinition[] for offset-based pagination.
    /// Produces $match → $skip → $project stages that the repository applies in sequence before materialization.
    /// Returns null when cursor is provided (indicating seek-based pagination should be used instead).
    /// </summary>
    public static IEnumerable<PipelineStageDefinition<BsonDocument, BsonDocument>> BuildPipelineStages(
        PagedRequest request)
    {
        if (request.CursorInfo != null)
            yield break;

        var maxDepth = request.MaxDepthCap;
        long totalDepth = (long)(request.PageNumber - 1) * request.PageSize;
        
        if (totalDepth > maxDepth)
            throw new ArgumentException(
                $"Offset pagination depth ({totalDepth}) exceeds configured MaxDepthCap ({maxDepth}). Use cursor-based pagination instead.");

        var skipStage = new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(
            new BsonDocument("$skip", request.PageSize * (request.PageNumber - 1)));
        var limitStage = new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(
            new BsonDocument("$limit", request.PageSize));
        
        // Return the pipeline stages in correct order (skip first since MongoDB applies stages sequentially)
        yield return skipStage;
        yield return limitStage;
    }

    /// <summary>
    /// Validates offset pagination depth against MaxDepthCap threshold and returns a warning result when approaching cap.
    /// Provides proactive performance monitoring by checking depth ratio before executing query.
    /// </summary>
    public static PaginationDepthCheck CheckDepth(int pageNumber, int pageSize, int maxDepthCap)
    {
        long totalDepth = (long)(pageNumber - 1) * pageSize;
        double ratio = (totalDepth + pageSize) / (double)maxDepthCap;
        
        return new PaginationDepthCheck(
            TotalDepthUsed: totalDepth,
            MaxDepthCap: maxDepthCap,
            PageNumber: pageNumber,
            PageSize: pageSize,
            DepthRatio: ratio,
            IsExceeded: totalDepth + pageSize > maxDepthCap,
            ApproachingCap: ratio > 0.8 // Warn when using more than 80% of cap
        );
    }

    /// <summary>
    /// Returns pagination parameters that can be directly passed to MongoDB's native aggregation pipeline.
    /// Provides the raw numeric values without creating intermediate objects for maximum performance.
    /// </summary>
    public static (long Skip, int Take) GetSkipTakeValues(PagedRequest request) => (
        Skip: (long)(request.PageNumber - 1) * request.PageSize,
        Take: request.PageSize
    );

    /// <summary>
    /// Validates that pageSize is within acceptable bounds. Throws ArgumentOutOfRangeException for invalid sizes to prevent accidental full-collection scans from bad requests.
    /// </summary>
    public static void ValidatePageSize(int? pageSize = null)
    {
        if (pageSize == null || pageSize <= 0)
            throw new ArgumentException("Page size must be a positive integer", nameof(pageSize));

        if (pageSize > 10000)
            throw new ArgumentOutOfRangeException(nameof(pageSize), 
                "PageSize cannot exceed 10,000 items to prevent full-collection performance degradation");
    }

    /// <summary>
    /// Validates pageNumber is valid and positive. Ensures queries don't use zero or negative page numbers which would cause unexpected behavior.
    /// </summary>
    public static void ValidatePageNumber(int pageNumber)
    {
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), 
                $"Page number must be greater than zero but received '{pageNumber}'");
    }

    // Helper class to return check results with all depth-related fields populated for reporting
    public sealed record PaginationDepthCheck(
        long TotalDepthUsed,
        int MaxDepthCap,
        int PageNumber,
        int PageSize,
        double DepthRatio,
        bool IsExceeded,
        bool ApproachingCap
    );
}
