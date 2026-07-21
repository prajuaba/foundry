#nullable enable
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Collections.Generic;
using MediatR;
using MongoDB.Bson;
using Foundry.Api.Manifest;
using Foundry.Api.MediatR;
using Foundry.Core.Search;

namespace Foundry.Api.Endpoints;

public static class GeneratedEndpoints
{
    public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder endpoints, ApiManifest manifest)
    {
        // Endpoint Config for TodoItem
        var config_TodoItem = manifest.Endpoints.Find(e => e.Entity == "TodoItem")!;

            var builderGet = endpoints.MapGet("/api/v1/todos", async (HttpContext context, ISender sender) =>
            {
                var sortBy = context.Request.Query["sortBy"].ToString();
                var limitStr = context.Request.Query["limit"].ToString();
                var limit = int.TryParse(limitStr, out var parsedLimit) ? parsedLimit : 100;
                var sortOrder = string.Equals(context.Request.Query["sortOrder"].ToString(), "asc", System.StringComparison.OrdinalIgnoreCase) || string.Equals(context.Request.Query["sortOrder"].ToString(), "ascending", System.StringComparison.OrdinalIgnoreCase) ? Foundry.Core.Paging.SortOrder.Ascending : Foundry.Core.Paging.SortOrder.Descending;

                // Advanced Criteria Support
                var criteriaJson = context.Request.Query["criteria"].ToString();
                SearchCriterion[]? criteria = null;
                if (!string.IsNullOrEmpty(criteriaJson))
                {
                    try {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                        criteria = JsonSerializer.Deserialize<SearchCriterion[]>(criteriaJson, options);
                    } catch {}
                }

                var filterExpr = DynamicEndpointRouteBuilder.BuildFilterExpression<Foundry.Api.Template.Domain.TodoItem>(context);
                var query = new FindManyQuery<Foundry.Api.Template.Domain.TodoItem>(filterExpr, sortBy, sortOrder, limit, criteria);
                var result = await sender.Send(query, context.RequestAborted);
                return Results.Text(JsonSerializer.Serialize(result), "application/json");
            });
            ConfigureMetadata(builderGet, config_TodoItem, "GET", typeof(Foundry.Api.Template.Domain.TodoItem), 200);
            var builderGetId = endpoints.MapGet("/api/v1/todos/{id}", async (string id, HttpContext context, ISender sender) =>
            {
                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId.");
                var query = new GetByIdQuery<Foundry.Api.Template.Domain.TodoItem>(objectId);
                var result = await sender.Send(query, context.RequestAborted);
                return result != null ? Results.Text(JsonSerializer.Serialize(result), "application/json") : Results.NotFound();
            });
            ConfigureMetadata(builderGetId, config_TodoItem, "GET_BY_ID", typeof(Foundry.Api.Template.Domain.TodoItem), 200);
            var builderPost = endpoints.MapPost("/api/v1/todos", async (Foundry.Api.Template.Domain.TodoItem entity, HttpContext context, ISender sender) =>
            {
                var command = new InsertCommand<Foundry.Api.Template.Domain.TodoItem>(entity);
                var result = await sender.Send(command, context.RequestAborted);
                context.Response.Headers.Location = "/api/v1/todos/" + ((dynamic)result).Id;
                return Results.Text(JsonSerializer.Serialize(result), "application/json", statusCode: 201);
            });
            ConfigureMetadata(builderPost, config_TodoItem, "POST", typeof(Foundry.Api.Template.Domain.TodoItem), 201);
            var builderPut = endpoints.MapPut("/api/v1/todos/{id}", async (string id, Foundry.Api.Template.Domain.TodoItem entity, HttpContext context, ISender sender) =>
            {
                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId.");
                var updatedEntity = entity with { Id = objectId };
                var command = new UpdateCommand<Foundry.Api.Template.Domain.TodoItem>(updatedEntity);
                var result = await sender.Send(command, context.RequestAborted);
                return Results.Text(JsonSerializer.Serialize(result), "application/json", statusCode: 200);
            });
            ConfigureMetadata(builderPut, config_TodoItem, "PUT", typeof(Foundry.Api.Template.Domain.TodoItem), 200);
            var builderDelete = endpoints.MapDelete("/api/v1/todos/{id}", async (string id, HttpContext context, ISender sender, Foundry.Core.User.ICurrentUserContext userContext) =>
            {
                if (!ObjectId.TryParse(id, out var objectId)) return Results.BadRequest("Invalid ObjectId.");
                var command = new DeleteCommand<Foundry.Api.Template.Domain.TodoItem>(objectId, userContext.OperatorId ?? string.Empty);
                await sender.Send(command, context.RequestAborted);
                return Results.NoContent();
            });
            ConfigureMetadata(builderDelete, config_TodoItem, "DELETE", typeof(Foundry.Api.Template.Domain.TodoItem), 204);
        return endpoints;
    }

    private static void ConfigureMetadata(RouteHandlerBuilder builder, EndpointConfig config, string method, Type entityType, int successStatusCode)
    {
        var rolesStr = config.Roles != null && config.Roles.TryGetValue(method, out var roles)
            ? string.Join(", ", roles)
            : "Admin";
        string summary = $"{(method == "GET_BY_ID" ? "Fetch by ID" : method == "GET" ? "List and Search" : method == "POST" ? "Insert new" : method == "PUT" ? "Update existing" : "Delete")} endpoint for {entityType.Name} collection";
        builder.WithMetadata(config)
               .WithName($"{method}_{entityType.Name}")
               .WithTags(entityType.Name)
               .WithSummary(summary)
               .WithDescription($"Access {entityType.Name} documents. Requires roles: {rolesStr}")
               .Produces(successStatusCode, entityType)
               .Produces(400, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
               .Produces(401)
               .Produces(403, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
               .Produces(500, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails));
    }
}
