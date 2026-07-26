#pragma warning disable IL2026, IL3050, IL2075, IL2090, IL2070, IL2060
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using HotChocolate.Resolvers;
using MediatR;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Foundry.Api.Manifest;
using Foundry.Api.MediatR;

namespace Microsoft.Extensions.DependencyInjection;

public static class GraphQLConfiguration
{
    [RequiresUnreferencedCode("Uses runtime reflection to register dynamic GraphQL schemas.")]
    [RequiresDynamicCode("Uses runtime dynamic code or generics.")]
    public static IServiceCollection AddDynamicGraphQL(this IServiceCollection services, ApiManifest? manifest = null)
    {
        if (manifest == null)
        {
            if (System.IO.File.Exists("api-manifest.json"))
            {
                var json = System.IO.File.ReadAllText("api-manifest.json");
                manifest = System.Text.Json.JsonSerializer.Deserialize<ApiManifest>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }

        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        var entityTypes = new List<Type>();
        if (manifest?.Endpoints != null)
        {
            foreach (var config in manifest.Endpoints)
            {
                var entityTypeName = $"{manifest.Namespace}.{config.Entity}";
                var entityType = allTypes.FirstOrDefault(t => t.FullName?.Equals(entityTypeName, StringComparison.OrdinalIgnoreCase) == true);
                if (entityType == null)
                {
                    entityType = allTypes.FirstOrDefault(t => t.Name.Equals(config.Entity, StringComparison.OrdinalIgnoreCase) == true 
                        && typeof(IEntity<ObjectId>).IsAssignableFrom(t));
                }

                if (entityType != null)
                {
                    entityTypes.Add(entityType);
                }
            }
        }

        services.AddGraphQLServer()
            .AddQueryType(d =>
            {
                d.Name("Query");
                foreach (var entityType in entityTypes)
                {
                    var entityName = entityType.Name;
                    var helperType = typeof(GraphQLResolverHelper<>).MakeGenericType(entityType);
                    var resolveCollectionMethod = helperType.GetMethod(nameof(GraphQLResolverHelper<OrderPlaceholder>.ResolveCollection))!;
                    var resolveByIdMethod = helperType.GetMethod(nameof(GraphQLResolverHelper<OrderPlaceholder>.ResolveById))!;

                    // get{Entity}s
                    d.Field($"get{entityName}s")
                        .Type(typeof(ListType<>).MakeGenericType(entityType))
                        .Resolve(ctx => resolveCollectionMethod.Invoke(null, new object[] { ctx }))
                        .UseFiltering()
                        .UseSorting();

                    // get{Entity}ById
                    d.Field($"get{entityName}ById")
                        .Type(entityType)
                        .Argument("id", a => a.Type<NonNullType<StringType>>())
                        .Resolve(async ctx =>
                        {
                            var id = ctx.ArgumentValue<string>("id");
                            var task = (Task)resolveByIdMethod.Invoke(null, new object[] { ctx, id })!;
                            await task;
                            return ((dynamic)task).Result;
                        });
                }
            })
            .AddMutationType(d =>
            {
                d.Name("Mutation");
                foreach (var entityType in entityTypes)
                {
                    var entityName = entityType.Name;
                    var mutationHelperType = typeof(GraphQLMutationHelper<>).MakeGenericType(entityType);
                    var createMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.CreateEntity))!;
                    var updateMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.UpdateEntity))!;
                    var deleteMethod = mutationHelperType.GetMethod(nameof(GraphQLMutationHelper<OrderPlaceholder>.DeleteEntity))!;

                    // create{Entity}
                    d.Field($"create{entityName}")
                        .Type(entityType)
                        .Argument("input", a => a.Type(typeof(NonNullType<>).MakeGenericType(entityType)))
                        .Resolve(async ctx =>
                        {
                            var input = ctx.ArgumentValue<object>("input");
                            var task = (Task)createMethod.Invoke(null, new object[] { ctx, input })!;
                            await task;
                            return ((dynamic)task).Result;
                        });

                    // update{Entity}
                    d.Field($"update{entityName}")
                        .Type(entityType)
                        .Argument("id", a => a.Type<NonNullType<StringType>>())
                        .Argument("input", a => a.Type(typeof(NonNullType<>).MakeGenericType(entityType)))
                        .Resolve(async ctx =>
                        {
                            var id = ctx.ArgumentValue<string>("id");
                            var input = ctx.ArgumentValue<object>("input");
                            var task = (Task)updateMethod.Invoke(null, new object[] { ctx, id, input })!;
                            await task;
                            return ((dynamic)task).Result;
                        });

                    // delete{Entity}
                    d.Field($"delete{entityName}")
                        .Type<BooleanType>()
                        .Argument("id", a => a.Type<NonNullType<StringType>>())
                        .Resolve(async ctx =>
                        {
                            var id = ctx.ArgumentValue<string>("id");
                            var task = (Task<bool>)deleteMethod.Invoke(null, new object[] { ctx, id })!;
                            return await task;
                        });
                }
            })
            .AddMongoDbFiltering()
            .AddMongoDbSorting();

        return services;
    }

    // Dummy placeholder for generic mapping signature resolving
    private record OrderPlaceholder : BaseEntity<ObjectId>;
}

public static class GraphQLResolverHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{
    public static IQueryable<TEntity> ResolveCollection(IResolverContext context)
    {
        var repo = context.Service<IRepository<TEntity>>();
        return repo.Collection.AsQueryable();
    }

    public static async Task<TEntity?> ResolveById(IResolverContext context, string id)
    {
        var repo = context.Service<IRepository<TEntity>>();
        if (!ObjectId.TryParse(id, out var objectId)) return null;
        return await repo.GetByIdAsync(objectId, ct: context.RequestAborted);
    }
}

public static class GraphQLMutationHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{
    public static async Task<TEntity> CreateEntity(IResolverContext context, TEntity input)
    {
        var sender = context.Service<ISender>();
        return await sender.Send(new InsertCommand<TEntity>(input), context.RequestAborted);
    }

    public static async Task<TEntity> UpdateEntity(IResolverContext context, string id, TEntity input)
    {
        var sender = context.Service<ISender>();
        if (!ObjectId.TryParse(id, out var objectId)) throw new ArgumentException("Invalid ID");
        
        var idProp = typeof(TEntity).GetProperty("Id");
        if (idProp != null && idProp.CanWrite)
        {
            idProp.SetValue(input, objectId);
        }
        
        return await sender.Send(new UpdateCommand<TEntity>(input), context.RequestAborted);
    }

    public static async Task<bool> DeleteEntity(IResolverContext context, string id)
    {
        var sender = context.Service<ISender>();
        if (!ObjectId.TryParse(id, out var objectId)) throw new ArgumentException("Invalid ID");

        var userContext = context.Service<Foundry.Core.User.ICurrentUserContext>();
        var operatorId = userContext.OperatorId ?? "anonymous";

        return await sender.Send(new DeleteCommand<TEntity>(objectId, operatorId), context.RequestAborted);
    }
}
